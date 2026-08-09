using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIArena.Core.Persistence;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.VerificationLab;
using AIArena.Wpf;
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
                        && primaryNode.GetProperty("HasKeyboardFocus").GetBoolean(), "focused button evidence should expose control type and keyboard state");
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
                    var automationRelativePath = Path.GetRelativePath(exportsRoot, automationPath).Replace('\\', '/');
                    var screenshotRelativePath = Path.GetRelativePath(exportsRoot, screenshot.Path).Replace('\\', '/');
                    Require(!Path.IsPathRooted(automationRelativePath)
                        && !Path.IsPathRooted(screenshotRelativePath)
                        && File.Exists(automationPath)
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
                        [new(automationId, "automation-tree", automationRelativePath, automation.Sha256, automationProvenance),
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
}
