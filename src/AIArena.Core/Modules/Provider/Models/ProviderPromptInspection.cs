using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Services;

namespace AIArena.Core.Providers;

public enum ProviderTokenEvidenceKind
{
    Measured,
    ProviderReported,
    Estimated,
    Unavailable
}

public sealed record ProviderTokenEvidence(
    ProviderTokenEvidenceKind Kind,
    int? Value,
    string Explanation)
{
    public static ProviderTokenEvidence Unavailable(string explanation) =>
        new(ProviderTokenEvidenceKind.Unavailable, null, explanation);
}

public sealed record ProviderPromptRoleTrace(
    int Index,
    string Role,
    string RedactedContent,
    string Transformation);

public sealed record ProviderContextExplanation(
    string Subject,
    string EvidenceState,
    string Explanation);

/// <summary>
/// Non-persisted context supplied by prompt construction. It contains only
/// bounded counts and fixed explanations, never prompt or response content.
/// </summary>
public sealed record ProviderRequestInspectionContext(
    string CorrelationId,
    string Phase,
    IReadOnlyList<ProviderContextExplanation> Explanations);

/// <summary>
/// A privacy-safe view of one physical provider request. PayloadSha256 is
/// calculated over the exact UTF-8 bytes placed on HttpContent. The payload
/// shown to inspection surfaces is independently redacted and may be bounded.
/// </summary>
public sealed record ProviderRequestTrace(
    string RequestId,
    DateTimeOffset ObservedAtUtc,
    string CorrelationId,
    string Phase,
    string ApiMode,
    string Transport,
    string Model,
    bool RequestedStreaming,
    bool PayloadStreaming,
    int Attempt,
    string PayloadSha256,
    int PayloadByteCount,
    string RedactedPayload,
    bool RedactedPayloadTruncated,
    IReadOnlyList<ProviderPromptRoleTrace> Roles,
    IReadOnlyList<ProviderContextExplanation> Context,
    ProviderTokenEvidence PromptTokens,
    ProviderTokenEvidence CompletionTokens,
    ProviderTokenEvidence TotalTokens,
    string Outcome);

public sealed record ProviderRequestCompletionObservation(
    string RequestId,
    string Outcome,
    ProviderTokenEvidence PromptTokens,
    ProviderTokenEvidence CompletionTokens,
    ProviderTokenEvidence TotalTokens);

public interface IProviderRequestObserver
{
    void ObserveRequest(ProviderRequestTrace trace);

    void ObserveCompletion(ProviderRequestCompletionObservation completion);
}

internal enum ProviderRequestObservationDetail
{
    MetadataOnly,
    RedactedPreview
}

/// <summary>
/// Internal fast path for observers that can explicitly declare whether a
/// redacted content preview currently has a consumer. Prepared traces have
/// already crossed the provider-boundary privacy scrubber and never contain a
/// deferred reference to the exact request body.
/// </summary>
internal interface IPreparedProviderRequestObserver : IProviderRequestObserver
{
    ProviderRequestObservationDetail ObservationDetail { get; }

    void ObservePreparedRequest(ProviderRequestTrace trace);
}

/// <summary>
/// Process-memory-only, bounded trace storage for the Context &amp; Prompt
/// Inspector. This type intentionally has no persistence API.
/// </summary>
public sealed class ProviderRequestTraceStore : IPreparedProviderRequestObserver
{
    private readonly object _sync = new();
    private readonly LinkedList<ProviderRequestTrace> _traces = [];
    private readonly int _maximumEntries;
    private int _redactedPreviewSubscribers;

    public ProviderRequestTraceStore(int maximumEntries = 64)
    {
        _maximumEntries = Math.Clamp(maximumEntries, 1, 256);
    }

    public void ObserveRequest(ProviderRequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Add(ProviderPromptInspection.BoundForStore(trace));
    }

    void IPreparedProviderRequestObserver.ObservePreparedRequest(ProviderRequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Add(ProviderPromptInspection.CopyPreparedForStore(trace));
    }

