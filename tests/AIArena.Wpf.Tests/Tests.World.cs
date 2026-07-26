using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static void AgentWorldLayoutMapsActiveChatState()
{
    var alpha = new AgentState("alpha", "Alpha", "thinking error", "Lead analyst", "plain_language", "chaos", "#35D6FF", "alpha-model", true, true, ["watch premise quality"]);
    var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "", "beta-model", true, false, []);
    var gamma = new AgentState("gamma", "Gamma", "waiting", "Observer", "default", "default", "", "gamma-model", false, false, []);
    var longText = string.Join(" ", Enumerable.Repeat("carefully layered argument", 10));
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "shared-model",
        providerLastError: "",
        turnIndex: 2,
        [
            TranscriptForTest(1, "Beta", "beta", "message", "ok") with { Text = "Beta's latest claim." },
            TranscriptForTest(2, "Tool", "internet", "internet", "ok") with { Text = "Tool chatter should not become a speech bubble.", InternetRequester = "alpha", InternetTool = "web_search" },
            TranscriptForTest(3, "Source", "system", "source", "ok") with { Text = "Source chatter should create a beacon.", InternetRequester = "beta", InternetSources = ["https://example.test/source"] },
            TranscriptForTest(4, "Alpha", "alpha", "message", "ok") with { Text = $"First line\n{longText}", PromptTokens = 14, CompletionTokens = 7, TotalTokens = 21 }
        ],
        [alpha, beta, gamma])
        with
        {
            Summary = "Public synthesis note."
        };

    var world = AgentWorldLayout.Build(snapshot);
    var repeated = AgentWorldLayout.Build(snapshot);
    var noBubbleWorld = AgentWorldLayout.Build(snapshot, maxBubbles: 0);
    var narratorSnapshot = snapshot with
    {
        NarratorModel = "narrator-model",
        NarratorPersona = "Observes and summarizes the match.",
        NarratorVoiceStyle = "ringside_commentary",
        NarratorAccentColor = "#D185CE",
        NarratorLocked = true,
        Agents =
        [
            alpha with { Status = "waiting" },
            beta,
            gamma
        ],
        Messages =
        [
            .. snapshot.Messages,
            TranscriptForTest(5, "Narrator", "narrator", "narration", "ok") with { Text = "Narrator closes the loop.", PromptTokens = 9, CompletionTokens = 6, TotalTokens = 15 }
        ]
    };
    var narratorWorld = AgentWorldLayout.Build(narratorSnapshot);
    var narratorNoBubbleWorld = AgentWorldLayout.Build(narratorSnapshot, maxBubbles: 0);
    var thinkingSnapshot = snapshot with
    {
        Agents =
        [
            alpha with { Status = "waiting" },
            beta with { Status = "thinking" },
            gamma
        ]
    };
    var thinkingWorld = AgentWorldLayout.Build(thinkingSnapshot);
    var staleActivitySnapshot = snapshot with
    {
        TurnIndex = 12,
        Messages =
        [
            TranscriptForTest(1, "Tool", "internet", "internet", "ok") with { Text = "Old tool work should fade from world beacons.", InternetRequester = "alpha", InternetTool = "web_search" },
            TranscriptForTest(2, "Source", "system", "source", "ok") with { Text = "Old source activity should fade from world beacons.", InternetRequester = "beta", InternetSources = ["https://example.test/old"] },
            TranscriptForTest(12, "Beta", "beta", "message", "ok") with { Text = "Recent normal beta turn." }
        ]
    };
    var staleActivityWorld = AgentWorldLayout.Build(staleActivitySnapshot);
    var alphaAvatar = world.Avatars.Single(item => item.Id == "alpha");
    var betaAvatar = world.Avatars.Single(item => item.Id == "beta");
    var narratorAvatar = narratorWorld.Avatars.Single(item => item.Id == "narrator");
    var thinkingBetaAvatar = thinkingWorld.Avatars.Single(item => item.Id == "beta");
    var staleAlphaAvatar = staleActivityWorld.Avatars.Single(item => item.Id == "alpha");
    var staleBetaAvatar = staleActivityWorld.Avatars.Single(item => item.Id == "beta");

    Require(world.Avatars.Count == 2, "world layout should include only active agents");
    Require(world.SessionId == "session", "world layout should preserve session id");
    Require(world.Avatars.Count(item => item.Speaking) == 1, "world layout should expose exactly one speaker bubble by default");
    Require(alphaAvatar.Speaking, "latest eligible agent should be the only speaking avatar");
    Require(alphaAvatar.VoiceStyle == "plain_language", "world avatars should preserve agent voice style");
    Require(alphaAvatar.PressureProfile == "chaos", "world avatars should preserve agent pressure profile");
    Require(alphaAvatar.Locked, "world avatars should preserve agent lock state");
    Require(alphaAvatar.BubbleTurn == 4, "speech bubble should track the latest eligible turn");
    Require(alphaAvatar.BubbleText.Length <= 128, "speech bubble text should be bounded for overlay fit");
    Require(alphaAvatar.BubbleText.EndsWith("...", StringComparison.Ordinal), "long speech bubbles should be trimmed with ellipsis text");
    Require(!alphaAvatar.BubbleText.Contains('\n'), "speech bubble text should collapse whitespace");
    Require(!betaAvatar.Speaking, "non-latest agents should not keep stale speaking bubbles");
    Require(!noBubbleWorld.Avatars.Any(item => item.Speaking), "max bubble count zero should suppress speaker bubbles");
    Require(narratorWorld.Avatars.Count == 3, "world layout should include narrator when narrator has transcript activity");
    Require(narratorAvatar.Speaking, "latest narrator narration should get a speaker bubble");
    Require(narratorAvatar.Model == "narrator-model", "narrator world avatar should preserve the narrator model");
    Require(narratorAvatar.AccentColor == "#D185CE", "narrator world avatar should preserve the narrator accent");
    Require(narratorAvatar.VoiceStyle == "ringside_commentary", "narrator world avatar should preserve the narrator voice style");
    Require(narratorAvatar.Locked, "narrator world avatar should preserve the narrator lock state");
    Require(narratorAvatar.LastMessageTurn == 5 && narratorAvatar.TotalTokens == 15, "narrator world avatar should expose narration telemetry");
    Require(!narratorNoBubbleWorld.Avatars.Any(item => item.Speaking), "max bubble count zero should suppress narrator bubbles too");
    Require(thinkingBetaAvatar.Speaking, "working agents should take active speaker focus while generation is in flight");
    Require(thinkingBetaAvatar.BubbleText == "Thinking...", "thinking speakers should show a live thinking placeholder instead of repeating stale chat text");
    Require(!thinkingWorld.Avatars.Single(item => item.Id == "alpha").Speaking, "working speaker focus should replace the last completed speaker");
    Require(!staleAlphaAvatar.HasToolActivity, "old tool activity should decay out of AI World beacons");
    Require(!staleBetaAvatar.HasInternetActivity, "old source activity should decay out of AI World beacons");
    Require(Math.Abs(alphaAvatar.Z - betaAvatar.Z) > 5, "world layout should give agents room to walk around");
    Require(alphaAvatar.Thinking, "thinking status should create a world event");
    Require(alphaAvatar.HasError, "error status should create a warning event");
    Require(alphaAvatar.HasToolActivity, "tool-request activity should create a world beacon");
    Require(betaAvatar.HasInternetActivity, "source/web activity should create a web beacon");
    Require(alphaAvatar.LastMessageTurn == 4, "inspector should use the latest agent-authored message");
    Require(alphaAvatar.TotalTokens == 21, "inspector token totals should come from the latest agent message");
    Require(alphaAvatar.PrivateNotesSummary.Contains("watch premise", StringComparison.OrdinalIgnoreCase), "private notes should summarize into the world avatar");
    Require(alphaAvatar.PublicNotesSummary.Contains("Public synthesis", StringComparison.OrdinalIgnoreCase), "public summary should summarize into the world avatar");
    Require(alphaAvatar.X == repeated.Avatars.Single(item => item.Id == "alpha").X, "world positions should be deterministic");
    Require(alphaAvatar.MotionPhase == repeated.Avatars.Single(item => item.Id == "alpha").MotionPhase, "animation phase should be deterministic");
}

static void AgentWorldPulseSummarizesLiveTelemetry()
{
    var alpha = new AgentState("alpha", "Alpha", "thinking error", "Lead analyst", "plain_language", "chaos", "#35D6FF", "alpha-model", true, true, ["watch premise quality"]);
    var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "shared-model",
        providerLastError: "",
        turnIndex: 0,
        [
            TranscriptForTest(1, "Tool", "internet", "internet", "ok") with { Text = "Old tool work should not stay lit just because scheduler index is low.", InternetRequester = "alpha", InternetTool = "web_search" },
            TranscriptForTest(27, "Beta", "beta", "message", "ok") with { Text = "Beta left an earlier critique.", PromptTokens = 60, CompletionTokens = 40, TotalTokens = 100 },
            TranscriptForTest(28, "Tool", "internet", "internet", "ok") with { Text = "Recent beta tool work should stay lit.", InternetRequester = "beta", InternetTool = "web_fetch" },
            TranscriptForTest(29, "Source", "system", "source", "ok") with { Text = "Recent beta source activity should stay lit.", InternetRequester = "beta", InternetSources = ["https://example.test/source"] },
            TranscriptForTest(30, "Alpha", "alpha", "message", "ok") with { Text = "Alpha is now thinking through the latest turn.", PromptTokens = 1200, CompletionTokens = 345, TotalTokens = 1545 }
        ],
        [alpha, beta]);

    var world = AgentWorldLayout.Build(snapshot);
    var pulse = world.Pulse;
    var alphaAvatar = world.Avatars.Single(item => item.Id == "alpha");
    var betaAvatar = world.Avatars.Single(item => item.Id == "beta");

    Require(world.TurnIndex == 0, "world should preserve scheduler turn index separately from transcript chronology");
    Require(pulse.ActiveCount == 2, "world pulse should count active avatars");
    Require(pulse.ThinkingCount == 1, "world pulse should count thinking avatars");
    Require(pulse.AlertCount == 1, "world pulse should count alert avatars");
    Require(pulse.LockedCount == 1, "world pulse should count locked avatars");
    Require(pulse.ToolActivityCount == 1, "world pulse should count only recent tool activity");
    Require(pulse.InternetActivityCount == 1, "world pulse should count recent source activity");
    Require(pulse.SpeakingCount == 1 && pulse.SpeakerName == "Alpha" && pulse.SpeakerTurn == 30, "world pulse should identify the live speaker and turn");
    Require(pulse.LatestTurn == 30, "world pulse should use latest transcript turn as chronology");
    Require(pulse.LatestPromptTokens == 1260 && pulse.LatestCompletionTokens == 385 && pulse.LatestTotalTokens == 1645, "world pulse should aggregate latest per-agent token telemetry");
    Require(!alphaAvatar.HasToolActivity, "old alpha tool activity should decay by latest transcript turn, not scheduler index");
    Require(betaAvatar.HasToolActivity && betaAvatar.HasInternetActivity, "recent beta activity should remain visible in the pulse window");
}

