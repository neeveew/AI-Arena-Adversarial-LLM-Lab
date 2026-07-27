using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class ScenarioSeedInspectorCoordinator
{
    private readonly Panel scenarioSeedInspector;
    private readonly ShellCardFactory shellCards;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, string> displayStatusValue;

    public ScenarioSeedInspectorCoordinator(
        Panel scenarioSeedInspector,
        ShellCardFactory shellCards,
        Func<string, Brush> resourceBrush,
        Func<string, string> displayStatusValue)
    {
        this.scenarioSeedInspector = scenarioSeedInspector;
        this.shellCards = shellCards;
        this.resourceBrush = resourceBrush;
        this.displayStatusValue = displayStatusValue;
    }

    public void Populate(ArenaViewSnapshot snapshot)
    {
        scenarioSeedInspector.Children.Clear();

        var scenarioSeed = displayStatusValue(snapshot.ScenarioGeneratorSeed);
        var scenarioStyle = displayStatusValue(snapshot.ScenarioGeneratorStyle);
        var scenarioIntensity = displayStatusValue(snapshot.ScenarioGeneratorIntensity);
        var scenarioRolePack = displayStatusValue(snapshot.ScenarioGeneratorRolePack);
        var scenarioAbsurdity = displayStatusValue(snapshot.ScenarioGeneratorAbsurdity);
        var personaSeed = displayStatusValue(snapshot.PersonaGeneratorSeed);
        var personaStyle = displayStatusValue(snapshot.PersonaGeneratorStyle);
        var source = ScenarioSeedSource(snapshot.ScenarioGeneratorSeed, snapshot.PersonaGeneratorStyle);

        // Seeds are reproducibility artifacts rather than setup choices, so the
        // raw values live in the source chip's tooltip and in Copy Seed instead
        // of occupying two chips of prime header space.
        AddChip("Source", source, "TextBrush", SeedToolTip(scenarioSeed, personaSeed));
        AddChip("Style", scenarioStyle, "TextBrush");
        if (scenarioIntensity != "-")
        {
            AddChip("Pressure", scenarioIntensity, "TextBrush");
        }
        if (ShouldShowRolePack(scenarioRolePack))
        {
            AddChip("Pack", scenarioRolePack.Replace('_', ' '), "TextBrush");
        }
        if (ShouldShowAbsurdity(scenarioAbsurdity))
        {
            AddChip("Absurdity", scenarioAbsurdity, "TextBrush");
        }
        if (personaStyle != "-" && !personaStyle.Equals(scenarioStyle, StringComparison.OrdinalIgnoreCase))
        {
            AddChip("Persona style", personaStyle, "TextBrush");
        }
    }

    internal static string SeedToolTip(string scenarioSeed, string personaSeed)
    {
        return $"Scenario seed: {scenarioSeed}\nPersona seed: {personaSeed}\nUse Copy Seed to reproduce this setup.";
    }

    internal static string ScenarioSeedSource(string scenarioSeed, string personaStyle)
    {
        var seed = scenarioSeed?.Trim() ?? "";
        var style = personaStyle?.Trim() ?? "";
        if (seed.StartsWith("YOLO-", StringComparison.OrdinalIgnoreCase)
            || style.Equals("yolo", StringComparison.OrdinalIgnoreCase))
        {
            return "Wild Seed";
        }

        if (seed.Equals("ai-choice", StringComparison.OrdinalIgnoreCase))
        {
            return "AI Choice";
        }

        return string.IsNullOrWhiteSpace(seed) || seed == "-"
            ? "Manual"
            : "Random";
    }

    internal static bool ShouldShowRolePack(string rolePack)
    {
        return rolePack != "-" && !rolePack.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldShowAbsurdity(string absurdity)
    {
        return absurdity != "-" && !absurdity.Equals("grounded", StringComparison.OrdinalIgnoreCase);
    }

    private void AddChip(string label, string value, string accentResourceKey, string? toolTip = null)
    {
        scenarioSeedInspector.Children.Add(shellCards.CreateSetupChip(label, value, resourceBrush(accentResourceKey), toolTip));
    }
}
