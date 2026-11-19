using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenPedalHub;

// The "game connection" checklist.
//
// Getting pedal values into a game needs two free helper programs that this app does not include:
//   vJoy     provides the virtual controller the game reads.
//   HidHide  hides the real pedals from the game, so it sees one set of pedals rather than two (optional).
//
// This screen shows which pieces are in place and helps with the ones that are not. It never downloads or
// runs an installer itself: it opens the projects' own download pages and leaves installing to the user.
// The one thing it does on the PC is create the virtual pedals in vJoy, after Windows asks for approval.
public partial class MainWindow
{
    const string HidHideDownloadPage = "https://github.com/nefarius/HidHide/releases";

    void Connection_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        ShowConnection();
    }

    void ShowConnection()
    {
        RefreshConnection();
        ConnOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>True when everything needed to reach a game is in place (HidHide is optional and not counted).</summary>
    static bool ConnectionReady => VJoyOutput.Probe() is VJoyState.Ready or VJoyState.Busy;

    void RefreshConnection()
    {
        var vjoy = App.PretendState ?? VJoyOutput.Probe();
        bool installed = vjoy != VJoyState.NotInstalled;
        bool running = installed && vjoy != VJoyState.DriverNotRunning;
        bool device = vjoy is VJoyState.Ready or VJoyState.Busy;

        // 1. vJoy itself
        SetRow(ConnDot1, ConnText1, ConnBtn1, running,
            running ? "vJoy is installed and its driver is running."
            : installed ? "vJoy is installed, but its driver is not running. Restart the PC. If that does not help, reinstall vJoy from its download page."
            : "vJoy provides the virtual controller that games read. Install it from its download page, then click Check again.",
            running ? null : "Get vJoy");

        // 2. The virtual pedals
        SetRow(ConnDot2, ConnText2, ConnBtn2, device,
            device ? (vjoy == VJoyState.Busy ? "The virtual pedals exist, but another program is using them right now." : "The virtual pedals are set up.")
            : running ? "vJoy needs a virtual controller with three pedal axes. Set up creates it as vJoy controller 1 (replacing that controller's layout if you already use it). Windows will ask for approval."
            : "Available once vJoy is installed and running.",
            device || !running ? null : "Set up");

        // 3. HidHide
        bool hide = Hider.Available && App.PretendState is null;
        SetRow(ConnDot3, ConnText3, ConnBtn3, hide,
            hide ? "HidHide is installed. Real pedals are hidden from games while this app is sending. If a game still shows them, unplug the pedals and plug them back in once."
            : "Optional. HidHide hides the real pedals so a game sees one set instead of two. Install it from its download page, restart the PC, then click Check again.",
            hide ? null : "Get HidHide", optional: true);

        ConnSummary.Text = device
            ? "Ready. Open this app before your game, then choose the vJoy pedals when you calibrate in the game."
            : "Not ready yet. Work down the list, then click Check again.";
    }

    void SetRow(Border dot, TextBlock text, Button button, bool ok, string message, string? action, bool optional = false)
    {
        dot.Background = ok ? Theme.Brush("Brand") : optional ? Theme.Brush("Groove") : new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
        ((TextBlock)dot.Child).Text = ok ? "" : optional ? "" : "";   // tick, question, warning
        text.Text = message;
        button.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        button.Content = action;
    }

    static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available: the name of what to install is on screen */ }
    }

    void ConnBtn1_Click(object sender, RoutedEventArgs e) => OpenPage(VJoyOutput.DownloadPage);

    void ConnBtn2_Click(object sender, RoutedEventArgs e)
    {
        VJoyOutput.ConfigureDevice();
        RefreshConnection();
        UpdateMode();   // start sending straight away if it worked
    }

    void ConnBtn3_Click(object sender, RoutedEventArgs e) => OpenPage(HidHideDownloadPage);

    void ConnCheck_Click(object sender, RoutedEventArgs e)
    {
        RefreshConnection();
        UpdateMode();
    }

    void ConnClose_Click(object sender, RoutedEventArgs e) => ConnOverlay.Visibility = Visibility.Collapsed;
}