static void AgentWorldCueRibbonSummarizesLiveEvents()
{
    var alpha = new AgentState("alpha", "Alpha", "thinking error", "Lead analyst", "plain_language", "chaos", "#35D6FF", "alpha-model", true, true, ["watch premise quality"]);
    var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "shared-model",
        providerLastError: "",
        turnIndex: 2,
        [
            TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Alpha opens with a live cue.", PromptTokens = 80, CompletionTokens = 20, TotalTokens = 100 },
            TranscriptForTest(2, "Tool", "internet", "internet", "ok") with { Text = "Tool work should become a world cue.", InternetRequester = "alpha", InternetTool = "web_search" },
            TranscriptForTest(3, "Source", "system", "source", "ok") with { Text = "Source work should become a source cue.", InternetRequester = "beta", InternetSources = ["https://example.test/source"] }
        ],
        [alpha, beta])
        with
        {
            Summary = "Shared public state."
        };

    var world = AgentWorldLayout.Build(snapshot);
    var cueLabels = world.Cues.Select(cue => cue.Label).ToArray();
    var cueDetails = world.Cues.Select(cue => cue.Detail).ToArray();

    Require(cueLabels.SequenceEqual(["Speaker", "Alert", "Thinking", "Tool", "Sources", "Locks", "Memory", "Style", "Tokens"]), "world cues should be ordered by operator urgency");
    Require(cueDetails.Any(detail => detail.Contains("Alpha turn 1", StringComparison.Ordinal)), "speaker cue should name speaker and turn");
    Require(cueDetails.Any(detail => detail.Contains("needs review", StringComparison.OrdinalIgnoreCase)), "alert cue should explain review need");
    Require(cueDetails.Any(detail => detail.Contains("recent tool", StringComparison.OrdinalIgnoreCase)), "tool cue should summarize tool activity");
    Require(cueDetails.Any(detail => detail.Contains("source/web", StringComparison.OrdinalIgnoreCase)), "source cue should summarize source/web activity");
    Require(cueDetails.Any(detail => detail.Contains("private notes", StringComparison.OrdinalIgnoreCase)), "memory cue should summarize private notes");
    Require(cueDetails.Any(detail => detail.Contains("voice/pressure", StringComparison.OrdinalIgnoreCase)), "style cue should summarize voice and pressure state");
    Require(cueDetails.Any(detail => detail.Contains("latest load", StringComparison.OrdinalIgnoreCase)), "token cue should summarize latest load");
    Require(world.Cues.Any(cue => cue.Severity == "alert"), "cue model should preserve alert severity");
    Require(world.Cues.Any(cue => cue.Severity == "active"), "cue model should preserve active severity");
    Require(world.Cues.Any(cue => cue.Severity == "signal"), "cue model should preserve signal severity");

    RunStaTest(() =>
    {
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };
        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        var cueTexts = control.DebugWorldCueTexts.ToArray();
        var cueAutomationNames = control.DebugWorldCueAutomationNames.ToArray();
        var cueMaxWidth = control.DebugWorldCuePanelMaxWidth;

        Require(control.DebugWorldCuePanelVisible, "world cue ribbon should be visible for active worlds");
        Require(control.DebugWorldCueCount == world.Cues.Count, "world cue ribbon should render one chip per world cue");
        Require(cueTexts.Any(text => text.Contains("SPEAKER", StringComparison.Ordinal) && text.Contains("Alpha turn 1", StringComparison.Ordinal)), "world cue ribbon should show speaker cue text");
        Require(cueTexts.Any(text => text.Contains("ALERT", StringComparison.Ordinal) && text.Contains("needs review", StringComparison.OrdinalIgnoreCase)), "world cue ribbon should show alert cue text");
        Require(cueAutomationNames.Any(text => text.Contains("World cue Tool", StringComparison.Ordinal)), "world cue chips should expose automation names");
        Require(cueAutomationNames.Any(text => text.Contains("World cue Tokens", StringComparison.Ordinal)), "world cue chips should expose token cue automation names");
        Require(cueMaxWidth <= 520 && cueMaxWidth > 300, "wide world cue panel should clamp to a readable width");

        control.Width = 360;
        control.Measure(new Size(360, 420));
        control.Arrange(new Rect(0, 0, 360, 420));
        control.UpdateLayout();
        Require(control.DebugWorldCuePanelMaxWidth <= 328, "narrow world cue panel should fit inside pane margins");

        var empty = SnapshotForOverviewTest(true, "shared-model", "", 0, [], []);
        control.ApplySnapshot(empty);
        Require(!control.DebugWorldCuePanelVisible, "empty world snapshots should hide the cue ribbon");
        Require(control.DebugWorldCueCount == 0, "empty world snapshots should clear cue chips");
    });
}

