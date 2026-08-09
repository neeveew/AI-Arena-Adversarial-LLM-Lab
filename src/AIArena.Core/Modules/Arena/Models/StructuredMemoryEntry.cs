using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

public static class StructuredMemoryOrigins
{
    public const string Manual = "manual";
    public const string Turn = "turn";
    public const string Import = "import";
    public const string LegacyUnknown = "legacy_unknown";
    public const string System = "system";

    public static bool IsKnown(string? value) => value is Manual or Turn or Import or LegacyUnknown or System;
}

public static class StructuredMemoryVisibilities
{
    public const string Private = "private";
    public const string Shared = "shared";
    public const string System = "system";

    public static bool IsKnown(string? value) => value is Private or Shared or System;
}

/// <summary>
/// Session-native memory with enough provenance to select it safely and project
/// it at an historical transcript cursor. Text remains in the private session
/// snapshot; branch receipts contain identities and fingerprints only.
/// </summary>
public sealed class StructuredMemoryEntry
{
    [JsonPropertyName("memory_id")]
    public string MemoryId { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("origin")]
    public string Origin { get; set; } = StructuredMemoryOrigins.LegacyUnknown;

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = StructuredMemoryVisibilities.Private;

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; set; }

    [JsonPropertyName("revised_at")]
    public double RevisedAt { get; set; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExpiresAt { get; set; }

    [JsonPropertyName("source_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SourceMessageId { get; set; } = "";

    [JsonPropertyName("source_turn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceTurn { get; set; }

    [JsonPropertyName("source_provenance_ambiguous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SourceProvenanceAmbiguous { get; set; }

    [JsonPropertyName("branch_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string BranchId { get; set; } = "";

    [JsonPropertyName("supersedes_memory_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SupersedesMemoryId { get; set; } = "";

    [JsonPropertyName("correction_of_memory_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string CorrectionOfMemoryId { get; set; } = "";

    [JsonPropertyName("is_correction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCorrection { get; set; }
}

/// <summary>Stable identity helpers for new and legacy transcript messages.</summary>
public static class DialogueMessageIdentity
{
    public static string Resolve(DialogueMessage message, int occurrence = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        return string.IsNullOrWhiteSpace(message.MessageId)
            ? LegacyFallback(message, occurrence)
            : message.MessageId.Trim();
    }

    public static string LegacyFallback(DialogueMessage message, int occurrence = 0)
    {
        ArgumentNullException.ThrowIfNull(message);
        var canonical = string.Join(
            "\n",
            message.Turn.ToString(CultureInfo.InvariantCulture),
            (message.SpeakerId ?? "").Trim().ToLowerInvariant(),
            (message.Speaker ?? "").Trim(),
            (message.Kind ?? "").Trim().ToLowerInvariant(),
            message.CreatedAt.ToString("R", CultureInfo.InvariantCulture),
            (message.Status ?? "").Trim().ToLowerInvariant(),
            message.Text ?? "",
            Math.Max(0, occurrence).ToString(CultureInfo.InvariantCulture));
        return $"message:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24]}";
    }

    public static string Fingerprint(DialogueMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var canonical = string.Join(
            "\n",
            Resolve(message),
            message.Turn.ToString(CultureInfo.InvariantCulture),
            (message.SpeakerId ?? "").Trim().ToLowerInvariant(),
            (message.Kind ?? "").Trim().ToLowerInvariant(),
            message.CreatedAt.ToString("R", CultureInfo.InvariantCulture),
            message.Text ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
