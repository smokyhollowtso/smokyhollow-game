using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmokyHollow.Launcher.Core;

namespace SmokyHollow.Launcher;

public partial class MainWindow : Window
{
    private enum Mode { Loading, Install, Update, Play, Busy }

    private static readonly Regex UsernamePattern = new("^[a-z0-9][a-z0-9_]{2,23}$");

    private readonly LauncherCore _core = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private LauncherManifest _manifest = LauncherManifest.Fallback();
    private CancellationTokenSource? _work;
    private Mode _mode = Mode.Loading;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Opened += async (_, _) => await StartupAsync();
        Closing += (_, _) => _work?.Cancel();
    }

    // ---------------------------------------------------------------- startup

    private async Task StartupAsync()
    {
        SetMessage("Checking the town board…");
        await ReloadManifestAsync();
        await RefreshStatusAsync();
        _statusTimer.Start();
    }

    private async Task ReloadManifestAsync()
    {
        _manifest = await _core.LoadManifestAsync();
        ShowNews();
        UpdateMainButton();
    }

    private void ShowNews()
    {
        NewsList.Children.Clear();
        if (_manifest.News.Count == 0)
        {
            NewsList.Children.Add(Note(null, "Nothing posted yet", "Check back soon.", "gold"));
            return;
        }
        foreach (var item in _manifest.News)
            NewsList.Children.Add(Note(item.Date, item.Title ?? "", item.Body ?? "", item.Tag));
    }

    private static Control Note(string? date, string title, string body, string? tag)
    {
        var tack = new Ellipse
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = new SolidColorBrush(Color.Parse(tag?.ToLowerInvariant() switch
            {
                "blue" => "#2E6DA4",
                "green" => "#2E8B4E",
                "gold" => "#C8952F",
                _ => "#C0392B"
            }))
        };

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(tack);
        if (!string.IsNullOrWhiteSpace(date))
            stack.Children.Add(new TextBlock { Text = date.ToUpperInvariant(), Classes = { "when" } });
        stack.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Classes = { "title" } });
        if (!string.IsNullOrWhiteSpace(body))
            stack.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 13 });

        return new Border { Classes = { "note" }, Child = stack };
    }

    // ---------------------------------------------------------------- town status

    private async Task RefreshStatusAsync()
    {
        var status = await _core.GetStatusAsync(_manifest.Server ?? LauncherCore.DefaultServer);
        HotSpotsList.Children.Clear();

        if (status == null || !status.IsOpen)
        {
            StatusDot.Fill = new SolidColorBrush(Color.Parse("#C0392B"));
            StatusText.Text = "The town is closed right now";
            StatusDetail.Text = "The lights are off up the holler. Check back soon.";
            HotSpotsList.Children.Add(new TextBlock { Text = "Nobody's out and about.", Classes = { "muted" } });
            return;
        }

        StatusDot.Fill = new SolidColorBrush(Color.Parse("#3FBF6A"));
        StatusText.Text = "The town is open";
        StatusDetail.Text = $"{Count(status.Players, "neighbor")} online · {Count(status.Lots, "lot")} open";

        if (status.HotSpots.Count == 0)
        {
            HotSpotsList.Children.Add(new TextBlock { Text = "It's quiet right now. Be the first one out!", Classes = { "muted" } });
            return;
        }
        foreach (var (name, players) in status.HotSpots)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var count = new TextBlock { Text = players.ToString(), Foreground = (IBrush)this.FindResource("Gold")!, FontWeight = FontWeight.Bold };
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            HotSpotsList.Children.Add(row);
        }
    }

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    // ---------------------------------------------------------------- the big button

    private void UpdateMainButton()
    {
        if (_mode == Mode.Busy) return;
        InstallBar.IsVisible = false;

        bool installed = _core.TsoInstalled && _core.GameInstalled(_manifest);
        if (!installed)
        {
            _mode = Mode.Install;
            MainButton.Content = "INSTALL";
            MainButton.IsEnabled = _manifest.FromWebsite;
            SetMessage(_manifest.FromWebsite
                ? "Ready to install. You'll need about 3 GB of free space, and the first download takes a while."
                : "Couldn't reach the Smoky Hollow website. Check your internet, then More → Check for updates.");
        }
        else if (_core.UpdateAvailable(_manifest))
        {
            _mode = Mode.Update;
            MainButton.Content = "UPDATE";
            MainButton.IsEnabled = true;
            SetMessage($"A new version of Smoky Hollow is ready ({_manifest.Client?.Version}).");
        }
        else
        {
            _mode = Mode.Play;
            MainButton.Content = "PLAY";
            MainButton.IsEnabled = true;
            SetMessage("Ready when you are.");
        }
    }

    private async void OnMainButton(object? sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Busy:
                _work?.Cancel();
                MainButton.IsEnabled = false;
                SetMessage("Stopping…");
                break;
            case Mode.Install:
            case Mode.Update:
                await RunInstallAsync();
                break;
            case Mode.Play:
                Play();
                break;
        }
    }

    private void Play()
    {
        try
        {
            // Two accounts per player, so two game windows are fine. A third is probably a double-click.
            int open = LauncherCore.RunningGameCount(LauncherCore.ExeName(_manifest));
            if (open >= 2)
            {
                SetMessage("You already have 2 Smoky Hollow windows open.");
                return;
            }
            _core.LaunchGame(_manifest);
            SetMessage(open == 1 ? "Second window coming up. Log in with your other account." : "Have fun up the holler!");
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            SetMessage("Couldn't start the game: " + ex.Message);
        }
    }

    private async Task RunInstallAsync()
    {
        _work = new CancellationTokenSource();
        var token = _work.Token;
        _mode = Mode.Busy;
        MainButton.Content = "STOP";
        MainButton.IsEnabled = true;
        InstallBar.IsVisible = true;
        InstallBar.IsIndeterminate = true;

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressText.Text = p.Message;
            if (p.Fraction is double f)
            {
                InstallBar.IsIndeterminate = false;
                InstallBar.Value = Math.Clamp(f * 100, 0, 100);
            }
            else
            {
                InstallBar.IsIndeterminate = true;
            }
        });

        string? result = null;
        try
        {
            await Task.Run(() => _core.InstallAsync(_manifest, progress, token));
            result = "All set. Welcome to the holler!";
        }
        catch (OperationCanceledException)
        {
            result = "Stopped. Press INSTALL to pick up where you left off.";
        }
        catch (Exception ex)
        {
            result = "Something went wrong: " + ex.Message;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            _mode = Mode.Loading;
            UpdateMainButton();
            if (result != null) SetMessage(result);
        }
    }

    private void SetMessage(string text) => ProgressText.Text = text;

    // ---------------------------------------------------------------- menu + links

    private void OnWebsite(object? sender, RoutedEventArgs e) =>
        LauncherCore.Open(_manifest.Website ?? LauncherCore.DefaultWebsite);

    private void OnCatalog(object? sender, RoutedEventArgs e) =>
        LauncherCore.Open(_manifest.Catalog ?? LauncherCore.DefaultCatalog);

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Busy) return;
        SetMessage("Checking for updates…");
        await ReloadManifestAsync();
        await RefreshStatusAsync();
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_core.Root);
        LauncherCore.Open(_core.Root);
    }

    private void OnRepair(object? sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Busy) return;
        if (LauncherCore.IsGameRunning(LauncherCore.ExeName(_manifest)))
        {
            SetMessage("Close Smoky Hollow first, then try Repair again.");
            return;
        }
        try
        {
            _core.RemoveTsoFiles();
            _mode = Mode.Loading;
            UpdateMainButton();
            SetMessage("The Sims Online files were cleared. Press INSTALL to download a fresh copy.");
        }
        catch (Exception ex)
        {
            SetMessage("Couldn't clear the game files: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- guest book

    private async void OnShowRegister(object? sender, RoutedEventArgs e)
    {
        BoardPanel.IsVisible = false;
        RegisterPanel.IsVisible = true;
        RegMessage.Text = "";
        RegUser.Focus();

        bool needsKey = await _core.RegistrationNeedsKeyAsync(_manifest.Server ?? LauncherCore.DefaultServer);
        RegKeyLabel.IsVisible = needsKey;
        RegKey.IsVisible = needsKey;
    }

    private void OnShowBoard(object? sender, RoutedEventArgs e)
    {
        RegisterPanel.IsVisible = false;
        BoardPanel.IsVisible = true;
    }

    private async void OnRegisterSubmit(object? sender, RoutedEventArgs e)
    {
        string user = (RegUser.Text ?? "").Trim().ToLowerInvariant();
        string email = (RegEmail.Text ?? "").Trim();
        string pass = RegPass.Text ?? "";
        string pass2 = RegPass2.Text ?? "";
        string key = (RegKey.Text ?? "").Trim();

        string? problem =
            !UsernamePattern.IsMatch(user) ? "Usernames are 3 to 24 lowercase letters, numbers or _, and start with a letter or number." :
            !email.Contains('@') || !email.Contains('.') ? "That email address doesn't look right." :
            pass.Length < 6 ? "Pick a password with at least 6 characters." :
            pass != pass2 ? "The two passwords don't match." :
            RegKey.IsVisible && key.Length == 0 ? "Enter the invite key from the friend who invited you." :
            null;

        if (problem != null)
        {
            ShowRegMessage(problem, false);
            return;
        }

        RegSubmit.IsEnabled = false;
        ShowRegMessage("Signing you in to the guest book…", true);
        var (ok, message) = await _core.RegisterAsync(_manifest.Server ?? LauncherCore.DefaultServer, user, email, pass, key);
        RegSubmit.IsEnabled = true;
        ShowRegMessage(message, ok);

        if (ok)
        {
            RegPass.Text = "";
            RegPass2.Text = "";
        }
    }

    private void ShowRegMessage(string text, bool good)
    {
        RegMessage.Text = text;
        RegMessage.Foreground = new SolidColorBrush(Color.Parse(good ? "#2F4A35" : "#A3301F"));
    }
}