static void AgentWorld3DControlRendersSnapshot()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "plain_language", "chaos", "not-a-color", "alpha-model", true, true, ["needs counterexample"]);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [
                TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "I think the stronger play is to test the claim directly.", InternetTool = "web_fetch", PromptTokens = 80, CompletionTokens = 20, TotalTokens = 100 },
                TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "Challenge accepted. I will pressure the weak premise.", InternetSources = ["https://example.test/beta"], PromptTokens = 50, CompletionTokens = 18, TotalTokens = 68 }
            ],
            [alpha, beta])
            with
            {
                Summary = "Shared public state."
            };
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        var initialNameTagTexts = control.DebugAgentNameTagTexts.ToArray();
        var initialNameTagAutomationNames = control.DebugAgentNameTagAutomationNames.ToArray();
        var initialNameTagAutomationHelpTexts = control.DebugAgentNameTagAutomationHelpTexts.ToArray();
        var initialBubbleAutomationNames = control.DebugSpeakerBubbleAutomationNames.ToArray();
        var initialBubbleAutomationHelpTexts = control.DebugSpeakerBubbleAutomationHelpTexts.ToArray();
        var initialBubbleTexts = control.DebugSpeakerBubbleTexts.ToArray();
        var initialBubbleAutomationStatuses = control.DebugSpeakerBubbleAutomationStatuses.ToArray();
        var initialBubbleBorderThicknesses = control.DebugSpeakerBubbleBorderThicknesses.ToArray();
        var nameTagEnterHandled = control.DebugActivateNameTag("beta", Key.Enter);
        var selectedByNameTagEnter = control.DebugSelectedAgentId;
        var bubbleSpaceHandled = control.DebugActivateBubble("alpha", Key.Space);
        var selectedByBubbleSpace = control.DebugSelectedAgentId;
        control.DebugSelectAgent("alpha");
        var projectedClickTargetId = control.DebugAgentIds[0];
        control.DebugClickProjectedAgent(0);
        var projectedClickSelectedId = control.DebugSelectedAgentId;
        control.DebugSelectAgent("alpha");
        var initialPositions = control.DebugAgentPositions.ToArray();
        var minimumObservedSeparation = control.DebugMinimumAgentSeparation;
        var initialYaw = control.DebugCameraYaw;
        var initialDistance = control.DebugCameraDistance;
        var initialMiniMapFrameElements = control.DebugMiniMapFrameElements.ToArray();
        var initialMiniMapMarkerElements = control.DebugMiniMapMarkerElements.ToArray();
        var initialMiniMapCenters = control.DebugMiniMapMarkerCenters;

        var bitmap = new RenderTargetBitmap(720, 460, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var pixels = new byte[720 * 460 * 4];
        bitmap.CopyPixels(pixels, 720 * 4, 0);
        var paintedBytes = pixels.Count(value => value != 0);
        var initialBubbleAnchorDistance = control.DebugSpeakerBubbleAnchorDistances.DefaultIfEmpty(double.MaxValue).Min();
        var peakSpeakingJump = 0.0;
        var peakGestureAngle = 0.0;
        var peakLegGestureAngle = 0.0;
        for (var index = 0; index < 14; index++)
        {
            control.DebugAdvanceWorld(0.11);
            minimumObservedSeparation = Math.Min(minimumObservedSeparation, control.DebugMinimumAgentSeparation);
            peakSpeakingJump = Math.Max(peakSpeakingJump, control.DebugSpeakingJumpOffsets.DefaultIfEmpty(0).Max());
            peakGestureAngle = Math.Max(peakGestureAngle, control.DebugSpeakingArmGestureAngles.DefaultIfEmpty(0).Max());
            peakLegGestureAngle = Math.Max(peakLegGestureAngle, control.DebugSpeakingLegGestureAngles.DefaultIfEmpty(0).Max());
        }

        var animatedMiniMapFrameElements = control.DebugMiniMapFrameElements.ToArray();
        var animatedMiniMapMarkerElements = control.DebugMiniMapMarkerElements.ToArray();
        var animatedBubbleAnchorDistance = control.DebugSpeakerBubbleAnchorDistances.DefaultIfEmpty(double.MaxValue).Min();

        control.DebugAdvanceWorld(7.5);
        minimumObservedSeparation = Math.Min(minimumObservedSeparation, control.DebugMinimumAgentSeparation);
        var movedPositions = control.DebugAgentPositions.ToArray();
        var focusedSpeakerPosition = movedPositions[0];
        var listenerPosition = movedPositions[1];
        var speakerFacingAngle = control.DebugAgentFacingAngles[0];
        var listenerFacingAngle = control.DebugAgentFacingAngles[1];
        var expectedSpeakerFacingAngle = Math.Atan2(-focusedSpeakerPosition.X, -focusedSpeakerPosition.Y) * 180 / Math.PI;
        var expectedListenerFacingAngle = Math.Atan2(focusedSpeakerPosition.X - listenerPosition.X, focusedSpeakerPosition.Y - listenerPosition.Y) * 180 / Math.PI;
        var speakerFacingDelta = AngleDeltaDegrees(speakerFacingAngle, expectedSpeakerFacingAngle);
        var listenerFacingDelta = AngleDeltaDegrees(listenerFacingAngle, expectedListenerFacingAngle);
        var followedDistance = DistanceCoordinates2D(control.DebugCameraTarget.X, control.DebugCameraTarget.Z, focusedSpeakerPosition.X, focusedSpeakerPosition.Y);
        var animatedMiniMapCenters = control.DebugMiniMapMarkerCenters;
        var betaMapPoint = animatedMiniMapCenters["beta"];
        control.DebugClickMiniMap(betaMapPoint.X + 1, betaMapPoint.Y + 1);
        var miniMapSelectedId = control.DebugSelectedAgentId;
        var miniMapFocusDistance = DistanceCoordinates2D(control.DebugCameraTarget.X, control.DebugCameraTarget.Z, movedPositions[1].X, movedPositions[1].Y);
        control.DebugSelectAgent("beta");
        var selectedBetaFocusDistance = DistanceCoordinates2D(control.DebugCameraTarget.X, control.DebugCameraTarget.Z, movedPositions[1].X, movedPositions[1].Y);
        var selectedAttentionHaloIds = control.DebugVisibleAttentionHaloIds.ToArray();
        var selectedAttentionHaloAnchorDistance = control.DebugAttentionHaloAnchorDistances.DefaultIfEmpty(double.MaxValue).Max();
        control.DebugSelectAgent("alpha");
        var selectedMiniMapMarkerElements = control.DebugMiniMapMarkerElements.ToArray();
        control.DebugOrbitCamera(60, -25);
        var orbitedBubbleAnchorDistance = control.DebugSpeakerBubbleAnchorDistances.DefaultIfEmpty(double.MaxValue).Min();
        var orbitedAttentionHaloAnchorDistance = control.DebugAttentionHaloAnchorDistances.DefaultIfEmpty(double.MaxValue).Max();
        var beforePanTarget = control.DebugCameraTarget;
        control.DebugPanCamera(45, -30);
        var pannedTarget = control.DebugCameraTarget;
        control.DebugZoomCamera(120);
        var zoomDistance = control.DebugCameraDistance;
        var controlPanelWheelIgnored = control.DebugWheelOverControlPanelLeavesCameraDistance(-120);
        var worldWheelZoomed = control.DebugWheelOverWorldChangesCameraDistance(120);
        var preOverviewDistance = control.DebugCameraDistance;
        var preOverviewPitch = control.DebugCameraPitch;
        control.DebugSetCameraMode("overview");
        var overviewMode = control.DebugCameraMode;
        var overviewDistance = control.DebugCameraDistance;
        control.DebugSetCameraMode("follow");
        var restoredFollowDistance = control.DebugCameraDistance;
        var restoredFollowPitch = control.DebugCameraPitch;
        control.DebugSetCinematicAutoCamera(true);
        var inspectorVisibleBeforeDismiss = control.DebugInspectorVisible;
        var inspectorNameBeforeDismiss = control.DebugInspectorName;
        var inspectorModelBeforeDismiss = control.DebugInspectorModel;
        var inspectorRoleBeforeDismiss = control.DebugInspectorRole;
        var inspectorLastMessageBeforeDismiss = control.DebugInspectorLastMessage;
        var inspectorNotesBeforeDismiss = control.DebugInspectorNotes;
        var inspectorEventCountBeforeDismiss = control.DebugInspectorEventCount;
        var inspectorEventsBeforeDismiss = control.DebugInspectorEventTexts.ToArray();
        control.DebugDismissInspector();
        var inspectorHiddenAfterDismiss = !control.DebugInspectorVisible;
        var selectedIdAfterDismiss = control.DebugSelectedAgentId;
        control.ApplySnapshot(snapshot);
        control.DebugAdvanceWorld(0.15);
        var inspectorHiddenAfterRefresh = !control.DebugInspectorVisible;
        var selectedIdAfterRefresh = control.DebugSelectedAgentId;
        control.DebugSelectAgent("beta");
        var inspectorReopenedAfterExplicitSelection = control.DebugInspectorVisible;
        var inspectorNameAfterExplicitSelection = control.DebugInspectorName;

        Require(control.DebugAvatarVisualCount == 2, "3D world should create one visual per active agent");
        Require(control.DebugWorldStatusText.Contains("Alpha speaking", StringComparison.OrdinalIgnoreCase), "3D world status should name the active speaker");
        Require(control.DebugWorldStatusText.Contains("watching", StringComparison.OrdinalIgnoreCase), "3D world status should summarize listener focus");
        Require(control.DebugWorldStatusText.Contains("next slot 1", StringComparison.OrdinalIgnoreCase), "3D world status should label scheduler position as next slot");
        Require(control.DebugWorldStatusText.Contains("latest turn 2", StringComparison.OrdinalIgnoreCase), "3D world status should include latest transcript turn");
        Require(control.DebugWorldStatusText.Contains("1 thinking", StringComparison.OrdinalIgnoreCase), "3D world status should summarize thinking avatars");
        Require(control.DebugWorldStatusText.Contains("1 tool", StringComparison.OrdinalIgnoreCase), "3D world status should summarize recent tool activity");
        Require(control.DebugWorldStatusText.Contains("1 web", StringComparison.OrdinalIgnoreCase), "3D world status should summarize recent source activity");
        Require(control.DebugWorldStatusText.Contains("1 locked", StringComparison.OrdinalIgnoreCase), "3D world status should summarize locked avatars");
        Require(control.DebugWorldStatusText.Contains("latest ~168 tok", StringComparison.OrdinalIgnoreCase), "3D world status should summarize latest token load");
        Require(control.DebugWorldPulseSummary.Contains("Alpha speaking turn 1", StringComparison.Ordinal), "3D world pulse summary should include speaker turn");
        Require(control.DebugWorldPulseSummary.Contains("latest turn 2", StringComparison.Ordinal), "3D world pulse summary should include latest transcript turn");
        Require(control.DebugAgentLegPartCount >= 16, "3D world robots should render visible leg geometry");
        Require(control.DebugAgentShadowPartCount == 2, "3D world robots should render floor contact shadows");
        Require(control.DebugSpeakerSpotlightPartCount == 8, "3D world should spotlight the active speaker with a stronger floor focus ring");
        Require(control.DebugLockedBadgePartCount >= 4, "3D world should render a visible lock badge for locked agents");
        Require(control.DebugVoicePressurePartCount >= 3, "3D world should render visible voice and pressure cues for styled agents");
        Require(control.DebugActivityPropPartCount >= 6, "3D world should render richer props for tool and source activity");
        Require(control.DebugGeometryModelCount > 0, "3D world should expose geometry for render diagnostics");
        Require(control.DebugWorldSceneryModelCount >= 70, "3D world should include arena scenery beyond robot geometry");
        Require(control.DebugUnfrozenGeometryMaterialCount == 0, "3D world geometry materials should be frozen for render-thread efficiency");
        Require(control.DebugDistinctGeometryMeshCount < control.DebugGeometryModelCount, "3D world should reuse repeated frozen box meshes");
        Require(control.DebugDistinctGeometryMaterialCount < control.DebugGeometryModelCount, "3D world should reuse repeated frozen materials");
        Require(initialNameTagTexts.Any(text => text.Contains("Alpha", StringComparison.Ordinal) && text.Contains("speaking", StringComparison.OrdinalIgnoreCase)), "3D world name tags should surface the active speaking state");
        Require(initialNameTagTexts.Any(text => text.Contains("Alpha", StringComparison.Ordinal) && text.Contains("locked", StringComparison.OrdinalIgnoreCase)), "3D world name tags should surface locked agent state");
        Require(initialNameTagTexts.Any(text => text.Contains("Beta", StringComparison.Ordinal) && text.Contains("web", StringComparison.OrdinalIgnoreCase)), "3D world name tags should surface compact event state");
        Require(initialNameTagAutomationNames.Any(text => text.Contains("Alpha", StringComparison.Ordinal) && text.Contains("speaking", StringComparison.OrdinalIgnoreCase)), "3D world name tags should expose automation names");
        Require(initialNameTagAutomationHelpTexts.All(text => text.Contains("Select and focus", StringComparison.Ordinal)), "3D world name tags should expose activation help text");
        Require(initialNameTagAutomationHelpTexts.Any(text => text.Contains("speaking turn 1", StringComparison.OrdinalIgnoreCase) && text.Contains("~100 tok", StringComparison.OrdinalIgnoreCase)), "3D world name tags should expose compact live telemetry");
        Require(initialBubbleAutomationNames.Any(text => text.Contains("Alpha speech bubble", StringComparison.Ordinal) && text.Contains("turn 1", StringComparison.OrdinalIgnoreCase)), "speech bubbles should expose the speaker and turn in automation names");
        Require(initialBubbleAutomationHelpTexts.Any(text => text.Contains("Thinking", StringComparison.OrdinalIgnoreCase)), "speech bubbles should expose the visible live bubble text as automation help");
        Require(initialBubbleTexts.Any(text => text.Contains("Alpha | turn 1", StringComparison.Ordinal) && text.Contains("Thinking", StringComparison.OrdinalIgnoreCase)), "speech bubbles should show a compact speaker and turn header");
        Require(initialBubbleAutomationStatuses.Any(text => text.Contains("speaker", StringComparison.OrdinalIgnoreCase)), "selected speaker bubbles should expose selected speaker status");
        Require(initialBubbleBorderThicknesses.DefaultIfEmpty(0).Max() >= 2, "selected speaker bubble should have stronger visual emphasis");
        Require(nameTagEnterHandled && selectedByNameTagEnter == "beta", "Enter should activate a focused 3D world name tag");
        Require(bubbleSpaceHandled && selectedByBubbleSpace == "alpha", "Space should activate a focused speech bubble and select its speaker");
        Require(control.DebugLegendItemCount == 2, "3D world HUD should list active agents");
        Require(control.DebugLegendTexts.Any(text => text.Contains("Alpha", StringComparison.Ordinal) && text.Contains("speaking turn 1", StringComparison.OrdinalIgnoreCase) && text.Contains("~100 tok", StringComparison.OrdinalIgnoreCase)), "3D world legend should show speaker turn and token load");
        Require(control.DebugLegendTexts.Any(text => text.Contains("Beta", StringComparison.Ordinal) && text.Contains("last turn 2", StringComparison.OrdinalIgnoreCase) && text.Contains("~68 tok", StringComparison.OrdinalIgnoreCase)), "3D world legend should show last turn and token load for listeners");
        Require(control.DebugMiniMapMarkerCount == 2, "3D world minimap should mark active agents");
        Require(initialMiniMapFrameElements.Length == 3, "3D world minimap should keep reusable frame geometry");
        Require(initialMiniMapCenters.Keys.OrderBy(key => key).SequenceEqual(["alpha", "beta"]), "3D world minimap should expose selectable marker centers");
        Require(initialMiniMapFrameElements.SequenceEqual(animatedMiniMapFrameElements), "3D world minimap should reuse static frame elements during animation");
        Require(initialMiniMapMarkerElements.SequenceEqual(animatedMiniMapMarkerElements), "3D world minimap should reuse marker elements during animation");
        Require(initialMiniMapMarkerElements.SequenceEqual(selectedMiniMapMarkerElements), "3D world minimap should reuse marker elements across selection changes");
        Require(projectedClickSelectedId == projectedClickTargetId, "projected world click should select the nearest agent");
        Require(control.DebugAgentNameTagUsesInteractiveOverlayGuard(0), "agent name-tag mouse down should not start world camera drag");
        Require(control.DebugAgentBubbleUsesInteractiveOverlayGuard(0), "speech bubble mouse down should not start world camera drag");
        Require(inspectorVisibleBeforeDismiss, "agent selection should show the inspector");
        Require(selectedAttentionHaloIds.Contains("alpha") && selectedAttentionHaloIds.Contains("beta"), "3D world should mark both the active speaker and selected non-speaker in-scene");
        Require(inspectorNameBeforeDismiss == "Alpha", "inspector should show selected agent identity");
        Require(inspectorModelBeforeDismiss == "alpha-model", "inspector should show the selected agent model");
        Require(inspectorRoleBeforeDismiss.Contains("thinking", StringComparison.OrdinalIgnoreCase), "inspector should show role and status");
        Require(inspectorRoleBeforeDismiss.Contains("Voice: plain language", StringComparison.OrdinalIgnoreCase), "inspector should show normalized voice style");
        Require(inspectorRoleBeforeDismiss.Contains("Pressure: chaos", StringComparison.OrdinalIgnoreCase), "inspector should show pressure profile");
        Require(inspectorRoleBeforeDismiss.Contains("Locked", StringComparison.OrdinalIgnoreCase), "inspector should show locked state");
        Require(inspectorLastMessageBeforeDismiss.Contains("[message / ok]", StringComparison.OrdinalIgnoreCase), "inspector should show last-message kind and status");
        Require(inspectorLastMessageBeforeDismiss.Contains("Tokens: 80 prompt / 20 completion / 100 total", StringComparison.OrdinalIgnoreCase), "inspector should show last message token usage");
        Require(inspectorNotesBeforeDismiss.Contains("needs counterexample", StringComparison.OrdinalIgnoreCase), "inspector should show private note summary");
        Require(inspectorNotesBeforeDismiss.Contains("Shared public", StringComparison.OrdinalIgnoreCase), "inspector should show public note summary");
        Require(inspectorEventCountBeforeDismiss >= 2, "inspector should show event chips for active world events");
        Require(inspectorEventsBeforeDismiss.Contains("LOCKED") && inspectorEventsBeforeDismiss.Contains("VOICE") && inspectorEventsBeforeDismiss.Contains("PRESSURE"), "inspector event chips should expose lock, voice, and pressure cues");
        Require(inspectorHiddenAfterDismiss, "closing the inspector should hide it");
        Require(selectedIdAfterDismiss == "alpha", "closing the inspector should keep the selected robot focus");
        Require(inspectorHiddenAfterRefresh, "closed inspector should not reopen on snapshot refresh");
        Require(selectedIdAfterRefresh == "alpha", "snapshot refresh should preserve selected robot while inspector is dismissed");
        Require(inspectorReopenedAfterExplicitSelection, "explicit agent selection should reopen a dismissed inspector");
        Require(inspectorNameAfterExplicitSelection == "Beta", "explicit selection should refresh the reopened inspector content");
        Require(!control.DebugIsAnimationRunning, "offscreen unloaded world should not run its animation timer");
        Require(paintedBytes > 12000, "3D world should render non-empty offscreen pixels");
        Require(initialBubbleAnchorDistance < 70, "speech bubble should be anchored near the speaking avatar");
        Require(animatedBubbleAnchorDistance < 70, "speech bubble should stay attached while the speaker moves");
        Require(orbitedBubbleAnchorDistance < 90, "speech bubble should stay attached after camera orbit");
        Require(selectedAttentionHaloAnchorDistance < 45, "attention halos should stay anchored to highlighted robots");
        Require(orbitedAttentionHaloAnchorDistance < 60, "attention halos should stay anchored after camera orbit");
        Require(peakSpeakingJump > 0.16, "speaking agents should visibly jump while talking");
        Require(peakGestureAngle > 42, "speaking agents should wave or gesture with their arms");
        Require(peakLegGestureAngle > 20, "speaking agents should move their legs with the walking gait");
        Require(speakerFacingDelta < 18, "speaking agents should gesture toward the arena focus instead of walking away");
        Require(listenerFacingDelta < 22, "listening agents should turn toward the active speaker");
        Require(minimumObservedSeparation >= 0.82, "3D world agents should keep collision spacing while walking");
        Require(Distance2D(initialPositions[0], movedPositions[0]) > 0.35 || Distance2D(initialPositions[1], movedPositions[1]) > 0.35, "3D world agents should walk through the larger arena over time");
        Require(followedDistance < 0.05, "3D world camera should follow the active speaking agent");
        Require(miniMapSelectedId == "beta", "clicking the minimap near an agent should select that agent");
        Require(miniMapFocusDistance < 0.05, "minimap selection should focus the camera on the selected agent");
        Require(selectedBetaFocusDistance < 0.05, "selecting a non-speaker should focus the camera on that selected agent");
        Require(Math.Abs(control.DebugCameraYaw - initialYaw) > 0.1, "mouse orbit should rotate the camera");
        Require(DistanceCoordinates2D(beforePanTarget.X, beforePanTarget.Z, pannedTarget.X, pannedTarget.Z) > 0.05, "mouse pan should immediately update the camera target");
        Require(zoomDistance < initialDistance, "mouse wheel zoom should move the camera closer");
        Require(controlPanelWheelIgnored, "mouse wheel over the world HUD should not zoom the camera");
        Require(worldWheelZoomed, "mouse wheel over the world scene should still zoom the camera");
        Require(overviewMode == "Overview", "overview camera mode should be selectable");
        Require(overviewDistance >= 20, "overview camera should pull back to show the world");
        Require(Math.Abs(restoredFollowDistance - preOverviewDistance) < 0.001, "leaving overview should restore the prior follow camera distance");
        Require(Math.Abs(restoredFollowPitch - preOverviewPitch) < 0.001, "leaving overview should restore the prior follow camera pitch");
        Require(control.DebugCinematicAutoCamera, "cinematic auto-camera toggle should be stored");
    });
}

