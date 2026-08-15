using AIArena.Wpf.Services;

internal static partial class Program
{
    private static void ApplicationStatusCenterPreservesCausalityAndPriority()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var center = new ApplicationStatusCenter(() => now, historyCapacity: 20);
        var identity = new ApplicationStatusIdentity("session-a", "provider-a");
        center.SetContext(identity);
        var globalReceipt = center.Begin("app.export", "App", "Exporting…");

        var first = center.Begin(
            "models.load",
            "Models",
            "Loading Alpha…",
            identity: identity,
            navigationTarget: "models:alpha");
        now = now.AddSeconds(1);
        var replacement = center.Begin(
            "models.load",
            "Models",
            "Loading Beta…",
            identity: identity,
            navigationTarget: "models:beta");
        Require(!center.Complete(first, "Alpha loaded"), "superseded status receipts must reject stale completion");
        Require(center.Update(replacement, "Waiting for LM Studio…", progress: 50), "current status receipt should update");

        now = now.AddSeconds(1);
        center.PublishNotice("provider.offline", "Provider", ApplicationStatusState.Blocked, "Provider is offline.");
        for (var index = 0; index < 6; index++)
        {
            now = now.AddSeconds(1);
            center.PublishNotice($"copy.{index}", "App", ApplicationStatusState.Succeeded, $"Copied item {index}.");
        }

        var visible = center.VisibleEntries;
        Require(visible.Count == ApplicationStatusCenter.CompactEntryCount, "status center should project at most four compact entries when activity is present");
        Require(visible[0].Summary == "Copied item 5.", "compact status rows should remain newest first");
        Require(visible.Any(entry => entry.Key == "provider.offline"), "an unresolved blocker must remain visible despite newer successes");
        Require(center.Primary.Key == "provider.offline", "blocking failures must outrank newer routine results in the primary projection");
        for (var index = 0; index < 5; index++)
        {
            now = now.AddSeconds(1);
            center.PublishNotice($"warning.{index}", "Provider", ApplicationStatusState.Warning, $"Warning {index}");
        }

        Require(center.VisibleEntries.Any(entry => entry.Key == "provider.offline"),
            "newer low-severity warnings must not displace an unresolved blocker from all four compact rows");

        center.SetContext(new ApplicationStatusIdentity("session-b", "provider-b"));
        Require(!center.Fail(replacement, "Late failure"), "a provider/session switch must reject stale status completion");
        Require(center.Complete(globalReceipt, "Exported."), "a session switch must not invalidate a process-wide operation with no scoped identity");
        Require(center.History.Any(entry => entry.Key == "models.load" && entry.IsResolved), "context replacement should resolve the old scoped entry without projecting it into the new session");
        var staleBegin = center.Begin(
            "models.stale",
            "Models",
            "Late old-session operation",
            identity: identity);
        var staleNotice = center.PublishNotice(
            "provider.stale",
            "Provider",
            ApplicationStatusState.Warning,
            "Late old-session warning",
            identity: identity);
        Require(staleBegin.IsEmpty && staleNotice.IsEmpty,
            "new work carrying a stale nonempty session identity must be rejected at insertion");
        Require(center.History.All(entry => entry.Key is not "models.stale" and not "provider.stale"),
            "stale-session insertions must not reappear in history after a context switch");

        var sessionOnlyReceipt = center.Begin(
            "arena.same-session",
            "Arena",
            "Running in the active session",
            identity: new ApplicationStatusIdentity("session-b"));
        var providerScopedReceipt = center.Begin(
            "provider.same-session",
            "Provider",
            "Testing provider B",
            identity: new ApplicationStatusIdentity("session-b", "provider-b"));
        center.SetContext(new ApplicationStatusIdentity("session-b", "provider-c"));
        Require(center.Complete(sessionOnlyReceipt, "Arena operation completed."),
            "changing provider identity in the same session must not invalidate session-only Arena or Agent work");
        Require(!center.Complete(providerScopedReceipt, "Old provider test completed."),
            "changing provider identity in the same session must reject the old provider-scoped receipt");