    ProviderRequestObservationDetail IPreparedProviderRequestObserver.ObservationDetail =>
        Volatile.Read(ref _redactedPreviewSubscribers) > 0
            ? ProviderRequestObservationDetail.RedactedPreview
            : ProviderRequestObservationDetail.MetadataOnly;

    /// <summary>
    /// Keeps bounded, redacted payload previews for requests observed while the
    /// returned lease is alive. Without a lease the store remains useful for
    /// exact hashes, byte counts, routing, structure, roles, and completion
    /// receipts, but retains no request content.
    /// </summary>
    public IDisposable SubscribeToRedactedPreviews()
    {
        Interlocked.Increment(ref _redactedPreviewSubscribers);
        return new RedactedPreviewSubscription(this);
    }

    internal bool IsRedactedPreviewCaptureActive => Volatile.Read(ref _redactedPreviewSubscribers) > 0;

    private void Add(ProviderRequestTrace trace)
    {
        lock (_sync)
        {
            _traces.AddLast(trace);
            while (_traces.Count > _maximumEntries)
            {
                _traces.RemoveFirst();
            }
        }
    }

    public void ObserveCompletion(ProviderRequestCompletionObservation completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var requestId = ProviderPromptInspection.SafeCorrelationId(completion.RequestId, completion.RequestId);
        lock (_sync)
        {
            var node = _traces.Last;
            while (node is not null)
            {
                if (string.Equals(node.Value.RequestId, requestId, StringComparison.Ordinal))
                {
                    node.Value = node.Value with
                    {
                        Outcome = ProviderPromptInspection.BoundOutcome(completion.Outcome),
                        PromptTokens = ProviderPromptInspection.BoundTokenEvidence(completion.PromptTokens),
                        CompletionTokens = ProviderPromptInspection.BoundTokenEvidence(completion.CompletionTokens),
                        TotalTokens = ProviderPromptInspection.BoundTokenEvidence(completion.TotalTokens)
                    };
                    return;
                }

                node = node.Previous;
            }
        }
    }

    public ImmutableArray<ProviderRequestTrace> Snapshot()
    {
        lock (_sync)
        {
            return [.. _traces.Select(trace => trace with
            {
                Roles = trace.Roles.ToImmutableArray(),
                Context = trace.Context.ToImmutableArray()
            })];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _traces.Clear();
        }
    }

    private void ReleaseRedactedPreviewSubscription()
    {
        var remaining = Interlocked.Decrement(ref _redactedPreviewSubscribers);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _redactedPreviewSubscribers, 0);
            throw new InvalidOperationException("Provider preview subscription count became unbalanced.");
        }
    }

    private sealed class RedactedPreviewSubscription(ProviderRequestTraceStore owner) : IDisposable
    {
        private ProviderRequestTraceStore? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseRedactedPreviewSubscription();
    }
}

internal static partial class ProviderPromptInspection
{
    internal const int MaximumRedactedPayloadCharacters = 32 * 1024;
    internal const int MaximumPayloadBytesForPreview = 512 * 1024;
    internal const int MaximumRoleContentCharacters = 2 * 1024;
    internal const int MaximumRoleEntries = 128;
    internal const int MaximumContextEntries = 64;
    internal const int MaximumExplanationCharacters = 1024;
    private const string ScopedMemoryRedactionMarker = "[REDACTED:SCOPED_MEMORY]";
    private const string InactivePreviewMarker = "[NOT_CAPTURED:INSPECTION_INACTIVE]";
    private const string OversizedPreviewMarker = "[OMITTED:PAYLOAD_EXCEEDS_SAFE_PREVIEW_BOUND]";