static void AgentWorld3DControlRendersNarratorIdentityCues()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "waiting", "Listens for the recap.", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 3,
            [
                TranscriptForTest(3, "Narrator", "narrator", "narration", "ok") with { Text = "Narrator cuts in with the arena recap." }
            ],
            [alpha])
            with
            {
                NarratorModel = "narrator-model",
                NarratorPersona = "Calls the match with crisp color commentary.",
                NarratorVoiceStyle = "ringside_commentary",
                NarratorAccentColor = "#D185CE",
                NarratorLocked = true
            };
        var control = new AgentWorld3DControl
        {
            Width = 640,
            Height = 420
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(640, 420));
        control.Arrange(new Rect(0, 0, 640, 420));
        control.UpdateLayout();
        control.DebugSelectAgent("narrator");
        var inspectorEvents = control.DebugInspectorEventTexts.ToArray();
        var bubbleTexts = control.DebugSpeakerBubbleTexts.ToArray();
        var nameTagTexts = control.DebugAgentNameTagTexts.ToArray();

        Require(control.DebugAvatarVisualCount == 2, "narrator world should render narrator alongside active agents");
        Require(control.DebugNarratorIdentityPartCount >= 5, "3D world should render a distinct narrator booth identity");
        Require(control.DebugLockedBadgePartCount >= 4, "locked narrator should render a lock badge");
        Require(control.DebugVoicePressurePartCount >= 1, "styled narrator voice should render a visible voice cue");
        Require(nameTagTexts.Any(text => text.Contains("Narrator", StringComparison.Ordinal) && text.Contains("locked", StringComparison.OrdinalIgnoreCase)), "narrator name tag should surface locked state");
        Require(control.DebugInspectorRole.Contains("Voice: ringside commentary", StringComparison.OrdinalIgnoreCase), "narrator inspector should show voice style");
        Require(control.DebugInspectorRole.Contains("Locked", StringComparison.OrdinalIgnoreCase), "narrator inspector should show lock state");
        Require(inspectorEvents.Contains("NARRATOR") && inspectorEvents.Contains("LOCKED") && inspectorEvents.Contains("VOICE"), "narrator inspector chips should expose identity, lock, and voice cues");
        Require(bubbleTexts.Any(text => text.Contains("Narrator | turn 3", StringComparison.Ordinal) && text.Contains("arena recap", StringComparison.OrdinalIgnoreCase)), "narrator bubble should show speaker and turn header");
    });
}

