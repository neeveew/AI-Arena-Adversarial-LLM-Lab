using System.Windows;
using System.Windows.Media;

namespace AIArena.Wpf;

/// <summary>
/// Code-behind access to the design tokens in UI/Theming/DesignTokens.xaml so
/// programmatically built UI (card renderers, coordinators) shares the same
/// scale as XAML. Fallbacks mirror the dictionary values for design-time and
/// test hosts that run without the application resources loaded.
/// </summary>
internal static class ArenaTokens
{
    private static double Number(string key, double fallback)
    {
        return Application.Current?.TryFindResource(key) is double value ? value : fallback;
    }

    private static double RadiusValue(string key, double fallback)
    {
        return Application.Current?.TryFindResource(key) is CornerRadius value ? value.TopLeft : fallback;
    }

    public static double CaptionFontSize => Number("Arena.Type.CaptionSize", 10.5);
    public static double LabelFontSize => Number("Arena.Type.LabelSize", 11.5);
    public static double BodyFontSize => Number("Arena.Type.BodySize", 12.5);
    public static double HeadingFontSize => Number("Arena.Type.HeadingSize", 14);
    public static double TitleFontSize => Number("Arena.Type.TitleSize", 17);

    public static double SmallRadiusValue => RadiusValue("Arena.Radius.Small", 6);
    public static double MediumRadiusValue => RadiusValue("Arena.Radius.Medium", 8);
    public static double LargeRadiusValue => RadiusValue("Arena.Radius.Large", 12);

    public static CornerRadius SmallRadius => new(SmallRadiusValue);
    public static CornerRadius MediumRadius => new(MediumRadiusValue);
    public static CornerRadius LargeRadius => new(LargeRadiusValue);

    public static FontFamily IconFontFamily =>
        Application.Current?.TryFindResource("Arena.Type.IconFontFamily") as FontFamily
        ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
}
