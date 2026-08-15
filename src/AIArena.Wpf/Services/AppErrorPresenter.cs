using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Persistence;

namespace AIArena.Wpf;

/// <summary>
/// Stable product areas used in privacy-safe error codes. Codes follow
/// <c>AA-{CONTEXT}-{CATEGORY}</c>; enum names and segments are compatibility
/// identifiers and must not be repurposed.
/// </summary>
internal enum AppErrorContext
{
    AppFatal,
    Arena,
    Settings,
    Experiment,
    FaultLab,
    VoiceNarration,
    SearchBackend,
    Internet,
    LlamaRuntime,
    SavedState,
    QaEvidence,
    Provider,
    FileTransfer,
    ControlPlane,
    Agent,
    Collaborate
}

/// <summary>Stable failure categories used in user-visible support codes.</summary>
internal enum AppErrorCategory
{
    Cancelled,
    AccessDenied,
    NotFound,
    Io,
    InvalidData,
    Json,
    Cryptography,
    Timeout,
    Network,
    Provider,
    Unexpected
}

/// <summary>
/// Immutable, privacy-safe user presentation. <see cref="DisplayText"/> is the
/// visible status; <see cref="CopyDetails"/> is bounded support evidence. Neither
/// contains exception messages, stack traces, paths, prompts, or inner errors.
/// </summary>
internal sealed record AppErrorPresentation(
    string Code,
    string Summary,
    string Action,
    string SafeDetail,
    string DisplayText,
    string CopyDetails,
    AppErrorCategory Category,
    bool IsCancellation);

internal sealed record AppPostCommitEvidenceResult(
    bool Recorded,
    string Warning,
    AppErrorPresentation? Error)
{
    internal string AppendTo(string committedOutcome) => Recorded
        ? committedOutcome
        : $"{committedOutcome} {Warning}";
}

/// <summary>
/// Contains secondary event-log failures after an authoritative state commit.
/// Event evidence is valuable, but it must never turn a completed destructive
/// mutation into a reported failure or suppress the required UI refresh.
/// </summary>
internal static class AppPostCommitEvidence
{
    internal static async Task<AppPostCommitEvidenceResult> TryAppendAsync(
        EventLogStore eventLogStore,
        string sessionId,
        string eventType,
        object payload,
        AppErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(eventLogStore);
        try
        {
            await eventLogStore.AppendAsync(
                sessionId,
                eventType,
                payload,
                CancellationToken.None);
            return new AppPostCommitEvidenceResult(true, "", null);
        }
        catch (Exception exception)
        {
            var presentation = AppErrorPresenter.Present(exception, context);
            var warning = $"Warning: the change was committed, but activity-log evidence could not be recorded. "
                + $"{presentation.Action} Code: {presentation.Code}.";
            return new AppPostCommitEvidenceResult(false, warning, presentation);
        }
    }

    /// <summary>
    /// Completes projection work after an authoritative mutation has committed.
    /// A projection failure is a secondary warning, never a reason to describe
    /// the already-durable mutation as failed.
    /// </summary>
    internal static async Task<string> TryCompleteAsync(
        Func<Task> completion,
        string failureSummary,
        AppErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(completion);
        try
        {
            await completion();
            return "";
        }
        catch (Exception exception)
        {
            var presentation = AppErrorPresenter.Present(exception, context);
            return $"Warning: the change was committed, but {failureSummary}. "
                + $"{presentation.Action} Code: {presentation.Code}.";
        }
    }

    internal static string AppendWarning(string outcome, string warning) =>
        string.IsNullOrWhiteSpace(warning) ? outcome : $"{outcome} {warning}";
}

/// <summary>
/// Authoritative mapping and redaction boundary for WPF error presentation.
/// Raw exception text is deliberately excluded because it can contain provider
/// tokens, local paths, private memory, or an echoed user prompt.
/// </summary>
internal static partial class AppErrorPresenter
{
    internal const int MaximumRedactionInputLength = 64 * 1024;
    internal const int MaximumSafeDetailLength = 800;
    internal const int MaximumCopyDetailsLength = 1600;
    private const int RegexTimeoutMilliseconds = 100;
    private static readonly HashSet<string> StableCodes = Enum
        .GetValues<AppErrorContext>()
        .SelectMany(context => Enum.GetValues<AppErrorCategory>()
            .Select(category => $"AA-{Context(context).Code}-{CategoryCode(category)}"))
        .ToHashSet(StringComparer.Ordinal);