static void AgentWorldRespectsReducedMotionAndReusesFrameState()
{
    var animated = AgentWorld3DControl.ResolveRenderPolicy(
        isLoaded: true,
        isVisible: true,
        hasAvatars: true,
        animationsEnabled: true);
    var reduced = AgentWorld3DControl.ResolveRenderPolicy(
        isLoaded: true,
        isVisible: true,
        hasAvatars: true,
        animationsEnabled: false);
    var hidden = AgentWorld3DControl.ResolveRenderPolicy(
        isLoaded: true,
        isVisible: false,
        hasAvatars: true,
        animationsEnabled: true);

    Require(animated.RunContinuousAnimation && !animated.RenderStableFrame, "animation-enabled worlds should retain the full-rate motion loop");
    Require(!reduced.RunContinuousAnimation && reduced.RenderStableFrame, "reduced-motion worlds should render one stable frame without a continuous timer");
    Require(!hidden.RunContinuousAnimation && !hidden.RenderStableFrame, "hidden worlds should spend no work on animated or stable frames");
    Require(SystemMotionPreferences.IsAnimationPreferenceChange(nameof(SystemParameters.ClientAreaAnimation)), "Windows client-area animation changes should refresh the world render policy");
    Require(!SystemMotionPreferences.IsAnimationPreferenceChange(nameof(SystemParameters.MenuDropAlignment)), "unrelated Windows parameter changes should not rebuild the world motion policy");

    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Reduced motion should hold a readable speaker pose." }],
            [alpha, beta]);
        var control = new AgentWorld3DControl(() => false)
        {
            Width = 640,
            Height = 420
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(640, 420));
        control.Arrange(new Rect(0, 0, 640, 420));
        control.UpdateLayout();
        control.DebugRenderStableWorldState();
        var stablePositions = control.DebugAgentPositions.ToArray();
        var stableJumpOffsets = control.DebugSpeakingJumpOffsets.ToArray();
        var stableGestureAngles = control.DebugSpeakingArmGestureAngles.ToArray();
        var bufferResizeCount = control.DebugAnimationBufferResizeCount;
        var markers = control.DebugMiniMapMarkerElements
            .Cast<System.Windows.Shapes.Ellipse>()
            .ToArray();
        var markerFills = markers.Select(marker => marker.Fill).ToArray();
        var markerStrokes = markers.Select(marker => marker.Stroke).ToArray();

        for (var index = 0; index < 6; index++)
        {
            control.DebugAdvanceWorld(0.05);
        }

        var resizeCountAfterFrames = control.DebugAnimationBufferResizeCount;
        var markersAfterFrames = control.DebugMiniMapMarkerElements
            .Cast<System.Windows.Shapes.Ellipse>()
            .ToArray();
        control.DebugRenderStableWorldState();
        var repeatedStablePositions = control.DebugAgentPositions.ToArray();

        Require(control.DebugReducedMotion, "injected animation-disabled preference should activate reduced motion");
        Require(!control.DebugIsAnimationRunning, "offscreen reduced-motion worlds should not run the animation timer");
        Require(control.DebugAnimationBufferCapacity == 2, "animation buffers should size once to the active roster");
        Require(bufferResizeCount == resizeCountAfterFrames, "animation frames should reuse position and phase buffers instead of allocating three arrays per tick");
        Require(stablePositions.SequenceEqual(repeatedStablePositions), "reduced-motion rendering should settle into a deterministic stable layout");
        Require(stableJumpOffsets.All(offset => Math.Abs(offset) < 0.001), "reduced-motion speakers should not bob or jump");
        Require(stableGestureAngles.Any(angle => angle > 40), "reduced-motion speakers should keep an expressive but stationary pose");
        Require(markers.Length == markersAfterFrames.Length && markers.Length == 2, "minimap should retain one marker per active agent");
        for (var index = 0; index < markers.Length; index++)
        {
            Require(ReferenceEquals(markerFills[index], markersAfterFrames[index].Fill), "unchanged minimap marker fills should be reused across frames");
            Require(ReferenceEquals(markerStrokes[index], markersAfterFrames[index].Stroke), "unchanged minimap marker strokes should be reused across frames");
        }
    });
}

static void AgentWorldSkipsRebuildForUnchangedSnapshots()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "speaking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Opening argument." }],
            [alpha, beta]);
        var control = new AgentWorld3DControl { Width = 640, Height = 420 };
        control.ApplySnapshot(snapshot);
        control.Measure(new Size(640, 420));
        control.Arrange(new Rect(0, 0, 640, 420));
        control.UpdateLayout();

        var rebuildsAfterFirst = control.DebugSceneRebuildCount;
        control.ApplySnapshot(snapshot);
        control.ApplySnapshot(snapshot);
        var rebuildsAfterIdenticalReapplies = control.DebugSceneRebuildCount;

        var betaSpeaking = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 2,
            [TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "Rebuttal." }],
            [alpha with { Status = "waiting" }, beta with { Status = "speaking" }]);
        control.ApplySnapshot(betaSpeaking);
        var rebuildsAfterChange = control.DebugSceneRebuildCount;

        Require(rebuildsAfterFirst >= 1, "first snapshot should build the scene");
        Require(rebuildsAfterIdenticalReapplies == rebuildsAfterFirst, "identical snapshot re-applies should skip the scene rebuild");
        Require(rebuildsAfterChange == rebuildsAfterFirst + 1, "a changed snapshot should rebuild the scene exactly once");
    });
}

static void AgentWorldEmptySnapshotResetsTransientWorldState()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var active = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 3,
            [
                TranscriptForTest(3, "Alpha", "alpha", "message", "ok") with { Text = "Keep the camera honest before the arena empties." }
            ],
            [alpha, beta]);
        var empty = SnapshotForOverviewTest(true, "shared-model", "", 0, [], []);
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(active);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        control.DebugSelectAgent("beta");
        control.DebugPressWorldKey(Key.Right);
        control.DebugPressWorldKey(Key.OemPlus);
        control.DebugClickMiniMap(150, 92);
        var modeBeforeEmpty = control.DebugCameraMode;
        var targetBeforeEmpty = control.DebugCameraTarget;
        var yawBeforeEmpty = control.DebugCameraYaw;
        var distanceBeforeEmpty = control.DebugCameraDistance;
        var selectedBeforeEmpty = control.DebugSelectedAgentId;
        var miniMapVisibleBeforeEmpty = control.DebugMiniMapVisible;

        control.ApplySnapshot(empty);
        control.DebugAdvanceWorld(0.1);

        Require(selectedBeforeEmpty == "beta", "test should begin from a manually selected agent");
        Require(modeBeforeEmpty == "Free", "test should dirty the camera through free minimap focus");
        Require(Math.Abs(yawBeforeEmpty) > 0.01, "test should dirty the camera yaw before clearing the world");
        Require(Math.Abs(distanceBeforeEmpty - 12.2) > 0.01, "test should dirty the camera distance before clearing the world");
        Require(DistanceCoordinates2D(targetBeforeEmpty.X, targetBeforeEmpty.Z, 0, 0) > 0.1, "test should dirty the camera target before clearing the world");
        Require(miniMapVisibleBeforeEmpty, "test should begin with a visible minimap");
        Require(control.DebugEmptyStateVisible, "empty world snapshots should show the empty state");
        Require(!control.DebugMiniMapVisible, "empty world snapshots should hide the minimap");
        Require(!control.DebugLegendVisible, "empty world snapshots should hide the legend");
        Require(!control.DebugInspectorVisible, "empty world snapshots should hide the inspector");
        Require(!control.DebugMiniMapCameraTargetVisible, "empty world snapshots should hide the minimap camera target");
        Require(control.DebugSelectedAgentId == "", "empty world snapshots should clear selected agents");
        Require(control.DebugCameraMode == "FollowSpeaker", "empty world snapshots should reset camera mode");
        Require(Math.Abs(control.DebugCameraYaw) < 0.001, "empty world snapshots should reset camera yaw");
        Require(Math.Abs(control.DebugCameraDistance - 12.2) < 0.001, "empty world snapshots should reset camera distance");
        Require(Math.Abs(control.DebugCameraPitch - 0.56) < 0.001, "empty world snapshots should reset camera pitch");
        Require(DistanceCoordinates2D(control.DebugCameraTarget.X, control.DebugCameraTarget.Z, 0, 0) < 0.001, "empty world snapshots should reset camera target");
    });
}

static void AgentWorldMiniMapMarkersExposeAutomationLabels()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Evidence mapper", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 2,
            [
                TranscriptForTest(2, "Alpha", "alpha", "message", "ok") with { Text = "Name the dots so the world is not visual-only." }
            ],
            [alpha, beta]);
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        var markersById = control.DebugMiniMapMarkerElements
            .OfType<FrameworkElement>()
            .ToDictionary(marker => marker.Tag?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

        Require(markersById.Count == 2, "minimap should expose one marker element per active agent");
        Require(AutomationProperties.GetName(markersById["alpha"]).Contains("Alpha", StringComparison.Ordinal), "alpha minimap marker should expose its agent name");
        Require(AutomationProperties.GetName(markersById["beta"]).Contains("Beta", StringComparison.Ordinal), "beta minimap marker should expose its agent name");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(markersById["alpha"])), "minimap marker should expose action help text");
        Require(markersById["alpha"].Focusable && markersById["beta"].Focusable, "minimap markers should be keyboard focusable");

        control.DebugSelectAgent("beta");

        Require(AutomationProperties.GetName(markersById["beta"]).Contains("selected", StringComparison.OrdinalIgnoreCase), "selected minimap marker name should expose selected state");
        Require(AutomationProperties.GetItemStatus(markersById["beta"]) == "selected", "selected minimap marker should expose selected item status");
        Require(AutomationProperties.GetItemStatus(markersById["alpha"]).Length > 0, "unselected minimap marker should expose an item status");

        control.DebugSelectAgent("alpha");
        var enterHandled = control.DebugActivateMiniMapMarker("beta", Key.Enter);
        var selectedAfterEnter = control.DebugSelectedAgentId;
        control.DebugSelectAgent("alpha");
        var spaceHandled = control.DebugActivateMiniMapMarker("beta", Key.Space);
        var selectedAfterSpace = control.DebugSelectedAgentId;

        Require(enterHandled && selectedAfterEnter == "beta", "Enter should activate a focused minimap marker");
        Require(spaceHandled && selectedAfterSpace == "beta", "Space should activate a focused minimap marker");
    });
}

