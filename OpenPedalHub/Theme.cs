using System.Windows;
using System.Windows.Media;

namespace OpenPedalHub;

/// <summary>
/// The two looks: Default (glossy silver) and Dark. Every colour the interface uses is a named brush set here,
/// and the XAML refers to them as dynamic resources, so switching takes effect at once.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var r = Application.Current.Resources;
        void Set(string key, Brush brush) { brush.Freeze(); r[key] = brush; }
        Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        Brush Down(params (string hex, double at)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            foreach (var (hex, at) in stops) b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex), at));
            return b;
        }

        // Shared by both looks.
        Set("Brand", Down(("#A5DC55", 0), ("#74C22A", 0.5), ("#55A816", 0.5), ("#3F8F0E", 1)));
        Set("Knob", Down(("#FFFFFF", 0), ("#CDD3D8", 1)));

        if (!dark)
        {
            Set("Bg", Solid("#E3E7EA"));
            Set("WindowBg", Down(("#FAFBFB", 0), ("#DCE1E5", 0.55), ("#BEC6CC", 1)));
            Set("Swoosh", Solid("#B4FFFFFF")); Set("Swoosh2", Solid("#40FFFFFF")); Set("SwooshEdge", Solid("#70FFFFFF"));
            Set("CardBg", Down(("#FFFFFF", 0), ("#F3F5F7", 0.6), ("#DFE4E8", 1)));
            Set("CardSolid", Solid("#FFFFFF")); Set("CardEdge", Solid("#9FA8AF"));
            Set("Line", Solid("#AEB6BD")); Set("Groove", Solid("#C2C9CF")); Set("GrooveEdge", Solid("#A3ACB3"));
            Set("Fg", Solid("#262B2F")); Set("Muted", Solid("#66707A")); Set("Green", Solid("#3F8F0E"));
            Set("Glass", Down(("#FFFFFF", 0), ("#F2F4F6", 0.5), ("#DDE2E6", 0.5), ("#EAEEF1", 1)));
            Set("GlassHot", Down(("#FFFFFF", 0), ("#EAF7DA", 0.5), ("#CFEBAE", 0.5), ("#E2F4CC", 1)));
            Set("KnobEdge", Solid("#8B949B")); Set("Divider", Solid("#22000000")); Set("Hover", Solid("#22000000"));
            Set("Field", Solid("#FFFFFF")); Set("Shade", Solid("#B8DDE2E6"));
            Set("PlotBg", Down(("#FFFFFF", 0), ("#E4E9ED", 1))); Set("PlotEdge", Solid("#AEB6BD"));
            Set("PlotGrid", Solid("#16000000")); Set("PlotRef", Solid("#8A6A747D")); Set("PlotDead", Solid("#14000000"));
        }
        else
        {
            Set("Bg", Solid("#14171A"));
            Set("WindowBg", Down(("#2A2F35", 0), ("#1A1D21", 0.55), ("#0E1012", 1)));
            Set("Swoosh", Solid("#0AFFFFFF")); Set("Swoosh2", Solid("#06FFFFFF")); Set("SwooshEdge", Solid("#16FFFFFF"));
            Set("CardBg", Down(("#363C43", 0), ("#2B3036", 0.6), ("#202428", 1)));
            Set("CardSolid", Solid("#2B3036")); Set("CardEdge", Solid("#4E565E"));
            Set("Line", Solid("#515961")); Set("Groove", Solid("#3A4148")); Set("GrooveEdge", Solid("#23272B"));
            Set("Fg", Solid("#EEF1F3")); Set("Muted", Solid("#9AA4AD")); Set("Green", Solid("#74C22A"));
            Set("Glass", Down(("#535B63", 0), ("#454D54", 0.5), ("#32383E", 0.5), ("#3B4248", 1)));
            Set("GlassHot", Down(("#6B8F3C", 0), ("#577A2C", 0.5), ("#3F5E1C", 0.5), ("#4A6C22", 1)));
            Set("KnobEdge", Solid("#15181A")); Set("Divider", Solid("#22FFFFFF")); Set("Hover", Solid("#22FFFFFF"));
            Set("Field", Solid("#1A1D21")); Set("Shade", Solid("#C80E1012"));
            Set("PlotBg", Down(("#2E343A", 0), ("#121518", 1))); Set("PlotEdge", Solid("#5A636B"));
            Set("PlotGrid", Solid("#1EFFFFFF")); Set("PlotRef", Solid("#70C8D0D6")); Set("PlotDead", Solid("#1CFFFFFF"));
        }
        Changed?.Invoke();
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