    private static readonly IReadOnlySet<string> SensitivePropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "api_token", "authorization", "base_url", "client_secret", "endpoint",
        "password", "previous_response_id", "private_key", "refresh_token", "response_id",
        "secret", "session_token", "token", "url"
    };

    internal static ProviderRequestTrace CreateTrace(
        string requestId,
        ModelProviderConfig config,
        byte[] exactPayload,
        string transport,
        bool requestedStreaming,
        int attempt,
        ProviderRequestObservationDetail observationDetail = ProviderRequestObservationDetail.RedactedPreview)
    {
        var apiMode = ModelProviderApiModes.Normalize(config.ApiMode);
        var previewRequested = observationDetail == ProviderRequestObservationDetail.RedactedPreview;
        var canPreviewContent = previewRequested && exactPayload.Length <= MaximumPayloadBytesForPreview;
        string redactedPayload;
        IReadOnlyList<ProviderPromptRoleTrace> roles;
        bool payloadStreaming;
        bool hasPreviousResponseId;
        if (canPreviewContent)
        {
            using var document = JsonDocument.Parse(exactPayload);
            var root = document.RootElement;
            redactedPayload = RedactJson(root);
            roles = ExtractRoles(root, apiMode, includeContent: true);
            payloadStreaming = PayloadRequestsStreaming(root);
            hasPreviousResponseId = root.TryGetProperty("previous_response_id", out _);
        }
        else
        {
            var metadata = InspectPayloadMetadata(
                exactPayload,
                apiMode,
                previewRequested ? OversizedPreviewMarker : InactivePreviewMarker);
            redactedPayload = previewRequested ? OversizedPreviewMarker : InactivePreviewMarker;
            roles = metadata.Roles;
            payloadStreaming = metadata.PayloadStreaming;
            hasPreviousResponseId = metadata.HasPreviousResponseId;
        }

        var truncated = !canPreviewContent || redactedPayload.Length > MaximumRedactedPayloadCharacters;
        if (redactedPayload.Length > MaximumRedactedPayloadCharacters)
        {
            redactedPayload = Bound(redactedPayload, MaximumRedactedPayloadCharacters);
        }

        var context = new List<ProviderContextExplanation>
        {
            new("payload_correspondence", "observed", "SHA-256 and byte count were measured from the exact UTF-8 body supplied to HttpClient."),
            new("provider_endpoint", "unavailable", "The provider endpoint is intentionally not retained by the prompt inspector."),
            new("authorization", "unavailable", "Authorization headers and credentials are intentionally not retained by the prompt inspector."),
            new(
                "scoped_memory_visibility",
                "observed",
                "This is a default-deny operator aggregate view: private and shared structured-memory contents are replaced with a marker. Only bounded counts and omission reasons remain; no authorized per-agent content view is exposed by this foundation.")
        };
        if (config.RequestInspectionContext is { } supplied)
        {
            context.AddRange((supplied.Explanations ?? [])
                .Take(MaximumContextEntries - 16)
                .Where(explanation => explanation is not null)
                .Select(BoundContextExplanation));
        }
        else
        {
            context.Add(new ProviderContextExplanation(
                "upstream_context_omissions",
                "unavailable",
                "This provider-boundary trace can prove the final payload, but the caller supplied no upstream truncation evidence."));
        }

        AddAdapterExplanations(
            context,
            config,
            apiMode,
            requestedStreaming,
            payloadStreaming,
            hasPreviousResponseId);
        if (!previewRequested)
        {
            context.Add(new ProviderContextExplanation(
                "payload_preview",
                "unavailable",
                $"The exact {exactPayload.Length}-byte payload was hashed, but content capture was inactive. No raw bytes, raw strings, or deferred content were retained."));
        }
        else if (!canPreviewContent)
        {
            context.Add(new ProviderContextExplanation(
                "payload_preview",
                "unavailable",
                $"The exact {exactPayload.Length}-byte payload was hashed, but its content preview was omitted because it exceeded the {MaximumPayloadBytesForPreview}-byte inspection bound."));
        }
        var unavailable = ProviderTokenEvidence.Unavailable(
            "No tokenizer ran at the outbound boundary; a provider response may later supply token counts.");
        var inspectionContext = config.RequestInspectionContext;
        return new ProviderRequestTrace(
            requestId,
            DateTimeOffset.UtcNow,
            SafeCorrelationId(inspectionContext?.CorrelationId, requestId),
            NormalizePhase(inspectionContext?.Phase),
            apiMode,
            Bound(RedactText(transport), 64),
            Bound(RedactText(config.Model), 256),
            requestedStreaming,
            payloadStreaming,
            Math.Clamp(attempt, 1, 100),
            Convert.ToHexString(SHA256.HashData(exactPayload)).ToLowerInvariant(),
            exactPayload.Length,
            redactedPayload,
            truncated,
            roles,
            context.Take(MaximumContextEntries).Select(BoundContextExplanation).ToArray(),
            unavailable,
            unavailable,
            unavailable,
            "pending");
    }

    internal static ProviderRequestTrace BoundForStore(ProviderRequestTrace trace)
    {
        var originalPayload = trace.RedactedPayload ?? "";
        var safePayload = originalPayload.Length > MaximumRedactedPayloadCharacters * 2
            ? "[OMITTED:UNBOUNDED_OBSERVER_PAYLOAD]"
            : Bound(RedactText(originalPayload), MaximumRedactedPayloadCharacters);
        var roles = (trace.Roles ?? [])
            .Take(MaximumRoleEntries)
            .Where(role => role is not null)
            .Select((role, index) => new ProviderPromptRoleTrace(
                index,
                Bound(RedactText(role.Role), 64),
                BoundRoleContent(RedactText(role.RedactedContent)),
                Bound(RedactText(role.Transformation), MaximumExplanationCharacters)))
            .ToImmutableArray();
        var context = (trace.Context ?? [])
            .Take(MaximumContextEntries)
            .Where(explanation => explanation is not null)
            .Select(BoundContextExplanation)
            .ToImmutableArray();
        var hash = trace.PayloadSha256 is { Length: 64 } && trace.PayloadSha256.All(Uri.IsHexDigit)
            ? trace.PayloadSha256.ToLowerInvariant()
            : "unavailable";
        return trace with
        {
            RequestId = SafeCorrelationId(trace.RequestId, Guid.NewGuid().ToString("N")),
            CorrelationId = SafeCorrelationId(trace.CorrelationId, trace.RequestId),
            Phase = NormalizePhase(trace.Phase),
            ApiMode = Bound(RedactText(trace.ApiMode), 64),
            Transport = Bound(RedactText(trace.Transport), 64),
            Model = Bound(RedactText(trace.Model), 256),
            Attempt = Math.Clamp(trace.Attempt, 1, 100),
            PayloadSha256 = hash,
            PayloadByteCount = Math.Max(0, trace.PayloadByteCount),
            RedactedPayload = safePayload,
            RedactedPayloadTruncated = trace.RedactedPayloadTruncated || originalPayload.Length > MaximumRedactedPayloadCharacters,
            Roles = roles,
            Context = context,
            PromptTokens = BoundTokenEvidence(trace.PromptTokens),
            CompletionTokens = BoundTokenEvidence(trace.CompletionTokens),
            TotalTokens = BoundTokenEvidence(trace.TotalTokens),
            Outcome = BoundOutcome(trace.Outcome)
        };
    }

    /// <summary>
    /// Copies a trace produced by CreateTrace into immutable bounded
    /// collections without running its already-redacted preview through the
    /// sanitizer a second time. This is reachable only through the internal
    /// prepared-observer contract; arbitrary public observer input continues to
    /// use BoundForStore's defensive scrub.
    /// </summary>
    internal static ProviderRequestTrace CopyPreparedForStore(ProviderRequestTrace trace) => trace with
    {
        Roles = (trace.Roles ?? []).Take(MaximumRoleEntries).ToImmutableArray(),
        Context = (trace.Context ?? []).Take(MaximumContextEntries).ToImmutableArray()
    };

    internal static string RedactText(string? value)
    {
        var text = value ?? "";
        text = RedactScopedMemorySections(text);
        text = PrivateMemoryLineRegex().Replace(text, match => $"{match.Groups[1].Value}[REDACTED:PRIVATE_MEMORY]");
        text = NamedSecretRegex().Replace(text, match => $"{match.Groups[1].Value}[REDACTED:SECRET]");
        text = KnownCredentialRegex().Replace(text, "[REDACTED:SECRET]");
        text = CredentialCandidateRegex().Replace(text, match => LooksLikeCredential(match.Value)
            ? "[REDACTED:SECRET]"
            : match.Value);
        text = SensitiveUrlParameterRegex().Replace(text, match => $"{match.Groups[1].Value}[REDACTED:SECRET]");
        text = EmailRegex().Replace(text, "[REDACTED:EMAIL]");
        text = AbsoluteUrlRegex().Replace(text, "[REDACTED:URL]");
        text = AbsolutePathRegex().Replace(text, "[REDACTED:PATH]");
        return text;
    }

    private static string RedactScopedMemorySections(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var redacted = new StringBuilder(text.Length);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var heading = text.IndexOf(
                StructuredMemoryService.PromptSectionHeading,
                cursor,
                StringComparison.OrdinalIgnoreCase);
            if (heading < 0)
            {
                redacted.Append(text, cursor, text.Length - cursor);
                break;
            }

            redacted.Append(text, cursor, heading - cursor);
            redacted.Append(
                text,
                heading,
                StructuredMemoryService.PromptSectionHeading.Length);
            var afterHeading = heading + StructuredMemoryService.PromptSectionHeading.Length;

            // Redaction is deliberately idempotent because a trace is scrubbed
            // once at the outbound boundary and again when admitted to the
            // bounded store. JSON previews spell newlines as `\n`, so the
            // immediate-token check accepts both physical and escaped spacing.
            if (TryFindImmediateSectionToken(
                text,
                afterHeading,
                ScopedMemoryRedactionMarker,
                out var existingMarker))
            {
                redacted.Append(text, afterHeading, existingMarker - afterHeading);
                redacted.Append(ScopedMemoryRedactionMarker);
                cursor = existingMarker + ScopedMemoryRedactionMarker.Length;
                continue;
            }

            if (!TryFindImmediateSectionToken(
                text,
                afterHeading,
                StructuredMemoryService.PromptSectionBegin,
                out var sectionBegin))
            {
                // A heading without the structural envelope is ambiguous. The
                // only safe aggregate behavior is to deny the remainder rather
                // than guess that an attacker-supplied Transcript: line is the
                // public boundary.
                redacted.Append(ScopedMemoryRedactionMarker);
                break;
            }

            var sectionEnd = text.IndexOf(
                StructuredMemoryService.PromptSectionEnd,
                sectionBegin + StructuredMemoryService.PromptSectionBegin.Length,
                StringComparison.Ordinal);
            if (sectionEnd < 0)
            {
                redacted.Append(text, afterHeading, sectionBegin - afterHeading);
                redacted.Append(ScopedMemoryRedactionMarker);
                break;
            }

            redacted.Append(text, afterHeading, sectionBegin - afterHeading);
            redacted.Append(ScopedMemoryRedactionMarker);
            cursor = sectionEnd + StructuredMemoryService.PromptSectionEnd.Length;
        }

        return redacted.ToString();
    }

    private static bool TryFindImmediateSectionToken(
        string text,
        int start,
        string token,
        out int tokenIndex)
    {
        const int maximumSeparatorCharacters = 32;
        tokenIndex = text.IndexOf(token, start, StringComparison.Ordinal);
        if (tokenIndex < start || tokenIndex - start > maximumSeparatorCharacters)
        {
            return false;
        }

        for (var index = start; index < tokenIndex; index++)
        {
            var character = text[index];
            if (!char.IsWhiteSpace(character)
                && character is not '\\' and not 'r' and not 'n')
            {
                return false;
            }
        }

        return true;
    }

    private static string RedactJson(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRedacted(writer, root, propertyName: null);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        if (propertyName is not null && SensitivePropertyNames.Contains(propertyName))
        {
            writer.WriteStringValue("[REDACTED:PRIVATE_FIELD]");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item, propertyName: null);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString()));
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static IReadOnlyList<ProviderPromptRoleTrace> ExtractRoles(
        JsonElement root,
        string apiMode,
        bool includeContent)
    {
        var roles = new List<ProviderPromptRoleTrace>();
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (index >= MaximumRoleEntries)
                {
                    break;
                }

                if (message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = message.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
                    ? roleValue.GetString() ?? "unavailable"
                    : "unavailable";
                var content = includeContent
                    && message.TryGetProperty("content", out var contentValue)
                    && contentValue.ValueKind == JsonValueKind.String
                    ? contentValue.GetString() ?? ""
                    : "[OMITTED:PAYLOAD_EXCEEDS_SAFE_PREVIEW_BOUND]";
                roles.Add(new ProviderPromptRoleTrace(
                    index++,
                    Bound(RedactText(role), 64),
                    BoundRoleContent(RedactText(content)),
                    apiMode == ModelProviderApiModes.OllamaNative
                        ? "Role was normalized by the Ollama adapter before serialization."
                        : "Role and content correspond to the serialized messages array."));
            }

            return roles;
        }

        var nativeIndex = 0;
        if (root.TryGetProperty("system_prompt", out var systemPrompt) && systemPrompt.ValueKind == JsonValueKind.String)
        {
            roles.Add(new ProviderPromptRoleTrace(
                nativeIndex++,
                "system",
                includeContent
                    ? BoundRoleContent(RedactText(systemPrompt.GetString()))
                    : "[OMITTED:PAYLOAD_EXCEEDS_SAFE_PREVIEW_BOUND]",
                "System messages were trimmed and consolidated into system_prompt by the LM Studio native adapter."));
        }

        if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String)
        {
            roles.Add(new ProviderPromptRoleTrace(
                nativeIndex,
                "input",
                includeContent
                    ? BoundRoleContent(RedactText(input.GetString()))
                    : "[OMITTED:PAYLOAD_EXCEEDS_SAFE_PREVIEW_BOUND]",
                "LM Studio native serialized the non-system conversation into one input string. Any assistant or public-speaker attribution is encoded in that string rather than represented as a structured native role."));
        }

        return roles;
    }

    private static PayloadMetadata InspectPayloadMetadata(
        ReadOnlySpan<byte> exactPayload,
        string apiMode,
        string contentMarker)
    {
        var roles = new List<ProviderPromptRoleTrace>();
        var reader = new Utf8JsonReader(exactPayload, isFinalBlock: true, state: default);
        string? rootProperty = null;
        string? messageProperty = null;
        var messagesDepth = -1;
        var messageObjectDepth = -1;
        var messageRole = "unavailable";
        var payloadStreaming = false;
        var hasPreviousResponseId = false;
        var hasMessagesArray = false;
        var hasSystemPrompt = false;
        var hasInput = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (messageObjectDepth >= 0 && reader.CurrentDepth == messageObjectDepth + 1)
                {
                    messageProperty = reader.GetString();
                }
                else if (reader.CurrentDepth == 1)
                {
                    rootProperty = reader.GetString();
                }

                continue;
            }

            if (messageObjectDepth >= 0 && messageProperty is not null)
            {
                if (messageProperty.Equals("role", StringComparison.Ordinal)
                    && reader.TokenType == JsonTokenType.String)
                {
                    messageRole = reader.GetString() ?? "unavailable";
                }

                messageProperty = null;
            }

            if (rootProperty is not null)
            {
                if (rootProperty.Equals("stream", StringComparison.Ordinal)
                    && reader.TokenType == JsonTokenType.True)
                {
                    payloadStreaming = true;
                }
                else if (rootProperty.Equals("previous_response_id", StringComparison.Ordinal))
                {
                    hasPreviousResponseId = true;
                }
                else if (rootProperty.Equals("messages", StringComparison.Ordinal)
                    && reader.TokenType == JsonTokenType.StartArray)
                {
                    hasMessagesArray = true;
                    messagesDepth = reader.CurrentDepth;
                }
                else if (rootProperty.Equals("system_prompt", StringComparison.Ordinal)
                    && reader.TokenType == JsonTokenType.String)
                {
                    hasSystemPrompt = true;
                }
                else if (rootProperty.Equals("input", StringComparison.Ordinal)
                    && reader.TokenType == JsonTokenType.String)
                {
                    hasInput = true;
                }

                rootProperty = null;
            }

            if (messagesDepth >= 0
                && reader.TokenType == JsonTokenType.StartObject
                && reader.CurrentDepth == messagesDepth + 1)
            {
                messageObjectDepth = reader.CurrentDepth;
                messageRole = "unavailable";
                messageProperty = null;
                continue;
            }

            if (messageObjectDepth >= 0
                && reader.TokenType == JsonTokenType.EndObject
                && reader.CurrentDepth == messageObjectDepth)
            {
                if (roles.Count < MaximumRoleEntries)
                {
                    roles.Add(new ProviderPromptRoleTrace(
                        roles.Count,
                        Bound(RedactText(messageRole), 64),
                        contentMarker,
                        apiMode == ModelProviderApiModes.OllamaNative
                            ? "Role was normalized by the Ollama adapter before serialization. Content preview was not retained."
                            : "Role corresponds to the serialized messages array. Content preview was not retained."));
                }

                messageObjectDepth = -1;
                messageProperty = null;
                continue;
            }

            if (messagesDepth >= 0
                && reader.TokenType == JsonTokenType.EndArray
                && reader.CurrentDepth == messagesDepth)
            {
                messagesDepth = -1;
            }
        }

        if (!hasMessagesArray && hasSystemPrompt)
        {
            roles.Add(new ProviderPromptRoleTrace(
                roles.Count,
                "system",
                contentMarker,
                "System messages were trimmed and consolidated into system_prompt by the LM Studio native adapter. Content preview was not retained."));
        }

        if (!hasMessagesArray && roles.Count < MaximumRoleEntries && hasInput)
        {
            roles.Add(new ProviderPromptRoleTrace(
                roles.Count,
                "input",
                contentMarker,
                "LM Studio native serialized the non-system conversation into one input string. Content preview was not retained."));
        }

        return new PayloadMetadata(payloadStreaming, hasPreviousResponseId, roles.ToImmutableArray());
    }

    private static void AddAdapterExplanations(
        ICollection<ProviderContextExplanation> context,
        ModelProviderConfig config,
        string apiMode,
        bool requestedStreaming,
        bool payloadStreaming,
        bool hasPreviousResponseId)
    {
        if (apiMode == ModelProviderApiModes.LmStudioNative)
        {
            context.Add(new ProviderContextExplanation(
                "role_transformation",
                "observed",
                "LM Studio native transport consolidates system messages into system_prompt and serializes non-system conversation entries into one input string; blank entries are omitted and role or public-speaker attribution is encoded in that string."));
            context.Add(hasPreviousResponseId
                ? new ProviderContextExplanation("native_continuation", "observed", "A previous response identifier was sent but its value is intentionally redacted.")
                : new ProviderContextExplanation("native_continuation", "observed", "No valid previous response identifier was sent; the payload contains the supplied transcript context."));
        }
        else if (apiMode == ModelProviderApiModes.OllamaNative)
        {
            context.Add(new ProviderContextExplanation(
                "role_transformation",
                "observed",
                "Ollama accepts system, user, assistant, and tool roles; any other supplied role is normalized to user."));
            if (requestedStreaming && !payloadStreaming)
            {
                context.Add(new ProviderContextExplanation(
                    "streaming",
                    "observed",
                    "The streaming client path was requested, but the Ollama adapter sent a non-streaming payload."));
            }
        }
        else
        {
            if (config.ContextLength > 0)
            {
                context.Add(new ProviderContextExplanation(
                    "context_length",
                    "observed",
                    "The OpenAI-compatible adapter does not serialize the configured context length; server context remains provider-controlled."));
            }

            if (!string.IsNullOrWhiteSpace(ModelProviderReasoningModes.Normalize(config.Reasoning)))
            {
                context.Add(new ProviderContextExplanation(
                    "reasoning",
                    "observed",
                    "The OpenAI-compatible adapter does not serialize AI Arena's reasoning mode because compatible providers do not share one portable field."));
            }
        }
    }

    private static bool PayloadRequestsStreaming(JsonElement root) =>
        root.TryGetProperty("stream", out var stream)
        && stream.ValueKind is JsonValueKind.True;

    private sealed record PayloadMetadata(
        bool PayloadStreaming,
        bool HasPreviousResponseId,
        IReadOnlyList<ProviderPromptRoleTrace> Roles);

    private static string BoundRoleContent(string value) => Bound(value, MaximumRoleContentCharacters);

    private static ProviderContextExplanation BoundContextExplanation(ProviderContextExplanation explanation) => new(
        Bound(RedactText(explanation.Subject), 64),
        NormalizeEvidenceState(explanation.EvidenceState),
        Bound(RedactText(explanation.Explanation), MaximumExplanationCharacters));

    internal static ProviderTokenEvidence BoundTokenEvidence(ProviderTokenEvidence? evidence)
    {
        if (evidence is null)
        {
            return ProviderTokenEvidence.Unavailable("Token evidence was not supplied.");
        }

        var value = evidence.Value is >= 0 ? evidence.Value : null;
        var kind = value is null ? ProviderTokenEvidenceKind.Unavailable : evidence.Kind;
        return new ProviderTokenEvidence(
            kind,
            value,
            Bound(RedactText(evidence.Explanation), MaximumExplanationCharacters));
    }

    private static string NormalizeEvidenceState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "observed" => "observed",
        "inferred" => "inferred",
        _ => "unavailable"
    };

    private static string NormalizePhase(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "primary" => "primary",
        "fallback" => "fallback",
        "repair" => "repair",
        "retry" => "retry",
        _ => "direct"
    };

    internal static string SafeCorrelationId(string? value, string fallback)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return guid.ToString("N");
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            return $"correlation:{Convert.ToHexString(SHA256.HashData(valueBytes)).ToLowerInvariant()[..24]}";
        }

        if (Guid.TryParse(fallback, out guid))
        {
            return guid.ToString("N");
        }

        var bytes = Encoding.UTF8.GetBytes(fallback ?? "");
        return $"correlation:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..24]}";
    }

    private static string Bound(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..Math.Max(0, maximumCharacters - "...[TRUNCATED]".Length)] + "...[TRUNCATED]";

    internal static string BoundOutcome(string? value) => Bound(RedactText(value), 64);

    private static bool LooksLikeCredential(string value)
    {
        var token = value.Trim('=', '-', '_');
        if (token.Length < 24)
        {
            return false;
        }

        var compact = token.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (compact.Length >= 32 && compact.All(Uri.IsHexDigit))
        {
            return true;
        }

        var categories = 0;
        categories += token.Any(char.IsLower) ? 1 : 0;
        categories += token.Any(char.IsUpper) ? 1 : 0;
        categories += token.Any(char.IsDigit) ? 1 : 0;
        categories += token.Any(character => character is '+' or '/' or '_' or '-' or '=') ? 1 : 0;
        return token.Length >= 28 && categories >= 3 && token.Distinct().Count() >= 12;
    }

    [GeneratedRegex(@"(?ix)(\b(?:api[_\s-]?key|access[_\s-]?token|auth(?:orization)?|bearer|client[_\s-]?secret|password|passwd|private[_\s-]?key|refresh[_\s-]?token)\b\s*(?::|=|\s)\s*[\""']?)[A-Za-z0-9_+./~=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"(?im)^(\s*-\s*\[[^\]\r\n]*\bvisibility\s*=\s*private\b[^\]\r\n]*\]\s*).*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateMemoryLineRegex();

    [GeneratedRegex(@"(?ix)\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_]{8,}|github_pat_[A-Za-z0-9_]{8,}|xox[baprs]-[A-Za-z0-9-]{8,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownCredentialRegex();

    [GeneratedRegex(@"[A-Za-z0-9_+/=-]{24,}", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialCandidateRegex();

    [GeneratedRegex(@"(?i)([?&](?:api[_-]?key|access[_-]?token|auth|authorization|client[_-]?secret|code|credential|jwt|key|password|refresh[_-]?token|secret|session|sig|signature|token)=)[^&#\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveUrlParameterRegex();

    [GeneratedRegex(@"(?i)(?<![A-Z0-9._%+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}(?![A-Z0-9._%+-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)https?://[^\s<>\""']+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteUrlRegex();

    [GeneratedRegex(@"(?ix)(?:(?<![a-z0-9_])[a-z]:[\\/](?:[^\s\""']+[\\/]?)+|/(?:users|home|root|var|tmp|etc|opt|mnt|private|volumes|srv|usr)(?:/[^\s\""']*)?)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();
}