static void AgentWorldFollowCameraTracksSpeakerChangesUntilManualSelection()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var messages =
            new[]
            {
                TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Alpha opens the round." },
                TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "Beta takes the next turn." }
            };
        var alphaSpeaking = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            messages,
            [alpha, beta]);
        var betaSpeaking = alphaSpeaking with
        {
            Agents =
            [
                alpha with { Status = "waiting" },
                beta with { Status = "thinking" }
            ]
        };
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();

        control.ApplySnapshot(alphaSpeaking);
        control.DebugAdvanceWorld(0.2);
        var alphaPositions = control.DebugAgentPositions.ToArray();
        var alphaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "alpha");
        var alphaAutoFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            alphaPositions[alphaIndex].X,
            alphaPositions[alphaIndex].Y);

        control.ApplySnapshot(betaSpeaking);
        var betaPositions = control.DebugAgentPositions.ToArray();
        var betaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "beta");
        var betaHandoffDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            betaPositions[betaIndex].X,
            betaPositions[betaIndex].Y);
        control.DebugAdvanceWorld(0.2);
        for (var index = 0; index < 44; index++)
        {
            control.DebugAdvanceWorld(0.1);
        }

        betaPositions = control.DebugAgentPositions.ToArray();
        var betaAutoFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            betaPositions[betaIndex].X,
            betaPositions[betaIndex].Y);
        var betaAutoSelectedId = control.DebugSelectedAgentId;

        control.DebugSelectAgent("alpha");
        control.ApplySnapshot(betaSpeaking);
        control.DebugAdvanceWorld(0.2);
        var pinnedPositions = control.DebugAgentPositions.ToArray();
        alphaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "alpha");
        var pinnedAlphaFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            pinnedPositions[alphaIndex].X,
            pinnedPositions[alphaIndex].Y);
        var pinnedSelectedId = control.DebugSelectedAgentId;

        control.DebugReturnToSpeakerFollow();
        control.DebugAdvanceWorld(0.2);
        var returnedPositions = control.DebugAgentPositions.ToArray();
        betaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "beta");
        var returnedBetaFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            returnedPositions[betaIndex].X,
            returnedPositions[betaIndex].Y);
        var returnedSelectedId = control.DebugSelectedAgentId;

        Require(alphaIndex >= 0 && betaIndex >= 0, "test snapshots should expose alpha and beta world agents");
        Require(control.DebugCameraMode == "FollowSpeaker", "world should stay in follow mode during speaker handoff");
        Require(alphaAutoFocusDistance < 0.05, "follow camera should start on the current speaker");
        Require(betaHandoffDistance > 0.05, "same-session speaker handoff should ease instead of snapping immediately");
        Require(betaAutoFocusDistance < 0.2, "follow camera should settle onto the new speaker when no manual selection is pinned");
        Require(betaAutoSelectedId == "beta", "automatic selection should move to the new speaker");
        Require(pinnedSelectedId == "alpha", "manual selection should remain visible after speaker snapshot refresh");
        Require(pinnedAlphaFocusDistance < 0.05, "manual selection should pin the follow camera even when another agent is speaking");
        Require(returnedBetaFocusDistance < 0.05, "follow button should unpin manual selection and return to the current speaker");
        Require(returnedSelectedId == "beta", "follow button should reselect the current speaker after clearing a pin");
    });
}

static void AgentWorldResetsTransientStateOnSessionChange()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var firstSession = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Old match alpha speaks." }],
            [alpha, beta])
            with
            {
                SessionId = "session-a"
            };
        var nextSession = firstSession with
        {
            SessionId = "session-b",
            TurnIndex = 2,
            Agents =
            [
                alpha with { Status = "waiting" },
                beta with { Status = "thinking" }
            ],
            Messages =
            [
                TranscriptForTest(2, "Beta", "beta", "message", "ok") with { Text = "Fresh match beta speaks." }
            ]
        };
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();

        control.ApplySnapshot(firstSession);
        control.DebugSelectAgent("alpha");
        control.DebugDismissInspector();
        control.DebugPressWorldKey(Key.Right);
        var dismissedBeforeSessionChange = !control.DebugInspectorVisible;
        var modeBeforeSessionChange = control.DebugCameraMode;

        control.ApplySnapshot(nextSession);
        control.DebugAdvanceWorld(0.2);
        var positions = control.DebugAgentPositions.ToArray();
        var betaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "beta");
        var betaFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            positions[betaIndex].X,
            positions[betaIndex].Y);

        Require(dismissedBeforeSessionChange, "test should begin with a dismissed inspector");
        Require(modeBeforeSessionChange == "Free", "test should begin with a dirty free camera");
        Require(control.DebugSelectedAgentId == "beta", "new sessions should reset manual selection to the current speaker");
        Require(control.DebugInspectorVisible, "new sessions should reopen the inspector for the fresh selected speaker");
        Require(control.DebugCameraMode == "FollowSpeaker", "new sessions should reset the camera mode to follow speaker");
        Require(betaFocusDistance < 0.05, "new sessions should focus the camera on the fresh speaker");
    });
}

static void AgentWorld3DControlUsesExtendedAccentFallbacks()
{
    RunStaTest(() =>
    {
        var epsilon = new AgentState("epsilon", "Epsilon", "thinking", "Explorer", "default", "default", "", "epsilon-model", true, false, []);
        var zeta = new AgentState("zeta", "Zeta", "waiting", "Builder", "default", "default", "", "zeta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 0,
            [],
            [epsilon, zeta]);
        var control = new AgentWorld3DControl
        {
            Width = 480,
            Height = 320
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(480, 320));
        control.Arrange(new Rect(0, 0, 480, 320));
        control.UpdateLayout();

        var colors = control.DebugAgentAccentColors;
        Require(colors.Count == 2, "extended accent test should create two active avatars");
        Require(colors[0] != colors[1], "extended 3D world agents should receive distinct fallback accents");
        Require(!colors.All(color => color.Equals("#FF4DD4EF", StringComparison.OrdinalIgnoreCase)), "extended 3D accents should not fall back to alpha cyan");
    });
}

static void AgentWorldMaterialCacheIsBoundedWithoutInvalidatingLiveGeometry()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState(
            "alpha",
            "Alpha",
            "waiting",
            "Cache probe",
            "scientific",
            "default",
            "#102030",
            "alpha-model",
            true,
            false,
            []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [],
            [alpha]);
        var control = new AgentWorld3DControl();

        control.ApplySnapshot(snapshot);
        var retainedModel = control.DebugFirstAgentGeometryModel
            ?? throw new InvalidOperationException("material cache test requires agent geometry");
        var retainedMaterial = retainedModel.Material
            ?? throw new InvalidOperationException("material cache test requires a front material");
        var initialMeshCounts = AgentWorld3DControl.DebugMeshCacheCounts;

        Require(AgentWorld3DControl.DebugMaterialCacheContains(retainedMaterial), "fresh agent material should begin in the shared cache");
        for (var index = 2; index <= 100; index++)
        {
            var rgb = (0x102030 + (index * 104729)) & 0xFFFFFF;
            control.ApplySnapshot(snapshot with
            {
                TurnIndex = index,
                Agents = [alpha with { AccentColor = $"#{rgb:X6}" }]
            });
            Require(
                AgentWorld3DControl.DebugMaterialCacheCount <= AgentWorld3DControl.DebugMaterialCacheCapacity,
                "material cache should never exceed its deterministic capacity");
        }

        var finalMeshCounts = AgentWorld3DControl.DebugMeshCacheCounts;
        Require(
            AgentWorld3DControl.DebugMaterialCacheCount == AgentWorld3DControl.DebugMaterialCacheCapacity,
            "unique accent stress should fill the bounded material cache");
        Require(!AgentWorld3DControl.DebugMaterialCacheContains(retainedMaterial), "least-recently-used agent material should be evicted from cache ownership");
        Require(ReferenceEquals(retainedModel.Material, retainedMaterial), "eviction should not detach material held by existing geometry");
        Require(retainedMaterial.IsFrozen, "evicted material held by existing geometry should remain frozen and usable");
        Require(finalMeshCounts == initialMeshCounts, "material eviction should not alter the independent mesh caches");
    });
}

static void AgentWorldInspectorCloseButtonExposesAutomationName()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/AgentWorld3DControl.xaml"));
    var button = XamlElementBlock(xaml, "InspectorCloseButton", "Button");

    Require(button.Contains("ToolTip=\"Close agent inspector\"", StringComparison.Ordinal), "inspector close button should expose a tooltip");
    Require(button.Contains("AutomationProperties.Name=\"Close agent inspector\"", StringComparison.Ordinal), "inspector close button should expose an automation name");
    Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "inspector close button should expose automation help text");
}

static void AgentWorldHudResizesForNarrowPanes()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Alpha opens from a compact world pane." }],
            [alpha, beta]);
        var control = new AgentWorld3DControl
        {
            Width = 360,
            Height = 420
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(360, 420));
        control.Arrange(new Rect(0, 0, 360, 420));
        control.UpdateLayout();
        control.DebugSelectAgent("alpha");
        var narrowInspectorWidth = control.DebugInspectorPanelWidth;
        var narrowControlMaxWidth = control.DebugWorldControlPanelMaxWidth;
        var narrowInspectorTop = control.DebugInspectorPanelMargin.Top;

        control.Width = 720;
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        var wideInspectorWidth = control.DebugInspectorPanelWidth;
        var wideControlMaxWidth = control.DebugWorldControlPanelMaxWidth;
        var wideInspectorTop = control.DebugInspectorPanelMargin.Top;

        Require(narrowInspectorWidth <= 328, "narrow AI World inspector should fit inside the pane margins");
        Require(narrowControlMaxWidth <= 328, "narrow AI World camera controls should fit inside the pane margins");
        Require(narrowInspectorTop >= 128, "narrow AI World inspector should sit below wrapped camera controls");
        Require(wideInspectorWidth == 310, "wide AI World inspector should keep its comfortable default width");
        Require(wideControlMaxWidth >= 680, "wide AI World camera controls should use the available HUD width");
        Require(wideInspectorTop == 96, "wide AI World inspector should keep its compact top position");
    });
}

