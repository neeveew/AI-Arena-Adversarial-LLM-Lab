using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Persistence;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.VerificationLab;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private const string UiEvidencePrivateText = "operator prompt that must never enter QA evidence";
    private const string UiEvidenceProviderText = "https://provider.invalid/v1?token=private-value";
    private const string UiEvidencePrivatePath = @"C:\Users\Private\models\private-model.gguf";
    private const string UiEvidenceRuntimeAutomationSecret = "RuntimePrivateModelIdentifier20260809";
    private const string UiEvidenceTreeFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    static void UiVerificationCapturesPrivacySafeDeterministicStructure()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-evidence-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var primary = new Button
                {
                    Name = "QaPrimaryButton",
                    Content = UiEvidencePrivateText,
                    Focusable = true
                };
                AutomationProperties.SetAutomationId(primary, "QaPrimaryButton");
                AutomationProperties.SetName(primary, UiEvidencePrivateText);
                AutomationProperties.SetHelpText(primary, UiEvidenceProviderText);

                var disabled = new Button
                {
                    Name = "QaDisabledButton",
                    Content = UiEvidenceProviderText,
                    IsEnabled = false,
                    Focusable = true
                };
                AutomationProperties.SetAutomationId(disabled, "QaDisabledButton");

                var privateInput = new TextBox
                {
                    Name = "PrivateInputStaticName",
                    Text = UiEvidencePrivateText,
                    Focusable = true
                };
                AutomationProperties.SetAutomationId(privateInput, UiEvidenceRuntimeAutomationSecret);
                AutomationProperties.SetName(privateInput, UiEvidenceProviderText);
                AutomationProperties.SetHelpText(privateInput, UiEvidencePrivatePath);

                var window = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = UiEvidencePrivateText },
                            primary,
                            disabled,
                            privateInput
                        }
                    }
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    FocusManager.SetFocusedElement(window, primary);
                    _ = Keyboard.Focus(primary);
                    window.UpdateLayout();

                    var service = new AIArenaUiVerificationControlService(window, dataRoot, () => "dark-blue");
                    var expectedState = service.DebugObservedExpectedState();
                    Require(service.TryResolveEvidencePath("nested/first", out var firstPath, out var firstRelativePath, out var firstPathError), $"safe UI evidence path should resolve: {firstPathError}");
                    Require(firstRelativePath == "exports/qa/ui-structure/nested/first.json", "UI evidence result should expose only a stable relative path");
                    Require(!service.TryResolveEvidencePath("../escape.json", out _, out _, out var traversalError)
                        && traversalError.Contains("cannot leave", StringComparison.OrdinalIgnoreCase), "UI evidence traversal should be rejected");
                    Require(!service.TryResolveEvidencePath(UiEvidencePrivatePath, out _, out _, out var absoluteError)
                        && absoluteError.Contains("relative", StringComparison.OrdinalIgnoreCase), "UI evidence absolute paths should be rejected");

                    var invalidFingerprint = service.CaptureStructureAsync(
                        "nested/invalid-fingerprint.json",
                        "not-a-hash",
                        expectedState).GetAwaiter().GetResult();
                    Require(!invalidFingerprint.Ok && invalidFingerprint.ErrorCode == "invalid_argument", "automation evidence should require exact source-tree provenance");
                    var invalidState = service.CaptureStructureAsync(
                        "nested/invalid-state.json",
                        UiEvidenceTreeFingerprint,
                        UiEvidencePrivateText).GetAwaiter().GetResult();
                    Require(!invalidState.Ok && invalidState.ErrorCode == "invalid_argument", "automation evidence should reject dynamic expected-state text");
                    var callerEchoState = service.CaptureStructureAsync(
                        "nested/caller-echo.json",
                        UiEvidenceTreeFingerprint,
                        "arena-empty.closed.dark-blue.w960.d1-0.system").GetAwaiter().GetResult();
                    Require(!callerEchoState.Ok
                        && callerEchoState.ErrorCode == "state_mismatch"
                        && !File.Exists(Path.Combine(dataRoot, "exports", "qa", "ui-structure", "nested", "caller-echo.json")),
                        "a bounded caller-supplied state must not become observed evidence when visible roots disagree");
                    var invalidRenderDpi = service.CaptureStructureAsync(
                        "nested/invalid-render-dpi.json",
                        UiEvidenceTreeFingerprint,
                        expectedState,
                        renderDpiScale: 1.25).GetAwaiter().GetResult();
                    Require(!invalidRenderDpi.Ok && invalidRenderDpi.ErrorCode == "invalid_argument", "automation evidence should reject an unreviewed render DPI before writing an artifact");

                    var first = service.CaptureStructureAsync(
                        "nested/first.json",
                        UiEvidenceTreeFingerprint,
                        expectedState).GetAwaiter().GetResult();
                    Require(first.Ok
                        && first.ArtifactKind == "automation-tree"
                        && first.PathBase == "data-root"
                        && first.RelativePath == firstRelativePath
                        && first.ByteSize > 0
                        && first.Sha256.Length == 64, $"first structure capture should expose an unambiguous data-root-relative artifact: {first.Message}");
                    Require(first.CaptureMode == AIArenaUiVerificationControlService.CaptureMode
                        && first.Limitation == AIArenaUiVerificationControlService.CaptureLimitation, "capture result should label the deterministic visual-tree limitation honestly");
                    Require(first.TreeFingerprint == UiEvidenceTreeFingerprint
                        && first.ExpectedState == expectedState
                        && first.ExpectedStateSource == AIArenaUiVerificationControlService.ExpectedStateSource
                        && first.DialogState == "closed"
                        && first.VisibleRootIdentities.Count > 0
                        && first.RequiredControlIdentities.Count > 0
                        && first.Theme == "dark-blue"
                        && first.ViewportWidthDip > 0
                        && first.ViewportHeightDip > 0
                        && first.DpiScale > 0
                        && first.RenderDpiScale == first.DpiScale
                        && !first.RenderDpiOverride
                        && first.DpiScaleX > 0
                        && first.DpiScaleY > 0
                        && first.MotionPreferenceSource == "system"
                        && first.AnimationsEnabled == SystemMotionPreferences.SystemAnimationsEnabled, "capture result should report frozen artifact provenance, theme, viewport, raster density, physical-scale context, and motion-preference state");
                    Require(!Path.IsPathRooted(first.RelativePath)
                        && !first.Message.Contains(dataRoot, StringComparison.OrdinalIgnoreCase), "capture response must not expose an absolute data path");

                    var firstBytes = File.ReadAllBytes(firstPath);
                    var firstJson = System.Text.Encoding.UTF8.GetString(firstBytes);
                    Require(Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant() == first.Sha256, "capture result hash should match the atomic artifact bytes");
                    Require(!firstJson.Contains(UiEvidencePrivateText, StringComparison.Ordinal)
                        && !firstJson.Contains(UiEvidenceProviderText, StringComparison.Ordinal)
                        && !firstJson.Contains(UiEvidencePrivatePath, StringComparison.OrdinalIgnoreCase)
                        && !firstJson.Contains(UiEvidenceRuntimeAutomationSecret, StringComparison.Ordinal)
                        && !firstJson.Contains("private-model.gguf", StringComparison.OrdinalIgnoreCase)
                        && !firstJson.Contains(dataRoot, StringComparison.OrdinalIgnoreCase), "UI evidence must omit dynamic text, provider content, model paths, and absolute data paths");

                    using var firstDocument = JsonDocument.Parse(firstJson);
                    var root = firstDocument.RootElement;
                    Require(root.GetProperty("Schema").GetString() == AIArenaUiVerificationControlService.EvidenceSchema, "UI evidence schema should be explicit");
                    Require(root.GetProperty("CaptureMode").GetString() == AIArenaUiVerificationControlService.CaptureMode, "UI evidence capture mode should be explicit");
                    Require(root.GetProperty("Limitation").GetString() == AIArenaUiVerificationControlService.CaptureLimitation, "UI evidence limitation should be explicit");
                    Require(root.GetProperty("TreeFingerprint").GetString() == UiEvidenceTreeFingerprint
                        && root.GetProperty("ExpectedState").GetString() == expectedState
                        && root.GetProperty("ExpectedStateSource").GetString() == AIArenaUiVerificationControlService.ExpectedStateSource
                        && root.GetProperty("DialogState").GetString() == "closed"
                        && root.GetProperty("ViewportWidthDip").GetInt32() > 0
                        && root.GetProperty("ViewportHeightDip").GetInt32() > 0
                        && root.GetProperty("DpiScale").GetDouble() > 0
                        && root.GetProperty("RenderDpiScale").GetDouble() == root.GetProperty("DpiScale").GetDouble()
                        && !root.GetProperty("RenderDpiOverride").GetBoolean()
                        && root.GetProperty("MotionPreferenceSource").GetString() == "system"
                        && root.GetProperty("AnimationsEnabled").GetBoolean() == SystemMotionPreferences.SystemAnimationsEnabled, "UI evidence should carry exact privacy-safe QA artifact provenance and effective motion state");
                    Require(root.GetProperty("FocusIdentity").GetString() == "QaPrimaryButton", "focused control should be identified without reading its dynamic accessible name");
                    Require(root.GetProperty("NodeCount").GetInt32() == root.GetProperty("Nodes").GetArrayLength(), "node count should match the bounded node array");
                    Require(!root.GetProperty("Truncated").GetBoolean(), "small verification tree should not be truncated");

                    var nodes = root.GetProperty("Nodes").EnumerateArray().ToArray();
                    var primaryNode = nodes.Single(node => node.GetProperty("AutomationId").GetString() == "QaPrimaryButton");
                    Require(primaryNode.GetProperty("ControlType").GetString() == "Button"
                        && primaryNode.GetProperty("IsEnabled").GetBoolean()
                        && primaryNode.GetProperty("IsFocusable").GetBoolean()
                        && primaryNode.GetProperty("HasKeyboardFocus").GetBoolean()
                        && primaryNode.GetProperty("BoundsWidth").GetDouble() > 0
                        && primaryNode.GetProperty("BoundsHeight").GetDouble() > 0
                        && primaryNode.GetProperty("EffectiveOpacity").GetDouble() > 0
                        && primaryNode.GetProperty("IntersectsViewport").GetBoolean()
                        && primaryNode.GetProperty("IsRendered").GetBoolean(),
                        "focused button evidence should expose control type, keyboard state, and fail-closed renderability geometry");
                    var disabledNode = nodes.Single(node => node.GetProperty("AutomationId").GetString() == "QaDisabledButton");
                    Require(!disabledNode.GetProperty("IsEnabled").GetBoolean(), "disabled state should be explicit and non-colour evidence");
                    var redactedNode = nodes.Single(node => node.GetProperty("Identity").GetString() == "PrivateInputStaticName");
                    Require(redactedNode.GetProperty("AutomationId").GetString()!.StartsWith("redacted-", StringComparison.Ordinal)
                        && redactedNode.GetProperty("AutomationIdRedacted").GetBoolean()
                        && redactedNode.GetProperty("ControlType").GetString() == "Edit", "unsafe automation IDs should become opaque while preserving control type");
                    Require(nodes.Select(node => node.GetProperty("Sequence").GetInt32()).SequenceEqual(Enumerable.Range(0, nodes.Length)), "visual-tree evidence order should be stable preorder");

                    var second = service.CaptureStructureAsync(
                        "nested/second.json",
                        UiEvidenceTreeFingerprint,
                        expectedState).GetAwaiter().GetResult();
                    Require(second.Ok, $"second structure capture should succeed: {second.Message}");
                    var secondPath = Path.Combine(dataRoot, second.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    using var secondDocument = JsonDocument.Parse(File.ReadAllText(secondPath));
                    Require(root.GetProperty("Nodes").GetRawText() == secondDocument.RootElement.GetProperty("Nodes").GetRawText(), "unchanged visual trees should serialize nodes in deterministic order");

                    var duplicate = service.CaptureStructureAsync(
                        "nested/first.json",
                        UiEvidenceTreeFingerprint,
                        expectedState).GetAwaiter().GetResult();
                    Require(!duplicate.Ok && duplicate.ErrorCode == "already_exists" && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes), "UI evidence should never overwrite an existing artifact");
                    Require(!Directory.EnumerateFiles(Path.GetDirectoryName(firstPath)!, "*.tmp").Any(), "atomic UI evidence writes should clean temporary files");
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void UiVerificationUsesSemanticTranscriptCountInsteadOfPlaceholderRows()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-transcript-state-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var transcriptItems = new ItemsControl { Name = "TranscriptItems" };
                transcriptItems.Items.Add(new Border());
                var transcriptPanel = new Grid { Name = "TranscriptPanel" };
                transcriptPanel.Children.Add(transcriptItems);
                var window = new Window
                {
                    Width = 960,
                    Height = 640,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = transcriptPanel
                };
                NameScope.SetNameScope(window, new NameScope());
                window.RegisterName(transcriptPanel.Name, transcriptPanel);
                window.RegisterName(transcriptItems.Name, transcriptItems);

                int? semanticMessageCount = 0;
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var service = new AIArenaUiVerificationControlService(
                        window,
                        dataRoot,
                        () => "dark-blue",
                        () => semanticMessageCount);

                    var emptyState = service.DebugObservedExpectedState(1.0);
                    Require(emptyState.StartsWith("arena-empty.closed.dark-blue.w960.d1-0.", StringComparison.Ordinal),
                        "a setup placeholder row must not be reported as transcript content when the semantic snapshot has no messages");

                    semanticMessageCount = 1;
                    var populatedState = service.DebugObservedExpectedState(1.0);
                    Require(populatedState.StartsWith("arena-populated.closed.dark-blue.w960.d1-0.", StringComparison.Ordinal),
                        "an observed semantic transcript message must report the Arena as populated");

                    semanticMessageCount = null;
                    var fallbackState = service.DebugObservedExpectedState(1.0);
                    Require(fallbackState.StartsWith("arena-populated.closed.dark-blue.w960.d1-0.", StringComparison.Ordinal),
                        "an unavailable semantic snapshot must conservatively fall back to the visible item collection");

                    var source = ReadMainWindowSource();
                    Require(source.Contains("() => _lastRenderedSnapshot?.Messages.Count", StringComparison.Ordinal),
                        "production QA observation must use semantic transcript messages rather than presentation placeholder rows");
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void UiVerificationControlHandlerBoundsWindowAndRoutesCommands()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-control-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var window = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = new Button { Content = UiEvidencePrivateText }
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var verification = new AIArenaUiVerificationControlService(window, dataRoot, () => "light");
                    var expectedState = verification.DebugObservedExpectedState();
                    Require(AIArenaUiVerificationControlService.IsSupportedSize(960, 640)
                        && AIArenaUiVerificationControlService.IsSupportedSize(1500, 1000)
                        && !AIArenaUiVerificationControlService.IsSupportedSize(959, 640)
                        && !AIArenaUiVerificationControlService.IsSupportedSize(960, 1001), "QA window size bounds should include only the reviewed matrix");

                    var originalWidth = window.Width;
                    var invalidDirect = verification.SetWindowSizeAsync(959, 640).GetAwaiter().GetResult();
                    Require(!invalidDirect.Ok && invalidDirect.ErrorCode == "invalid_argument" && window.Width == originalWidth, "invalid direct sizes must not mutate the window");
                    var minimum = verification.SetWindowSizeAsync(960, 640).GetAwaiter().GetResult();
                    Require(minimum.Ok
                        && Math.Abs(minimum.ActualWidthDip - 960) <= 2
                        && Math.Abs(minimum.ActualHeightDip - 640) <= 2
                        && minimum.DpiScaleX > 0
                        && minimum.DpiScaleY > 0, "minimum QA client size should be applied and measured in DIP");

                    window.MaxHeight = 650;
                    var constrained = verification.SetWindowSizeAsync(1000, 700).GetAwaiter().GetResult();
                    Require(!constrained.Ok
                        && constrained.ErrorCode == "not_available"
                        && constrained.ActualHeightDip <= 650,
                        "desktop-constrained QA sizes should be reported unavailable instead of claiming the requested viewport");
                    window.MaxHeight = double.PositiveInfinity;

                    var events = new AIArenaControlPlaneEventHub();
                    var published = new List<AIArenaControlEvent>();
                    using var subscription = events.Subscribe(published.Add);
                    var handler = new AIArenaAppControlHandler(
                        new AIArenaScreenshotControlService(window, dataRoot),
                        events,
                        onScreenshotCaptured: null,
                        verification: verification);
                    Require(handler.CanHandle(AIArenaControlCommands.AppQaWindowSize)
                        && handler.CanHandle(AIArenaControlCommands.AppQaStructureCapture)
                        && handler.CanHandle(AIArenaControlCommands.AppQaFocusAdvance)
                        && handler.CanHandle(AIArenaControlCommands.AppQaFocusCapture)
                        && handler.CanHandle(AIArenaControlCommands.AppQaFocusFeature)
                        && handler.CanHandle(AIArenaControlCommands.AppQaMotionSet)
                        && !handler.CanHandle(AIArenaControlCommands.ProviderState), "app handler should own only app screenshot and QA verification commands");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        """{"id":"bad-type","command":"app.qa.window.size","args":{"width":"wide","height":700}}""",
                        out var badTypeRequest,
                        out _), "invalid window-size shape should parse at the protocol boundary");
                    var badType = handler.ExecuteAsync(badTypeRequest).GetAwaiter().GetResult();
                    Require(!badType.Ok && badType.ErrorCode == "invalid_argument", "non-integer QA sizes should be rejected by the handler");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        """{"id":"bad-range","command":"app.qa.window.size","args":{"width":1501,"height":700}}""",
                        out var badRangeRequest,
                        out _), "out-of-range window size should parse at the protocol boundary");
                    var badRange = handler.ExecuteAsync(badRangeRequest).GetAwaiter().GetResult();
                    Require(!badRange.Ok && badRange.ErrorCode == "invalid_argument", "out-of-range QA sizes should be rejected without mutation");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        """{"id":"size","command":"app.qa.window.size","args":{"width":1000,"height":700}}""",
                        out var sizeRequest,
                        out _), "valid window size request should parse");
                    var sized = handler.ExecuteAsync(sizeRequest).GetAwaiter().GetResult();
                    Require(sized.Ok && sized.Data is AIArenaQaWindowSizeResult sizeResult
                        && Math.Abs(sizeResult.ActualWidthDip - 1000) <= 2
                        && published.Any(item => item.Type == "app.qa.window.sized"), "valid QA size should return measured evidence and publish one safe event");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        $"{{\"id\":\"capture\",\"command\":\"app.qa.structure.capture\",\"args\":{{\"path\":\"handler/evidence.json\",\"treeFingerprint\":\"{UiEvidenceTreeFingerprint}\",\"expectedState\":\"{expectedState}\"}}}}",
                        out var captureRequest,
                        out _), "valid structure request should parse");
                    var captured = handler.ExecuteAsync(captureRequest).GetAwaiter().GetResult();
                    Require(captured.Ok && captured.Data is AIArenaUiStructureCaptureResult captureResult
                        && captureResult.ArtifactKind == "automation-tree"
                        && captureResult.PathBase == "data-root"
                        && captureResult.RelativePath == "exports/qa/ui-structure/handler/evidence.json"
                        && captureResult.TreeFingerprint == UiEvidenceTreeFingerprint
                        && captureResult.ExpectedState == expectedState
                        && published.Any(item => item.Type == "app.qa.structure.captured"), "handler should return relative privacy-safe structure evidence and publish its event");
                    var serialized = AIArenaControlPlaneProtocol.Serialize(captured);
                    Require(!serialized.Contains(dataRoot, StringComparison.OrdinalIgnoreCase)
                        && !serialized.Contains(UiEvidencePrivateText, StringComparison.Ordinal), "control-plane UI evidence response must not leak data roots or dynamic content");

                    var source = ReadMainWindowSource();
                    Require(source.Contains("new AIArenaUiVerificationControlService(", StringComparison.Ordinal)
                        && source.Contains("_coreSessionStore.DataRoot", StringComparison.Ordinal)
                        && source.Contains("_appControlHandler.CanHandle(request.Command)", StringComparison.Ordinal), "MainWindow should wire QA controls through the existing authenticated app handler route");
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void UiVerificationBindsExperimentEvidenceToObservedFeature()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-feature-state-{Guid.NewGuid():N}");
        var previousDataRoot = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", dataRoot);
        try
        {
            RunStaTest(() =>
            {
                var firstFeatureButton = new Button
                {
                    Name = "FeatureMatrixFocusFirst",
                    Content = "Static QA control",
                    Focusable = true,
                    IsTabStop = true
                };
                var secondFeatureButton = new Button
                {
                    Name = "FeatureMatrixFocusSecond",
                    Content = "Static QA control",
                    Focusable = true,
                    IsTabStop = true
                };
                var featureFocusPanel = new StackPanel
                {
                    Children = { firstFeatureButton, secondFeatureButton }
                };
                var featureContent = new Border
                {
                    Name = "MatrixPanel",
                    Child = featureFocusPanel
                };
                var experimentLab = new Grid { Name = "ExperimentLabPanel" };
                experimentLab.Children.Add(featureContent);
                var shellRoot = new Grid();
                shellRoot.Children.Add(experimentLab);
                var window = new Window
                {
                    Width = 960,
                    Height = 640,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = shellRoot
                };
                NameScope.SetNameScope(window, new NameScope());
                window.RegisterName(experimentLab.Name, experimentLab);

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var selectedFeature = "matrix";
                    var verification = new AIArenaUiVerificationControlService(
                        window,
                        dataRoot,
                        () => "light",
                        selectedExperimentFeatureKey: () => selectedFeature);
                    var observedState = verification.DebugObservedExpectedState(renderDpiScale: 1.0);
                    Require(observedState.StartsWith(
                            "experiment-lab.feature-matrix.closed.light.w960.d1-0.",
                            StringComparison.Ordinal),
                        $"Experiment Lab evidence should bind its canonical state to the independently observed feature: {observedState}");

                    var events = new AIArenaControlPlaneEventHub();
                    var published = new List<AIArenaControlEvent>();
                    using var subscription = events.Subscribe(published.Add);
                    var handler = new AIArenaAppControlHandler(
                        new AIArenaScreenshotControlService(window, dataRoot),
                        events,
                        verification: verification);
                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                            """{"id":"feature-focus","command":"app.qa.focus.feature","args":{}}""",
                            out var featureFocusRequest,
                            out _),
                        "selected-feature focus command should parse at the authenticated protocol boundary");
                    var featureFocusResponse = handler.ExecuteAsync(featureFocusRequest).GetAwaiter().GetResult();
                    Require(featureFocusResponse.Ok
                        && featureFocusResponse.Data is AIArenaQaFocusTraversalResult featureBoundary
                        && featureBoundary.Direction == "feature-content"
                        && featureBoundary.AfterIdentity == "FeatureMatrixFocusFirst"
                        && firstFeatureButton.IsKeyboardFocused
                        && published.Any(item => item.Type == "app.qa.focus.feature"),
                        "selected-feature focus command did not enter the first tracked MatrixPanel tab stop");
                    var next = verification.AdvanceKeyboardFocusAsync("next").GetAwaiter().GetResult();
                    var previous = verification.AdvanceKeyboardFocusAsync("previous").GetAwaiter().GetResult();
                    var captureFocus = verification.AdvanceKeyboardFocusAsync("next").GetAwaiter().GetResult();
                    Require(next.Ok && previous.Ok && captureFocus.Ok
                        && next.BeforeIdentity == "FeatureMatrixFocusFirst"
                        && next.AfterIdentity == "FeatureMatrixFocusSecond"
                        && previous.BeforeIdentity == "FeatureMatrixFocusSecond"
                        && previous.AfterIdentity == "FeatureMatrixFocusFirst"
                        && captureFocus.BeforeIdentity == "FeatureMatrixFocusFirst"
                        && captureFocus.AfterIdentity == "FeatureMatrixFocusSecond",
                        "feature-local N/P/N traversal did not stay within the selected MatrixPanel content");

                    var captured = verification.CaptureStructureAsync(
                        "feature/matrix.json",
                        UiEvidenceTreeFingerprint,
                        observedState,
                        renderDpiScale: 1.0).GetAwaiter().GetResult();
                    Require(captured.Ok
                        && captured.SelectedView == "experiment-lab"
                        && captured.ObservedSurfaceState == "experiment-lab"
                        && captured.VisibleRootIdentities.SequenceEqual(["ExperimentLabPanel"]),
                        $"known Experiment Lab selection should produce feature-bound evidence: {captured.Message}");
                    var evidencePath = Path.Combine(
                        dataRoot,
                        captured.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    using (var document = JsonDocument.Parse(File.ReadAllBytes(evidencePath)))
                    {
                        var nodes = document.RootElement.GetProperty("Nodes").EnumerateArray().ToArray();
                        var visibleIdentities = nodes
                            .Where(node => node.GetProperty("IsVisible").GetBoolean())
                            .Select(node => node.GetProperty("Identity").GetString())
                            .ToArray();
                        Require(visibleIdentities.Count(identity => identity == "MatrixPanel") == 1,
                            "feature evidence should contain the closed feature-specific visible content identity exactly once");
                        var byIdentity = nodes.ToDictionary(
                            node => node.GetProperty("Identity").GetString() ?? "",
                            node => node,
                            StringComparer.Ordinal);
                        var focusedNode = byIdentity["FeatureMatrixFocusSecond"];
                        var parentSequence = focusedNode.GetProperty("ParentSequence").GetInt32();
                        var ancestors = new HashSet<string>(StringComparer.Ordinal);
                        while (parentSequence >= 0)
                        {
                            var parent = nodes.Single(node => node.GetProperty("Sequence").GetInt32() == parentSequence);
                            ancestors.Add(parent.GetProperty("Identity").GetString() ?? "");
                            var parentValue = parent.GetProperty("ParentSequence");
                            if (parentValue.ValueKind == JsonValueKind.Null) break;
                            parentSequence = parentValue.GetInt32();
                        }
                        Require(focusedNode.GetProperty("HasKeyboardFocus").GetBoolean()
                            && focusedNode.GetProperty("IsRendered").GetBoolean()
                            && ancestors.Contains("MatrixPanel"),
                            "captured feature focus was not a rendered descendant of MatrixPanel");
                    }

                    var focusBeforeNonIsolatedDenial = Keyboard.FocusedElement;
                    var nonIsolatedFeatureVerification = new AIArenaUiVerificationControlService(
                        window,
                        Path.Combine(dataRoot, "different-process-root"),
                        () => "light",
                        selectedExperimentFeatureKey: () => selectedFeature);
                    var nonIsolatedFeatureFocus = nonIsolatedFeatureVerification
                        .FocusSelectedFeatureContentAsync()
                        .GetAwaiter()
                        .GetResult();
                    Require(!nonIsolatedFeatureFocus.Ok
                        && nonIsolatedFeatureFocus.ErrorCode == "not_available"
                        && ReferenceEquals(Keyboard.FocusedElement, focusBeforeNonIsolatedDenial),
                        "a non-isolated feature-focus request must fail before mutating keyboard focus");

                    FocusManager.SetFocusedElement(window, secondFeatureButton);
                    _ = Keyboard.Focus(secondFeatureButton);
                    window.UpdateLayout();
                    KeyboardFocusChangedEventHandler? reparentOnFocus = null;
                    reparentOnFocus = (_, _) =>
                    {
                        firstFeatureButton.GotKeyboardFocus -= reparentOnFocus;
                        featureFocusPanel.Children.Remove(firstFeatureButton);
                        experimentLab.Children.Add(firstFeatureButton);
                    };
                    firstFeatureButton.GotKeyboardFocus += reparentOnFocus;
                    var reparentedTarget = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!reparentedTarget.Ok
                        && reparentedTarget.ErrorCode == "not_available"
                        && experimentLab.Children.Contains(firstFeatureButton),
                        "feature focus trusted a target reparented outside the selected feature during GotKeyboardFocus");
                    experimentLab.Children.Remove(firstFeatureButton);
                    featureFocusPanel.Children.Insert(0, firstFeatureButton);
                    window.UpdateLayout();

                    experimentLab.Children.Remove(featureContent);
                    shellRoot.Children.Add(featureContent);
                    window.UpdateLayout();
                    var relocatedRoot = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!relocatedRoot.Ok && relocatedRoot.ErrorCode == "not_available",
                        "feature focus accepted a rendered feature root outside ExperimentLabPanel");
                    shellRoot.Children.Remove(featureContent);
                    experimentLab.Children.Add(featureContent);
                    window.UpdateLayout();

                    featureContent.Opacity = 0;
                    window.UpdateLayout();
                    var transparentRoot = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!transparentRoot.Ok && transparentRoot.ErrorCode == "not_available",
                        "selected-feature focus entered a non-rendered transparent feature root");
                    featureContent.Opacity = 1;
                    featureContent.Child = new TextBlock { Text = "Static QA surface" };
                    window.UpdateLayout();
                    var noTabStop = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!noTabStop.Ok && noTabStop.ErrorCode == "not_available",
                        "selected-feature focus claimed success without a tracked tab stop");
                    featureContent.Child = featureFocusPanel;
                    var duplicateFeatureRoot = new Border
                    {
                        Name = "MatrixPanel",
                        Child = new Button { Name = "DuplicateFeatureFocus", Focusable = true, IsTabStop = true }
                    };
                    experimentLab.Children.Add(duplicateFeatureRoot);
                    window.UpdateLayout();
                    var duplicateRoot = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!duplicateRoot.Ok && duplicateRoot.ErrorCode == "not_available",
                        "selected-feature focus chose one of two duplicate feature roots");
                    experimentLab.Children.Remove(duplicateFeatureRoot);

                    FrameworkElement deepFocusTree = new Button
                    {
                        Name = "TooDeepFeatureFocus",
                        Focusable = true,
                        IsTabStop = true
                    };
                    for (var depth = 0; depth <= 128; depth++)
                    {
                        deepFocusTree = new Border { Child = deepFocusTree };
                    }
                    featureContent.Child = deepFocusTree;
                    window.UpdateLayout();
                    var depthBounded = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!depthBounded.Ok && depthBounded.ErrorCode == "not_available",
                        "selected-feature focus traversed beyond the shared 128-depth evidence boundary");

                    var overNodeLimit = new Grid();
                    overNodeLimit.Children.Add(new Button
                    {
                        Name = "EarlyButUntrustedFeatureFocus",
                        Focusable = true,
                        IsTabStop = true
                    });
                    for (var index = 0; index <= 5000; index++)
                    {
                        overNodeLimit.Children.Add(new Border());
                    }
                    featureContent.Child = overNodeLimit;
                    window.UpdateLayout();
                    var nodeBounded = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!nodeBounded.Ok && nodeBounded.ErrorCode == "not_available",
                        "selected-feature focus trusted an early target in a tree beyond the 5,000-node evidence boundary");

                    selectedFeature = "not-registered";
                    var unknownFocus = verification.FocusSelectedFeatureContentAsync().GetAwaiter().GetResult();
                    Require(!unknownFocus.Ok && unknownFocus.ErrorCode == "not_available",
                        "selected-feature focus accepted an unknown registry key");
                    var unknownState = verification.DebugObservedExpectedState(renderDpiScale: 1.0);
                    Require(unknownState.StartsWith(
                            "experiment-lab.feature-unavailable.closed.light.w960.d1-0.",
                            StringComparison.Ordinal),
                        "unregistered feature keys must fail closed in observed state");

                    var defaultDeny = new AIArenaUiVerificationControlService(window, dataRoot, () => "light");
                    var unavailableState = defaultDeny.DebugObservedExpectedState(renderDpiScale: 1.0);
                    Require(unavailableState.Contains(".feature-unavailable.", StringComparison.Ordinal),
                        "startup or omitted feature observation must default deny rather than infer a feature");
                    var callerClaim = defaultDeny.CaptureStructureAsync(
                        "feature/caller-claim.json",
                        UiEvidenceTreeFingerprint,
                        observedState,
                        renderDpiScale: 1.0).GetAwaiter().GetResult();
                    Require(!callerClaim.Ok
                        && callerClaim.ErrorCode == "state_mismatch"
                        && !File.Exists(Path.Combine(dataRoot, "exports", "qa", "ui-structure", "feature", "caller-claim.json")),
                        "caller-supplied expected state must not substitute for missing observed feature state");
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", previousDataRoot);
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void SharedPointerFeedbackReleasesDeadStaMotionSubscriptions()
    {
        SystemMotionPreferences.ClearQaOverride();
        RunStaTest(() =>
        {
            var strandedButton = new Button();
            ArenaMotion.SetIsPointerFeedbackEnabled(strandedButton, true);
            strandedButton.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, strandedButton));
        });

        try
        {
            RunStaTest(() =>
            {
                SystemMotionPreferences.SetQaAnimationsEnabledOverride(true);
                SystemMotionPreferences.SetQaAnimationsEnabledOverride(false);
            });
        }
        finally
        {
            SystemMotionPreferences.ClearQaOverride();
        }
    }

    static void UiVerificationAdvancesPrivacySafeFocusAndOverridesMotionOnlyInIsolation()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-matrix-{Guid.NewGuid():N}");
        var previousDataRoot = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", dataRoot);
        try
        {
            RunStaTest(() =>
            {
                var firstButton = new Button
                {
                    Name = "QaFocusFirst",
                    Content = UiEvidencePrivateText,
                    Focusable = true,
                    IsTabStop = true
                };
                AutomationProperties.SetAutomationId(firstButton, "QaFocusFirst");
                var privateButton = new Button
                {
                    Content = UiEvidenceProviderText,
                    Focusable = true,
                    IsTabStop = true
                };
                AutomationProperties.SetAutomationId(privateButton, UiEvidenceRuntimeAutomationSecret);
                var hiddenButton = new Button
                {
                    Visibility = Visibility.Collapsed,
                    Focusable = false,
                    IsTabStop = false
                };
                var unnamedButton = new Button
                {
                    Focusable = true,
                    IsTabStop = true
                };
                var duplicateNameFirst = new Button
                {
                    Name = "HeaderSite",
                    Focusable = true,
                    IsTabStop = true
                };
                var duplicateNameSecond = new Button
                {
                    Name = "HeaderSite",
                    Focusable = true,
                    IsTabStop = true
                };
                var lastButton = new Button
                {
                    Name = "QaFocusLast",
                    Content = UiEvidencePrivatePath,
                    Focusable = true,
                    IsTabStop = true
                };
                AutomationProperties.SetAutomationId(lastButton, "QaFocusLast");
                var panel = new StackPanel
                {
                    Children =
                    {
                        firstButton,
                        privateButton,
                        hiddenButton,
                        unnamedButton,
                        duplicateNameFirst,
                        duplicateNameSecond,
                        lastButton
                    }
                };
                KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
                var window = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = panel
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    FocusManager.SetFocusedElement(window, firstButton);
                    _ = Keyboard.Focus(firstButton);
                    window.UpdateLayout();

                    var service = new AIArenaUiVerificationControlService(window, dataRoot, () => "high-contrast");
                    Require(AIArenaUiVerificationControlService.IsIsolatedQaDataRoot(dataRoot), "QA process overrides should require an exact AI_ARENA_DATA_DIR match");
                    var invalidFocus = service.AdvanceKeyboardFocusAsync("sideways").GetAwaiter().GetResult();
                    Require(!invalidFocus.Ok && invalidFocus.ErrorCode == "invalid_argument", "unsupported focus directions should be rejected without moving focus");

                    var next = service.AdvanceKeyboardFocusAsync("next").GetAwaiter().GetResult();
                    Require(next.Ok
                        && next.Moved
                        && next.FocusChanged
                        && next.BeforeIdentity == "QaFocusFirst"
                        && next.BeforeControlType == "Button"
                        && next.AfterIdentity.StartsWith("redacted-", StringComparison.Ordinal)
                        && next.AfterControlType == "Button", "QA focus traversal should follow WPF tab order and redact runtime automation identities");
                    var nextJson = AIArenaControlPlaneProtocol.Serialize(next);
                    Require(!nextJson.Contains(UiEvidencePrivateText, StringComparison.Ordinal)
                        && !nextJson.Contains(UiEvidenceProviderText, StringComparison.Ordinal)
                        && !nextJson.Contains(UiEvidencePrivatePath, StringComparison.OrdinalIgnoreCase)
                        && !nextJson.Contains(UiEvidenceRuntimeAutomationSecret, StringComparison.Ordinal), "focus traversal evidence must contain only static or hashed identities");

                    var previous = service.AdvanceKeyboardFocusAsync("previous").GetAwaiter().GetResult();
                    Require(previous.Ok
                        && previous.Moved
                        && previous.AfterIdentity == "QaFocusFirst", "reverse traversal should return to the preceding WPF tab stop deterministically");

                    FocusManager.SetFocusedElement(window, privateButton);
                    _ = Keyboard.Focus(privateButton);
                    window.UpdateLayout();
                    var unnamed = service.AdvanceKeyboardFocusAsync("next").GetAwaiter().GetResult();
                    var unnamedEvidence = service.CaptureStructureAsync(
                        "focus/unnamed.json",
                        UiEvidenceTreeFingerprint,
                        service.DebugObservedExpectedState()).GetAwaiter().GetResult();
                    Require(unnamed.Ok
                        && unnamed.AfterIdentity.StartsWith("Button#", StringComparison.Ordinal)
                        && unnamedEvidence.Ok
                        && unnamedEvidence.FocusIdentity == unnamed.AfterIdentity,
                        "unnamed focus identities should use the same visible-tree ordinal as structure evidence");

                    FocusManager.SetFocusedElement(window, duplicateNameFirst);
                    _ = Keyboard.Focus(duplicateNameFirst);
                    window.UpdateLayout();
                    var duplicateNamed = service.AdvanceKeyboardFocusAsync("next").GetAwaiter().GetResult();
                    var duplicateEvidence = service.CaptureStructureAsync(
                        "focus/duplicate-name.json",
                        UiEvidenceTreeFingerprint,
                        service.DebugObservedExpectedState()).GetAwaiter().GetResult();
                    Require(duplicateNamed.Ok
                        && duplicateNamed.Moved
                        && duplicateNamed.FocusChanged
                        && duplicateNamed.BeforeIdentity.StartsWith("HeaderSite#", StringComparison.Ordinal)
                        && duplicateNamed.AfterIdentity.StartsWith("HeaderSite#", StringComparison.Ordinal)
                        && duplicateNamed.BeforeIdentity != duplicateNamed.AfterIdentity
                        && duplicateEvidence.Ok
                        && duplicateEvidence.FocusIdentity == duplicateNamed.AfterIdentity,
                        "duplicate template names should receive distinct privacy-safe visible-tree identities shared by focus and structure evidence");
                    var duplicateEvidencePath = Path.Combine(
                        dataRoot,
                        duplicateEvidence.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    using (var duplicateDocument = JsonDocument.Parse(File.ReadAllText(duplicateEvidencePath)))
                    {
                        var duplicateRoot = duplicateDocument.RootElement;
                        var duplicateNodes = duplicateRoot.GetProperty("Nodes").EnumerateArray().ToArray();
                        var identities = duplicateNodes
                            .Select(node => node.GetProperty("Identity").GetString() ?? "")
                            .ToArray();
                        var headerSiteIdentities = identities
                            .Where(identity => identity.StartsWith("HeaderSite#", StringComparison.Ordinal))
                            .ToArray();
                        Require(identities.Distinct(StringComparer.Ordinal).Count() == identities.Length
                            && headerSiteIdentities.Length == 2
                            && headerSiteIdentities.Distinct(StringComparer.Ordinal).Count() == 2
                            && identities.Count(identity => identity == duplicateEvidence.FocusIdentity) == 1
                            && duplicateNodes.Count(node => node.GetProperty("HasKeyboardFocus").GetBoolean()) == 1
                            && identities.Contains("QaFocusFirst", StringComparer.Ordinal),
                            "structure evidence should preserve unique static identities while ordinal-disambiguating every repeated template identity");
                    }

                    var motionChanges = 0;
                    PropertyChangedEventHandler motionHandler = (_, args) =>
                    {
                        if (SystemMotionPreferences.IsAnimationPreferenceChange(args.PropertyName))
                        {
                            motionChanges++;
                        }
                    };
                    SystemMotionPreferences.PreferenceChanged += motionHandler;
                    try
                    {
                        var normal = service.SetMotionPreferenceAsync("normal").GetAwaiter().GetResult();
                        Require(normal.Ok
                            && normal.IsolatedProcess
                            && normal.OverrideActive
                            && normal.AnimationsEnabled
                            && normal.PreferenceSource == "qa-normal", "normal QA motion should be an explicit in-memory process override");
                        var animatedOverlay = new Border { Visibility = Visibility.Collapsed };
                        ArenaMotion.RevealOverlay(animatedOverlay);
                        Require(animatedOverlay.Visibility == Visibility.Visible
                            && animatedOverlay.HasAnimatedProperties, "normal QA motion should exercise the real reveal animation path");
                        ArenaMotion.CancelReveal(animatedOverlay);
                        var normalFeedback = new[] { new Border(), new Border(), new Border(), new Border() };
                        ArenaMotion.NavigationSelected(normalFeedback[0]);
                        ArenaMotion.StatusChanged(normalFeedback[1]);
                        ArenaMotion.DisclosureOpened(normalFeedback[2]);
                        ArenaMotion.RevealCard(normalFeedback[3]);
                        Require(normalFeedback.All(element => element.HasAnimatedProperties)
                            && ArenaMotion.NavigationDuration >= TimeSpan.FromMilliseconds(120)
                            && ArenaMotion.NavigationDuration <= TimeSpan.FromMilliseconds(180)
                            && ArenaMotion.FeedbackDuration >= TimeSpan.FromMilliseconds(120)
                            && ArenaMotion.FeedbackDuration <= TimeSpan.FromMilliseconds(180)
                            && ArenaMotion.DisclosureDuration >= TimeSpan.FromMilliseconds(120)
                            && ArenaMotion.DisclosureDuration <= TimeSpan.FromMilliseconds(180),
                            "navigation, status, disclosure, and card feedback should use the shared restrained 120-180 ms motion contract");
                        foreach (var element in normalFeedback) ArenaMotion.CancelReveal(element);

                        var reduced = service.SetMotionPreferenceAsync("reduced").GetAwaiter().GetResult();
                        Require(reduced.Ok
                            && reduced.OverrideActive
                            && !reduced.AnimationsEnabled
                            && reduced.PreferenceSource == "qa-reduced", "reduced QA motion should disable animations without changing persisted settings");
                        var reducedOverlay = new Border { Visibility = Visibility.Collapsed };
                        ArenaMotion.RevealOverlay(reducedOverlay);
                        Require(reducedOverlay.Visibility == Visibility.Visible
                            && !reducedOverlay.HasAnimatedProperties
                            && reducedOverlay.Opacity == 1, "reduced QA motion should reveal immediately with no animation clock");
                        var reducedFeedback = new[] { new Border(), new Border(), new Border(), new Border() };
                        ArenaMotion.NavigationSelected(reducedFeedback[0]);
                        ArenaMotion.StatusChanged(reducedFeedback[1]);
                        ArenaMotion.DisclosureOpened(reducedFeedback[2]);
                        ArenaMotion.RevealCard(reducedFeedback[3]);
                        Require(reducedFeedback.All(element => !element.HasAnimatedProperties && element.Opacity == 1),
                            "reduced motion should remove all feedback clocks without changing layout, visibility, opacity, or interaction state");

                        var reducedEvidence = service.CaptureStructureAsync(
                            "motion/reduced.json",
                            UiEvidenceTreeFingerprint,
                            service.DebugObservedExpectedState(1.5),
                            renderDpiScale: 1.5).GetAwaiter().GetResult();
                        Require(reducedEvidence.Ok
                            && reducedEvidence.DpiScale == 1.5
                            && reducedEvidence.RenderDpiScale == 1.5
                            && reducedEvidence.RenderDpiOverride
                            && reducedEvidence.DpiScaleX > 0
                            && reducedEvidence.MotionPreferenceSource == "qa-reduced"
                            && !reducedEvidence.AnimationsEnabled, "structure evidence should freeze the effective reduced-motion state");

                        var events = new AIArenaControlPlaneEventHub();
                        var published = new List<AIArenaControlEvent>();
                        using var subscription = events.Subscribe(published.Add);
                        var handler = new AIArenaAppControlHandler(
                            new AIArenaScreenshotControlService(window, dataRoot),
                            events,
                            verification: service);
                        Require(AIArenaControlPlaneProtocol.TryParseRequest(
                            """{"id":"focus","command":"app.qa.focus.advance","args":{"direction":"next"}}""",
                            out var focusRequest,
                            out _), "focus command should parse at the authenticated protocol boundary");
                        var focusResponse = handler.ExecuteAsync(focusRequest).GetAwaiter().GetResult();
                        Require(focusResponse.Ok
                            && focusResponse.Data is AIArenaQaFocusTraversalResult
                            && published.Any(item => item.Type == "app.qa.focus.advanced"), "app handler should route focus traversal and publish privacy-safe evidence");
                        Require(AIArenaControlPlaneProtocol.TryParseRequest(
                            """{"id":"motion","command":"app.qa.motion.set","args":{"mode":"system"}}""",
                            out var motionRequest,
                            out _), "motion command should parse at the authenticated protocol boundary");
                        var system = handler.ExecuteAsync(motionRequest).GetAwaiter().GetResult();
                        Require(system.Ok
                            && system.Data is AIArenaQaMotionPreferenceResult systemResult
                            && !systemResult.OverrideActive
                            && systemResult.PreferenceSource == "system"
                            && systemResult.AnimationsEnabled == systemResult.SystemAnimationsEnabled
                            && published.Any(item => item.Type == "app.qa.motion.changed"), "system mode should clear the in-memory override through the focused handler");

                        _ = service.SetMotionPreferenceAsync("normal").GetAwaiter().GetResult();
                        service.ResetProcessOverrides();
                        Require(!SystemMotionPreferences.HasQaOverride
                            && SystemMotionPreferences.PreferenceSource == "system"
                            && motionChanges >= 4, "process shutdown reset should restore Windows motion and notify live consumers");

                        var nonIsolatedService = new AIArenaUiVerificationControlService(
                            window,
                            Path.Combine(dataRoot, "different-process-root"),
                            () => "high-contrast");
                        var denied = nonIsolatedService.SetMotionPreferenceAsync("reduced").GetAwaiter().GetResult();
                        Require(!denied.Ok
                            && denied.ErrorCode == "not_available"
                            && !denied.IsolatedProcess
                            && !SystemMotionPreferences.HasQaOverride, "normal app processes must reject QA motion overrides without mutating the effective preference");
                    }
                    finally
                    {
                        SystemMotionPreferences.PreferenceChanged -= motionHandler;
                        SystemMotionPreferences.ClearQaOverride();
                    }

                    var serializedEvents = AIArenaControlPlaneProtocol.Serialize(next);
                    Require(!serializedEvents.Contains(dataRoot, StringComparison.OrdinalIgnoreCase), "QA focus and motion responses must not expose the isolated data root");
                    var mainWindow = ReadMainWindowSource();
                    Require(mainWindow.Contains("_appControlHandler.ResetProcessOverrides()", StringComparison.Ordinal), "closing the app should always clear process-only QA overrides");
                }
                finally
                {
                    SystemMotionPreferences.ClearQaOverride();
                    window.Close();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", previousDataRoot);
            SystemMotionPreferences.ClearQaOverride();
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void UiVerificationCapturesOwnerBoundAtomicShellFocusCycle()
    {
        var owner = Guid.NewGuid().ToString("N");
        var mismatchedOwner = Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-ui-focus-capture-{owner}");
        var ownerMarker = Path.Combine(dataRoot, ".ai-arena-qa-owner");
        var previousDataRoot = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        var previousOwner = Environment.GetEnvironmentVariable(AIArenaControlPlaneProtocol.OwnerEnvironmentVariable);
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(ownerMarker, owner + "\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", dataRoot);
            Environment.SetEnvironmentVariable(AIArenaControlPlaneProtocol.OwnerEnvironmentVariable, owner);
            RunStaTest(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var navigationRail = new ShellNavigationRailControl();
                var anchor = navigationRail.ArenaNavigationButton;
                var hiddenExperiment = navigationRail.ExperimentLabNavigationButton;
                hiddenExperiment.Visibility = MainWindow.IsExperimentLabEnabled(new WpfSettings())
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                var peer = navigationRail.AgentNavigationButton;
                var trailing = new Button
                {
                    Name = "QaFocusTrailing",
                    Content = UiEvidencePrivatePath,
                    ToolTip = UiEvidenceProviderText,
                    Focusable = true,
                    IsTabStop = true
                };
                AutomationProperties.SetHelpText(trailing, UiEvidencePrivateText);
                var panel = new Grid();
                panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                panel.Children.Add(navigationRail);
                Grid.SetRow(trailing, 1);
                panel.Children.Add(trailing);
                KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
                var window = new Window
                {
                    Width = 1000,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = panel
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    Require(hiddenExperiment.Visibility == Visibility.Collapsed
                            && !hiddenExperiment.IsVisible
                            && AIArenaUiVerificationControlService.IsFocusCapturePeerIdentity(peer.Name),
                        "Debug-off focus verification should skip the hidden Experiment Lab route and retain a valid visible peer");
                    FocusManager.SetFocusedElement(window, trailing);
                    _ = Keyboard.Focus(trailing);
                    window.UpdateLayout();

                    var focusSequence = new List<string>();
                    var interleaveQueued = false;
                    anchor.GotKeyboardFocus += (_, _) =>
                    {
                        focusSequence.Add(anchor.Name);
                        if (!interleaveQueued)
                        {
                            interleaveQueued = true;
                            _ = dispatcher.BeginInvoke(
                                () => focusSequence.Add("interleaved-dispatch"),
                                DispatcherPriority.Input);
                        }
                    };
                    peer.GotKeyboardFocus += (_, _) => focusSequence.Add(peer.Name);
                    trailing.GotKeyboardFocus += (_, _) => focusSequence.Add(trailing.Name);

                    var service = new AIArenaUiVerificationControlService(window, dataRoot, () => "dark-blue");
                    using var leaseAcquired = new ManualResetEventSlim(false);
                    service.DebugQaOwnerLeaseAcquired = leaseAcquired.Set;
                    Require(AIArenaUiVerificationControlService.IsOwnerBoundIsolatedQaDataRoot(dataRoot),
                        "atomic focus capture should require the exact isolated data root, owner namespace, and owner marker");
                    var events = new AIArenaControlPlaneEventHub();
                    var published = new List<AIArenaControlEvent>();
                    using var subscription = events.Subscribe(published.Add);
                    var handler = new AIArenaAppControlHandler(
                        new AIArenaScreenshotControlService(window, dataRoot),
                        events,
                        verification: service);
                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                            """{"id":"focus-capture","command":"app.qa.focus.capture","args":{}}""",
                            out var request,
                            out _),
                        "atomic focus capture should parse at the authenticated protocol boundary");
                    var responseTask = Task.Run(() => handler.ExecuteAsync(request));
                    Require(leaseAcquired.Wait(TimeSpan.FromSeconds(5)),
                        "atomic focus capture did not acquire its owner authorization lease before UI dispatch");
                    var markerWriteBlocked = false;
                    try
                    {
                        using var writer = new FileStream(
                            ownerMarker,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (IOException)
                    {
                        markerWriteBlocked = true;
                    }
                    Require(markerWriteBlocked,
                        "owner marker could be replaced while its authorized focus capture was queued");
                    var response = QaPumpDispatcherTask(responseTask);
                    using (var writer = new FileStream(
                        ownerMarker,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        Require(writer.CanWrite, "owner authorization lease was not released after atomic focus capture");
                    }
                    var captured = response.Data as AIArenaQaFocusCaptureResult;
                    Require(response.Ok
                        && captured is { Ok: true, FailureStage: "none", IsolatedProcess: true, OwnerBound: true }
                        && captured.Anchor.Direction == "anchor"
                        && captured.Anchor.BeforeIdentity == "QaFocusTrailing"
                        && captured.Anchor.AfterIdentity == AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity
                        && captured.Next.BeforeIdentity == AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity
                        && captured.Next.AfterIdentity == peer.Name
                        && captured.Previous.BeforeIdentity == peer.Name
                        && captured.Previous.AfterIdentity == AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity
                        && captured.Capture.BeforeIdentity == AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity
                        && captured.Capture.AfterIdentity == peer.Name
                        && focusSequence.Take(4).SequenceEqual([
                            AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity,
                            peer.Name,
                            AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity,
                            peer.Name])
                        && published.Count(item => item.Type == "app.qa.focus.captured") == 1,
                        "one control-plane request should anchor and complete the exact contiguous next/previous/next shell focus cycle");
                    dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Require(focusSequence.SequenceEqual([
                            AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity,
                            peer.Name,
                            AIArenaUiVerificationControlService.FocusCaptureAnchorIdentity,
                            peer.Name,
                            "interleaved-dispatch"]),
                        "a queued same-priority dispatcher callback interleaved with the UI-thread-atomic focus cycle");

                    var serialized = AIArenaControlPlaneProtocol.Serialize(response);
                    Require(serialized.Length < 8 * 1024
                        && !serialized.Contains(dataRoot, StringComparison.OrdinalIgnoreCase)
                        && !serialized.Contains(owner, StringComparison.OrdinalIgnoreCase)
                        && !serialized.Contains(UiEvidencePrivateText, StringComparison.Ordinal)
                        && !serialized.Contains(UiEvidenceProviderText, StringComparison.Ordinal)
                        && !serialized.Contains(UiEvidencePrivatePath, StringComparison.OrdinalIgnoreCase),
                        "atomic focus evidence should be bounded and contain only privacy-safe static identities");

                    FocusManager.SetFocusedElement(window, trailing);
                    _ = Keyboard.Focus(trailing);
                    window.UpdateLayout();
                    File.WriteAllText(ownerMarker, mismatchedOwner + "\n", new UTF8Encoding(false));
                    var denied = handler.ExecuteAsync(request).GetAwaiter().GetResult();
                    Require(!denied.Ok
                        && denied.ErrorCode == "not_available"
                        && denied.Data is AIArenaQaFocusCaptureResult
                        {
                            Ok: false,
                            FailureStage: "owner-boundary",
                            IsolatedProcess: true,
                            OwnerBound: false
                        }
                        && trailing.IsKeyboardFocused
                        && published.Count(item => item.Type == "app.qa.focus.captured") == 1,
                        "an owner-marker mismatch should fail closed before focus mutation or success publication");

                    File.WriteAllText(ownerMarker, new string('a', 41), new UTF8Encoding(false));
                    var oversizedMarker = handler.ExecuteAsync(request).GetAwaiter().GetResult();
                    Require(!oversizedMarker.Ok
                        && oversizedMarker.Data is AIArenaQaFocusCaptureResult
                        {
                            FailureStage: "owner-boundary",
                            OwnerBound: false
                        },
                        "an oversized owner marker should fail before any unbounded content read");

                    File.WriteAllText(ownerMarker, owner + "\n", new UTF8Encoding(false));
                    anchor.Visibility = Visibility.Collapsed;
                    window.UpdateLayout();
                    var missingAnchor = handler.ExecuteAsync(request).GetAwaiter().GetResult();
                    Require(!missingAnchor.Ok
                        && missingAnchor.Data is AIArenaQaFocusCaptureResult
                        {
                            FailureStage: "anchor",
                            OwnerBound: true
                        } missingAnchorResult
                        && !missingAnchorResult.Anchor.Ok
                        && !missingAnchorResult.Next.Ok
                        && !missingAnchorResult.Previous.Ok
                        && !missingAnchorResult.Capture.Ok,
                        "a missing rendered named shell anchor should return a fixed safe failure class and complete bounded step shapes");
                }
                finally
                {
                    window.Close();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", previousDataRoot);
            Environment.SetEnvironmentVariable(AIArenaControlPlaneProtocol.OwnerEnvironmentVariable, previousOwner);
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static void QaScreenshotRenderDpiPreservesDipViewportAndBoundsRasterScale()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-render-dpi-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var window = new Window
                {
                    Width = 320,
                    Height = 200,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Content = new Border { Background = System.Windows.Media.Brushes.Navy }
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var service = new AIArenaScreenshotControlService(window, dataRoot);
                    Require(AIArenaScreenshotControlService.IsSupportedQaRenderDpiScale(1.0)
                        && AIArenaScreenshotControlService.IsSupportedQaRenderDpiScale(1.5)
                        && AIArenaScreenshotControlService.IsSupportedQaRenderDpiScale(2.0)
                        && !AIArenaScreenshotControlService.IsSupportedQaRenderDpiScale(1.25)
                        && !AIArenaScreenshotControlService.IsSupportedQaRenderDpiScale(double.NaN), "QA screenshot raster density should accept only the reviewed bounded matrix");

                    var invalid = QaPumpDispatcherTask(service.CaptureAsync("invalid.png", renderDpiScale: 1.25));
                    Require(!invalid.Ok
                        && invalid.ErrorCode == "invalid_argument"
                        && !File.Exists(Path.Combine(dataRoot, "exports", "screenshots", "invalid.png")), "invalid render DPI should fail before creating an artifact");

                    var captured = QaPumpDispatcherTask(service.CaptureAsync("scale-150.png", renderDpiScale: 1.5));
                    Require(captured.Ok
                        && captured.RenderDpiOverride
                        && captured.RenderDpiScale == 1.5
                        && captured.DisplayDpiScaleX > 0
                        && captured.DisplayDpiScaleY > 0
                        && Math.Abs(captured.ViewportWidthDip - window.ActualWidth) <= 0.1
                        && Math.Abs(captured.ViewportHeightDip - window.ActualHeight) <= 0.1
                        && captured.PixelWidth == (int)Math.Ceiling(captured.ViewportWidthDip * 1.5)
                        && captured.PixelHeight == (int)Math.Ceiling(captured.ViewportHeightDip * 1.5), "QA render scale should change raster density while preserving the measured DIP viewport");
                    Require(ReadPngChunkTypes(captured.Path).SequenceEqual(["IHDR", "IDAT", "IEND"]),
                        "QA screenshots should contain only essential PNG chunks and no ancillary text, profile, or physical-density metadata");

                    var defaultCapture = QaPumpDispatcherTask(service.CaptureAsync("scale-system.png"));
                    Require(defaultCapture.Ok
                        && !defaultCapture.RenderDpiOverride
                        && defaultCapture.RenderDpiScale == defaultCapture.DisplayDpiScaleX
                        && defaultCapture.PixelWidth == (int)Math.Ceiling(defaultCapture.ViewportWidthDip * defaultCapture.DisplayDpiScaleX), "default screenshot behavior should continue following the display DPI");
                }
                finally
                {
                    window.Close();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    static IReadOnlyList<string> ReadPngChunkTypes(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        Require(signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "QA screenshot should have a valid PNG signature");

        var chunkTypes = new List<string>();
        Span<byte> header = stackalloc byte[8];
        while (true)
        {
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            Require(length <= int.MaxValue, "QA screenshot PNG chunk should be bounded");
            var chunkType = System.Text.Encoding.ASCII.GetString(header[4..8]);
            chunkTypes.Add(chunkType);
            stream.Seek(checked((int)length + 4), SeekOrigin.Current);
            if (chunkType == "IEND")
            {
                Require(stream.Position == stream.Length, "QA screenshot should not contain bytes after IEND");
                return chunkTypes;
            }
        }
    }

    static void RealWpfAutomationArtifactPassesAuthoritativeEvidenceCli()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-real-ui-bundle-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var window = new Window
                {
                    Width = 960,
                    Height = 640,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1200,
                    Top = SystemParameters.VirtualScreenTop - 1200,
                    Background = System.Windows.Media.Brushes.Navy,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Content = new Button
                    {
                        Name = "QaRealArtifactButton",
                        Content = UiEvidencePrivateText,
                        Focusable = true,
                        Background = new System.Windows.Media.LinearGradientBrush(
                            System.Windows.Media.Colors.Navy,
                            System.Windows.Media.Colors.SteelBlue,
                            0)
                    }
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var verification = new AIArenaUiVerificationControlService(window, dataRoot, () => "dark-blue");
                    var expectedState = verification.DebugObservedExpectedState(1.0);
                    var automation = QaPumpDispatcherTask(verification.CaptureStructureAsync(
                        "real/tree.json",
                        UiEvidenceTreeFingerprint,
                        expectedState,
                        renderDpiScale: 1.0));
                    var screenshot = QaPumpDispatcherTask(
                        new AIArenaScreenshotControlService(window, dataRoot).CaptureAsync(
                            "real/view.png",
                            renderDpiScale: 1.0));
                    Require(automation.Ok
                        && !automation.Truncated
                        && screenshot.Ok
                        && automation.RenderDpiScale == screenshot.RenderDpiScale
                        && automation.ViewportWidthDip == (int)Math.Round(screenshot.ViewportWidthDip, MidpointRounding.AwayFromZero)
                        && automation.ViewportHeightDip == (int)Math.Round(screenshot.ViewportHeightDip, MidpointRounding.AwayFromZero), "real WPF screenshot and automation captures should freeze matching artifact provenance");

                    var exportsRoot = NativeDataPaths.ExportsRoot(dataRoot);
                    var automationPath = Path.Combine(
                        dataRoot,
                        automation.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var currentAutomationRoot = JsonNode.Parse(File.ReadAllBytes(automationPath))?.AsObject()
                        ?? throw new InvalidOperationException("Current real WPF automation evidence was unavailable.");
                    Require((string?)currentAutomationRoot["Schema"] == "ai_arena.ui_structure_evidence.v2"
                        && currentAutomationRoot["Nodes"]!.AsArray().All(node =>
                            node?["IsRendered"] is not null
                            && node["EffectiveOpacity"] is not null
                            && node["IntersectsViewport"] is not null),
                        "the real WPF producer should continue emitting the enhanced v2 renderability schema");

                    currentAutomationRoot["Schema"] = "ai_arena.ui_structure_evidence.v1";
                    foreach (var node in currentAutomationRoot["Nodes"]!.AsArray().Select(item => item!.AsObject()))
                    {
                        _ = node.Remove("BoundsX");
                        _ = node.Remove("BoundsY");
                        _ = node.Remove("BoundsWidth");
                        _ = node.Remove("BoundsHeight");
                        _ = node.Remove("EffectiveOpacity");
                        _ = node.Remove("IntersectsViewport");
                        _ = node.Remove("IsRendered");
                    }
                    var legacyAutomationBytes = JsonSerializer.SerializeToUtf8Bytes(currentAutomationRoot);
                    var legacyAutomationPath = Path.Combine(Path.GetDirectoryName(automationPath)!, "tree-v1.json");
                    File.WriteAllBytes(legacyAutomationPath, legacyAutomationBytes);
                    var automationRelativePath = Path.GetRelativePath(exportsRoot, legacyAutomationPath).Replace('\\', '/');
                    var legacyAutomationSha256 = QaSha256(legacyAutomationBytes);
                    var screenshotRelativePath = Path.GetRelativePath(exportsRoot, screenshot.Path).Replace('\\', '/');
                    Require(!Path.IsPathRooted(automationRelativePath)
                        && !Path.IsPathRooted(screenshotRelativePath)
                        && File.Exists(legacyAutomationPath)
                        && File.Exists(screenshot.Path), "real WPF evidence should resolve only through bundle-relative artifact identifiers");

                    const string automationId = "artifact:real-automation";
                    const string screenshotId = "artifact:real-screenshot";
                    var observed = new ArenaEvidenceAssertion(
                        "evidence:real-wpf",
                        ArenaEvidenceState.Observed,
                        "Recorded by deterministic local UI verification.",
                        screenshotId);
                    var unavailable = new ArenaEvidenceAssertion(
                        "evidence:no-provider",
                        ArenaEvidenceState.Unavailable,
                        "No live provider is required for the UI artifact contract.",
                        Limitation: "No external provider is required.");
                    var automationCapturedAt = automation.CapturedAtUtc!.Value;
                    var screenshotCapturedAt = screenshot.CapturedAt!.Value;
                    var startedAt = (automationCapturedAt < screenshotCapturedAt
                        ? automationCapturedAt
                        : screenshotCapturedAt).AddSeconds(-1);
                    var completedAt = (automationCapturedAt > screenshotCapturedAt
                        ? automationCapturedAt
                        : screenshotCapturedAt).AddSeconds(1);
                    var automationProvenance = new ArenaQaArtifactProvenance(
                        UiEvidenceTreeFingerprint,
                        automationCapturedAt,
                        automation.Theme,
                        automation.ViewportWidthDip,
                        automation.ViewportHeightDip,
                        (decimal)automation.RenderDpiScale,
                        automation.ExpectedState,
                        null,
                        null);
                    var screenshotProvenance = automationProvenance with
                    {
                        CapturedAtUtc = screenshotCapturedAt,
                        LinkedAutomationArtifactId = automationId
                    };
                    var contract = new ArenaQaEvidenceContract(
                        ArenaContractSchemas.QaEvidence,
                        "qa:real-wpf-artifact",
                        completedAt,
                        new string('a', 40),
                        UiEvidenceTreeFingerprint,
                        ArenaQaSealManifestV1.Id,
                        true,
                        [new("map", new string('b', 40), new string('c', 64), true)],
                        startedAt,
                        completedAt,
                        ArenaQaVerdict.Partial,
                        1,
                        new("Windows", "x64", Environment.Version.ToString(), "10.0.100", "Release", true),
                        [new("dotnet", Environment.Version.ToString()), new("powershell", "7.5.2")],
                        [new("gate:real-wpf", ArenaQaGateOutcome.Pass, true, 1, new(1, 0, 0, 1), observed)],
                        [new(automationId, "automation-tree", automationRelativePath, legacyAutomationSha256, automationProvenance),
                            new(screenshotId, "rendered-ui-screenshot", screenshotRelativePath, QaSha256(File.ReadAllBytes(screenshot.Path)), screenshotProvenance)],
                        [new("performance:real-wpf", "validator-duration", 1m, "milliseconds", ArenaQaThresholdKind.Maximum, 10_000m, observed)],
                        [new("schema:qa", ArenaContractSchemas.QaEvidence, null, ArenaQaGateOutcome.Pass, observed)],
                        new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider is required."),
                        QaRequiredLimitations(),
                        new(false, null, null, [], [], unavailable),
                        [observed]);
                    Directory.CreateDirectory(exportsRoot);
                    var evidencePath = Path.Combine(exportsRoot, "qa-evidence.json");
                    File.WriteAllText(
                        evidencePath,
                        ArenaContractCodec.Serialize(contract),
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    var validatorAssembly = typeof(VerificationEvidenceValidator).Assembly.Location;
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "dotnet",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    process.StartInfo.ArgumentList.Add(validatorAssembly);
                    process.StartInfo.ArgumentList.Add("--validate-evidence");
                    process.StartInfo.ArgumentList.Add(evidencePath);
                    Require(process.Start(), "authoritative evidence CLI did not start");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    Require(process.WaitForExit(30_000), "authoritative evidence CLI exceeded its focused integration-test timeout");
                    var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                    Require(process.ExitCode == 0
                        && output.Contains($"PASS {ArenaContractSchemas.QaEvidence} bundle", StringComparison.Ordinal)
                        && !output.Contains(dataRoot, StringComparison.OrdinalIgnoreCase)
                        && !File.ReadAllText(evidencePath).Contains(dataRoot, StringComparison.OrdinalIgnoreCase), $"real WPF evidence was rejected by --validate-evidence: {output}");
                }
                finally
                {
                    window.Close();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static void WorkspacePageHeaderHostsResponsiveAccessibleContract()
    {
        RunStaTest(() =>
        {
            var requested = 0;
            var header = new WorkspacePageHeaderControl
            {
                Title = "Context & Prompt Inspector",
                Description = "Inspect bounded context assembly, prompt inputs, and current evidence without exposing private prompt content.",
                Status = "Revalidating 12 current records",
                StatusKind = "Revalidating",
                PrimaryActionText = "Refresh evidence",
                PrimaryActionAutomationName = "Refresh inspector evidence",
                PrimaryActionHelpText = "Reload the current bounded inspector evidence."
            };
            header.PrimaryActionRequested += (_, _) => requested++;
            var host = new Window
            {
                Width = 1500,
                Height = 260,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = header
            };
            try
            {
                host.Show();
                foreach (var width in new[] { 1500d, 960d })
                {
                    host.Width = width;
                    host.UpdateLayout();
                    Require(!header.UsesCompactLayout
                        && Grid.GetRow(header.PrimaryActionButton) == 0
                        && Grid.GetColumn(header.PrimaryActionButton) == 1
                        && header.ActualWidth <= width
                        && header.DescriptionText.ActualWidth <= header.ActualWidth,
                        $"workspace header did not retain its wide title/purpose/status/action contract at {width:0} DIP");
                }

                header.Width = 746;
                host.UpdateLayout();
                Require(header.UsesCompactLayout
                    && Grid.GetRow(header.PrimaryActionButton) == 1
                    && Grid.GetColumn(header.PrimaryActionButton) == 0
                    && header.PrimaryActionButton.HorizontalAlignment == HorizontalAlignment.Left
                    && header.StatusText.TextWrapping == TextWrapping.NoWrap
                    && header.StatusText.TextTrimming == TextTrimming.CharacterEllipsis
                    && header.StatusChip.ActualWidth <= 300.5
                    && ToolTipService.GetToolTip(header.StatusChip)?.ToString() == header.Status,
                    "workspace header did not wrap its contextual action or constrain its visible status at the approximately 746-DIP hosted tier");
                var statusPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(header.StatusText)
                    ?? throw new InvalidOperationException("Workspace header status did not create a TextBlock automation peer.");
                Require(AutomationProperties.GetName(header) == "Context & Prompt Inspector page header"
                    && AutomationProperties.GetHelpText(header) == header.Description
                    && statusPeer.GetName().Contains(header.Status, StringComparison.Ordinal)
                    && statusPeer.GetHelpText() == header.Status
                    && statusPeer.GetItemStatus() == "Revalidating"
                    && statusPeer.GetLiveSetting() == AutomationLiveSetting.Polite
                    && AutomationProperties.GetName(header.PrimaryActionButton) == "Refresh inspector evidence"
                    && AutomationProperties.GetHelpText(header.PrimaryActionButton) == "Reload the current bounded inspector evidence.",
                    "workspace header automation did not expose title, purpose, truthful peer-backed live status, and contextual action");
                Require(header.PrimaryActionButton.Focus(), "workspace header primary action was not keyboard focusable");
                header.PrimaryActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(requested == 1, "workspace header did not route its contextual primary action");

                header.Status = "Ready for bounded inspection";
                header.StatusKind = "Ready";
                host.UpdateLayout();
                Require(statusPeer.GetName().Contains("Ready for bounded inspection", StringComparison.Ordinal)
                    && statusPeer.GetHelpText() == "Ready for bounded inspection"
                    && statusPeer.GetItemStatus() == "Ready"
                    && statusPeer.GetLiveSetting() == AutomationLiveSetting.Polite,
                    "workspace header status updates did not remain available through the live TextBlock automation peer");

                header.Status = "";
                header.PrimaryActionText = "";
                host.UpdateLayout();
                Require(header.StatusChip.Visibility == Visibility.Collapsed
                    && header.PrimaryActionButton.Visibility == Visibility.Collapsed
                    && header.CompactActionRow.Height.Value == 0,
                    "workspace header optional status and action left duplicate or empty chrome behind");

                header.IsCompactPresentation = true;
                header.AnnounceStatusChanges = false;
                header.PrimaryActionText = "Transcript filters";
                header.Status = "12 shown";
                header.Width = 746;
                host.UpdateLayout();
                header.Measure(new Size(746, double.PositiveInfinity));
                Require(!header.UsesCompactLayout
                    && Grid.GetRow(header.DescriptionText) == 0
                    && Grid.GetColumn(header.DescriptionText) == 1
                    && Grid.GetRow(header.PrimaryActionButton) == 0
                    && Grid.GetColumn(header.PrimaryActionButton) == 1
                    && header.DescriptionText.TextWrapping == TextWrapping.NoWrap
                    && header.DescriptionText.TextTrimming == TextTrimming.CharacterEllipsis
                    && statusPeer.GetLiveSetting() == AutomationLiveSetting.Off
                    && header.DesiredSize.Height <= 48,
                    "compact workspace headers should keep purpose, count, and action on one bounded row to preserve dense workspace height");
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static System.Collections.Immutable.ImmutableArray<ArenaQaAcceptedLimitation> QaRequiredLimitations() =>
        [.. ArenaQaSealManifestV1.RequiredLimitations.Select(requirement => new ArenaQaAcceptedLimitation(
            requirement.Id,
            requirement.Summary,
            false,
            new(
                requirement.EvidenceId,
                ArenaEvidenceState.Unavailable,
                requirement.EvidenceSummary,
                requirement.ReferenceId,
                null,
                requirement.EvidenceLimitation)))];

    private static string QaSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static T QaPumpDispatcherTask<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    private static void SharedMenuPopupHostsThemeFocusAndDisabledContracts()
    {
        var result = RunSharedPopupFixtureProcess(injectDispatcherFailure: false);
        Require(result.ExitCode == 0,
            $"shared popup theme fixture failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output}{result.Error}");
    }

    private static void SharedPopupFixtureReportsDispatcherFailureWithoutAppStartup()
    {
        var result = RunSharedPopupFixtureProcess(injectDispatcherFailure: true);
        Require(result.ExitCode == 1
                && result.Error.Contains("injected popup dispatcher failure", StringComparison.Ordinal)
                && result.Error.Contains(nameof(SharedPopupThemeFixtureCore), StringComparison.Ordinal)
                && result.Output.Contains("popup fixture startup isolated; windows=1", StringComparison.Ordinal)
                && !result.Output.Contains("PASS hosted shared popup theme fixture", StringComparison.Ordinal),
            $"Injected dispatcher failure did not fail the isolated fixture with its original stack: "
            + $"exit={result.ExitCode}{Environment.NewLine}{result.Output}{result.Error}");
        Console.WriteLine($"popup dispatcher failure proof: child exit={result.ExitCode}; original exception retained; fixture-only startup");
        Console.WriteLine(result.Error);
    }

    private static (int ExitCode, string Output, string Error) RunSharedPopupFixtureProcess(bool injectDispatcherFailure)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The test process executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = Path.GetDirectoryName(FindWorkspaceFile("AI Arena - WPF.sln"))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        }
        startInfo.ArgumentList.Add("--shared-popup-theme-fixture");
        if (injectDispatcherFailure) startInfo.ArgumentList.Add("--inject-dispatcher-failure");
        // The resource fixture does not start app services, but keep any future
        // accidental persistence scoped to a fresh fixture-owned data root.
        startInfo.Environment["AI_ARENA_DATA_DIR"] = Path.Combine(Path.GetTempPath(), $"ai-arena-popup-fixture-{Guid.NewGuid():N}");
        startInfo.Environment["AI_ARENA_CONTROL_OWNER"] = Guid.NewGuid().ToString("N");
        startInfo.Environment.Remove("AI_ARENA_CONTROL_TOKEN");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The shared popup theme fixture did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The shared popup theme fixture timed out.");
        }

        return (process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static int RunSharedPopupThemeFixture(bool injectDispatcherFailure)
    {
        try
        {
            RunStaTest(() => SharedPopupThemeFixtureCore(injectDispatcherFailure));
            Console.WriteLine("PASS hosted shared popup theme fixture");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void SharedPopupThemeFixtureCore(bool injectDispatcherFailure)
    {
        Require(Application.Current is null,
            "the isolated popup fixture should own the only WPF Application in its process");
        var fixture = new SharedPopupFixtureApplication();
        var application = fixture.Application;
        // Use the compiled production App.xaml, including its implicit styles.
        // Startup has already been isolated before loading these resources;
        // the production StartupUri remains intact but cannot be dispatched.
        application.InitializeComponent();

        var target = new Button { Content = "Open menu", Width = 120, Height = 36 };
        target.SetResourceReference(FrameworkElement.StyleProperty, "Arena.Button.Base");
        var enabled = new MenuItem { Header = "Enabled action" };
        var checkedItem = new MenuItem
        {
            Header = "Pinned action",
            InputGestureText = "Ctrl+P",
            IsCheckable = true,
            IsChecked = true
        };
        var icon = new TextBlock { Text = "!" };
        var iconItem = new MenuItem { Header = "Icon action", Icon = icon };
        var nestedChild = new MenuItem { Header = "Nested action" };
        var nested = new MenuItem { Header = "More actions", Items = { nestedChild } };
        var disabled = new MenuItem { Header = "Unavailable action", IsEnabled = false };
        var separator = new Separator();
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Items = { enabled, checkedItem, iconItem, nested, separator, disabled }
        };
        target.ContextMenu = menu;

        var toolTip = new ToolTip
        {
            Content = "Theme-aware help",
            PlacementTarget = target
        };
        target.ToolTip = toolTip;
        var combo = new ComboBox
        {
            Width = 220,
            Items = { "Current model", "Alternate model" },
            SelectedIndex = 0
        };
        var list = new ListBox
        {
            Width = 220,
            Items = { "Current run", "Previous run" },
            SelectedIndex = 0
        };
        var topLevelChild = new MenuItem { Header = "Top-level nested action" };
        var topLevelHeader = new MenuItem { Header = "Actions", Items = { topLevelChild } };
        var topLevelItem = new MenuItem { Header = "Direct action" };
        var menuBar = new Menu { Items = { topLevelHeader, topLevelItem } };
        var content = new StackPanel { Margin = new Thickness(12) };
        content.Children.Add(menuBar);
        content.Children.Add(target);
        content.Children.Add(combo);
        content.Children.Add(list);
        var host = new Window
        {
            Content = content,
            Width = 420,
            Height = 300,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };

        try
        {
            host.Show();
            host.Activate();
            // Finish owner-window activation before opening native popups. An
            // outstanding activation can otherwise dismiss the first ContextMenu.
            FlushSharedPopupDispatcher(host, fixture);
            fixture.AssertIsolatedStartup(host);
            Console.WriteLine("popup fixture startup isolated; windows=1");
            if (injectDispatcherFailure)
            {
                host.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    throw new InvalidOperationException("injected popup dispatcher failure")));
                FlushSharedPopupDispatcher(host, fixture);
                throw new InvalidOperationException("The injected dispatcher failure was lost.");
            }
            Require(menu.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue
                    && toolTip.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue
                    && combo.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue,
                "the hosted popup controls should consume implicit application styles without test-only style injection");

            foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
            {
                var theme = ThemePalette.Resolve(themeId);
                ShellNavigationCoordinator.ApplyThemeResources(host.Resources, theme);
                Require(ExperimentBrushMatches(application.Resources["CardBrush"] as Brush, theme.Card)
                        && ExperimentBrushMatches(application.Resources["TextBrush"] as Brush, theme.Text),
                    $"production theme application did not synchronize the {themeId} application popup scope");
                Require(ThemePalette.ContrastRatio(theme.PrimaryBorder, theme.Primary) >= 3.0
                        && ThemePalette.ContrastRatio(theme.HoverBorder, theme.NavHover) >= 3.0,
                    $"{themeId} selected and hover item boundaries did not retain 3:1 non-text contrast");
                if (themeId == "light")
                {
                    Require(ThemePalette.ContrastRatio(theme.DisabledText, theme.Disabled) >= 3.0,
                        "Light unavailable/disabled chrome did not retain a 3:1 perceivable boundary");
                }

                menu.IsOpen = true;
                FlushSharedPopupDispatcher(host, fixture);
                menu.ApplyTemplate();
                foreach (var item in new[] { enabled, checkedItem, iconItem, nested, disabled })
                {
                    item.ApplyTemplate();
                }

                var menuChrome = RequireSharedPopupPart<Border>(menu, "MenuChrome");
                var enabledChrome = RequireSharedPopupPart<Border>(enabled, "MenuItemChrome");
                var disabledChrome = RequireSharedPopupPart<Border>(disabled, "MenuItemChrome");
                Require(ExperimentBrushMatches(menuChrome.Background, theme.Card)
                        && ExperimentBrushMatches(menuChrome.BorderBrush, theme.Border),
                    $"shared context menu fell through the {themeId} application palette");
                Require(ExperimentBrushMatches(disabledChrome.Background, theme.Disabled)
                        && ExperimentBrushMatches(disabledChrome.BorderBrush, theme.DisabledBorder)
                        && ExperimentBrushMatches(disabled.Foreground, theme.DisabledText),
                    $"shared disabled menu item fell through the {themeId} palette");
                Require(enabled.Focus(), $"shared menu item was not keyboard focusable under {themeId} (menuOpen={menu.IsOpen}, loaded={enabled.IsLoaded}, visible={enabled.IsVisible}, enabled={enabled.IsEnabled}, focusable={enabled.Focusable}, source={PresentationSource.FromVisual(enabled) is not null})");
                FlushSharedPopupDispatcher(host, fixture);
                Require(ExperimentBrushMatches(enabledChrome.Background, theme.NavActive)
                        && ExperimentBrushMatches(enabledChrome.BorderBrush, theme.PrimaryBorder),
                    $"shared menu item focus was not visible under {themeId}");

                nested.IsSubmenuOpen = true;
                FlushSharedPopupDispatcher(host, fixture);
                var nestedPopup = RequireSharedPopupPart<System.Windows.Controls.Primitives.Popup>(nested, "PART_Popup");
                var nestedChrome = RequireSharedPopupPart<Border>(nested, "SubmenuChrome");
                nestedChild.ApplyTemplate();
                Require(nested.Role == MenuItemRole.SubmenuHeader
                        && nestedPopup.IsOpen
                        && ExperimentBrushMatches(nestedChrome.Background, theme.Card)
                        && ExperimentBrushMatches(nestedChrome.BorderBrush, theme.Border)
                        && RequireSharedPopupPart<System.Windows.Shapes.Path>(nested, "SubmenuArrow").Visibility == Visibility.Visible,
                    $"nested menu behavior or chrome was not preserved under {themeId}");
                Require(nestedChild.Focus(), $"nested menu item was not keyboard focusable under {themeId}");
                nested.IsSubmenuOpen = false;
                menu.IsOpen = false;
                FlushSharedPopupDispatcher(host, fixture);

                combo.IsDropDownOpen = true;
                FlushSharedPopupDispatcher(host, fixture);
                combo.ApplyTemplate();
                var dropDown = RequireSharedPopupPart<Border>(combo, "DropDown");
                var selectedComboItem = combo.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem
                    ?? throw new InvalidOperationException("Hosted ComboBox did not realize its selected item.");
                selectedComboItem.ApplyTemplate();
                var selectedComboChrome = RequireSharedPopupPart<Border>(selectedComboItem, "ItemChrome");
                var highlightTrigger = selectedComboItem.Template.Triggers
                    .OfType<Trigger>()
                    .SingleOrDefault(trigger => trigger.Property == ComboBoxItem.IsHighlightedProperty
                        && Equals(trigger.Value, true));
                Require(ExperimentBrushMatches(dropDown.Background, theme.Input)
                        && ExperimentBrushMatches(dropDown.BorderBrush, theme.Border)
                        && ExperimentBrushMatches(selectedComboChrome.Background, theme.Primary)
                        && ExperimentBrushMatches(selectedComboChrome.BorderBrush, theme.PrimaryBorder),
                    $"ComboBox popup or selected item fell through the {themeId} palette");
                Require(highlightTrigger is not null
                        && highlightTrigger.Setters.OfType<Setter>().Any(setter =>
                            setter.TargetName == "ItemChrome" && setter.Property == Border.BorderBrushProperty),
                    "ComboBoxItem highlight did not retain a concrete hover-border contract in the hosted template");
                combo.IsDropDownOpen = false;
                FlushSharedPopupDispatcher(host, fixture);

                toolTip.IsOpen = true;
                FlushSharedPopupDispatcher(host, fixture);
                toolTip.ApplyTemplate();
                var toolTipChrome = RequireSharedPopupPart<Border>(toolTip, "ToolTipChrome");
                Require(ExperimentBrushMatches(toolTipChrome.Background, theme.Card)
                        && ExperimentBrushMatches(toolTipChrome.BorderBrush, theme.Border)
                        && ExperimentBrushMatches(toolTip.Foreground, theme.Text),
                    $"shared ToolTip fell through the {themeId} application palette");
                toolTip.IsOpen = false;
                FlushSharedPopupDispatcher(host, fixture);
            }

            menu.IsOpen = true;
            FlushSharedPopupDispatcher(host, fixture);
            foreach (var item in new[] { checkedItem, iconItem, nested })
            {
                item.ApplyTemplate();
            }
            Require(RequireSharedPopupPart<System.Windows.Shapes.Path>(checkedItem, "CheckMark").Visibility == Visibility.Visible
                    && RequireSharedPopupPart<TextBlock>(checkedItem, "InputGestureText").Text == "Ctrl+P",
                "checkable menu items did not expose their checked marker and input gesture");
            Require(ReferenceEquals(RequireSharedPopupPart<ContentPresenter>(iconItem, "IconPresenter").Content, icon),
                "menu item icons were dropped by the shared role-aware template");
            Require(new System.Windows.Automation.Peers.MenuItemAutomationPeer(checkedItem)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle) is not null
                    && new System.Windows.Automation.Peers.MenuItemAutomationPeer(nested)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse) is not null
                    && new System.Windows.Automation.Peers.MenuItemAutomationPeer(enabled).GetAutomationControlType()
                        == System.Windows.Automation.Peers.AutomationControlType.MenuItem
                    && !disabled.IsEnabled,
                "shared menu popup lost check, expansion, automation type, or disabled semantics");

            menu.IsOpen = false;
            topLevelHeader.IsSubmenuOpen = true;
            FlushSharedPopupDispatcher(host, fixture);
            topLevelHeader.ApplyTemplate();
            topLevelItem.ApplyTemplate();
            var topLevelPopup = RequireSharedPopupPart<System.Windows.Controls.Primitives.Popup>(topLevelHeader, "PART_Popup");
            Require(topLevelHeader.Role == MenuItemRole.TopLevelHeader
                    && topLevelItem.Role == MenuItemRole.TopLevelItem
                    && topLevelPopup.IsOpen
                    && topLevelPopup.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom
                    && RequireSharedPopupPart<System.Windows.Shapes.Path>(topLevelHeader, "SubmenuArrow").Visibility == Visibility.Collapsed,
                "shared MenuItem template did not preserve top-level header/item roles and bottom submenu placement");
            topLevelHeader.IsSubmenuOpen = false;

            FlushSharedPopupDispatcher(host, fixture);
            var listItem = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Hosted ListBox did not realize its first item.");
            AssertSharedPointerMotion(target, host, "Arena.Button.Base");
            AssertSharedPointerMotion(listItem, host, "Arena.ListBoxItem");

            combo.IsDropDownOpen = true;
            FlushSharedPopupDispatcher(host, fixture);
            var comboItem = combo.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem
                ?? throw new InvalidOperationException("Hosted ComboBox did not realize its first popup item for pointer feedback.");
            AssertSharedPointerMotion(comboItem, host, "Arena.ComboBoxItem");
            combo.IsDropDownOpen = false;

            menu.IsOpen = true;
            FlushSharedPopupDispatcher(host, fixture);
            AssertSharedPointerMotion(enabled, host, "Arena.MenuItem");
            fixture.AssertIsolatedStartup(host);
            fixture.ThrowDispatcherFailure();
        }
        finally
        {
            SystemMotionPreferences.ClearQaOverride();
            toolTip.IsOpen = false;
            menu.IsOpen = false;
            combo.IsDropDownOpen = false;
            host.Close();
            application.Shutdown();
            fixture.ThrowDispatcherFailure();
        }
    }

    private static T RequireSharedPopupPart<T>(Control control, string name)
        where T : DependencyObject
    {
        control.ApplyTemplate();
        return control.Template.FindName(name, control) as T
            ?? throw new InvalidOperationException(
                $"{control.GetType().Name} did not realize shared template part '{name}'.");
    }

    private static void FlushSharedPopupDispatcher(Window host, SharedPopupFixtureApplication fixture)
    {
        host.UpdateLayout();
        // Popup HWNDs have their own presentation sources. Updating the owner
        // window's layout alone does not finish their queued Loaded/focus work.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));
        fixture.ThrowDispatcherFailure();
        host.UpdateLayout();
    }

    private static void AssertSharedPointerMotion(FrameworkElement element, Window host, string label)
    {
        Require(element.IsLoaded && ArenaMotion.GetIsPointerFeedbackEnabled(element),
            $"{label} did not load with the shared pointer-feedback behavior");
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        SystemMotionPreferences.SetQaAnimationsEnabledOverride(true);
        element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseEnterEvent
        });
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        });
        Require(element.HasAnimatedProperties
                && Math.Abs((double)element.GetAnimationBaseValue(UIElement.OpacityProperty) - 0.82) < 0.001
                && ArenaMotion.FeedbackDuration >= TimeSpan.FromMilliseconds(120)
                && ArenaMotion.FeedbackDuration <= TimeSpan.FromMilliseconds(180),
            $"{label} did not use the shared 120-180 ms normal-motion pressed cue");

        SystemMotionPreferences.SetQaAnimationsEnabledOverride(false);
        host.UpdateLayout();
        Require(!element.HasAnimatedProperties
                && Math.Abs(element.Opacity - 0.82) < 0.001
                && Math.Abs(element.ActualWidth - width) < 0.01
                && Math.Abs(element.ActualHeight - height) < 0.01,
            $"{label} reduced motion did not preserve the same pressed opacity and layout without an animation clock");

        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
        });
        Require(!element.HasAnimatedProperties
                && (Math.Abs(element.Opacity - 0.94) < 0.001 || Math.Abs(element.Opacity - 1) < 0.001),
            $"{label} reduced-motion pointer release did not restore the equivalent hover/rest state immediately "
            + $"(opacity {element.Opacity:F2}, over {element.IsMouseOver}, animated {element.HasAnimatedProperties})");
        element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = Mouse.MouseLeaveEvent
        });
        Require(!element.HasAnimatedProperties && Math.Abs(element.Opacity - 1) < 0.001,
            $"{label} reduced-motion pointer leave did not restore full opacity immediately");
    }
}
