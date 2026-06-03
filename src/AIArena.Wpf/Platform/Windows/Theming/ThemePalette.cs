using System.Windows.Media;

namespace AIArena.Wpf.Services;

public sealed record ThemePalette(
    string Id,
    string Name,
    Color AppBackground,
    Color TopBar,
    Color Panel,
    Color Card,
    Color Input,
    Color Border,
    Color Text,
    Color MutedText,
    Color Primary,
    Color PrimaryBorder,
    Color Assist,
    Color AssistBorder,
    Color Danger,
    Color DangerBorder,
    Color DangerText,
    Color Disabled,
    Color DisabledBorder,
    Color DisabledText,
    Color HoverBorder,
    Color PressedPrimary,
    Color Overlay,
    Color AlphaAccent,
    Color BetaAccent,
    Color GammaAccent,
    Color DeltaAccent,
    Color NarratorAccent,
    Color OperatorAccent)
{
    public override string ToString()
    {
        return Name;
    }

    public static IReadOnlyList<ThemePalette> BuiltIn { get; } =
    [
        new(
            "system",
            "System",
            ColorFrom("#101916"),
            ColorFrom("#1A2621"),
            ColorFrom("#18231F"),
            ColorFrom("#14201B"),
            ColorFrom("#0D1714"),
            ColorFrom("#48645A"),
            ColorFrom("#DDE7E2"),
            ColorFrom("#B8C7BF"),
            ColorFrom("#105444"),
            ColorFrom("#2EA889"),
            ColorFrom("#492337"),
            ColorFrom("#E17DB6"),
            ColorFrom("#432827"),
            ColorFrom("#D66F72"),
            ColorFrom("#FF7B82"),
            ColorFrom("#111917"),
            ColorFrom("#27342F"),
            ColorFrom("#6F7D76"),
            ColorFrom("#56C0A2"),
            ColorFrom("#0E3E34"),
            ColorFrom("#CC1A2621"),
            ColorFrom("#4DD4EF"),
            ColorFrom("#F0C36A"),
            ColorFrom("#7DD98B"),
            ColorFrom("#9EA6FF"),
            ColorFrom("#E17DB6"),
            ColorFrom("#D66F72")),
        new(
            "dark-arena",
            "Dark Green",
            ColorFrom("#101916"),
            ColorFrom("#1A2621"),
            ColorFrom("#18231F"),
            ColorFrom("#14201B"),
            ColorFrom("#0D1714"),
            ColorFrom("#48645A"),
            ColorFrom("#DDE7E2"),
            ColorFrom("#B8C7BF"),
            ColorFrom("#105444"),
            ColorFrom("#2EA889"),
            ColorFrom("#492337"),
            ColorFrom("#E17DB6"),
            ColorFrom("#432827"),
            ColorFrom("#D66F72"),
            ColorFrom("#FF7B82"),
            ColorFrom("#111917"),
            ColorFrom("#27342F"),
            ColorFrom("#6F7D76"),
            ColorFrom("#56C0A2"),
            ColorFrom("#0E3E34"),
            ColorFrom("#CC1A2621"),
            ColorFrom("#4DD4EF"),
            ColorFrom("#F0C36A"),
            ColorFrom("#7DD98B"),
            ColorFrom("#9EA6FF"),
            ColorFrom("#E17DB6"),
            ColorFrom("#D66F72")),
        new(
            "dark-green",
            "Green",
            ColorFrom("#07130F"),
            ColorFrom("#10241C"),
            ColorFrom("#10271E"),
            ColorFrom("#0D211A"),
            ColorFrom("#091A14"),
            ColorFrom("#47705F"),
            ColorFrom("#E0EEE8"),
            ColorFrom("#AFC7BB"),
            ColorFrom("#0F6B50"),
            ColorFrom("#35C093"),
            ColorFrom("#3F2740"),
            ColorFrom("#D88BC9"),
            ColorFrom("#3B2222"),
            ColorFrom("#C96868"),
            ColorFrom("#FF8B8B"),
            ColorFrom("#0B1612"),
            ColorFrom("#20392F"),
            ColorFrom("#70827A"),
            ColorFrom("#54D0A4"),
            ColorFrom("#0D4D3B"),
            ColorFrom("#CC10241C"),
            ColorFrom("#61D7EC"),
            ColorFrom("#F2CA71"),
            ColorFrom("#83D98E"),
            ColorFrom("#A5AEFF"),
            ColorFrom("#D88BC9"),
            ColorFrom("#C96868")),
        new(
            "dark-blue",
            "Dark Blue",
            ColorFrom("#0B111A"),
            ColorFrom("#121D2A"),
            ColorFrom("#142132"),
            ColorFrom("#101B29"),
            ColorFrom("#0D1724"),
            ColorFrom("#4B6582"),
            ColorFrom("#E0EAF4"),
            ColorFrom("#B6C6D7"),
            ColorFrom("#174C78"),
            ColorFrom("#4BA3DD"),
            ColorFrom("#3C2543"),
            ColorFrom("#D185CE"),
            ColorFrom("#3A2428"),
            ColorFrom("#D1747B"),
            ColorFrom("#FF8D96"),
            ColorFrom("#0D1520"),
            ColorFrom("#263647"),
            ColorFrom("#738294"),
            ColorFrom("#68B8EA"),
            ColorFrom("#123A5B"),
            ColorFrom("#CC121D2A"),
            ColorFrom("#6EC9F1"),
            ColorFrom("#F1C96B"),
            ColorFrom("#85D99C"),
            ColorFrom("#9EAFFF"),
            ColorFrom("#D185CE"),
            ColorFrom("#D1747B")),
        new(
            "high-contrast",
            "High Contrast",
            ColorFrom("#050706"),
            ColorFrom("#0B100E"),
            ColorFrom("#0E1512"),
            ColorFrom("#090F0D"),
            ColorFrom("#040806"),
            ColorFrom("#8DB7A8"),
            ColorFrom("#F5FFF9"),
            ColorFrom("#D0E4DA"),
            ColorFrom("#007D64"),
            ColorFrom("#61FFD1"),
            ColorFrom("#5B2146"),
            ColorFrom("#FF8FD4"),
            ColorFrom("#4E1717"),
            ColorFrom("#FF7474"),
            ColorFrom("#FFB1B1"),
            ColorFrom("#080B0A"),
            ColorFrom("#3C514A"),
            ColorFrom("#A3B6AD"),
            ColorFrom("#96FFE0"),
            ColorFrom("#005744"),
            ColorFrom("#CC050706"),
            ColorFrom("#78E7FF"),
            ColorFrom("#FFD978"),
            ColorFrom("#A7FF9B"),
            ColorFrom("#B3B8FF"),
            ColorFrom("#FF8FD4"),
            ColorFrom("#FF7474"))
    ];

    public static ThemePalette Resolve(string? id)
    {
        var normalizedId = NormalizeId(id);
        if (string.Equals(normalizedId, "system", StringComparison.OrdinalIgnoreCase))
        {
            return BuiltIn.First(item => item.Id == "dark-arena") with { Id = "system", Name = "System" };
        }

        return BuiltIn.FirstOrDefault(item => string.Equals(item.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? BuiltIn.First(item => item.Id == "dark-arena");
    }

    public static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "dark-blue";
        }

        if (string.Equals(id.Trim(), "system", StringComparison.OrdinalIgnoreCase))
        {
            return "dark-blue";
        }

        var cleaned = id.Trim();
        var byId = BuiltIn.FirstOrDefault(item => string.Equals(item.Id, cleaned, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId.Id;
        }

        var byName = BuiltIn.FirstOrDefault(item => string.Equals(item.Name, cleaned, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName.Id;
        }

        var slug = cleaned.ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return BuiltIn.FirstOrDefault(item => string.Equals(item.Id, slug, StringComparison.OrdinalIgnoreCase))?.Id
            ?? "dark-blue";
    }

    private static Color ColorFrom(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value)!;
    }
}
