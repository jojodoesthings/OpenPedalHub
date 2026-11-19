using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenPedalHub;

public partial class App : Application
{
    /// <summary>Whether to play the startup animation. Off for the developer check modes unless "--intro" is given.</summary>
    public static bool ShowIntro { get; private set; } = true;

    /// <summary>True for the developer check modes ("--screenshot", "--probe"): no tray icon, no sounds.</summary>
    public static bool CheckMode { get; private set; }
    /// <summary>Developer aid: "--dark" shows the dark theme for a screenshot without changing the saved choice.</summary>
    public static bool ForceDark { get; private set; }
    /// <summary>
    /// Developer aid: "--pretend NotInstalled" (or DeviceNotSetUp, ...) makes the game connection screen show
    /// that situation, so its wording and buttons can be checked on a PC where everything is installed.
    /// Affects that screen's display only.
    /// </summary>
    public static VJoyState? PretendState { get; private set; }
    /// <summary>True when launched with "--tray" (Start with Windows): no window, so nothing should pop up.</summary>
    public static bool StartedInTray { get; private set; }
    /// <summary>Developer aid: "--prefer VID:PID" picks that controller when several known pedal sets are plugged in.</summary>
    public static string PreferDevice { get; private set; } = "";

    // Only one copy should run: two would fight over the virtual controller.
    const string ShowSignal = "OpenPedalHub.ShowWindow";
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;   // the overlay is a second window; closing the main one still exits
        Theme.Apply(dark: false);   // colours must exist before any window is built; the saved choice is applied by the window
        ForceDark = e.Args.Contains("--dark");
        int ps = Array.IndexOf(e.Args, "--pretend");
        if (ps >= 0 && ps + 1 < e.Args.Length && Enum.TryParse<VJoyState>(e.Args[ps + 1], out var pretend)) PretendState = pretend;
        int pd = Array.IndexOf(e.Args, "--prefer");
        if (pd >= 0 && pd + 1 < e.Args.Length) PreferDevice = e.Args[pd + 1].ToUpperInvariant();
        bool checkMode = CheckMode = e.Args.Contains("--screenshot") || e.Args.Contains("--probe");
        // "--tray" (used by Start with Windows): open quietly in the notification area.
        bool toTray = StartedInTray = e.Args.Contains("--tray") && !checkMode;
        ShowIntro = (!checkMode || e.Args.Contains("--intro")) && !toTray;

        if (!checkMode)
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal, out bool first);
            if (!first)
            {
                // Already running: ask that copy to show its window, and leave.
                if (!toTray) _showSignal.Set();
                Shutdown();
                return;
            }
        }

        if (!checkMode) { UiSounds.Init(); Autostart.CarryOverFromOldName(); }
        var window = new MainWindow();
        if (toTray) new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();   // running, but not shown
        else window.Show();

        if (_showSignal is { } signal)
        {
            var listener = new Thread(() =>
            {
                while (signal.WaitOne()) Dispatcher.BeginInvoke(window.ShowFromTray);
            }) { IsBackground = true };
            listener.Start();
        }
        // Whatever way the app ends, release the virtual pedals and unhide the real ones.
        Exit += (_, _) => window.Cleanup();
        DispatcherUnhandledException += (_, _) => window.Cleanup();

        // Developer aid: "--probe file.txt" writes what the read-only listener sees, then exits.
        int pi = Array.IndexOf(e.Args, "--probe");
        if (pi >= 0 && pi + 1 < e.Args.Length)
        {
            string report = e.Args[pi + 1];
            // "--pattern" also holds a known test pattern (25 / 50 / 75 %) on the virtual controller.
            if (e.Args.Contains("--pattern"))
            {
                var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                hold.Tick += (_, _) => window.Output.Send(0.25, 0.5, 0.75);
                hold.Start();
            }
            // "--sample-report file.json" also writes the report the set-up screen would produce, for inspection.
            int ri = Array.IndexOf(e.Args, "--sample-report");
            if (ri >= 0 && ri + 1 < e.Args.Length)
            {
                string sample = e.Args[ri + 1];
                var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                later.Tick += (_, _) => { later.Stop(); File.WriteAllText(sample, window.DebugSampleReport()); };
                later.Start();
            }
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            wait.Tick += (_, _) => { wait.Stop(); File.WriteAllText(report, window.Reader.Describe() + $"known pedal sets: {string.Join("; ", PedalSets.All.Select(d => d.Id + " " + d.Name + " (" + d.Source + ")"))}\nhidden from games: {window.Hider.Hidden}\noutput active: {window.Output.Active}\noutput status: {window.Output.Status}\noutput sends: {window.Output.Sent}\n"); Shutdown(); };
            wait.Start();
            return;
        }

        // Developer aid: "--screenshot file.png" renders the window to an image and exits.
        int i = Array.IndexOf(e.Args, "--screenshot");
        if (i < 0 || i + 1 >= e.Args.Length) return;
        string path = e.Args[i + 1];
        // "--delay ms" picks the moment, e.g. to capture a frame of the startup animation with "--intro".
        int di = Array.IndexOf(e.Args, "--delay");
        int delay = di >= 0 && di + 1 < e.Args.Length && int.TryParse(e.Args[di + 1], out int ms) ? ms : 2600;
        // "--show custom|calibration|settings" captures that part of the interface.
        int si = Array.IndexOf(e.Args, "--show");
        string show = si >= 0 && si + 1 < e.Args.Length ? e.Args[si + 1] : "";
        var root = (FrameworkElement)window.Content;
        var prepare = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, delay - 900)) };
        prepare.Tick += (_, _) => { prepare.Stop(); root = window.DebugShow(show); };
        prepare.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            const double scale = 1.5;
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) png.Save(fs);
            Shutdown();
        };
        timer.Start();
    }
}
