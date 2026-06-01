using System.IO;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

namespace AIArena.Wpf.Services;

public sealed class ScenarioTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string TemplatePath { get; }

    public ScenarioTemplateStore()
    {
        var dataRoot = NativeDataPaths.DefaultDataRoot();
        TemplatePath = NativeDataPaths.TemplatePath(dataRoot, "scenario-templates.json");
    }

    public IReadOnlyList<ScenarioTemplate> Load()
    {
        if (!File.Exists(TemplatePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(TemplatePath);
            return JsonSerializer.Deserialize<List<ScenarioTemplate>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public ScenarioTemplate Save(string name, ArenaSnapshot snapshot)
    {
        var templates = Load().ToList();
        var trimmedName = string.IsNullOrWhiteSpace(name) ? $"Template {DateTime.Now:yyyy-MM-dd HHmm}" : name.Trim();
        var template = FromSnapshot(trimmedName, snapshot);
        var existingIndex = templates.FindIndex(item => item.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            templates[existingIndex] = template;
        }
        else
        {
            templates.Add(template);
        }

        SaveAll(templates);
        return template;
    }

    public bool Delete(string id)
    {
        var templates = Load().ToList();
        var removed = templates.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            SaveAll(templates);
        }

        return removed;
    }

    public static void Apply(ScenarioTemplate template, ArenaSnapshot snapshot)
    {
        snapshot.MatchType = template.MatchType;
        snapshot.Engine.Steering.Topic = template.Topic;
        snapshot.Engine.Steering.Global = template.Global;
        if (!string.IsNullOrWhiteSpace(template.ScenarioGeneratorStyle)
            || !string.IsNullOrWhiteSpace(template.ScenarioGeneratorSeed)
            || !string.IsNullOrWhiteSpace(template.PersonaGeneratorStyle)
            || !string.IsNullOrWhiteSpace(template.PersonaGeneratorSeed))
        {
            snapshot.ScenarioGenerator.Style = template.ScenarioGeneratorStyle;
            snapshot.ScenarioGenerator.Seed = template.ScenarioGeneratorSeed;
            snapshot.ScenarioGenerator.Intensity = template.ScenarioGeneratorIntensity;
            snapshot.ScenarioGenerator.RolePack = template.ScenarioGeneratorRolePack;
            snapshot.ScenarioGenerator.Absurdity = template.ScenarioGeneratorAbsurdity;
            snapshot.PersonaRandomizer.Style = template.PersonaGeneratorStyle;
            snapshot.PersonaRandomizer.Seed = template.PersonaGeneratorSeed;
            snapshot.PersonaRandomizer.RolePack = template.ScenarioGeneratorRolePack;
            snapshot.PersonaRandomizer.Absurdity = template.ScenarioGeneratorAbsurdity;
        }
        snapshot.MatchLocks["topic"] = template.TopicLocked;
        snapshot.MatchLocks["global"] = template.GlobalLocked;

        foreach (var agentTemplate in template.Agents)
        {
            if (agentTemplate.Id.Equals("narrator", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.Engine.Narrator.Persona = agentTemplate.Persona;
                snapshot.Engine.Narrator.VoiceStyle = agentTemplate.VoiceStyle ?? "";
                snapshot.MatchLocks["narrator"] = agentTemplate.Locked;
                continue;
            }

            var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(agentTemplate.Id, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                agent = new DialogueAgent { Id = agentTemplate.Id };
                snapshot.Engine.Agents.Add(agent);
            }

            agent.Name = agentTemplate.Name;
            agent.Persona = agentTemplate.Persona;
            agent.VoiceStyle = agentTemplate.VoiceStyle ?? "";
            agent.Active = agentTemplate.Active;
            snapshot.MatchLocks[agentTemplate.Id] = agentTemplate.Locked;
        }

        foreach (var roleKey in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
        {
            if (!template.ModelConfigs.ContainsKey(roleKey))
            {
                snapshot.Configs.Remove(roleKey);
            }
        }

        foreach (var (key, config) in template.ModelConfigs)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(config.Model))
            {
                continue;
            }

            snapshot.Configs[key] = new ModelProviderConfig
            {
                BaseUrl = config.BaseUrl,
                Model = config.Model,
                Timeout = config.Timeout,
                Temperature = config.Temperature,
                MaxOutputTokens = config.MaxOutputTokens
            };
        }
    }

    private static ScenarioTemplate FromSnapshot(string name, ArenaSnapshot snapshot)
    {
        var agents = snapshot.Engine.Agents
            .Select(agent => new ScenarioTemplateAgent(
                agent.Id,
                agent.Name,
                agent.Persona,
                agent.Active,
                snapshot.MatchLocks.TryGetValue(agent.Id, out var locked) && locked,
                agent.VoiceStyle))
            .Append(new ScenarioTemplateAgent(
                "narrator",
                "Narrator",
                snapshot.Engine.Narrator.Persona,
                true,
                snapshot.MatchLocks.TryGetValue("narrator", out var narratorLocked) && narratorLocked,
                snapshot.Engine.Narrator.VoiceStyle))
            .ToArray();

        var configs = snapshot.Configs.ToDictionary(
            item => item.Key,
            item => new ScenarioTemplateModelConfig(
                item.Value.BaseUrl,
                item.Value.Model,
                item.Value.Timeout,
                item.Value.Temperature,
                item.Value.MaxOutputTokens),
            StringComparer.OrdinalIgnoreCase);

        return new ScenarioTemplate(
            Guid.NewGuid().ToString("N"),
            name,
            DateTimeOffset.Now,
            snapshot.MatchType,
            snapshot.Engine.Steering.Topic,
            snapshot.Engine.Steering.Global,
            snapshot.MatchLocks.TryGetValue("topic", out var topicLocked) && topicLocked,
            snapshot.MatchLocks.TryGetValue("global", out var globalLocked) && globalLocked,
            agents.ToList(),
            configs,
            snapshot.ScenarioGenerator.Style,
            snapshot.ScenarioGenerator.Seed,
            snapshot.ScenarioGenerator.Intensity,
            snapshot.ScenarioGenerator.RolePack,
            snapshot.ScenarioGenerator.Absurdity,
            snapshot.PersonaRandomizer.Style,
            snapshot.PersonaRandomizer.Seed);
    }

    private void SaveAll(IReadOnlyList<ScenarioTemplate> templates)
    {
        var ordered = templates
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
        File.WriteAllText(TemplatePath, JsonSerializer.Serialize(ordered, JsonOptions));
    }
}

public sealed record ScenarioTemplate(
    string Id,
    string Name,
    DateTimeOffset SavedAt,
    string MatchType,
    string Topic,
    string Global,
    bool TopicLocked,
    bool GlobalLocked,
    List<ScenarioTemplateAgent> Agents,
    Dictionary<string, ScenarioTemplateModelConfig> ModelConfigs,
    string ScenarioGeneratorStyle = "",
    string ScenarioGeneratorSeed = "",
    string ScenarioGeneratorIntensity = "",
    string ScenarioGeneratorRolePack = "",
    string ScenarioGeneratorAbsurdity = "",
    string PersonaGeneratorStyle = "",
    string PersonaGeneratorSeed = "");

public sealed record ScenarioTemplateAgent(
    string Id,
    string Name,
    string Persona,
    bool Active,
    bool Locked,
    string VoiceStyle = "");

public sealed record ScenarioTemplateModelConfig(
    string BaseUrl,
    string Model,
    int Timeout,
    double Temperature,
    int MaxOutputTokens);