        center.Resolve("provider.offline");
        foreach (var index in Enumerable.Range(0, 5))
        {
            center.Resolve($"warning.{index}");
        }

        center.PublishNotice(
            "warning.background",
            "Provider",
            ApplicationStatusState.Warning,
            "Background warning",
            background: true);
        now = now.AddSeconds(1);
        center.PublishNotice(
            "warning.foreground",
            "Models",
            ApplicationStatusState.Warning,
            "User-triggered warning");
        Require(center.Primary.Key == "warning.foreground",
            "a user-triggered outcome must outrank an equivalent background warning");
        center.PublishNotice(
            "poll.background",
            "Provider",
            ApplicationStatusState.Running,
            "Background catalog polling",
            background: true);
        Require(center.Primary.Key == "warning.foreground",
            "background polling must not outrank a user-triggered warning");
        center.Resolve("warning.foreground");
        center.Resolve("warning.background");
        center.PublishNotice(
            "copy.foreground",
            "App",
            ApplicationStatusState.Succeeded,
            "Copy completed");
        Require(center.Primary.Key == "copy.foreground",
            "background polling must not outrank a recent user-triggered success");
    }

    private static void ApplicationStatusCenterCoalescesExpiresSanitizesAndClearsSafely()
    {
        var now = new DateTimeOffset(2026, 8, 12, 13, 0, 0, TimeSpan.Zero);
        var center = new ApplicationStatusCenter(
            () => now,
            historyCapacity: 20,
            successLifetime: TimeSpan.FromSeconds(6));
        var changes = new List<ApplicationStatusChangedEventArgs>();
        center.Changed += (_, change) => changes.Add(change);

        Require(!center.PublishHeartbeatTransition("provider.health", "Provider", healthy: true), "initial healthy heartbeat must remain silent");
        Require(!center.PublishHeartbeatTransition("provider.health", "Provider", healthy: true), "repeated healthy heartbeat must remain silent");
        Require(center.History.Count == 0, "silent healthy heartbeat must not create status history");
        Require(center.PublishHeartbeatTransition("provider.health", "Provider", healthy: false, "Provider is offline."), "a real heartbeat transition should publish once");
        Require(!center.PublishHeartbeatTransition("provider.health", "Provider", healthy: false, "Provider is offline."), "repeated unhealthy heartbeat must coalesce");
        Require(changes.All(change => change.AnnouncementKind == ApplicationStatusAnnouncement.None), "background heartbeat transitions must never announce through the shell live region");
        var providerA = new ApplicationStatusIdentity("session-a", "provider-a");
        var providerB = new ApplicationStatusIdentity("session-a", "provider-b");
        Require(center.PublishHeartbeatTransition("provider.switch", "Provider", healthy: false, "Provider A is offline.", "first error", providerA),
            "the first unhealthy provider identity should publish a warning");
        Require(center.PublishHeartbeatTransition("provider.switch", "Provider", healthy: true, "Provider B is online.", identity: providerB),
            "a healthy replacement provider should meaningfully clear the prior provider warning");
        Require(center.VisibleEntries.All(entry => entry.Key != "provider.switch"),
            "a first healthy sample for a replacement provider left the old provider warning visible");
        Require(center.PublishHeartbeatTransition("provider.switch", "Provider", healthy: false, "Provider B is offline.", "error one", providerB),
            "the replacement provider offline transition should publish");
        Require(center.PublishHeartbeatTransition("provider.switch", "Provider", healthy: false, "Provider B is offline.", "error two", providerB),
            "materially changed offline evidence should update even when health is unchanged");
        Require(center.VisibleEntries.Single(entry => entry.Key == "provider.switch").Detail.Contains("error two", StringComparison.Ordinal),
            "changed provider error evidence did not replace the stale detail");
        Require(center.History.Count(entry => entry.Key == "provider.switch" && entry.Identity == providerB) == 1,
            "changing heartbeat error evidence should update one incident instead of filling history with warning generations");
        center.SetContext(providerB);
        Require(!center.PublishHeartbeatTransition(
                "provider.switch",
                "Provider",
                healthy: true,
                "Late Provider A success.",
                identity: providerA),
            "a heartbeat carrying a stale provider identity must be rejected before it mutates health state");
        Require(center.VisibleEntries.Single(entry => entry.Key == "provider.switch").Identity == providerB,
            "a late heartbeat from the replaced provider must not clear the active provider's warning");

        var receipt = center.Begin("arena.turn", "Arena", "Running one turn…");
        var announcementsBeforeDuplicate = changes.Count(change => change.AnnouncementKind != ApplicationStatusAnnouncement.None);
        Require(center.Update(receipt, "Running one turn…"), "an exact repeated progress update should remain causally accepted");
        Require(changes.Count(change => change.AnnouncementKind != ApplicationStatusAnnouncement.None) == announcementsBeforeDuplicate,
            "exact repeated progress updates must coalesce without another live announcement");
        center.PublishNotice(
            "privacy.failure",
            "Provider",
            ApplicationStatusState.Failed,
            "Failed token=super-secret at C:/Users/private/model.gguf",
            "Bearer another-secret https://user:password@example.test/v1?token=third-secret /home/private/model.gguf");
        var privacy = center.History.Single(entry => entry.Key == "privacy.failure");
        Require(!privacy.Summary.Contains("super-secret", StringComparison.Ordinal), "status summary must redact token values");
        Require(!privacy.Detail.Contains("another-secret", StringComparison.Ordinal), "status detail must redact bearer values");
        Require(!privacy.Detail.Contains("password", StringComparison.Ordinal), "status detail must redact URL credentials");
        Require(!privacy.Summary.Contains("C:/Users", StringComparison.OrdinalIgnoreCase), "status summary must redact Windows paths");
        Require(!privacy.Detail.Contains("/home/private", StringComparison.Ordinal), "status detail must redact Unix paths");
        center.PublishNotice(
            "privacy.formats",
            "Provider",
            ApplicationStatusState.Failed,
            """{"api_key":"sk-json-secret"} api key sk-spaced-secret""",
            "Authorization: Basic dXNlcjpwYXNzd29yZA== bearer sk-standalone-secret");
        var privacyFormats = center.History.Single(entry => entry.Key == "privacy.formats");
        Require(!privacyFormats.Summary.Contains("sk-json-secret", StringComparison.Ordinal)
                && !privacyFormats.Summary.Contains("sk-spaced-secret", StringComparison.Ordinal)
                && !privacyFormats.Detail.Contains("dXNlcjpwYXNzd29yZA", StringComparison.Ordinal)
                && !privacyFormats.Detail.Contains("sk-standalone-secret", StringComparison.Ordinal),
            "status sanitization must redact JSON, spaced-key, Basic, and standalone bearer credentials");
        center.PublishNotice(
            "privacy.urls-paths",
            "Provider",
            ApplicationStatusState.Failed,
            "Failed at https://models.example.test/private/v1 and sk-proj-standalonesecret123",
            "Paths 'C:\\Users\\Cyber\\Private Models\\secret.gguf' and '/home/user/Private Models/secret.gguf'");
        var privacyUrlsPaths = center.History.Single(entry => entry.Key == "privacy.urls-paths");
        Require(!privacyUrlsPaths.Summary.Contains("models.example.test", StringComparison.OrdinalIgnoreCase)
                && !privacyUrlsPaths.Summary.Contains("standalonesecret", StringComparison.Ordinal)
                && !privacyUrlsPaths.Detail.Contains("Private Models", StringComparison.Ordinal)
                && !privacyUrlsPaths.Detail.Contains("secret.gguf", StringComparison.Ordinal),
            "status sanitization must redact ordinary URLs, standalone credential prefixes, and paths containing spaces");

        Require(center.Complete(receipt, "Turn completed."), "active status should complete causally");
        now = now.AddSeconds(7);
        var changeCountBeforeExpiry = changes.Count;
        Require(center.RefreshExpirations(), "transient success should expire after its configured lifetime");
        Require(changes.Count == changeCountBeforeExpiry + 1 && changes[^1].ExpiredOnly, "expiry should produce a non-announcing projection change");
        Require(changes[^1].AnnouncementKind == ApplicationStatusAnnouncement.None, "expiry must not be announced");
        Require(center.History.Any(entry => entry.Key == "arena.turn" && entry.State == ApplicationStatusState.Succeeded),
            "success expiry should remove the compact current entry without erasing current-run dashboard history");

        Require(!center.Update(receipt, "Late progress"), "a terminal receipt must reject late progress resurrection");
        Require(!center.Fail(receipt, "Late failure"), "a terminal receipt must reject late terminal rewrites");

        var active = center.Begin("agent.run", "Agent", "Agent is running…");
        center.PublishNotice("arena.blocked", "Arena", ApplicationStatusState.Blocked, "Context recovery is required.");
        center.PublishNotice("copy.done", "App", ApplicationStatusState.Succeeded, "Copied.");
        var removed = center.ClearCompleted();
        Require(removed > 0, "clear completed should remove terminal nonblocking history");
        Require(center.History.Any(entry => entry.Key == "agent.run" && entry.IsActive), "clear completed must retain active operations");
        Require(center.History.Any(entry => entry.Key == "arena.blocked" && entry.IsUnresolved), "clear completed must retain unresolved blockers");
        Require(center.Cancel(active, "Agent stopped."), "retained active operation should remain causally mutable");

        var protectedCenter = new ApplicationStatusCenter(() => now, historyCapacity: 20);
        for (var index = 0; index < 25; index++)
        {
            protectedCenter.PublishNotice($"blocked.{index}", "Arena", ApplicationStatusState.Blocked, $"Blocked {index}");
        }

        Require(protectedCenter.History.Count == 25,
            "the completed-history capacity must not erase active or unresolved status evidence");
        foreach (var index in Enumerable.Range(0, 25))
        {
            protectedCenter.Resolve($"blocked.{index}");
        }
        Require(protectedCenter.History.Count == 20,
            "once protected overflow is resolved, history should return to its configured completed-entry capacity");
        string? navigated = null;
        protectedCenter.NavigationRequested += (_, target) => navigated = target;
        Require(protectedCenter.RequestNavigation("models:publisher/model")
                && navigated == "models:publisher/model",
            "privacy-safe model identifiers containing a slash should remain navigable from status history");
        Require(protectedCenter.RequestNavigation("models:publisher%2Fmodel%20name")
                && navigated == "models:publisher%2Fmodel%20name",
            "percent-encoded opaque model identifiers should remain navigable without exposing raw text");
    }

    private static void ApplicationStatusCenterCoversResidualShellFeedback()
    {
        var mainWindowSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
        var mainWindowXaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
        var shellTopBarSource = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml.cs");
        var sessionOverviewSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/SessionOverviewCoordinator.cs");
        var scenarioSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ScenarioWorkflowCoordinator.cs");
        var transcriptMutationSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/TranscriptMutationCoordinator.cs");
        var internetSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/InternetWorkflowCoordinator.cs");
        var collaborateSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/CollaborateCoordinator.cs");

        var settingsStatusHelper = CSharpMethodBlock(mainWindowSource, "private void SetSettingsTransferStatus");
        Require(settingsStatusHelper.Contains("SetApplicationStatus", StringComparison.Ordinal)
                && settingsStatusHelper.Contains("\"app.settings-transfer\"", StringComparison.Ordinal),
            "settings import and export outcomes should publish through the universal status center");
        Require(mainWindowSource.Contains("SetApplicationStatus(\"app.match-setup-transfer\", \"Match Setup\"", StringComparison.Ordinal),
            "portable Match Setup copy and import outcomes should use a source-specific universal status");
        Require(mainWindowSource.Contains("SetApplicationStatus(\"app.match-setup-copy\", \"Match Setup\"", StringComparison.Ordinal),
            "generation and current-setup clipboard outcomes should use a source-specific universal status");
        Require(!CSharpMethodBlock(mainWindowSource, "private async void CopyCurrentSetupSpecButton_Click").Contains("SetArenaRunStatus", StringComparison.Ordinal)
                && !CSharpMethodBlock(mainWindowSource, "private async void ImportCurrentSetupSpecButton_Click").Contains("SetArenaRunStatus", StringComparison.Ordinal),
            "Match Setup transfer feedback should not also publish through the generic Arena compatibility mirror");
        Require(scenarioSource.Contains("this.setTransferStatus = setTransferStatus ?? setArenaRunStatus;", StringComparison.Ordinal)
                && scenarioSource.Contains("SetTransferStatus(successStatus);", StringComparison.Ordinal),
            "scenario clipboard feedback should preserve compatibility while preferring its dedicated status-center callback");
        Require(mainWindowSource.Contains("\"app.snapshot-refresh\"", StringComparison.Ordinal)
                && mainWindowSource.Contains("\"app.session-load\"", StringComparison.Ordinal)
                && mainWindowSource.Contains("\"agent.inspection-refresh\"", StringComparison.Ordinal),
            "session and inspection background failures should no longer disappear into hidden load text or debug output");
        var renderSnapshot = CSharpMethodBlock(mainWindowSource, "private void RenderSnapshot(ArenaViewSnapshot snapshot)");
        Require(renderSnapshot.Contains("StatusCenter.SetContext(ProviderStatusIdentity(statusSessionId, snapshot))", StringComparison.Ordinal)
                && renderSnapshot.IndexOf("StatusCenter.SetContext", StringComparison.Ordinal)
                    < renderSnapshot.IndexOf("PreserveCurrentSessionSettingsDraft", StringComparison.Ordinal),
            "a rendered session must synchronously advance the causal status scope before new-session operations can publish");
        var loadSession = CSharpMethodBlock(mainWindowSource, "private async Task LoadSessionAsync");
        Require(loadSession.Contains("StatusCenter.SetContext(new ApplicationStatusIdentity(session.Id))", StringComparison.Ordinal)
                && loadSession.Contains("new ApplicationStatusIdentity(session.Id),", StringComparison.Ordinal),
            "a failed session switch must still replace the old status scope and bind its failure to the requested session");
        var collaborateRecoveryPublisher = CSharpMethodBlock(
            mainWindowSource,
            "private void PublishCollaborateRecoveryWarning");
        var collaborateHistoryLoad = CSharpMethodBlock(
            collaborateSource,
            "private void LoadPersistedConversations()");
        var collaboratePendingPublication = CSharpMethodBlock(
            collaborateSource,
            "internal bool PublishPendingRecoveryWarning");
        Require(loadSession.Contains("PublishPendingRecoveryWarning(PublishCollaborateRecoveryWarning)", StringComparison.Ordinal)
                && loadSession.IndexOf("StatusCenter.SetContext", StringComparison.Ordinal)
                    < loadSession.IndexOf("PublishPendingRecoveryWarning", StringComparison.Ordinal)
                && collaborateRecoveryPublisher.Contains("\"collaborate.history-recovery\"", StringComparison.Ordinal)
                && collaborateRecoveryPublisher.Contains("ApplicationStatusState.Warning", StringComparison.Ordinal)
                && collaborateRecoveryPublisher.Contains("new ApplicationStatusIdentity(_activeSession?.Id ?? \"\")", StringComparison.Ordinal)
                && collaborateHistoryLoad.Contains("pendingRecoveryWarning = historyStore.LastLoadWarning", StringComparison.Ordinal)
                && !collaborateHistoryLoad.Contains("setShellStatus", StringComparison.Ordinal)
                && collaboratePendingPublication.Contains("pendingRecoveryWarning = \"\"", StringComparison.Ordinal),
            "Collaborate history recovery should defer one typed warning until the active status identity is established");
        var publishReadiness = CSharpMethodBlock(mainWindowSource, "private void PublishArenaReadiness");
        Require(publishReadiness.Contains("SetArenaRunStatusCompatibilityText", StringComparison.Ordinal)
                && !publishReadiness.Contains("ArenaRunStatus.Text =", StringComparison.Ordinal)
                && publishReadiness.Contains("ApplicationStatusLifetime.UntilResolved", StringComparison.Ordinal)
                && publishReadiness.Contains("\"arena.readiness\"", StringComparison.Ordinal),
            "authoritative readiness should publish one typed durable entry while updating the legacy text without a duplicate announcement");
        Require(CSharpMethodBlock(shellTopBarSource, "public void SetArenaRunStatusCompatibilityText")
                    .Contains("suppressArenaStatusPresentation", StringComparison.Ordinal)
                && !CSharpMethodBlock(sessionOverviewSource, "public void UpdateTopBarStatus")
                    .Contains("arenaRunStatus.Text", StringComparison.Ordinal),
            "readiness compatibility text must bypass the legacy live mirror so one blocked projection emits one announcement");
        Require(mainWindowSource.Contains("SetApplicationStatus(\"app.open-releases\"", StringComparison.Ordinal)
                && mainWindowSource.Contains("\"app.user-guide\"", StringComparison.Ordinal),
            "release and User Guide launch failures should be visible in the universal status center");
        Require(mainWindowSource.Contains("SetProviderProfileStatus", StringComparison.Ordinal)
                && mainWindowSource.Contains("\"provider.profile\"", StringComparison.Ordinal),
            "provider profile save, delete, and validation outcomes should publish typed status history");
        Require(CSharpMethodBlock(mainWindowSource, "private async void ApplyProviderPresetButton_Click")
                .Contains("\"provider.preset\"", StringComparison.Ordinal),
            "the non-persisting manual provider preset guidance should still reach the universal status center");
        var transcriptMutationStatus = CSharpMethodBlock(mainWindowSource, "private void SetTranscriptMutationStatus");
        Require(mainWindowSource.Contains("SetTranscriptMutationStatus);", StringComparison.Ordinal)
                && mainWindowSource.Contains("RefreshActiveSessionForTranscriptMutationAsync", StringComparison.Ordinal)
                && mainWindowSource.Contains("RefreshActiveSessionAsync(status, setArenaStatus: false)", StringComparison.Ordinal)
                && transcriptMutationStatus.Contains("\"app.transcript-mutation\"", StringComparison.Ordinal)
                && transcriptMutationStatus.Contains("ApplicationStatusState.Blocked", StringComparison.Ordinal)
                && transcriptMutationSource.Contains("setMutationStatus?.Invoke(successStatus);", StringComparison.Ordinal)
                && transcriptMutationSource.Contains("setMutationStatus?.Invoke(status);", StringComparison.Ordinal),
            "transcript pin/delete outcomes and blocked failures should use one dedicated status-center callback without the generic Arena mirror");
        var internetDiagnostic = CSharpMethodBlock(internetSource, "public async Task TestInternetAsync()");
        Require(internetDiagnostic.Contains("\"app.internet-test\"", StringComparison.Ordinal)
                && internetDiagnostic.Contains("statusCenter?.Complete", StringComparison.Ordinal)
                && internetDiagnostic.Contains("statusCenter?.Fail", StringComparison.Ordinal),
            "user-triggered Internet diagnostics should begin and finish one causal universal status");
        Require(!CSharpMethodBlock(internetSource, "public async Task RefreshBackendHealthAsync()")
                .Contains("statusCenter", StringComparison.Ordinal),
            "routine Internet backend health polling should remain silent");

        foreach (var name in new[] { "SettingsTransferStatusText", "ProviderPresetStatusText", "ProviderProfileStatusText", "InternetBackendStatusText", "InternetDiagnosticResultText", "SettingsPendingChangesText" })
        {
            var contextualStatus = XamlStartTag(mainWindowXaml, name, "TextBlock");
            Require(contextualStatus.Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
                $"the contextual {name} must not duplicate the universal live announcement");
        }
    }
}
