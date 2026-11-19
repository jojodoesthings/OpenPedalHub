using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OpenPedalHub;

/// <summary>
/// Interface sounds: "select" on clicks, "next" / "back" when the profile selector moves right / left.
/// Each is a .wav next to the app; any that is missing is simply silent.
/// </summary>
public static class UiSounds
{
    static readonly Dictionary<string, MediaPlayer> Players = [];

    public static void Init()
    {
        Load("select", "select3.wav");
        Load("next", "next.wav");   // profile selector, moving right
        Load("back", "back.wav");   // profile selector, moving left
        // One handler for every clickable control in the app.
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
    }

    static void Load(string name, string file)
    {
        string wav = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(wav)) return;
        var player = new MediaPlayer { Volume = 1.0 };   // the files are recorded quietly
        player.Open(new Uri(wav));
        Players[name] = player;
    }

    public static void Play(string name)
    {
        if (!Players.TryGetValue(name, out var player)) return;
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    static void OnClick(object sender, RoutedEventArgs e)
    {
        // Slider tracks are built from repeat buttons that "click" continuously while held; leave those quiet.
        // Controls tagged "silent" play their own sound instead (the profile arrows).
        if (sender is RepeatButton || sender is FrameworkElement { Tag: "silent" }) return;
        Play("select");
    }
}
