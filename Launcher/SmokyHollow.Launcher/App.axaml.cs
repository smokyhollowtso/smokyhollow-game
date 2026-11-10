using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;

namespace SmokyHollow.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The carved-sign lettering (Alfa Slab One) is fetched by the build script. If it isn't there,
        // fall back to the heaviest serif the computer has.
        var font = new Uri("avares://SmokyHollowLauncher/Assets/Fonts/AlfaSlabOne-Regular.ttf");
        Resources["BrandFont"] = AssetLoader.Exists(font)
            ? new FontFamily("avares://SmokyHollowLauncher/Assets/Fonts#Alfa Slab One")
            : new FontFamily("Rockwell Extra Bold, Rockwell, Georgia, serif");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