static void AgentWorldHeaderBadgeFitsLongSpeakerNames()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState(
            "alpha",
            "Alpha Operational Translator With An Extremely Long Research Title",
            "thinking",
            "Lead analyst",
            "default",
            "default",
            "#35D6FF",
            "alpha-model",
            true,
            false,
            []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Long name badge should not collide with the world header." }],
            [alpha]);
        var control = new AgentWorld3DControl
        {
            Width = 360,
            Height = 420
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(360, 420));
        control.Arrange(new Rect(0, 0, 360, 420));
        control.UpdateLayout();
        control.DebugAdvanceWorld(0.1);

        var headerBounds = control.DebugWorldHeaderTextBounds;
        var badgeBounds = control.DebugWorldBadgeBounds;

        Require(control.DebugWorldBadgeText.StartsWith("FOLLOWING ALPHA OPERATIONAL", StringComparison.Ordinal), "world badge should still describe the followed speaker");
        Require(control.DebugWorldBadgeMaxWidth <= 148, "narrow AI World badge should clamp to a compact width");
        Require(!headerBounds.IntersectsWith(badgeBounds), "world badge should not overlap the title/status header text");
        Require(badgeBounds.Right <= 360 - 16 + 0.5, "world badge should stay inside the right pane margin");
        Require(badgeBounds.Left >= headerBounds.Right - 0.5, "world badge should reserve its own header column");
    });
}

static void AgentWorldKeyboardControlsCameraAndInspector()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Keyboard camera controls should be testable." }],
            [alpha, beta]);
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        control.DebugSelectAgent("alpha");

        var escapeHandled = control.DebugPressWorldKey(Key.Escape);
        var inspectorHidden = !control.DebugInspectorVisible;
        var yawBeforeOrbit = control.DebugCameraYaw;
        var rightHandled = control.DebugPressWorldKey(Key.Right);
        var yawAfterOrbit = control.DebugCameraYaw;
        var targetBeforePan = control.DebugCameraTarget;
        var panHandled = control.DebugPressWorldKey(Key.Up, ModifierKeys.Shift);
        var targetAfterPan = control.DebugCameraTarget;
        var distanceBeforeZoom = control.DebugCameraDistance;
        var zoomInHandled = control.DebugPressWorldKey(Key.OemPlus);
        var distanceAfterZoomIn = control.DebugCameraDistance;
        var zoomOutHandled = control.DebugPressWorldKey(Key.OemMinus);
        var distanceAfterZoomOut = control.DebugCameraDistance;
        var overviewHandled = control.DebugPressWorldKey(Key.O);
        var overviewMode = control.DebugCameraMode;
        var overviewAutomationName = control.DebugOverviewCameraAutomationName;
        var overviewAutomationHelp = control.DebugOverviewCameraAutomationHelpText;
        var followHandled = control.DebugPressWorldKey(Key.F);
        var followMode = control.DebugCameraMode;
        var followAutomationName = control.DebugFollowCameraAutomationName;
        var followAutomationHelp = control.DebugFollowCameraAutomationHelpText;
        var worldAutomationName = control.DebugWorldAutomationName;
        var worldAutomationHelp = control.DebugWorldAutomationHelpText;
        var nextHandled = control.DebugPressWorldKey(Key.N);
        var selectedAfterNext = control.DebugSelectedAgentId;
        var inspectorNameAfterNext = control.DebugInspectorName;
        var previousHandled = control.DebugPressWorldKey(Key.P);
        var selectedAfterPrevious = control.DebugSelectedAgentId;
        var cinematicHandled = control.DebugPressWorldKey(Key.C);
        var cinematicAfterToggle = control.DebugCinematicAutoCamera;
        var resetHandled = control.DebugPressWorldKey(Key.R);
        var resetMode = control.DebugCameraMode;
        var resetYaw = control.DebugCameraYaw;
        var resetPitch = control.DebugCameraPitch;
        var resetDistance = control.DebugCameraDistance;
        var positions = control.DebugAgentPositions.ToArray();
        var alphaIndex = Array.IndexOf(control.DebugAgentIds.ToArray(), "alpha");
        var alphaFocusDistance = DistanceCoordinates2D(
            control.DebugCameraTarget.X,
            control.DebugCameraTarget.Z,
            positions[alphaIndex].X,
            positions[alphaIndex].Y);
        var ignoredHandled = control.DebugPressWorldKey(Key.A);

        Require(alphaIndex >= 0, "keyboard control test should include alpha");
        Require(escapeHandled, "Escape should be handled by the world keyboard layer");
        Require(inspectorHidden, "Escape should close the selected-agent inspector");
        Require(rightHandled, "arrow keys should be handled for world orbit");
        Require(Math.Abs(yawAfterOrbit - yawBeforeOrbit) > 0.05, "right arrow should orbit the world camera");
        Require(panHandled, "Shift+arrow should be handled for world pan");
        Require(DistanceCoordinates2D(targetBeforePan.X, targetBeforePan.Z, targetAfterPan.X, targetAfterPan.Z) > 0.01, "Shift+arrow should pan the world camera target");
        Require(zoomInHandled && distanceAfterZoomIn < distanceBeforeZoom, "plus key should zoom in");
        Require(zoomOutHandled && distanceAfterZoomOut > distanceAfterZoomIn, "minus key should zoom out");
        Require(overviewHandled && overviewMode == "Overview", "O key should switch to overview camera");
        Require(overviewAutomationName.Contains("Overview camera", StringComparison.Ordinal) && overviewAutomationName.Contains("selected", StringComparison.OrdinalIgnoreCase), "overview camera button should expose selected automation state");
        Require(overviewAutomationHelp.Contains("Shortcut O", StringComparison.Ordinal), "overview camera button should expose its keyboard shortcut");
        Require(followHandled && followMode == "FollowSpeaker", "F key should return to speaker follow camera");
        Require(followAutomationName.Contains("Follow speaker camera", StringComparison.Ordinal) && followAutomationName.Contains("selected", StringComparison.OrdinalIgnoreCase), "follow camera button should expose selected automation state");
        Require(followAutomationHelp.Contains("Shortcut F or Home", StringComparison.Ordinal), "follow camera button should expose its keyboard shortcuts");
        Require(worldAutomationName == "AI World 3D arena", "world surface should expose an automation name");
        Require(worldAutomationHelp.Contains("N and P cycle agents", StringComparison.Ordinal) && worldAutomationHelp.Contains("R resets", StringComparison.Ordinal) && worldAutomationHelp.Contains("C toggles", StringComparison.Ordinal), "world surface should expose keyboard shortcut help");
        Require(nextHandled && selectedAfterNext == "beta", "N key should cycle to the next agent");
        Require(inspectorNameAfterNext == "Beta", "N key should focus the cycled agent in the inspector");
        Require(previousHandled && selectedAfterPrevious == "alpha", "P key should cycle back to the previous agent");
        Require(cinematicHandled && cinematicAfterToggle, "C key should toggle cinematic camera on");
        Require(resetHandled && resetMode == "FollowSpeaker", "R key should reset to follow camera mode");
        Require(Math.Abs(resetYaw) < 0.001, "R key should reset camera yaw");
        Require(Math.Abs(resetPitch - 0.56) < 0.001, "R key should reset camera pitch");
        Require(Math.Abs(resetDistance - 12.2) < 0.001, "R key should reset camera distance");
        Require(alphaFocusDistance < 0.05, "F key should focus the active speaker");
        Require(!ignoredHandled, "unmapped keys should not be swallowed by the world keyboard layer");
    });
}

static void AgentWorldOverviewRestoresFollowPanOffset()
{
    RunStaTest(() =>
    {
        var alpha = new AgentState("alpha", "Alpha", "thinking", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [TranscriptForTest(1, "Alpha", "alpha", "message", "ok") with { Text = "Overview should preserve a hand-framed follow shot." }],
            [alpha]);
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        control.DebugAdvanceWorld(0.2);
        var panHandled = control.DebugPressWorldKey(Key.Up, ModifierKeys.Shift);
        var pannedFollowTarget = control.DebugCameraTarget;
        control.DebugSetCameraMode("overview");
        control.DebugSetCameraMode("follow");
        var restoredFollowTarget = control.DebugCameraTarget;

        Require(panHandled, "Shift+arrow should pan the follow camera before overview");
        Require(control.DebugCameraMode == "FollowSpeaker", "test should return to follow camera");
        Require(Math.Abs(restoredFollowTarget.X - pannedFollowTarget.X) < 0.001, "leaving overview should restore follow camera pan X offset");
        Require(Math.Abs(restoredFollowTarget.Z - pannedFollowTarget.Z) < 0.001, "leaving overview should restore follow camera pan Z offset");
    });
}

static void AgentWorldCollisionSolverSeparatesCrowdedRobots()
{
    Point[] crowded =
    [
        new(0, 0),
        new(0, 0),
        new(0.04, 0.02),
        new(-0.03, -0.02),
        new(0.02, -0.04),
        new(-0.02, 0.03)
    ];

    var resolved = AgentWorld3DControl.DebugResolveCollisionPoints(crowded).ToArray();
    for (var firstIndex = 0; firstIndex < resolved.Length; firstIndex++)
    {
        for (var secondIndex = firstIndex + 1; secondIndex < resolved.Length; secondIndex++)
        {
            Require(Distance2D(resolved[firstIndex], resolved[secondIndex]) >= 0.82, "collision solver should separate overlapping robot bodies");
        }
    }

    var denseCluster = Enumerable.Range(0, 18)
        .Select(index => new Point(
            ((index % 6) - 2.5) * 0.025,
            ((index / 6) - 1) * 0.025))
        .ToArray();
    var denseResolved = AgentWorld3DControl.DebugResolveCollisionPoints(denseCluster).ToArray();
    var denseMinimumDistance = double.MaxValue;
    for (var firstIndex = 0; firstIndex < denseResolved.Length; firstIndex++)
    {
        Require(Math.Abs(denseResolved[firstIndex].X) <= 8.1, "dense collision solver should keep robots inside world X bounds");
        Require(Math.Abs(denseResolved[firstIndex].Y) <= 5.7, "dense collision solver should keep robots inside world Z bounds");
        for (var secondIndex = firstIndex + 1; secondIndex < denseResolved.Length; secondIndex++)
        {
            denseMinimumDistance = Math.Min(denseMinimumDistance, Distance2D(denseResolved[firstIndex], denseResolved[secondIndex]));
        }
    }

    Require(denseMinimumDistance >= 0.82, "collision solver should separate dense robot crowds without residual overlap");
}

