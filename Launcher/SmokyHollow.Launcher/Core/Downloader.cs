using System.Net;
using System.Net.Http.Headers;

namespace SmokyHollow.Launcher.Core;

/// <summary>Downloads a file to disk, resuming a half-finished download when the server allows it.</summary>
public static class Downloader
{
    /// <param name="progress">Called with (bytes so far, total bytes if known).</param>
    public static async Task DownloadAsync(HttpClient http, string url, string destination,
        Action<long, long?>? progress, CancellationToken ct)
    {
        string part = destination + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already complete.
            File.Move(part, destination, true);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The download server answered {(int)response.StatusCode} ({response.ReasonPhrase}).");

        bool resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming) existing = 0;

        long? total = response.Content.Headers.ContentLength is long len ? len + existing : null;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(part, resuming ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long done = existing;
            long lastReport = 0;
            progress?.Invoke(done, total);

            while (true)
            {
                int n = await source.ReadAsync(buffer, ct);
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;

                long now = Environment.TickCount64;
                if (now - lastReport > 150)
                {
                    lastReport = now;
                    progress?.Invoke(done, total);
                }
            }
            progress?.Invoke(done, total);

            if (total is long expected && done < expected)
                throw new IOException("The download stopped early. Press the button again to pick up where it left off.");
        }

        File.Move(part, destination, true);
    }
}
