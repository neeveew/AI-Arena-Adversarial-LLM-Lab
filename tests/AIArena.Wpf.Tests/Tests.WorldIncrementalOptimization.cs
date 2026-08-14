using AIArena.Core.Models;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

internal static partial class Program
{
    static void AgentWorldIncrementalReconciliationPreservesStableIdentityAndState()
    {
        RunStaTest(() =>
        {
            var alpha = new AgentState("alpha", "Alpha", "speaking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
            var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
            var gamma = new AgentState("gamma", "Gamma", "waiting", "Risk reviewer", "default", "default", "#A7A4FF", "gamma-model", true, false, []);
            var opening = SnapshotForOverviewTest(
                providerOnline: true,
                providerModel: "shared-model",
                providerLastError: "",
                turnIndex: 1,
                [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Opening argument.", PromptTokens = 40, CompletionTokens = 20, TotalTokens = 60 }],
                [alpha, beta]);
            var control = new AgentWorld3DControl(() => false)
            {
                Width = 760,
                Height = 480
            };
            var host = new Window
            {
                Content = control,
                Width = 760,
                Height = 480,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                host.Show();
                control.ApplySnapshot(opening);
                control.UpdateLayout();
                control.DebugSelectAgent("beta");
                control.DebugSetCameraMode("free");
                control.DebugPressWorldKey(Key.Right);
                control.DebugZoomCamera(120);

                var fullRebuilds = control.DebugSceneRebuildCount;
                var staticRoot = control.DebugStaticSceneElement;
                var skylineRoot = control.DebugSkylineSceneElement;
                var betaIndex = control.DebugAgentIds.ToList().FindIndex(id => id == "beta");
                var betaModel = control.DebugAgentModelElements[betaIndex];
                var betaTransform = control.DebugAgentTransformElements[betaIndex];
                var betaNameTag = (Border)control.DebugAgentNameTagElements[betaIndex];
                var betaBubble = control.DebugAgentBubbleElements[betaIndex];
                var betaLegend = (Border)control.DebugLegendElements[betaIndex];
                var betaMarker = control.DebugMiniMapMarkerElements
                    .Cast<Ellipse>()
                    .Single(marker => string.Equals(marker.Tag as string, "beta", StringComparison.OrdinalIgnoreCase));
                var speakerCue = FindWorldCue(control, "Speaker");
                var tokenCue = FindWorldCue(control, "Tokens");
                var yaw = control.DebugCameraYaw;
                var distance = control.DebugCameraDistance;
                Require(betaLegend.Focus() && betaLegend.IsKeyboardFocused, "AI World legend could not receive keyboard focus before reconciliation");

                var rebuttal = SnapshotForOverviewTest(
                    providerOnline: true,
                    providerModel: "shared-model",
                    providerLastError: "",
                    turnIndex: 2,
                    [
                        TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Opening argument.", PromptTokens = 40, CompletionTokens = 20, TotalTokens = 60 },
                        TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "Evidence-backed rebuttal.", PromptTokens = 45, CompletionTokens = 25, TotalTokens = 70 }
                    ],
                    [beta with { Status = "speaking" }, alpha with { Status = "waiting" }, gamma]);
                control.ApplySnapshot(rebuttal);
                control.UpdateLayout();

                var reorderedBetaIndex = control.DebugAgentIds.ToList().FindIndex(id => id == "beta");
                var reorderedBetaLegendIndex = control.DebugAgentIds.ToList().FindIndex(id => id == "beta");
                var reorderedBetaMarker = control.DebugMiniMapMarkerElements
                    .Cast<Ellipse>()
                    .Single(marker => string.Equals(marker.Tag as string, "beta", StringComparison.OrdinalIgnoreCase));
                Require(control.DebugSceneRebuildCount == fullRebuilds, "speaker and roster changes should not trigger a full scene rebuild");
                Require(ReferenceEquals(staticRoot, control.DebugStaticSceneElement), "static geometry root identity should survive snapshot changes");
                Require(ReferenceEquals(skylineRoot, control.DebugSkylineSceneElement), "token skyline root identity should survive telemetry changes");
                Require(ReferenceEquals(betaModel, control.DebugAgentModelElements[reorderedBetaIndex]), "existing avatar model root should survive speaker and roster changes");
                Require(ReferenceEquals(betaTransform, control.DebugAgentTransformElements[reorderedBetaIndex]), "existing avatar transform should survive speaker and roster changes");
                Require(ReferenceEquals(betaNameTag, control.DebugAgentNameTagElements[reorderedBetaIndex]), "existing avatar name tag should survive speaker and roster changes");
                Require(ReferenceEquals(betaBubble, control.DebugAgentBubbleElements[reorderedBetaIndex]), "existing avatar bubble should survive speaker and roster changes");
                Require(ReferenceEquals(betaLegend, control.DebugLegendElements[reorderedBetaLegendIndex]), "existing legend chip should survive roster reordering");
                Require(ReferenceEquals(betaMarker, reorderedBetaMarker), "existing minimap marker should survive roster reordering");
                Require(ReferenceEquals(speakerCue, FindWorldCue(control, "Speaker")), "speaker cue identity should survive speaker changes");
                Require(ReferenceEquals(tokenCue, FindWorldCue(control, "Tokens")), "semantic cue identity should survive detail changes");
                Require(betaLegend.IsKeyboardFocused, "focused legend chip should retain keyboard focus across roster reordering");
                Require(control.DebugSelectedAgentId == "beta" && control.DebugCameraMode == "Free", "manual selection and free-camera mode should survive snapshot reconciliation");
                Require(Math.Abs(control.DebugCameraYaw - yaw) < 0.001 && Math.Abs(control.DebugCameraDistance - distance) < 0.001, "camera orbit and zoom should survive snapshot reconciliation");
                Require(control.DebugAgentNameTagAutomationNames[reorderedBetaIndex].Contains("speaking", StringComparison.OrdinalIgnoreCase), "reconciled name tag should expose current speaking state to automation");
                Require(control.DebugSpeakerBubbleAutomationHelpTexts.Any(text => text.Contains("Evidence-backed rebuttal", StringComparison.Ordinal)), "reconciled speech bubble should expose current text to automation");
                Require(control.DebugReducedMotion && !control.DebugIsAnimationRunning, "incremental updates should preserve reduced-motion behavior");

                var withoutAlpha = rebuttal with
                {
                    TurnIndex = 3,
                    Agents = [gamma, beta with { Status = "speaking" }]
                };
                control.ApplySnapshot(withoutAlpha);
                var betaAfterRemovalIndex = control.DebugAgentIds.ToList().FindIndex(id => id == "beta");
                Require(control.DebugAvatarVisualCount == 2 && !control.DebugAgentIds.Contains("alpha"), "roster removal should remove only the departed avatar");
                Require(ReferenceEquals(betaModel, control.DebugAgentModelElements[betaAfterRemovalIndex]), "unaffected avatar identity should survive peer removal");
                Require(ReferenceEquals(betaLegend, control.DebugLegendElements[betaAfterRemovalIndex]), "unaffected legend identity should survive peer removal");

                var staticRefreshes = control.DebugStaticGeometryRefreshCount;
                control.Resources["PrimaryBorderBrush"] = new SolidColorBrush(Color.FromRgb(214, 92, 166));
                control.ApplySnapshot(withoutAlpha);
                Require(control.DebugStaticGeometryRefreshCount == staticRefreshes + 1, "theme changes should refresh static materials exactly once");
                Require(ReferenceEquals(staticRoot, control.DebugStaticSceneElement), "theme refresh should preserve the static scene root");
                Require(ReferenceEquals(betaModel, control.DebugAgentModelElements[betaAfterRemovalIndex]), "theme refresh should preserve avatar model-root identity");
                Require(ReferenceEquals(betaNameTag, control.DebugAgentNameTagElements[betaAfterRemovalIndex]), "theme refresh should preserve avatar overlay identity");
                Require(control.DebugUnfrozenGeometryMaterialCount == 0, "incremental theme refresh should retain frozen render-thread materials");
                var bitmap = new RenderTargetBitmap(760, 480, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(control);
                var pixels = new byte[760 * 480 * 4];
                bitmap.CopyPixels(pixels, 760 * 4, 0);
                Require(pixels.Count(value => value != 0) > 20_000, "incrementally reconciled and recolored AI World should remain visibly painted");

                var switchedSession = withoutAlpha with { SessionId = "session-two", TurnIndex = 0 };
                control.ApplySnapshot(switchedSession);
                Require(control.DebugSceneRebuildCount == fullRebuilds, "session switches should reset state without tearing down the scene");
                Require(control.DebugCameraMode == "FollowSpeaker" && control.DebugSelectedAgentId == "beta", "session switch should reset camera state and select the current speaker");
                Require(ReferenceEquals(betaModel, control.DebugAgentModelElements[control.DebugAgentIds.ToList().FindIndex(id => id == "beta")]), "session switch should preserve stable avatar roots for matching ids");
            }
            finally
            {
                host.Close();
            }
        });
    }

    static void AgentWorldIncrementalReconciliationRecordsThousandSnapshotReceipt()
    {
        RunStaTest(() =>
        {
            const int snapshotCount = 1000;
            var alpha = new AgentState("alpha", "Alpha", "speaking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
            var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
            var snapshots = Enumerable.Range(1, snapshotCount)
                .Select(turn => SnapshotForOverviewTest(
                    providerOnline: true,
                    providerModel: "shared-model",
                    providerLastError: "",
                    turnIndex: turn,
                    [TranscriptForTest(turn, "Alpha", "alpha", "message", "ok") with { Text = $"Stable-speaker update {turn}.", PromptTokens = 40, CompletionTokens = 20, TotalTokens = 60 }],
                    [alpha, beta]))
                .ToArray();

            var legacy = new AgentWorld3DControl(() => false);
            var legacyRebuildsBefore = legacy.DebugSceneRebuildCount;
            var legacyGeometryBefore = legacy.DebugGeometryModelCreationCount;
            var legacyAvatarCreationsBefore = legacy.DebugAvatarVisualCreationCount;
            var legacyOverlayCreationsBefore = legacy.DebugOverlayElementCreationCount;
            var legacyLegendCreationsBefore = legacy.DebugLegendElementCreationCount;
            var legacyCueCreationsBefore = legacy.DebugCueElementCreationCount;
            var legacyOperationsBefore = legacy.DebugSceneStructuralOperationCount;
            var legacyAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var legacyClock = Stopwatch.StartNew();
            foreach (var snapshot in snapshots)
            {
                legacy.DebugApplySnapshotWithFullRebuild(snapshot);
            }

            legacyClock.Stop();
            var legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyAllocatedBefore;
            var legacyRebuilds = legacy.DebugSceneRebuildCount - legacyRebuildsBefore;
            var legacyGeometry = legacy.DebugGeometryModelCreationCount - legacyGeometryBefore;
            var legacyAvatarCreations = legacy.DebugAvatarVisualCreationCount - legacyAvatarCreationsBefore;
            var legacyOverlayCreations = legacy.DebugOverlayElementCreationCount - legacyOverlayCreationsBefore;
            var legacyLegendCreations = legacy.DebugLegendElementCreationCount - legacyLegendCreationsBefore;
            var legacyCueCreations = legacy.DebugCueElementCreationCount - legacyCueCreationsBefore;
            var legacyOperations = legacy.DebugSceneStructuralOperationCount - legacyOperationsBefore;

            var incremental = new AgentWorld3DControl(() => false);
            incremental.ApplySnapshot(snapshots[0]);
            var incrementalRebuildsBefore = incremental.DebugSceneRebuildCount;
            var incrementalGeometryBefore = incremental.DebugGeometryModelCreationCount;
            var incrementalAvatarCreationsBefore = incremental.DebugAvatarVisualCreationCount;
            var incrementalOverlayCreationsBefore = incremental.DebugOverlayElementCreationCount;
            var incrementalLegendCreationsBefore = incremental.DebugLegendElementCreationCount;
            var incrementalCueCreationsBefore = incremental.DebugCueElementCreationCount;
            var incrementalOperationsBefore = incremental.DebugSceneStructuralOperationCount;
            var incrementalAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var incrementalClock = Stopwatch.StartNew();
            foreach (var snapshot in snapshots)
            {
                incremental.ApplySnapshot(snapshot);
            }

            incrementalClock.Stop();
            var incrementalAllocated = GC.GetAllocatedBytesForCurrentThread() - incrementalAllocatedBefore;
            var incrementalRebuilds = incremental.DebugSceneRebuildCount - incrementalRebuildsBefore;
            var incrementalGeometry = incremental.DebugGeometryModelCreationCount - incrementalGeometryBefore;
            var incrementalAvatarCreations = incremental.DebugAvatarVisualCreationCount - incrementalAvatarCreationsBefore;
            var incrementalOverlayCreations = incremental.DebugOverlayElementCreationCount - incrementalOverlayCreationsBefore;
            var incrementalLegendCreations = incremental.DebugLegendElementCreationCount - incrementalLegendCreationsBefore;
            var incrementalCueCreations = incremental.DebugCueElementCreationCount - incrementalCueCreationsBefore;
            var incrementalOperations = incremental.DebugSceneStructuralOperationCount - incrementalOperationsBefore;

            var switchingSnapshots = Enumerable.Range(1, snapshotCount)
                .Select(turn =>
                {
                    var betaTurn = turn % 2 == 0;
                    var speakerId = betaTurn ? "beta" : "alpha";
                    var speakerName = betaTurn ? "Beta" : "Alpha";
                    return SnapshotForOverviewTest(
                        providerOnline: true,
                        providerModel: "shared-model",
                        providerLastError: "",
                        turnIndex: turn,
                        [TranscriptForTest(turn, speakerName, speakerId, "message", "ok") with { Text = $"Alternating speaker update {turn}.", PromptTokens = 40, CompletionTokens = 20, TotalTokens = 60 }],
                        [alpha with { Status = "waiting" }, beta]);
                })
                .ToArray();
            var switching = new AgentWorld3DControl(() => false);
            switching.ApplySnapshot(switchingSnapshots[0]);
            var switchingRebuildsBefore = switching.DebugSceneRebuildCount;
            var switchingGeometryBefore = switching.DebugGeometryModelCreationCount;
            var switchingAvatarCreationsBefore = switching.DebugAvatarVisualCreationCount;
            var switchingOverlayCreationsBefore = switching.DebugOverlayElementCreationCount;
            var switchingLegendCreationsBefore = switching.DebugLegendElementCreationCount;
            var switchingCueCreationsBefore = switching.DebugCueElementCreationCount;
            var switchingOperationsBefore = switching.DebugSceneStructuralOperationCount;
            var switchingAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var switchingClock = Stopwatch.StartNew();
            foreach (var snapshot in switchingSnapshots)
            {
                switching.ApplySnapshot(snapshot);
            }

            switchingClock.Stop();
            var switchingAllocated = GC.GetAllocatedBytesForCurrentThread() - switchingAllocatedBefore;
            var switchingRebuilds = switching.DebugSceneRebuildCount - switchingRebuildsBefore;
            var switchingGeometry = switching.DebugGeometryModelCreationCount - switchingGeometryBefore;
            var switchingAvatarCreations = switching.DebugAvatarVisualCreationCount - switchingAvatarCreationsBefore;
            var switchingOverlayCreations = switching.DebugOverlayElementCreationCount - switchingOverlayCreationsBefore;
            var switchingLegendCreations = switching.DebugLegendElementCreationCount - switchingLegendCreationsBefore;
            var switchingCueCreations = switching.DebugCueElementCreationCount - switchingCueCreationsBefore;
            var switchingOperations = switching.DebugSceneStructuralOperationCount - switchingOperationsBefore;

            Console.WriteLine(
                $"AI World 1000-snapshot receipt: legacy rebuilds={legacyRebuilds}, geometry={legacyGeometry}, avatars={legacyAvatarCreations}, overlays={legacyOverlayCreations}, legends={legacyLegendCreations}, cues={legacyCueCreations}, renderOps={legacyOperations}, alloc={legacyAllocated}, elapsedMs={legacyClock.Elapsed.TotalMilliseconds:0.0}; " +
                $"incremental-hot rebuilds={incrementalRebuilds}, geometry={incrementalGeometry}, avatars={incrementalAvatarCreations}, overlays={incrementalOverlayCreations}, legends={incrementalLegendCreations}, cues={incrementalCueCreations}, renderOps={incrementalOperations}, alloc={incrementalAllocated}, elapsedMs={incrementalClock.Elapsed.TotalMilliseconds:0.0}; " +
                $"incremental-switching rebuilds={switchingRebuilds}, geometry={switchingGeometry}, avatars={switchingAvatarCreations}, overlays={switchingOverlayCreations}, legends={switchingLegendCreations}, cues={switchingCueCreations}, renderOps={switchingOperations}, alloc={switchingAllocated}, elapsedMs={switchingClock.Elapsed.TotalMilliseconds:0.0}.");

            Require(legacyRebuilds == snapshotCount, "legacy receipt should exercise one full rebuild per snapshot");
            Require(incrementalRebuilds == 0, "incremental receipt should perform no full rebuilds after warmup");
            Require(incrementalGeometry == 0, "stable roster, theme, speaking state, and quantized skyline should create no geometry after warmup");
            Require(incrementalAvatarCreations == 0 && incrementalOverlayCreations == 0, "stable identities should create no avatars or overlays after warmup");
            Require(incrementalLegendCreations == 0 && incrementalCueCreations == 0, "stable semantic legend and cue identities should create no elements after warmup");
            Require(incrementalOperations == 0, "stable snapshots should perform no render-tree structural operations after warmup");
            Require(legacyGeometry > 100_000 && legacyAvatarCreations == snapshotCount * 2 && legacyLegendCreations == snapshotCount * 2, "legacy receipt should expose the prior full-scene creation scale");
            Require(incrementalAllocated < legacyAllocated / 3, "incremental projection should cut 1000-snapshot allocations by at least two thirds");
            Require(incrementalClock.Elapsed < legacyClock.Elapsed, "incremental projection should be faster than legacy full rebuilds");
            Require(switchingRebuilds == 0 && switchingOperations == 0, "alternating speakers should update avatar internals without full rebuilds or render-tree structural mutations");
            Require(switchingAvatarCreations == 0 && switchingOverlayCreations == 0 && switchingLegendCreations == 0 && switchingCueCreations == 0, "alternating speakers should retain all stable avatar and overlay identities");
            Require(switchingGeometry > 0 && switchingGeometry < legacyGeometry, "alternating speakers should replace only dynamic avatar geometry, not static scenery");
            Require(switchingAllocated < legacyAllocated && switchingClock.Elapsed < legacyClock.Elapsed, "alternating-speaker reconciliation should still beat legacy full rebuild allocation and elapsed receipts");
        });
    }

    private static object FindWorldCue(AgentWorld3DControl control, string text)
    {
        var index = control.DebugWorldCueTexts.ToList().FindIndex(value => value.Contains(text, StringComparison.OrdinalIgnoreCase));
        Require(index >= 0, $"AI World cue containing '{text}' was not found");
        return control.DebugCueElements[index];
    }
}
