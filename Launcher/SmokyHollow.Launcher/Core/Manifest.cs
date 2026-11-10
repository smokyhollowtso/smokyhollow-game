using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmokyHollow.Launcher.Core;

/// <summary>
/// launcher.json on the Smoky Hollow website. Editing that file changes what every launcher shows
/// (news, which game build to install, where the town is) without rebuilding the launcher.
/// </summary>
public sealed class LauncherManifest
{
    public string? Server { get; set; }
    public string? Website { get; set; }
    public string? Catalog { get; set; }
    public string? TsoUrl { get; set; }
    public ClientInfo? Client { get; set; }
    public List<NewsItem> News { get; set; } = new();

    /// <summary>True when this came from the website, false when it's the built-in fallback.</summary>
    [JsonIgnore] public bool FromWebsite { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static LauncherManifest Fallback() => new()
    {
        Server = LauncherCore.DefaultServer,
        Website = LauncherCore.DefaultWebsite,
        Catalog = LauncherCore.DefaultCatalog,
        TsoUrl = LauncherCore.DefaultTsoUrl,
        News =
        {
            new NewsItem
            {
                Title = "Couldn't reach the town board",
                Body = "The launcher couldn't load the latest news. Check your internet connection, then try More → Check for updates.",
                Tag = "red"
            }
        }
    };
}

public sealed class ClientInfo
{
    public string? Version { get; set; }
    public ClientPackage? Windows { get; set; }
    public ClientPackage? Linux { get; set; }
    /// <summary>Macs with an Apple chip (M1 and newer).</summary>
    public ClientPackage? MacArm64 { get; set; }
    /// <summary>Older Macs with an Intel chip.</summary>
    public ClientPackage? MacX64 { get; set; }
}

public sealed class ClientPackage
{
    public string? Url { get; set; }
    public string? Exe { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class NewsItem
{
    public string? Date { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    /// <summary>Thumbtack color: red, blue, green or gold.</summary>
    public string? Tag { get; set; }
}

public sealed class LauncherState
{
    public string? ClientVersion { get; set; }
}

public readonly record struct InstallProgress(string Message, double? Fraction);

public sealed record TownStatus(string Status, string? Name, int Players, int Lots, IReadOnlyList<(string Name, int Players)> HotSpots)
{
    public bool IsOpen => !string.Equals(Status, "Down", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(Status, "Closed", StringComparison.OrdinalIgnoreCase);
}
