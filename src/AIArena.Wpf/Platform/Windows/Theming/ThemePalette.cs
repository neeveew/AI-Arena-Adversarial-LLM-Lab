using System.Windows.Media;

namespace AIArena.Wpf.Services;

/// <summary>
/// A complete surface palette. Members are named rather than positional so a
/// new theme cannot silently transpose two colors, and so the navigation
/// washes stay derived from the primary accent instead of being restated.
/// </summary>
public sealed record ThemePalette
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Color AppBackground { get; init; }
    public required Color TopBar { get; init; }
    public required Color Panel { get; init; }
    public required Color Card { get; init; }
    public required Color Input { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color MutedText { get; init; }
    public required Color Primary { get; init; }
    public required Color PrimaryBorder { get; init; }
    public required Color Assist { get; init; }
    public required Color AssistBorder { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerBorder { get; init; }
    public required Color DangerText { get; init; }
    public required Color Disabled { get; init; }
    public required Color DisabledBorder { get; init; }
    public required Color DisabledText { get; init; }
    public required Color HoverBorder { get; init; }
    public required Color PressedPrimary { get; init; }
    public required Color Overlay { get; init; }
    public required Color AlphaAccent { get; init; }
    public required Color BetaAccent { get; init; }
    public required Color GammaAccent { get; init; }
    public required Color DeltaAccent { get; init; }
    public required Color NarratorAccent { get; init; }
    public required Color OperatorAccent { get; init; }

    /// <summary>Navigation hover wash, derived from the input surface and primary accent.</summary>
    public Color NavHover => Blend(Input, PrimaryBorder, 0.18);

    /// <summary>Navigation selected wash.</summary>
    public Color NavActive => Blend(Input, PrimaryBorder, 0.24);

    /// <summary>Navigation pressed wash.</summary>
    public Color NavPressed => Blend(Input, PrimaryBorder, 0.12);

    public override string ToString()
    {
        return Name;
    }

    private static Color Blend(Color baseColor, Color accent, double accentAmount)
    {
        var amount = Math.Clamp(accentAmount, 0, 1);
        return Color.FromArgb(
            baseColor.A,
            (byte)Math.Round((baseColor.R * (1 - amount)) + (accent.R * amount)),
            (byte)Math.Round((baseColor.G * (1 - amount)) + (accent.G * amount)),
            (byte)Math.Round((baseColor.B * (1 - amount)) + (accent.B * amount)));
    }

    public static IReadOnlyList<ThemePalette> BuiltIn { get; } =
    [
        new()
        {
            Id = "system",
            Name = "System",
            AppBackground = ColorFrom("#101916"),
            TopBar = ColorFrom("#1A2621"),
            Panel = ColorFrom("#18231F"),
            Card = ColorFrom("#14201B"),
            Input = ColorFrom("#0D1714"),
            Border = ColorFrom("#4D6A5F"),
            Text = ColorFrom("#DDE7E2"),
            MutedText = ColorFrom("#B8C7BF"),
            Primary = ColorFrom("#105444"),
            PrimaryBorder = ColorFrom("#2EA889"),
            Assist = ColorFrom("#492337"),
            AssistBorder = ColorFrom("#E17DB6"),
            Danger = ColorFrom("#432827"),
            DangerBorder = ColorFrom("#D66F72"),
            DangerText = ColorFrom("#FF7B82"),
            Disabled = ColorFrom("#111917"),
            DisabledBorder = ColorFrom("#27342F"),
            DisabledText = ColorFrom("#6F7D76"),
            HoverBorder = ColorFrom("#56C0A2"),
            PressedPrimary = ColorFrom("#0E3E34"),
            Overlay = ColorFrom("#CC1A2621"),
            AlphaAccent = ColorFrom("#4DD4EF"),
            BetaAccent = ColorFrom("#F0C36A"),
            GammaAccent = ColorFrom("#7DD98B"),
            DeltaAccent = ColorFrom("#9EA6FF"),
            NarratorAccent = ColorFrom("#E17DB6"),
            OperatorAccent = ColorFrom("#7FB7FF"),
        },
        new()
        {
            Id = "dark-arena",
            Name = "Dark Green",
            AppBackground = ColorFrom("#101916"),
            TopBar = ColorFrom("#1A2621"),
            Panel = ColorFrom("#18231F"),
            Card = ColorFrom("#14201B"),
            Input = ColorFrom("#0D1714"),
            Border = ColorFrom("#4D6A5F"),
            Text = ColorFrom("#DDE7E2"),
            MutedText = ColorFrom("#B8C7BF"),
            Primary = ColorFrom("#105444"),
            PrimaryBorder = ColorFrom("#2EA889"),
            Assist = ColorFrom("#492337"),
            AssistBorder = ColorFrom("#E17DB6"),
            Danger = ColorFrom("#432827"),
            DangerBorder = ColorFrom("#D66F72"),
            DangerText = ColorFrom("#FF7B82"),
            Disabled = ColorFrom("#111917"),
            DisabledBorder = ColorFrom("#27342F"),
            DisabledText = ColorFrom("#6F7D76"),
            HoverBorder = ColorFrom("#56C0A2"),
            PressedPrimary = ColorFrom("#0E3E34"),
            Overlay = ColorFrom("#CC1A2621"),
            AlphaAccent = ColorFrom("#4DD4EF"),
            BetaAccent = ColorFrom("#F0C36A"),
            GammaAccent = ColorFrom("#7DD98B"),
            DeltaAccent = ColorFrom("#9EA6FF"),
            NarratorAccent = ColorFrom("#E17DB6"),
            OperatorAccent = ColorFrom("#7FB7FF"),
        },
        new()
        {
            Id = "dark-green",
            Name = "Green",
            AppBackground = ColorFrom("#07130F"),
            TopBar = ColorFrom("#10241C"),
            Panel = ColorFrom("#10271E"),
            Card = ColorFrom("#0D211A"),
            Input = ColorFrom("#091A14"),
            Border = ColorFrom("#47705F"),
            Text = ColorFrom("#E0EEE8"),
            MutedText = ColorFrom("#AFC7BB"),
            Primary = ColorFrom("#0F6B50"),
            PrimaryBorder = ColorFrom("#35C093"),
            Assist = ColorFrom("#3F2740"),
            AssistBorder = ColorFrom("#D88BC9"),
            Danger = ColorFrom("#3B2222"),
            DangerBorder = ColorFrom("#C96868"),
            DangerText = ColorFrom("#FF8B8B"),
            Disabled = ColorFrom("#0B1612"),
            DisabledBorder = ColorFrom("#20392F"),
            DisabledText = ColorFrom("#70827A"),
            HoverBorder = ColorFrom("#54D0A4"),
            PressedPrimary = ColorFrom("#0D4D3B"),
            Overlay = ColorFrom("#CC10241C"),
            AlphaAccent = ColorFrom("#61D7EC"),
            BetaAccent = ColorFrom("#F2CA71"),
            GammaAccent = ColorFrom("#83D98E"),
            DeltaAccent = ColorFrom("#A5AEFF"),
            NarratorAccent = ColorFrom("#D88BC9"),
            OperatorAccent = ColorFrom("#7FB7FF"),
        },
        new()
        {
            Id = "dark-blue",
            Name = "Dark Blue",
            AppBackground = ColorFrom("#0B111A"),
            TopBar = ColorFrom("#121D2A"),
            Panel = ColorFrom("#142132"),
            Card = ColorFrom("#101B29"),
            Input = ColorFrom("#0D1724"),
            Border = ColorFrom("#4C6684"),
            Text = ColorFrom("#E0EAF4"),
            MutedText = ColorFrom("#B6C6D7"),
            Primary = ColorFrom("#174C78"),
            PrimaryBorder = ColorFrom("#4BA3DD"),
            Assist = ColorFrom("#3C2543"),
            AssistBorder = ColorFrom("#D185CE"),
            Danger = ColorFrom("#3A2428"),
            DangerBorder = ColorFrom("#D1747B"),
            DangerText = ColorFrom("#FF8D96"),
            Disabled = ColorFrom("#0D1520"),
            DisabledBorder = ColorFrom("#263647"),
            DisabledText = ColorFrom("#738294"),
            HoverBorder = ColorFrom("#68B8EA"),
            PressedPrimary = ColorFrom("#123A5B"),
            Overlay = ColorFrom("#CC121D2A"),
            AlphaAccent = ColorFrom("#6EC9F1"),
            BetaAccent = ColorFrom("#F1C96B"),
            GammaAccent = ColorFrom("#85D99C"),
            DeltaAccent = ColorFrom("#9EAFFF"),
            NarratorAccent = ColorFrom("#D185CE"),
            OperatorAccent = ColorFrom("#7FB7FF"),
        },
        new()
        {
            Id = "light",
            Name = "Light",
            AppBackground = ColorFrom("#EDF2EF"),
            TopBar = ColorFrom("#E2E9E5"),
            Panel = ColorFrom("#E7EDEA"),
            Card = ColorFrom("#F3F6F4"),
            Input = ColorFrom("#FCFDFC"),
            Border = ColorFrom("#6E837A"),
            Text = ColorFrom("#17211D"),
            MutedText = ColorFrom("#46544D"),
            Primary = ColorFrom("#C2E5D8"),
            PrimaryBorder = ColorFrom("#177A5E"),
            Assist = ColorFrom("#F1DBE9"),
            AssistBorder = ColorFrom("#A93E78"),
            Danger = ColorFrom("#F5DADA"),
            DangerBorder = ColorFrom("#B04A4E"),
            DangerText = ColorFrom("#8F2E33"),
            Disabled = ColorFrom("#E2E6E4"),
            DisabledBorder = ColorFrom("#C2CBC7"),
            DisabledText = ColorFrom("#7C8781"),
            HoverBorder = ColorFrom("#1F8A6B"),
            PressedPrimary = ColorFrom("#A5D3C2"),
            Overlay = ColorFrom("#CCE2E9E5"),
            AlphaAccent = ColorFrom("#0C6E88"),
            BetaAccent = ColorFrom("#8A5F00"),
            GammaAccent = ColorFrom("#1F7A36"),
            DeltaAccent = ColorFrom("#4A55C0"),
            NarratorAccent = ColorFrom("#A93E78"),
            OperatorAccent = ColorFrom("#2C69AC"),
        },
        new()
        {
            Id = "high-contrast",
            Name = "High Contrast",
            AppBackground = ColorFrom("#050706"),
            TopBar = ColorFrom("#0B100E"),
            Panel = ColorFrom("#0E1512"),
            Card = ColorFrom("#090F0D"),
            Input = ColorFrom("#040806"),
            Border = ColorFrom("#8DB7A8"),
            Text = ColorFrom("#F5FFF9"),
            MutedText = ColorFrom("#D0E4DA"),
            Primary = ColorFrom("#007D64"),
            PrimaryBorder = ColorFrom("#61FFD1"),
            Assist = ColorFrom("#5B2146"),
            AssistBorder = ColorFrom("#FF8FD4"),
            Danger = ColorFrom("#4E1717"),
            DangerBorder = ColorFrom("#FF7474"),
            DangerText = ColorFrom("#FFB1B1"),
            Disabled = ColorFrom("#080B0A"),
            DisabledBorder = ColorFrom("#3C514A"),
            DisabledText = ColorFrom("#A3B6AD"),
            HoverBorder = ColorFrom("#96FFE0"),
            PressedPrimary = ColorFrom("#005744"),
            Overlay = ColorFrom("#CC050706"),
            AlphaAccent = ColorFrom("#78E7FF"),
            BetaAccent = ColorFrom("#FFD978"),
            GammaAccent = ColorFrom("#A7FF9B"),
            DeltaAccent = ColorFrom("#B3B8FF"),
            NarratorAccent = ColorFrom("#FF8FD4"),
            OperatorAccent = ColorFrom("#B9D2FF"),
        }
    ];

    public static ThemePalette Resolve(string? id)
    {
        var normalizedId = NormalizeId(id);
        if (string.Equals(normalizedId, "system", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSystem(SystemThemePreferences.HighContrast, SystemThemePreferences.AppsUseLightTheme);
        }

        return BuiltIn.FirstOrDefault(item => string.Equals(item.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ?? BuiltIn.First(item => item.Id == "dark-arena");
    }

    /// <summary>
    /// Every id a caller may legitimately ask for. "system" is added only when
    /// the built-in list does not already carry it, since it does today and
    /// listing it twice makes the error message look broken.
    /// </summary>
    public static IReadOnlyList<string> KnownIds { get; } =
        BuiltIn.Select(theme => theme.Id)
            .Concat(["system"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True when the id names a real theme.
    ///
    /// NormalizeId cannot answer this. It is deliberately total - it has to
    /// return something usable when reading a settings file written by an older
    /// build - so it silently substitutes a default for anything it does not
    /// recognise, which makes a typo indistinguishable from a choice.
    /// </summary>
    public static bool IsKnownId(string? id)
    {
        var cleaned = (id ?? "").Trim();
        return cleaned.Length > 0
            && KnownIds.Any(known => string.Equals(known, cleaned, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "dark-blue";
        }

        if (string.Equals(id.Trim(), "system", StringComparison.OrdinalIgnoreCase))
        {
            return "system";
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

    internal static ThemePalette ResolveSystem(bool highContrast, bool appsUseLightTheme = false)
    {
        var sourceId = highContrast
            ? "high-contrast"
            : appsUseLightTheme ? "light" : "system";
        return BuiltIn.First(item => item.Id == sourceId) with { Id = "system", Name = "System" };
    }

    internal static double ContrastRatio(Color first, Color second)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color)
        {
            return (0.2126 * Channel(color.R))
                + (0.7152 * Channel(color.G))
                + (0.0722 * Channel(color.B));
        }

        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