static void AgentWorldMiniMapMarkersStayInsideFrame()
{
    foreach (var selected in new[] { false, true })
    {
        foreach (var point in new[]
                 {
                     new Point(-8.1, -5.7),
                     new Point(-8.1, 5.7),
                     new Point(8.1, -5.7),
                     new Point(8.1, 5.7),
                     new Point(-42, 24),
                     new Point(42, -24)
                 })
        {
            var marker = AgentWorld3DControl.DebugCalculateMiniMapMarkerPlacement(point.X, point.Y, selected);
            Require(marker.Left >= 6, "minimap marker should stay inside the left frame edge");
            Require(marker.Top >= 16, "minimap marker should stay inside the top frame edge");
            Require(marker.Right <= 162, "minimap marker should stay inside the right frame edge");
            Require(marker.Bottom <= 100, "minimap marker should stay inside the bottom frame edge");
            Require(marker.Width == (selected ? 11 : 8), "minimap marker size should preserve selected/non-selected affordance");
        }
    }
}

static void AgentWorldMiniMapTracksCameraFocus()
{
    RunStaTest(() =>
    {
        // Both idle (no speaker): keeps the agents at their perimeter stations so the arena
        // centre stays clear for the centre-focus minimap assertions below. A speaking agent
        // walks up to the central dais, which would sit under a dead-centre minimap click.
        var alpha = new AgentState("alpha", "Alpha", "waiting", "Lead analyst", "default", "default", "#35D6FF", "alpha-model", true, false, []);
        var beta = new AgentState("beta", "Beta", "waiting", "Skeptic", "default", "default", "#F1C96B", "beta-model", true, false, []);
        var snapshot = SnapshotForOverviewTest(
            providerOnline: true,
            providerModel: "shared-model",
            providerLastError: "",
            turnIndex: 1,
            [],
            [alpha, beta]);
        var control = new AgentWorld3DControl
        {
            Width = 720,
            Height = 460
        };

        control.ApplySnapshot(snapshot);
        control.Measure(new Size(720, 460));
        control.Arrange(new Rect(0, 0, 720, 460));
        control.UpdateLayout();
        control.DebugAdvanceWorld(0.2);

        var initialTargetElement = control.DebugMiniMapCameraTargetElement;
        var initialFrameElements = control.DebugMiniMapFrameElements.ToArray();
        var targetVisible = control.DebugMiniMapCameraTargetVisible;
        control.DebugSelectAgent("beta");
        var selectedTargetElement = control.DebugMiniMapCameraTargetElement;
        var betaCenter = control.DebugMiniMapMarkerCenters["beta"];
        var selectedTargetCenter = control.DebugMiniMapCameraTargetCenter;
        control.DebugSelectAgent("alpha");
        control.DebugClickMiniMap(betaCenter.X + 1, betaCenter.Y + 1);
        var selectedIdAfterNearMarkerClick = control.DebugSelectedAgentId;
        control.DebugClickMiniMap(84, 58);
        var modeAfterEmptyMapClick = control.DebugCameraMode;
        var emptyClickTarget = control.DebugCameraTarget;
        var emptyClickTargetCenter = control.DebugMiniMapCameraTargetCenter;
        var keyPanHandled = control.DebugPressWorldKey(Key.Up, ModifierKeys.Shift);
        var pannedTargetElement = control.DebugMiniMapCameraTargetElement;
        var pannedTargetCenter = control.DebugMiniMapCameraTargetCenter;
        var pannedFrameElements = control.DebugMiniMapFrameElements.ToArray();

        Require(targetVisible, "minimap camera target marker should be visible with active agents");
        Require(keyPanHandled, "keyboard pan should be handled for minimap camera target test");
        Require(ReferenceEquals(initialTargetElement, selectedTargetElement) && ReferenceEquals(selectedTargetElement, pannedTargetElement), "minimap camera target marker should be reused");
        Require(initialFrameElements.Length == 3 && pannedFrameElements.Length == 3, "minimap focus marker should not be counted as reusable frame geometry");
        Require(Distance2D(betaCenter, selectedTargetCenter) < 2, "minimap camera target should snap near selected robot focus");
        Require(selectedIdAfterNearMarkerClick == "beta", "minimap click near a robot marker should select that robot");
        Require(modeAfterEmptyMapClick == "Free", "minimap click on empty space should switch to free camera focus");
        Require(Math.Abs(emptyClickTarget.X) < 0.05 && Math.Abs(emptyClickTarget.Z) < 0.05, "minimap center click should focus the arena center");
        Require(Distance2D(emptyClickTargetCenter, new Point(84, 58)) < 1, "minimap camera target should move to the empty-space click location");
        Require(Distance2D(emptyClickTargetCenter, pannedTargetCenter) > 0.25, "minimap camera target should move immediately when the camera pans");
        Require(pannedTargetCenter.X >= 6 && pannedTargetCenter.X <= 162, "minimap camera target should stay inside horizontal frame bounds");
        Require(pannedTargetCenter.Y >= 16 && pannedTargetCenter.Y <= 100, "minimap camera target should stay inside vertical frame bounds");
    });
}

static void AgentWorldBubblePlacementStaysVisibleAtEdges()
{
    var canvas = new Size(360, 220);
    var bubble = new Size(180, 64);
    var leftEdge = AgentWorld3DControl.DebugCalculateBubblePlacement(bubble, new Point(-55, 124), canvas);
    var topEdge = AgentWorld3DControl.DebugCalculateBubblePlacement(bubble, new Point(180, 20), canvas);
    var bottomEdge = AgentWorld3DControl.DebugCalculateBubblePlacement(bubble, new Point(180, 214), canvas);
    var clampedLeft = AgentWorld3DControl.DebugClampBubbleAnchor(new Point(-420, 124), canvas);
    var clampedRight = AgentWorld3DControl.DebugClampBubbleAnchor(new Point(980, 124), canvas);
    var clampedTop = AgentWorld3DControl.DebugClampBubbleAnchor(new Point(180, -240), canvas);
    var clampedBottom = AgentWorld3DControl.DebugClampBubbleAnchor(new Point(180, 540), canvas);
    var narrowCanvas = new Size(40, 160);
    var narrow = AgentWorld3DControl.DebugCalculateBubblePlacement(bubble, new Point(20, 80), narrowCanvas);
    var narrowBubbleWidth = Math.Max(1, Math.Min(bubble.Width, Math.Max(1, narrowCanvas.Width - 20)));

    Require(leftEdge.Left >= 10, "edge bubble should clamp inside the left canvas edge");
    Require(leftEdge.Left + bubble.Width <= canvas.Width - 10, "edge bubble should clamp inside the right canvas edge");
    Require(leftEdge.PointerX >= leftEdge.Left + 16, "edge bubble pointer should stay inside the bubble body");
    Require(leftEdge.PointerX <= leftEdge.Left + bubble.Width - 16, "edge bubble pointer should stay inside the bubble body");
    Require(leftEdge.DistanceTo(new Point(-55, 124)) < 130, "edge bubble should keep a bounded pointer distance instead of disappearing");

    Require(topEdge.PlacedBelow, "top-edge bubble should flip below the speaker anchor");
    Require(topEdge.BodyTop >= 50, "top-edge bubble body should respect HUD-safe top margin");
    Require(topEdge.DistanceTo(new Point(180, 20)) < 24, "top-edge bubble pointer should remain near the speaker anchor");

    Require(!bottomEdge.PlacedBelow, "bottom-edge bubble should stay above the speaker anchor");
    Require(bottomEdge.Top + bubble.Height <= canvas.Height - 10, "bottom-edge bubble should stay inside the canvas");
    Require(bottomEdge.DistanceTo(new Point(180, 214)) < 24, "bottom-edge bubble pointer should remain near the speaker anchor");

    Require(clampedLeft.X == 12, "offscreen-left speaker bubble anchor should clamp to the left edge");
    Require(clampedRight.X == canvas.Width - 12, "offscreen-right speaker bubble anchor should clamp to the right edge");
    Require(clampedTop.Y == 12, "offscreen-top speaker bubble anchor should clamp to the top edge");
    Require(clampedBottom.Y == canvas.Height - 12, "offscreen-bottom speaker bubble anchor should clamp to the bottom edge");
    Require(AgentWorld3DControl.DebugCalculateBubblePlacement(bubble, clampedLeft, canvas).Left >= 10, "clamped offscreen bubble should remain visible");
    Require(narrow.Left >= 10, "narrow bubble should clamp inside the left canvas edge");
    Require(narrow.Left + narrowBubbleWidth <= narrowCanvas.Width - 10, "narrow bubble should clamp inside the right canvas edge");
    Require(narrow.PointerX >= narrow.Left, "narrow bubble pointer should stay inside the bubble body");
    Require(narrow.PointerX <= narrow.Left + narrowBubbleWidth, "narrow bubble pointer should stay inside the bubble body");
}

static void MainWindowSkipsHiddenWorldSnapshotRefresh()
{
    Require(MainWindow.ShouldApplyWorldSnapshot(Visibility.Visible, worldDebugEnabled: true), "visible enabled AI World panel should receive fresh snapshots");
    Require(!MainWindow.ShouldApplyWorldSnapshot(Visibility.Visible, worldDebugEnabled: false), "disabled AI World should skip expensive 3D refreshes even if panel visibility is stale");
    Require(!MainWindow.ShouldApplyWorldSnapshot(Visibility.Collapsed, worldDebugEnabled: true), "collapsed AI World panel should skip expensive 3D refreshes");
    Require(!MainWindow.ShouldApplyWorldSnapshot(Visibility.Hidden, worldDebugEnabled: true), "hidden AI World panel should skip expensive 3D refreshes");
}

}
