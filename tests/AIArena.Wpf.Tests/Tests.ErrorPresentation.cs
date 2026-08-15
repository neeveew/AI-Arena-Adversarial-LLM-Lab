using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void AppErrorPresenterRedactsAndClassifiesSafely()
    {
        RunStaTest(() =>
        {
        var cases = new (Exception Error, AppErrorContext Context, string Code, AppErrorCategory Category)[]
        {
            (new UnauthorizedAccessException("PRIVATE_ACCESS_DETAIL"), AppErrorContext.Settings, "AA-SETTINGS-ACCESS", AppErrorCategory.AccessDenied),
            (new HttpRequestException("PRIVATE_HTTP_DETAIL", null, HttpStatusCode.Forbidden), AppErrorContext.Provider, "AA-PROVIDER-ACCESS", AppErrorCategory.AccessDenied),
            (new FileNotFoundException("PRIVATE_FILE_DETAIL", @"C:\Users\private\missing.json"), AppErrorContext.FileTransfer, "AA-FILE-NOT-FOUND", AppErrorCategory.NotFound),
            (new IOException("PRIVATE_IO_DETAIL"), AppErrorContext.SavedState, "AA-SAVED-IO", AppErrorCategory.Io),
            (new InvalidDataException("PRIVATE_DATA_DETAIL"), AppErrorContext.FileTransfer, "AA-FILE-INVALID-DATA", AppErrorCategory.InvalidData),
            (new JsonException("PRIVATE_JSON_DETAIL"), AppErrorContext.ControlPlane, "AA-CONTROL-JSON", AppErrorCategory.Json),
            (new CryptographicException("PRIVATE_CRYPTO_DETAIL"), AppErrorContext.Settings, "AA-SETTINGS-CRYPTO", AppErrorCategory.Cryptography),
            (new TimeoutException("PRIVATE_TIMEOUT_DETAIL"), AppErrorContext.Internet, "AA-INTERNET-TIMEOUT", AppErrorCategory.Timeout),
            (new HttpRequestException("PRIVATE_NETWORK_DETAIL"), AppErrorContext.Provider, "AA-PROVIDER-NETWORK", AppErrorCategory.Network),
            (new InvalidOperationException("PRIVATE_PROVIDER_DETAIL"), AppErrorContext.Provider, "AA-PROVIDER-PROVIDER", AppErrorCategory.Provider),
            (new InvalidOperationException("PRIVATE_UNEXPECTED_DETAIL"), AppErrorContext.Arena, "AA-ARENA-UNEXPECTED", AppErrorCategory.Unexpected)
        };

        foreach (var testCase in cases)
        {
            var first = AppErrorPresenter.Present(testCase.Error, testCase.Context);
            var second = AppErrorPresenter.Present(testCase.Error, testCase.Context);
            Require(first == second
                    && first.Code == testCase.Code
                    && first.Category == testCase.Category
                    && first.DisplayText.Contains(first.Action, StringComparison.Ordinal)
                    && first.CopyDetails.Contains($"Code: {first.Code}", StringComparison.Ordinal)
                    && !first.DisplayText.Contains("PRIVATE_", StringComparison.Ordinal)
                    && !first.CopyDetails.Contains("PRIVATE_", StringComparison.Ordinal),
                $"error presentation should deterministically classify and redact {testCase.Code}");
        }

        var cancelled = AppErrorPresenter.Present(
            new OperationCanceledException("PRIVATE_CANCEL_DETAIL"),
            AppErrorContext.Arena);
        Require(cancelled.Code == "AA-ARENA-CANCELLED"
                && cancelled.IsCancellation
                && cancelled.Category == AppErrorCategory.Cancelled
                && cancelled.DisplayText.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                && !cancelled.DisplayText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                && !cancelled.DisplayText.Contains("failure", StringComparison.OrdinalIgnoreCase),
            "cancellation should have a stable non-danger presentation and never be labelled a failure");

        var hostile = string.Join('\n',
        [
            "Authorization: Bearer bearer-secret-123456789",
            "Authorization=Basic YmFzaWMtc2VjcmV0OnBhc3M=",
            "{\"api_token\":\"json token with spaces\",\"password\":\"quoted password value\"}",
            "api_key=plain-api-value password=plain-password-value token=plain-token-value",
            "sk-proj-abcdefghijklmnop pk_live_123456789 api-secretcredential",
            "https://example.test/private?q=query-secret&token=url-token",
            @"C:\Users\private\arena\snapshot.json",
            @"\\private-server\private-share\secret.bin",
            "/home/private-user/arena/private.json",
            "private.person@example.test",
            "Server=private-sql;Database=private-db;User Id=private-user;Password=private-connection-password;",
            "visibility=private PRIVATE_PROMPT_SECRET",
            "control:\0\u0001end"
        ]).Replace("\\0", "\0", StringComparison.Ordinal).Replace("\\u0001", "\u0001", StringComparison.Ordinal);
        var safe = AppErrorPresenter.RedactAndBound(hostile, 4096, "safe fallback");
        var secretFragments = new[]
        {
            "bearer-secret", "YmFzaWM", "json token", "quoted password", "plain-api-value",
            "plain-password", "plain-token", "sk-proj", "pk_live", "api-secretcredential",
            "query-secret", "url-token", "private-server", "private-share", "private-user",
            "snapshot.json", "private.person", "private-sql", "private-db", "private-connection-password",
            "PRIVATE_PROMPT_SECRET"
        };
        Require(secretFragments.All(fragment => !safe.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                && safe.Contains("[REDACTED:CREDENTIAL]", StringComparison.Ordinal)
                && safe.Contains("[REDACTED:SECRET]", StringComparison.Ordinal)
                && safe.Contains("[REDACTED:URL]", StringComparison.Ordinal)
                && safe.Contains("[REDACTED:PATH]", StringComparison.Ordinal)
                && safe.Contains("[REDACTED:EMAIL]", StringComparison.Ordinal)
                && safe.Contains("[REDACTED:PRIVATE_MEMORY]", StringComparison.Ordinal)
                && !safe.Contains('\0')
                && !safe.Contains('\u0001'),
            "the central redactor should remove every required secret, identity, path, URL, and private-memory form");
        Require(AppErrorPresenter.RedactAndBound(safe, 4096, "safe fallback") == safe,
            "central redaction should be idempotent for already-safe copy details");

        var oversized = new string('x', 1_000_000) + "Bearer never-scanned-secret";
        var bounded = AppErrorPresenter.RedactAndBound(oversized, 512, "safe fallback");
        Require(bounded.Length <= 512 && bounded.Contains("truncated", StringComparison.OrdinalIgnoreCase),
            "maliciously large input should have bounded work and bounded output");
        Require(AppErrorPresenter.RedactAndBound(null, 16, "safe fallback") == "safe fallback",
            "empty input should use the bounded safe fallback");

        const string rawVoiceSecret = "Bearer voice-secret-123456789 at C:\\Users\\private\\voice.txt";
        using var voice = new VoiceNarrationService(() => throw new IOException(rawVoiceSecret));
        var voiceResult = voice.Speak("hello", new VoiceNarrationOptions("", 0, 100));
        Require(!voiceResult.Ok
                && voiceResult.Status.Contains("AA-VOICE-IO", StringComparison.Ordinal)
                && !voiceResult.Status.Contains("voice-secret", StringComparison.Ordinal)
                && !voiceResult.Status.Contains("C:\\Users", StringComparison.Ordinal),
            "representative voice UI integration should expose the stable code without exception text");

        var center = new ApplicationStatusCenter();
        var statusError = AppErrorPresenter.Present(new IOException(rawVoiceSecret), AppErrorContext.VoiceNarration);
        var receipt = center.Begin("voice.failure", "Voice", "Starting voice narration.");
        Require(center.Fail(receipt, statusError.Summary, statusError.CopyDetails),
            "the status center should accept the same authoritative presentation");
        Require(center.Primary.Detail.Contains(statusError.Code, StringComparison.Ordinal)
                && !center.Primary.Detail.Contains("voice-secret", StringComparison.Ordinal)
                && !center.Primary.Detail.Contains("C:\\Users", StringComparison.Ordinal),
            "status-center copy detail should share the code and redaction used by visible UI text");

        var recoveryRoot = Path.Combine(
            Path.GetTempPath(),
            "ai-arena-coded-status-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var historyPath = Path.Combine(recoveryRoot, "collaborate-history.json");
            Directory.CreateDirectory(recoveryRoot);
            File.WriteAllText(
                historyPath,
                "{ malformed VISUAL_CODE_PRIVACY_SENTINEL C:\\Users\\private\\collaborate-history.json");
            var historyStore = new CollaborateHistoryStore(historyPath);
            var collaborateStatus = new TextBlock();
            var coordinator = CreateCollaborateCoordinatorForTest(
                new FixedCollaborateModelClient("unused"),
                new TextBox(),
                collaborateStatus,
                () => null,
                _ => { },
                historyStore);
            coordinator.Initialize();
            Require(coordinator.ControlState.SavedConversationCount == 0,
                "malformed Collaborate history should initialize the real coordinator with the safe empty fallback");

            var recoveryWarning = historyStore.LastLoadWarning;
            Require(recoveryWarning.Contains("AA-SETTINGS-JSON", StringComparison.Ordinal)
                    && !recoveryWarning.Contains("VISUAL_CODE_PRIVACY_SENTINEL", StringComparison.Ordinal)
                    && !recoveryWarning.Contains("C:\\Users", StringComparison.Ordinal),
                "malformed Collaborate history should produce a privacy-safe coded recovery warning");

            var recoveryCenter = new ApplicationStatusCenter();
            var recoveryIdentity = new ApplicationStatusIdentity("session-recovery", "provider-recovery");
            recoveryCenter.SetContext(recoveryIdentity);
            recoveryCenter.PublishNotice(
                "arena.readiness",
                "Arena",
                ApplicationStatusState.Blocked,
                "Connect the configured provider before running the arena.",
                identity: recoveryIdentity,
                lifetime: ApplicationStatusLifetime.UntilResolved);
            var recoveryPublishCount = 0;
            void PublishRecovery(string warning)
            {
                recoveryPublishCount++;
                recoveryCenter.PublishNotice(
                    "collaborate.history-recovery",
                    "Collaborate",
                    ApplicationStatusState.Warning,
                    warning,
                    navigationTarget: "collaborate",
                    identity: recoveryIdentity,
                    lifetime: ApplicationStatusLifetime.UntilResolved);
            }

            Require(coordinator.PublishPendingRecoveryWarning(PublishRecovery)
                    && !coordinator.PublishPendingRecoveryWarning(PublishRecovery)
                    && recoveryPublishCount == 1,
                "the coordinator should defer recovery publication until composition establishes status context, then publish exactly once");

            var recoverySnapshot = recoveryCenter.Snapshot;
            var recoveryStatus = recoverySnapshot.History.Single(entry =>
                entry.Key.Equals("collaborate.history-recovery", StringComparison.Ordinal));
            var recoveryApi = AIArenaApplicationStatusControlProjection.Project(recoverySnapshot);
            var recoveryApiStatus = recoveryApi.Items.Single(entry =>
                entry.Id.StartsWith("collaborate.history-recovery:", StringComparison.Ordinal));
            Require(recoveryStatus.Summary.Length <= 180
                    && recoveryStatus.Summary.Contains("AA-SETTINGS-JSON", StringComparison.Ordinal)
                    && recoveryApiStatus.Summary.Contains("AA-SETTINGS-JSON", StringComparison.Ordinal)
                    && recoverySnapshot.VisibleEntries.Any(entry => entry.Id == recoveryStatus.Id)
                    && recoveryStatus.Identity == recoveryIdentity
                    && recoveryStatus.RepeatCount == 1,
                "bounded visible, history, and control-plane recovery summaries should retain their stable support code");
            Require(!recoveryStatus.Summary.Contains("VISUAL_CODE_PRIVACY_SENTINEL", StringComparison.Ordinal)
                    && !recoveryStatus.Summary.Contains("C:\\Users", StringComparison.Ordinal)
                    && !recoveryApiStatus.Summary.Contains("VISUAL_CODE_PRIVACY_SENTINEL", StringComparison.Ordinal)
                    && !recoveryApiStatus.Summary.Contains("C:\\Users", StringComparison.Ordinal)
                    && recoveryApi.Items.Any(entry => entry.Id.StartsWith("arena.readiness:", StringComparison.Ordinal)),
                "preserving a recovery code must not expose corrupt-file contents or local paths");

            var longCodedSummary = new string('S', 700) + " Code: AA-SETTINGS-JSON.";
            var longCodedDetail = "Code: AA-SETTINGS-JSON. " + new string('D', 900);
            recoveryCenter.PublishNotice(
                "collaborate.long-coded-recovery",
                "Collaborate",
                ApplicationStatusState.Warning,
                longCodedSummary,
                longCodedDetail,
                navigationTarget: "collaborate",
                identity: recoveryIdentity,
                lifetime: ApplicationStatusLifetime.UntilResolved);
            var longCodedSnapshot = recoveryCenter.Snapshot;
            var longCodedStatus = longCodedSnapshot.History.Single(entry =>
                entry.Key.Equals("collaborate.long-coded-recovery", StringComparison.Ordinal));
            var longCodedApiStatus = AIArenaApplicationStatusControlProjection.Project(longCodedSnapshot)
                .Items
                .Single(entry => entry.Id.StartsWith("collaborate.long-coded-recovery:", StringComparison.Ordinal));
            Require(longCodedStatus.Summary.Length <= 180
                    && longCodedStatus.Detail.Length <= 512
                    && longCodedStatus.Summary.Contains("Code: AA-SETTINGS-JSON.", StringComparison.Ordinal)
                    && longCodedStatus.Detail.Contains("Code: AA-SETTINGS-JSON.", StringComparison.Ordinal),
                "status bounding should retain a validated support code even when the safe source narrative exceeds the upstream 512-character bound");
            Require(longCodedApiStatus.Summary.Contains("Code: AA-SETTINGS-JSON.", StringComparison.Ordinal)
                    && longCodedApiStatus.Detail.Contains("Code: AA-SETTINGS-JSON.", StringComparison.Ordinal)
                    && longCodedApiStatus.Summary.Length <= 180
                    && longCodedApiStatus.Detail.Length <= 512,
                "visible and detail support codes should survive the downstream control-plane projection unchanged");

            var malformedCode = new string('M', 700) + " Code: AA-PRIVATE-PROMPTSECRET.";
            recoveryCenter.PublishNotice(
                "collaborate.invalid-code-shape",
                "Collaborate",
                ApplicationStatusState.Warning,
                malformedCode,
                navigationTarget: "collaborate",
                identity: recoveryIdentity,
                lifetime: ApplicationStatusLifetime.UntilResolved);
            var malformedStatus = recoveryCenter.Snapshot.History.Single(entry =>
                entry.Key.Equals("collaborate.invalid-code-shape", StringComparison.Ordinal));
            Require(!malformedStatus.Summary.Contains("AA-PRIVATE-PROMPTSECRET", StringComparison.Ordinal),
                "status bounding must not reconstruct a shape-valid token outside the finite product support-code namespace");
        }
        finally
        {
            if (Directory.Exists(recoveryRoot))
            {
                Directory.Delete(recoveryRoot, recursive: true);
            }
        }

        var targetedSources = new[]
        {
            "src/AIArena.Wpf/App.xaml.cs",
            "src/AIArena.Wpf/Platform/Windows/Diagnostics/CrashReporter.cs",
            "src/AIArena.Wpf/Services/ExperimentLabCoordinator.cs",
            "src/AIArena.Wpf/Services/FaultInjectionLabCoordinator.cs",
            "src/AIArena.Wpf/Modules/Narration/Services/VoiceNarrationService.cs",
            "src/AIArena.Wpf/Modules/Internet/Services/SearxngSupervisorService.cs",
            "src/AIArena.Wpf/Shell/InternetWorkflowCoordinator.cs",
            "src/AIArena.Wpf/Modules/Provider/Services/LlamaCppRuntimeService.cs",
            "src/AIArena.Wpf/Shell/LlamaCppRuntimeCoordinator.cs",
            "src/AIArena.Wpf/Shell/SavedStateWorkflowCoordinator.cs",
            "src/AIArena.Wpf/Shell/ArenaOperationCoordinator.cs",
            "src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs",
            "src/AIArena.Wpf/Platform/Windows/Settings/WpfSettingsStore.cs",
            "src/AIArena.Wpf/Platform/Windows/Settings/JsonFileRecovery.cs",
            "src/AIArena.Wpf/Shell/ArenaEvaluationHistoryStore.cs",
            "src/AIArena.Wpf/Shell/ArenaEvaluationCoordinator.cs",
            "src/AIArena.Wpf/Shell/MatchSetupPortabilityService.cs",
            "src/AIArena.Wpf/Shell/ControlPlane/AIArenaControlPlaneHost.cs",
            "src/AIArena.Wpf/Shell/ControlPlane/AIArenaControlPlaneProtocol.cs",
            "src/AIArena.Wpf/Shell/ControlPlane/AIArenaScreenshotControlService.cs",
            "src/AIArena.Wpf/Shell/AgentWorkspaceCommand.cs",
            "src/AIArena.Wpf/Shell/AgentWorkspaceCoordinator.cs",
            "src/AIArena.Wpf/Shell/CollaborateCoordinator.cs",
            "src/AIArena.Wpf/Shell/CollaborateHistoryStore.cs",
            "src/AIArena.Wpf/Shell/ShellFileExport.cs",
            "src/AIArena.Wpf/Shell/ShellProcessLauncher.cs",
            "src/AIArena.Wpf/Shell/MainWindow.xaml.cs"
        };
        foreach (var path in targetedSources)
        {
            var source = ReadWorkspaceFile(path);
            Require(!source.Contains("ex.Message", StringComparison.Ordinal)
                    && !source.Contains("exception.Message", StringComparison.Ordinal)
                    && !source.Contains("Exception.ToString", StringComparison.Ordinal),
                $"targeted user-facing failure sites must not publish raw exception text: {path}");
        }
        });
    }
}