    internal static bool IsStableCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && StableCodes.Contains(code);

    internal static AppErrorPresentation Present(
        Exception exception,
        AppErrorContext context,
        AppErrorCategory? categoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var category = categoryOverride ?? Classify(exception, context);
        var (contextCode, subject) = Context(context);
        var categoryCode = CategoryCode(category);
        var code = $"AA-{contextCode}-{categoryCode}";
        var summary = Summary(subject, context, category);
        var action = Action(context, category);
        var safeDetail = RedactAndBound(
            $"Code: {code}. Category: {categoryCode}. Exception text was omitted to protect private data.",
            MaximumSafeDetailLength,
            $"Code: {code}.");
        var displayText = category == AppErrorCategory.Cancelled
            ? $"{summary} {action} Code: {code}."
            : $"{summary} Action: {action} Code: {code}.";
        var copyDetails = RedactAndBound(
            $"Code: {code}\nSummary: {summary}\nAction: {action}\nDetail: {safeDetail}",
            MaximumCopyDetailsLength,
            $"Code: {code}.");
        return new AppErrorPresentation(
            code,
            summary,
            action,
            safeDetail,
            displayText,
            copyDetails,
            category,
            category == AppErrorCategory.Cancelled);
    }

    internal static AppErrorCategory Classify(Exception exception, AppErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => AppErrorCategory.Cancelled,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                AppErrorCategory.AccessDenied,
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => AppErrorCategory.NotFound,
            UnauthorizedAccessException or SecurityException => AppErrorCategory.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => AppErrorCategory.NotFound,
            JsonException => AppErrorCategory.Json,
            CryptographicException => AppErrorCategory.Cryptography,
            InvalidDataException or FormatException or UriFormatException or ArgumentException =>
                AppErrorCategory.InvalidData,
            TimeoutException => AppErrorCategory.Timeout,
            HttpRequestException or SocketException => AppErrorCategory.Network,
            Win32Exception { NativeErrorCode: 2 or 3 } => AppErrorCategory.NotFound,
            Win32Exception { NativeErrorCode: 5 } => AppErrorCategory.AccessDenied,
            Win32Exception { NativeErrorCode: 121 or 1460 } => AppErrorCategory.Timeout,
            IOException => AppErrorCategory.Io,
            _ when context is AppErrorContext.Provider or AppErrorContext.LlamaRuntime => AppErrorCategory.Provider,
            _ => AppErrorCategory.Unexpected
        };
    }

    /// <summary>
    /// Redacts untrusted display text with bounded work and bounded output. This
    /// is also the Prompt Inspector's defensive UI redactor via the compatibility
    /// wrapper below, so privacy behavior cannot silently diverge.
    /// </summary>
    internal static string RedactAndBound(string? value, int maximum, string fallback)
    {
        maximum = Math.Clamp(maximum, 1, MaximumRedactionInputLength);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Bound(CleanControls(fallback), maximum, "…");
        }

        var inputWasTruncated = value.Length > MaximumRedactionInputLength;
        var text = value[..Math.Min(value.Length, MaximumRedactionInputLength)].Trim();
        try
        {
            text = PrivateMemoryLineRegex().Replace(text, "[REDACTED:PRIVATE_MEMORY]");
            text = QuotedNamedSecretRegex().Replace(text, "${prefix}\"[REDACTED:SECRET]\"");
            text = ConnectionStringValueRegex().Replace(text, "${prefix}[REDACTED:CONNECTION]");
            text = AuthorizationCredentialRegex().Replace(text, "${prefix}[REDACTED:CREDENTIAL]");
            text = AuthorizationValueRegex().Replace(text, "${prefix}[REDACTED:CREDENTIAL]");
            text = NamedSecretRegex().Replace(text, "${prefix}[REDACTED:SECRET]");
            text = CredentialRegex().Replace(text, "[REDACTED:SECRET]");
            text = UrlRegex().Replace(text, "[REDACTED:URL]");
            text = FileUriRegex().Replace(text, "[REDACTED:PATH]");
            text = QuotedAbsolutePathRegex().Replace(text, "[REDACTED:PATH]");
            text = WindowsOrUncPathRegex().Replace(text, "[REDACTED:PATH]");
            text = PosixPathRegex().Replace(text, "[REDACTED:PATH]");
            text = EmailRegex().Replace(text, "[REDACTED:EMAIL]");
        }
        catch (RegexMatchTimeoutException)
        {
            return Bound("[REDACTED:UNSAFE_DETAIL]", maximum, "…");
        }

        text = CleanControls(text).Trim();
        if (text.Length == 0)
        {
            return Bound(CleanControls(fallback), maximum, "…");
        }

        if (inputWasTruncated)
        {
            return Bound(text, maximum, "… [input truncated]");
        }

        return Bound(text, maximum, "… [display truncated]");
    }

    private static (string Code, string Subject) Context(AppErrorContext context) => context switch
    {
        AppErrorContext.AppFatal => ("APP", "AI Arena - Lite"),
        AppErrorContext.Arena => ("ARENA", "The Arena operation"),
        AppErrorContext.Settings => ("SETTINGS", "Settings"),
        AppErrorContext.Experiment => ("EXPERIMENT", "Experiment Lab"),
        AppErrorContext.FaultLab => ("FAULT", "Fault Lab"),
        AppErrorContext.VoiceNarration => ("VOICE", "Voice narration"),
        AppErrorContext.SearchBackend => ("SEARCH", "Local search"),
        AppErrorContext.Internet => ("INTERNET", "Internet diagnostics"),
        AppErrorContext.LlamaRuntime => ("LLAMA", "The llama.cpp runtime"),
        AppErrorContext.SavedState => ("SAVED", "Saved State"),
        AppErrorContext.QaEvidence => ("QA", "QA evidence"),
        AppErrorContext.Provider => ("PROVIDER", "The model provider"),
        AppErrorContext.FileTransfer => ("FILE", "The file operation"),
        AppErrorContext.ControlPlane => ("CONTROL", "The control request"),
        AppErrorContext.Agent => ("AGENT", "The Agent operation"),
        AppErrorContext.Collaborate => ("COLLAB", "The Collaborate operation"),
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, "Unknown error context.")
    };

    private static string CategoryCode(AppErrorCategory category) => category switch
    {
        AppErrorCategory.Cancelled => "CANCELLED",
        AppErrorCategory.AccessDenied => "ACCESS",
        AppErrorCategory.NotFound => "NOT-FOUND",
        AppErrorCategory.Io => "IO",
        AppErrorCategory.InvalidData => "INVALID-DATA",
        AppErrorCategory.Json => "JSON",
        AppErrorCategory.Cryptography => "CRYPTO",
        AppErrorCategory.Timeout => "TIMEOUT",
        AppErrorCategory.Network => "NETWORK",
        AppErrorCategory.Provider => "PROVIDER",
        AppErrorCategory.Unexpected => "UNEXPECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown error category.")
    };

    private static string Summary(string subject, AppErrorContext context, AppErrorCategory category)
    {
        if (context == AppErrorContext.AppFatal && category != AppErrorCategory.Cancelled)
        {
            return "AI Arena - Lite must close safely.";
        }

        return category switch
        {
            AppErrorCategory.Cancelled => $"{subject} was cancelled.",
            AppErrorCategory.AccessDenied => $"{subject} could not access a required resource.",
            AppErrorCategory.NotFound => $"{subject} could not find a required item.",
            AppErrorCategory.Io => $"{subject} could not read or write required data.",
            AppErrorCategory.InvalidData => $"{subject} received invalid or corrupted data.",
            AppErrorCategory.Json => $"{subject} received invalid JSON data.",
            AppErrorCategory.Cryptography => $"{subject} could not unlock protected data.",
            AppErrorCategory.Timeout => $"{subject} timed out.",
            AppErrorCategory.Network => $"{subject} could not reach the required service.",
            AppErrorCategory.Provider => $"{subject} could not complete the request.",
            _ => $"{subject} could not complete."
        };
    }

    private static string Action(AppErrorContext context, AppErrorCategory category)
    {
        if (context == AppErrorContext.AppFatal && category != AppErrorCategory.Cancelled)
        {
            return "Restart AI Arena - Lite. If this repeats, copy the error details and include the code in a support report.";
        }

        return category switch
        {
            AppErrorCategory.Cancelled => "No action is required; retry when ready.",
            AppErrorCategory.AccessDenied => "Check access permissions or credentials, then try again.",
            AppErrorCategory.NotFound => "Confirm the item still exists or select it again, then retry.",
            AppErrorCategory.Io => "Check storage availability and permissions, then retry.",
            AppErrorCategory.InvalidData or AppErrorCategory.Json =>
                "Use a valid file or restore a known-good copy, then retry.",
            AppErrorCategory.Cryptography =>
                "Re-enter the protected credential or use the Windows account that saved it, then retry.",
            AppErrorCategory.Timeout => "Retry and verify that the required service is responsive.",
            AppErrorCategory.Network => "Check the provider, network, firewall, and DNS connection, then retry.",
            AppErrorCategory.Provider => "Verify the provider and model settings, then retry.",
            _ => "Retry once. If it repeats, copy the error details and include the code in a support report."
        };
    }

    private static string CleanControls(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return new string(value.Select(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t'
                ? ' '
                : character).ToArray());
    }

    private static string Bound(string value, int maximum, string truncationMarker)
    {
        if (value.Length <= maximum)
        {
            return value;
        }

        if (truncationMarker.Length >= maximum)
        {
            return truncationMarker[..maximum];
        }

        return value[..(maximum - truncationMarker.Length)].TrimEnd() + truncationMarker;
    }

    [GeneratedRegex(
        """(?im)^.*(?:\bvisibility\s*=\s*private\b|\bprivate(?:\s+memory|\s+notes?)?\s*[:=]).*$""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex PrivateMemoryLineRegex();

    [GeneratedRegex(
        """(?i)(?<prefix>["']?(?:authorization|api[\s_-]?(?:key|token)|access[\s_-]?token|password|private[\s_-]?key|refresh[\s_-]?token|secret|session[\s_-]?token|token)["']?\s*[:=]\s*)["'](?!\[REDACTED:(?:SECRET|CREDENTIAL)\])[^"'\r\n]*["']""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex QuotedNamedSecretRegex();

    [GeneratedRegex(
        """(?i)(?<prefix>\b(?:server|data\s+source|initial\s+catalog|database|user\s+id|uid|password|pwd)\s*=\s*)(?!\[REDACTED:CONNECTION\])[^;\r\n]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex ConnectionStringValueRegex();

    [GeneratedRegex(
        """(?i)(?<prefix>\b(?:(?:authorization)\s*[:=]\s*)?(?:bearer|basic)\s+)(?!\[REDACTED:CREDENTIAL\])[^\s,;}\]]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex AuthorizationCredentialRegex();

    [GeneratedRegex(
        """(?i)(?<prefix>\bauthorization\s*[:=]\s*)(?!(?:bearer|basic)\b|\[REDACTED:CREDENTIAL\])[^\s,;}\]]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex AuthorizationValueRegex();

    [GeneratedRegex(
        """(?i)(?<prefix>["']?(?:api[\s_-]?(?:key|token)|access[\s_-]?token|password|private[\s_-]?key|refresh[\s_-]?token|secret|session[\s_-]?token|token)["']?\s*(?:[:=]|\s)\s*["']?)(?!\[REDACTED:SECRET\])[^"',\s;}\]]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(
        """(?i)\b(?:sk|pk|api)[-_][A-Za-z0-9_-]{8,}\b""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(
        """(?i)\bhttps?://[^\s"'<>]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        """(?i)\bfile:///?[^\s"'<>]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex FileUriRegex();

    [GeneratedRegex(
        """(?i)["'](?:[A-Z]:[\\/]|\\\\|/)[^"'\r\n]+["']""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex QuotedAbsolutePathRegex();

    [GeneratedRegex(
        """(?i)(?:(?<![A-Za-z0-9_])[A-Z]:[\\/](?:[^\s"'<>|]+[\\/])*[^\s"'<>|]*|\\\\[^\s\\/]+\\[^\s"'<>|]+(?:\\[^\s"'<>|]+)*)""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex WindowsOrUncPathRegex();

    [GeneratedRegex(
        """(?<![:/\w])/(?!/)(?=[A-Za-z0-9._~-])[^\s"'<>|,;]+""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex PosixPathRegex();

    [GeneratedRegex(
        """(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b""",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex EmailRegex();
}

/// <summary>
/// Compatibility seam for the Prompt Inspector. All behavior is owned by the
/// central presenter so inspector redaction remains unchanged or stronger.
/// </summary>
internal static class InspectionTextSafety
{
    internal static string RedactAndBound(string? value, int maximum, string fallback) =>
        AppErrorPresenter.RedactAndBound(value, maximum, fallback);
}
