using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal class CollaborateHistoryStore
{
    private const int MaxConversations = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public CollaborateHistoryStore()
        : this(NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "collaborate-history.json"))
    {
    }

    public CollaborateHistoryStore(string historyPath)
    {
        HistoryPath = string.IsNullOrWhiteSpace(historyPath)
            ? NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "collaborate-history.json")
            : historyPath;
    }

    public string HistoryPath { get; }

    public string LastLoadWarning { get; private set; } = "";

    public virtual IReadOnlyList<CollaborateHistoryConversation> Load()
    {
        LastLoadWarning = "";
        if (!File.Exists(HistoryPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(HistoryPath);
            var file = JsonSerializer.Deserialize<CollaborateHistoryFile>(json, JsonOptions) ?? new CollaborateHistoryFile();
            return Normalize(file.Conversations);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            LastLoadWarning = JsonFileRecovery.BackupCorruptFile(HistoryPath, "Collaborate history", ex);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadWarning = $"Collaborate history could not be read and was left unchanged: {ex.Message}";
            return [];
        }
    }

    public virtual void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
    {
        LastLoadWarning = "";
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var file = new CollaborateHistoryFile
        {
            Conversations = Normalize(conversations).ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        JsonFileRecovery.WriteTextReplacing(HistoryPath, json);
    }

    private static IReadOnlyList<CollaborateHistoryConversation> Normalize(IReadOnlyList<CollaborateHistoryConversation>? conversations)
    {
        if (conversations is null)
        {
            return [];
        }

        var now = DateTimeOffset.Now;
        return conversations
            .OfType<CollaborateHistoryConversation>()
            .Select(item => NormalizeConversation(item, now))
            .Where(item => item.Exchanges.Count > 0)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(MaxConversations)
            .ToList();
    }

    private static CollaborateHistoryConversation NormalizeConversation(CollaborateHistoryConversation item, DateTimeOffset now)
    {
        var createdAt = item.CreatedAt == default ? now : item.CreatedAt;
        var updatedAt = item.UpdatedAt == default ? createdAt : item.UpdatedAt;
        return new CollaborateHistoryConversation
        {
            Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id,
            Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled chat" : item.Title.Trim(),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Exchanges = (item.Exchanges ?? [])
                .OfType<CollaborateHistoryExchange>()
                .Where(exchange => !string.IsNullOrWhiteSpace(exchange.Prompt) || !string.IsNullOrWhiteSpace(exchange.Answer))
                .Select(NormalizeExchange)
                .ToList(),
            MemoryNotes = CollaborateCoordinator.NormalizeMemoryNotes(item.MemoryNotes).ToList()
        };
    }

    private static CollaborateHistoryExchange NormalizeExchange(CollaborateHistoryExchange exchange)
    {
        return new CollaborateHistoryExchange
        {
            Prompt = exchange.Prompt ?? "",
            Answer = exchange.Answer ?? "",
            TraceSteps = (exchange.TraceSteps ?? [])
                .OfType<CollaborateHistoryStep>()
                .Select(NormalizeStep)
                .ToList()
        };
    }

    private static CollaborateHistoryStep NormalizeStep(CollaborateHistoryStep step)
    {
        return new CollaborateHistoryStep
        {
            RoleId = step.RoleId ?? "",
            RoleName = step.RoleName ?? "",
            Model = step.Model ?? "",
            Label = step.Label ?? "",
            Text = step.Text ?? "",
            Ok = step.Ok,
            Error = step.Error ?? "",
            LatencyMs = step.LatencyMs,
            TotalTokens = step.TotalTokens
        };
    }
}

internal sealed class CollaborateHistoryFile
{
    public int Version { get; set; } = 1;
    public List<CollaborateHistoryConversation> Conversations { get; set; } = [];
}

internal sealed class CollaborateHistoryConversation
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CollaborateHistoryExchange> Exchanges { get; set; } = [];
    public List<string> MemoryNotes { get; set; } = [];
}

internal sealed class CollaborateHistoryExchange
{
    public string Prompt { get; set; } = "";
    public string Answer { get; set; } = "";
    public List<CollaborateHistoryStep> TraceSteps { get; set; } = [];
}

internal sealed class CollaborateHistoryStep
{
    public string RoleId { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string Model { get; set; } = "";
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int LatencyMs { get; set; }
    public int TotalTokens { get; set; }
}
