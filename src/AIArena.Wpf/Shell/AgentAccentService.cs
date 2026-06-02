using System.Windows.Media;

namespace AIArena.Wpf.Services;

internal static class AgentAccentService
{
    public static IReadOnlyList<AgentAccentOption> PresetOptions { get; } =
    [
        new("Alpha cyan", "#6EC9F1"),
        new("Beta amber", "#F1C96B"),
        new("Gamma green", "#85D99C"),
        new("Delta periwinkle", "#9EAFFF"),
        new("Epsilon coral", "#FF8A6A"),
        new("Zeta teal", "#38D3C1"),
        new("Eta violet", "#B37BFF"),
        new("Theta rose", "#FF8EB3"),
        new("Narrator magenta", "#D185CE"),
        new("Operator red", "#D1747B")
    ];

    public static Brush ResolveBrush(string speaker, string? overrideColor, Func<string, Brush> resourceBrush)
    {
        if (TryParseColor(overrideColor, out var custom))
        {
            return new SolidColorBrush(custom);
        }

        return NormalizeSpeakerId(speaker) switch
        {
            "alpha" => resourceBrush("AlphaAccentBrush"),
            "beta" => resourceBrush("BetaAccentBrush"),
            "gamma" => resourceBrush("GammaAccentBrush"),
            "delta" => resourceBrush("DeltaAccentBrush"),
            "epsilon" => BrushFrom("#FF8A6A"),
            "zeta" => BrushFrom("#38D3C1"),
            "eta" => BrushFrom("#B37BFF"),
            "theta" => BrushFrom("#FF8EB3"),
            "narrator" => resourceBrush("NarratorAccentBrush"),
            "operator" => resourceBrush("OperatorAccentBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    public static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var cleaned = value.Trim();
        if (!cleaned.StartsWith('#'))
        {
            cleaned = $"#{cleaned}";
        }

        if (cleaned.Length != 7 || !cleaned.Skip(1).All(Uri.IsHexDigit))
        {
            return "";
        }

        return cleaned.ToUpperInvariant();
    }

    public static bool TryParseColor(string? value, out Color color)
    {
        var normalized = NormalizeColor(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            color = default;
            return false;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(normalized)!;
            return true;
        }
        catch (FormatException)
        {
            color = default;
            return false;
        }
    }

    public static string NormalizeSpeakerId(string speaker)
    {
        return string.IsNullOrWhiteSpace(speaker) ? "" : speaker.Trim().ToLowerInvariant();
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    }
}

internal sealed record AgentAccentOption(string Label, string Hex);
