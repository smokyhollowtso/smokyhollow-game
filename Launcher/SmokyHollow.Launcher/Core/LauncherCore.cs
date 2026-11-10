using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmokyHollow.Launcher.Core;

/// <summary>Everything the launcher does that isn't drawing the window.</summary>
public sealed class LauncherCore
{
    public const string ManifestUrl = "https://smokyhollowtso.github.io/launcher.json";
    public const string DefaultServer = "http://smokyhollowtso.duckdns.org:9000";
    public const string DefaultWebsite = "https://smokyhollowtso.github.io/";
    public const string DefaultCatalog = "https://www.tsomania.net/index.php";
    public const string DefaultTsoUrl = "https://archive.org/download/TheSimsOnline_201802/TSO.zip";

    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SmokyHollowLauncher/1.0");
        return http;
    }

    public LauncherCore()
    {
        Root = DefaultRoot();
        State = LoadState();
    }

    // ---------------------------------------------------------------- where things live

    public string Root { get; }
    public string TsoDir => Path.Combine(Root, "The Sims Online");
    public string TsoClientDir => Path.Combine(TsoDir, "TSOClient");
    public string GameDir => Path.Combine(Root, "Game");
    public string DownloadDir => Path.Combine(Root, "Downloads");
    private string StatePath => Path.Combine(Root, "launcher-state.json");
    public LauncherState State { get; private set; }

    private static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmokyHollow");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SmokyHollow");

        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string baseDir = !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "SmokyHollow");
    }

    public static ClientPackage? PackageFor(LauncherManifest m) =>
        OperatingSystem.IsWindows() ? m.Client?.Windows :
        OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? m.Client?.MacArm64 ?? m.Client?.MacX64
            : m.Client?.MacX64) :
        OperatingSystem.IsLinux() ? m.Client?.Linux : null;

    /// <summary>On a Mac the game is an app bundle; the launcher starts the program inside it.</summary>
    public const string MacExe = "SmokyHollow.app/Contents/MacOS/SmokyHollow";

    public static string ExeName(LauncherManifest m) =>
        PackageFor(m)?.Exe ?? (OperatingSystem.IsWindows() ? "SmokyHollow.exe" : OperatingSystem.IsMacOS() ? MacExe : "SmokyHollow");

    public bool TsoInstalled => File.Exists(Path.Combine(TsoClientDir, "tuning.dat"));
    public bool GameInstalled(LauncherManifest m) => File.Exists(Path.Combine(GameDir, ExeName(m)));

    public bool UpdateAvailable(LauncherManifest m) =>
        m.Client?.Version is string v && GameInstalled(m) && !string.Equals(State.ClientVersion, v, StringComparison.Ordinal);

    private LauncherState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<LauncherState>(File.ReadAllText(StatePath), LauncherManifest.JsonOptions) ?? new();
        }
        catch { /* a damaged state file just means "nothing installed yet" */ }
        return new LauncherState();
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------- website + server

    public async Task<LauncherManifest> LoadManifestAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // The query string dodges stale caches so new posts show up right away.
            string json = await Http.GetStringAsync($"{ManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", cts.Token);
            var m = JsonSerializer.Deserialize<LauncherManifest>(json, LauncherManifest.JsonOptions) ?? LauncherManifest.Fallback();
            m.Server ??= DefaultServer;
            m.Website ??= DefaultWebsite;
            m.Catalog ??= DefaultCatalog;
            m.TsoUrl ??= DefaultTsoUrl;
            m.FromWebsite = true;
            return m;
        }
        catch
        {
            return LauncherManifest.Fallback();
        }
    }

    public async Task<TownStatus?> GetStatusAsync(string server)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            string json = await Http.GetStringAsync(server.TrimEnd('/') + "/userapi/status", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string status = "Up";
            string? name = null;
            if (Prop(root, "shards") is { ValueKind: JsonValueKind.Array } shards && shards.GetArrayLength() > 0)
            {
                status = Prop(shards[0], "status")?.ToString() ?? "Up";
                name = Prop(shards[0], "name")?.ToString();
            }

            var spots = new List<(string, int)>();
            if (Prop(root, "topLots") is { ValueKind: JsonValueKind.Array } lots)
            {
                foreach (var lot in lots.EnumerateArray())
                {
                    string lotName = Prop(lot, "name")?.ToString() ?? "";
                    int players = Prop(lot, "players") is { ValueKind: JsonValueKind.Number } p ? p.GetInt32() : 0;
                    if (lotName.Length > 0) spots.Add((lotName, players));
                }
            }

            return new TownStatus(status, name, Int(root, "playersOnline"), Int(root, "lotsOnline"), spots);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks the server whether sign-up needs an invite key. Assumes yes if it can't tell.</summary>
    public async Task<bool> RegistrationNeedsKeyAsync(string server)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            string json = await Http.GetStringAsync(server.TrimEnd('/') + "/userapi/registration/info", cts.Token);
            using var doc = JsonDocument.Parse(json);
            return Prop(doc.RootElement, "key_required") is not { ValueKind: JsonValueKind.False };
        }
        catch
        {
            return true;
        }
    }

    public async Task<(bool Ok, string Message)> RegisterAsync(string server, string username, string email, string password, string key)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["email"] = email,
            ["password"] = password,
            ["key"] = key
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.PostAsync(server.TrimEnd('/') + "/userapi/registration", form, cts.Token);
            string body = await response.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && Prop(root, "error") is { ValueKind: JsonValueKind.String } err)
                return (false, FriendlyRegistrationError(err.GetString(), Prop(root, "error_description")?.ToString()));

            if (!response.IsSuccessStatusCode)
                return (false, $"The town office said no ({(int)response.StatusCode}). Try again in a little while.");

            return (true, $"Welcome to Smoky Hollow, {username}! Use that name and password to log in.");
        }
        catch (JsonException)
        {
            return (false, "The town office gave a strange answer. Try again in a little while.");
        }
        catch (Exception)
        {
            return (false, "The town didn't answer. It may be closed right now. Try again later.");
        }
    }

    private static string FriendlyRegistrationError(string? error, string? description)
    {
        if (error == "key_wrong") return "That invite key isn't right. Ask the friend who invited you.";
        return description switch
        {
            "user_short" => "Usernames need at least 3 characters.",
            "user_long" => "Usernames can be 24 characters at most.",
            "user_invalid" => "Usernames use lowercase letters, numbers and underscores, and can't start with an underscore.",
            "pass_required" => "Pick a password.",
            "email_invalid" => "That email address doesn't look right.",
            "user_exists" => "That username or email is already taken.",
            "ip_banned" => "Sign-ups from this connection aren't allowed.",
            "registrations_too_frequent" => "Someone just made an account from here. Wait a minute and try again.",
            "too_many_accounts" => "This home already has 2 Smoky Hollow accounts. If someone else here wants to play, ask a Ranger on Discord.",
            "missing_confirmation_token" => "This town signs people up by email. Use the website to register.",
            _ => "Sign-up didn't go through. Try again in a little while."
        };
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static int Int(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int i) ? i : 0;

    // ---------------------------------------------------------------- install + update

    public async Task InstallAsync(LauncherManifest m, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress = new Throttled(progress);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DownloadDir);

        var package = PackageFor(m);
        if (package?.Url == null || m.Client?.Version == null)
            throw new InvalidOperationException(m.FromWebsite
                ? "There isn't a Smoky Hollow download for this kind of computer yet."
                : "Couldn't reach the Smoky Hollow website. Check your internet connection and try again.");

        if (!TsoInstalled)
            await InstallTsoAsync(m.TsoUrl ?? DefaultTsoUrl, progress, ct);

        if (!GameInstalled(m) || State.ClientVersion != m.Client.Version)
            await InstallGameAsync(m, m.Client.Version, package, progress, ct);
    }

    /// <summary>Deletes the TSO files so the next install fetches a fresh copy.</summary>
    public void RemoveTsoFiles()
    {
        if (Directory.Exists(TsoDir)) Directory.Delete(TsoDir, true);
    }

    private async Task InstallTsoAsync(string url, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        string zipPath = Path.Combine(DownloadDir, "TSO.zip");
        if (!File.Exists(zipPath))
        {
            await Downloader.DownloadAsync(Http, url, zipPath,
                (done, total) => progress.Report(Bytes("Downloading The Sims Online from the Internet Archive", done, total)), ct);
        }

        progress.Report(new InstallProgress("Opening the game archive…", null));
        string cabDir = Path.Combine(DownloadDir, "tso-cabs");
        if (Directory.Exists(cabDir)) Directory.Delete(cabDir, true);
        Directory.CreateDirectory(cabDir);

        string firstCab = await Task.Run(() => PullCabinets(zipPath, cabDir, progress, ct), ct);

        string newDir = TsoDir + ".new";
        if (Directory.Exists(newDir)) Directory.Delete(newDir, true);

        await Task.Run(() => new CabExtractor().Extract(firstCab, newDir,
            (done, total) => progress.Report(Bytes("Unpacking The Sims Online", done, total)), ct), ct);

        progress.Report(new InstallProgress("Checking the game files…", null));
        string? tuning = Directory.EnumerateFiles(newDir, "tuning.dat", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive
        }).FirstOrDefault();
        if (tuning == null)
            throw new InvalidDataException("The Sims Online files didn't unpack correctly (tuning.dat is missing).");

        string clientFolder = Path.GetDirectoryName(tuning)!;
        if (Directory.Exists(TsoDir)) Directory.Delete(TsoDir, true);
        Directory.CreateDirectory(TsoDir);
        Directory.Move(clientFolder, TsoClientDir);
        Directory.Delete(newDir, true);

        // The big downloads aren't needed once the files are unpacked.
        Directory.Delete(cabDir, true);
        File.Delete(zipPath);
    }

    /// <summary>Copies Data1.cab (and any Data2.cab, ...) out of the TSO archive. Returns Data1.cab's path.</summary>
    private static string PullCabinets(string zipPath, string cabDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var cabs = zip.Entries.Where(e => e.Name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cabs.Count == 0)
        {
            // Some copies wrap the installer in a second zip.
            var inner = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (inner == null)
                throw new InvalidDataException("The Sims Online download doesn't have the game files in it. Try More → Repair game files.");
            string innerPath = Path.Combine(cabDir, "inner.zip");
            inner.ExtractToFile(innerPath, true);
            string found = PullCabinets(innerPath, cabDir, progress, ct);
            File.Delete(innerPath);
            return found;
        }

        var first = cabs.FirstOrDefault(e => e.Name.Equals("Data1.cab", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("Couldn't find Data1.cab in The Sims Online download.");
        string folder = first.FullName.Substring(0, first.FullName.Length - first.Name.Length);
        var set = cabs.Where(e => e.FullName.Length > folder.Length &&
                                  e.FullName.StartsWith(folder, StringComparison.Ordinal) &&
                                  !e.FullName.Substring(folder.Length).Contains('/')).ToList();

        long total = set.Sum(e => e.Length);
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var entry in set)
        {
            using var src = entry.Open();
            using var dst = File.Create(Path.Combine(cabDir, entry.Name));
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buffer, 0, n);
                done += n;
                progress.Report(Bytes("Opening the game archive", done, total));
            }
        }
        return Path.Combine(cabDir, first.Name);
    }

    private async Task InstallGameAsync(LauncherManifest m, string version, ClientPackage package, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        string safeVersion = string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
        string zipPath = Path.Combine(DownloadDir, $"SmokyHollow-{safeVersion}.zip");

        foreach (var old in Directory.EnumerateFiles(DownloadDir, "SmokyHollow-*.zip*"))
            if (!old.StartsWith(zipPath, StringComparison.Ordinal)) File.Delete(old);

        if (!File.Exists(zipPath))
        {
            await Downloader.DownloadAsync(Http, package.Url!, zipPath,
                (done, total) => progress.Report(Bytes("Downloading Smoky Hollow", done, total)), ct);
        }

        if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            progress.Report(new InstallProgress("Checking the download…", null));
            string hash = await Task.Run(() =>
            {
                using var fs = File.OpenRead(zipPath);
                return Convert.ToHexString(SHA256.HashData(fs));
            }, ct);
            if (!hash.Equals(package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                throw new InvalidDataException("The Smoky Hollow download was damaged. Press the button to try again.");
            }
        }

        string exeName = ExeName(m);
        if (IsGameRunning(exeName))
            throw new InvalidOperationException("Close Smoky Hollow first, then try again.");

        string newDir = GameDir + ".new";
        if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
        await Task.Run(() => ExtractZip(zipPath, newDir,
            f => progress.Report(new InstallProgress("Unpacking Smoky Hollow", f)), ct), ct);

        // Accept zips that wrap everything in one top-level folder.
        string source = newDir;
        if (!File.Exists(Path.Combine(source, exeName)))
        {
            var dirs = Directory.GetDirectories(source);
            if (dirs.Length == 1 && Directory.GetFiles(source).Length == 0 && File.Exists(Path.Combine(dirs[0], exeName)))
                source = dirs[0];
            else
                throw new InvalidDataException($"The Smoky Hollow download is missing {exeName}.");
        }

        // Keep the player's game settings across updates.
        string configPath = Path.Combine(GameDir, "Content", "config.ini");
        byte[]? savedConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;

        progress.Report(new InstallProgress("Finishing up…", null));
        if (Directory.Exists(GameDir)) Directory.Delete(GameDir, true);
        Directory.Move(source, GameDir);
        if (Directory.Exists(newDir)) Directory.Delete(newDir, true);

        if (savedConfig != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllBytes(configPath, savedConfig);
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode runnable =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(Path.Combine(GameDir, exeName), runnable);
            string helper = Path.Combine(Path.GetDirectoryName(Path.Combine(GameDir, exeName))!, "createdump");
            if (File.Exists(helper)) File.SetUnixFileMode(helper, runnable);
        }

        State.ClientVersion = version;
        SaveState();
        File.Delete(zipPath);
    }

    private static void ExtractZip(string zipPath, string destination, Action<double> progress, CancellationToken ct)
    {
        string root = Path.GetFullPath(destination);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using var zip = ZipFile.OpenRead(zipPath);
        int count = zip.Entries.Count, i = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            string name = entry.FullName.Replace('\\', '/');
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal) && target != root)
                throw new InvalidDataException($"Unsafe file name in download: {entry.FullName}");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
            if (i % 25 == 0 || i == count) progress((double)i / count);
        }
    }

    /// <summary>Passes progress on at most a few times a second so the window stays smooth.</summary>
    private sealed class Throttled : IProgress<InstallProgress>
    {
        private readonly IProgress<InstallProgress> _inner;
        private long _last;
        public Throttled(IProgress<InstallProgress> inner) => _inner = inner;

        public void Report(InstallProgress value)
        {
            long now = Environment.TickCount64;
            bool important = value.Fraction is null || value.Fraction >= 1;
            if (!important && now - _last < 120) return;
            _last = now;
            _inner.Report(value);
        }
    }

    private static InstallProgress Bytes(string what, long done, long? total)
    {
        if (total is long t && t > 0)
            return new InstallProgress($"{what}… {Size(done)} of {Size(t)}", (double)done / t);
        return new InstallProgress($"{what}… {Size(done)}", null);
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" :
        $"{bytes / 1024.0:0} KB";

    // ---------------------------------------------------------------- play

    public static bool IsGameRunning(string exeName) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length > 0;

    public static int RunningGameCount(string exeName) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)).Length;

    public void LaunchGame(LauncherManifest m)
    {
        string exe = Path.Combine(GameDir, ExeName(m));
        if (!File.Exists(exe)) throw new FileNotFoundException("Smoky Hollow isn't installed yet.");
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = GameDir,
            UseShellExecute = false
        });
    }

    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", target);
            else
                Process.Start("xdg-open", target);
        }
        catch { /* nothing useful to do if the system can't open it */ }
    }
}
