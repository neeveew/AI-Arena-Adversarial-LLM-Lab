using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class CollaborateHistoryStore
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

    public IReadOnlyList<CollaborateHistoryConversation> Load()
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
        catch (Exception ex)
        {
            LastLoadWarning = JsonFileRecovery.BackupCorruptFile(HistoryPath, "Collaborate history", ex);
            return [];
        }
    }

    public void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
    {
        LastLoadWarning = "";
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var file = new CollaborateHistoryFile
        {
            Conversations = Normalize(conversations).ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var tempPath = $"{HistoryPath}.tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(HistoryPath))
        {
            File.SetAttributes(HistoryPath, File.GetAttributes(HistoryPath) & ~FileAttributes.ReadOnly);
        }

        File.Move(tempPath, HistoryPath, overwrite: true);
    }

    private static IReadOnlyList<CollaborateHistoryConversation> Normalize(IReadOnlyList<CollaborateHistoryConversation>? conversations)
    {
        if (conversations is null)
        {
            return [];
        }

        return conversations
            .Where(item => item.Exchanges.Count > 0)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(MaxConversations)
            .Select(item =>
            {
                item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
                item.Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled chat" : item.Title.Trim();
                item.CreatedAt = item.CreatedAt == default ? DateTimeOffset.Now : item.CreatedAt;
                item.UpdatedAt = item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt;
                item.Exchanges = item.Exchanges
                    .Where(exchange => !string.IsNullOrWhiteSpace(exchange.Prompt) || !string.IsNullOrWhiteSpace(exchange.Answer))
                    .ToList();
                return item;
            })
            .Where(item => item.Exchanges.Count > 0)
            .ToList();
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
