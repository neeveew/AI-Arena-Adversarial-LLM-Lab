using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed record AIArenaQaWindowSizeResult(
    bool Ok,
    string ErrorCode,
    string Message,
    int RequestedWidthDip,
    int RequestedHeightDip,
    double ActualWidthDip,
    double ActualHeightDip,
    double DpiScaleX,
    double DpiScaleY);

internal sealed record AIArenaQaFocusTraversalResult(
    bool Ok,
    string ErrorCode,
    string Message,
    string Direction,
    string BeforeIdentity,
    string BeforeControlType,
    string AfterIdentity,
    string AfterControlType,
    bool Moved,
    bool FocusChanged);

internal sealed record AIArenaQaMotionPreferenceResult(
    bool Ok,
    string ErrorCode,
    string Message,
    string RequestedMode,
    string PreferenceSource,
    bool OverrideActive,
    bool AnimationsEnabled,
    bool SystemAnimationsEnabled,
    bool IsolatedProcess);

internal sealed record AIArenaUiStructureNodeEvidence(
    int Sequence,
    int? ParentSequence,
    int VisualDepth,
    string Identity,
    string AutomationId,
    bool AutomationIdRedacted,
    string FrameworkType,
    string ControlType,
    bool IsVisible,
    bool IsEnabled,
    bool IsFocusable,
    bool HasKeyboardFocus);

internal sealed record AIArenaUiStructureEvidence(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    string CaptureMode,
    string Limitation,
    string TreeFingerprint,
    string ExpectedState,
    string ExpectedStateSource,
    string SelectedView,
    string ObservedSurfaceState,
    string DialogState,
    IReadOnlyList<string> VisibleRootIdentities,
    IReadOnlyList<string> RequiredControlIdentities,
    string Theme,
    int ViewportWidthDip,
    int ViewportHeightDip,
    double DpiScale,
    double RenderDpiScale,
    bool RenderDpiOverride,
    double ActualWidthDip,
    double ActualHeightDip,
    double DpiScaleX,
    double DpiScaleY,
    string MotionPreferenceSource,
    bool AnimationsEnabled,
    string FocusIdentity,
    int NodeCount,
    bool Truncated,
    IReadOnlyList<AIArenaUiStructureNodeEvidence> Nodes);

internal sealed record AIArenaUiStructureCaptureResult(
    bool Ok,
    string ErrorCode,
    string Message,
    string ArtifactKind,
    string PathBase,
    string RelativePath,
    long ByteSize,
    string Sha256,
    DateTimeOffset? CapturedAtUtc,
    string Schema,
    string CaptureMode,
    string Limitation,
    string TreeFingerprint,
    string ExpectedState,
    string ExpectedStateSource,
    string SelectedView,
    string ObservedSurfaceState,
    string DialogState,
    IReadOnlyList<string> VisibleRootIdentities,
    IReadOnlyList<string> RequiredControlIdentities,
    string Theme,
    int ViewportWidthDip,
    int ViewportHeightDip,
    double DpiScale,
    double RenderDpiScale,
    bool RenderDpiOverride,
    double ActualWidthDip,
    double ActualHeightDip,
    double DpiScaleX,
    double DpiScaleY,
    string MotionPreferenceSource,
    bool AnimationsEnabled,
    string FocusIdentity,
    int NodeCount,
    bool Truncated);

/// <summary>
/// Opt-in verification controls for the local QA plane. The structure artifact
/// contains only static accessibility metadata from the current WPF visual tree;
/// it never reads accessible names, help text, control content, transcripts,
/// prompts, provider values, or absolute paths.
/// </summary>
internal sealed class AIArenaUiVerificationControlService
{
    internal const string MotionModeSystem = "system";
    internal const string MotionModeNormal = "normal";
    internal const string MotionModeReduced = "reduced";
    public const int MinimumWidthDip = 960;
    public const int MaximumWidthDip = 1500;
    public const int MinimumHeightDip = 640;
    public const int MaximumHeightDip = 1000;
    internal const int MaximumNodes = 5_000;
    internal const int MaximumVisualDepth = 128;
    internal const int MaximumArtifactBytes = 4 * 1024 * 1024;
    internal const string EvidenceSchema = "ai_arena.ui_structure_evidence.v1";
    internal const string EvidenceArtifactKind = "automation-tree";
    internal const string EvidencePathBase = "data-root";
    internal const string CaptureMode = "wpf-visual-tree-accessibility";
    internal const string CaptureLimitation = "OS UI Automation and OS input are not queried; focus traversal and the privacy-safe visual-tree snapshot are programmatic and in-process. RenderDpiScale is off-screen raster density, not physical or per-monitor display DPI. Motion fields prove preference plumbing, not rendered animation playback. Accessible names, help text, and all dynamic control content are omitted.";
    internal const string ExpectedStateSource = "observed-visible-roots";

    private static readonly string[] SurfaceRootNames =
    [
        "TranscriptPanel",
        "CustomMatchPanel",
        "ExperimentLabPanel",
        "AgentWorldPanel",
        "AgentWorkspacePanel",
        "CollaboratePanel"
    ];

    private static readonly string[] ArenaRequiredControlNames =
    [
        "RootLayout",
        "ShellNavigationRail",
        "ShellTopBar",
        "TranscriptPanel",
        "TranscriptItems"
    ];

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Window window;
    private readonly string dataRoot;
    private readonly string evidenceRoot;
    private readonly Func<string> themeId;
    private readonly Func<int?>? transcriptMessageCount;
    private readonly bool isIsolatedQaProcess;

    public AIArenaUiVerificationControlService(
        Window window,
        string dataRoot,
        Func<string> themeId,
        Func<int?>? transcriptMessageCount = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(themeId);
        this.window = window;
        this.dataRoot = Path.GetFullPath(dataRoot);
        evidenceRoot = Path.Combine(NativeDataPaths.ExportsRoot(this.dataRoot), "qa", "ui-structure");
        this.themeId = themeId;
        this.transcriptMessageCount = transcriptMessageCount;
        isIsolatedQaProcess = IsIsolatedQaDataRoot(this.dataRoot);
    }

    public static bool IsSupportedSize(int widthDip, int heightDip)
    {
        return widthDip is >= MinimumWidthDip and <= MaximumWidthDip
            && heightDip is >= MinimumHeightDip and <= MaximumHeightDip;
    }

    public async Task<AIArenaQaWindowSizeResult> SetWindowSizeAsync(
        int widthDip,
        int heightDip,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedSize(widthDip, heightDip))
        {
            return WindowSizeFailure(
                "invalid_argument",
                $"QA window size must be {MinimumWidthDip}-{MaximumWidthDip} DIP wide and {MinimumHeightDip}-{MaximumHeightDip} DIP high.",
                widthDip,
                heightDip);
        }

        try
        {
            if (!window.Dispatcher.CheckAccess())
            {
                return await window.Dispatcher.InvokeAsync(
                    () => SetWindowSizeOnUiThread(widthDip, heightDip, cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken);
            }

            return SetWindowSizeOnUiThread(widthDip, heightDip, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return WindowSizeFailure(
                "not_available",
                "The current AI Arena window could not be resized for QA capture.",
                widthDip,
                heightDip);
        }
    }

    public async Task<AIArenaQaFocusTraversalResult> AdvanceKeyboardFocusAsync(
        string? direction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeFocusDirection(direction, out var normalizedDirection))
        {
            return FocusFailure(
                "invalid_argument",
                "args.direction must be 'next' or 'previous'.",
                direction?.Trim().ToLowerInvariant() ?? "");
        }

        try
        {
            if (!window.Dispatcher.CheckAccess())
            {
                return await window.Dispatcher.InvokeAsync(
                    () => AdvanceKeyboardFocusOnUiThread(normalizedDirection, cancellationToken),
                    DispatcherPriority.Input,
                    cancellationToken);
            }

            return AdvanceKeyboardFocusOnUiThread(normalizedDirection, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return FocusFailure(
                "not_available",
                "Keyboard focus traversal is not available for the current WPF visual tree.",
                normalizedDirection);
        }
    }

    public async Task<AIArenaQaMotionPreferenceResult> SetMotionPreferenceAsync(
        string? mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizeMotionMode(mode, out var normalizedMode))
        {
            return MotionFailure(
                "invalid_argument",
                "args.mode must be 'system', 'normal', or 'reduced'.",
                mode?.Trim().ToLowerInvariant() ?? "");
        }

        if (!isIsolatedQaProcess)
        {
            return MotionFailure(
                "not_available",
                "QA motion overrides require an isolated AI_ARENA_DATA_DIR process.",
                normalizedMode);
        }

        try
        {
            if (!window.Dispatcher.CheckAccess())
            {
                return await window.Dispatcher.InvokeAsync(
                    () => SetMotionPreferenceOnUiThread(normalizedMode, cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken);
            }

            return SetMotionPreferenceOnUiThread(normalizedMode, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return MotionFailure(
                "not_available",
                "The process-only QA motion preference could not be applied.",
                normalizedMode);
        }
    }

    public void ResetProcessOverrides()
    {
        SystemMotionPreferences.ClearQaOverride();
    }

    internal static bool IsIsolatedQaDataRoot(string dataRoot)
    {
        var configured = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(dataRoot))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(configured)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(dataRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public async Task<AIArenaUiStructureCaptureResult> CaptureStructureAsync(
        string? requestedPath,
        string treeFingerprint,
        string expectedState,
        CancellationToken cancellationToken = default,
        double? renderDpiScale = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSha256(treeFingerprint))
        {
            return StructureFailure(
                "invalid_argument",
                "args.treeFingerprint must be a 64-character SHA-256 value.");
        }

        if (!IsSafeExpectedState(expectedState))
        {
            return StructureFailure(
                "invalid_argument",
                "args.expectedState must be a bounded privacy-safe state identifier.");
        }

        var normalizedRenderDpiScale = 0d;
        if (renderDpiScale.HasValue
            && !AIArenaScreenshotControlService.TryNormalizeQaRenderDpiScale(
                renderDpiScale.Value,
                out normalizedRenderDpiScale))
        {
            return StructureFailure(
                "invalid_argument",
                "args.renderDpiScale must be one of 1.0, 1.5, or 2.0.");
        }
        if (renderDpiScale.HasValue)
        {
            renderDpiScale = normalizedRenderDpiScale;
        }

        if (!TryResolveEvidencePath(requestedPath, out var targetPath, out var relativePath, out var pathError))
        {
            return StructureFailure("invalid_argument", pathError);
        }

        if (File.Exists(targetPath))
        {
            return StructureFailure("already_exists", $"UI structure evidence already exists at {relativePath}.", relativePath);
        }

        AIArenaUiStructureEvidence evidence;
        try
        {
            evidence = window.Dispatcher.CheckAccess()
                ? CaptureStructureOnUiThread(treeFingerprint, renderDpiScale, cancellationToken)
                : await window.Dispatcher.InvokeAsync(
                    () => CaptureStructureOnUiThread(treeFingerprint, renderDpiScale, cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return StructureFailure(
                "not_available",
                "The current WPF visual-tree accessibility snapshot is not available.",
                relativePath);
        }

        if (!string.Equals(expectedState, evidence.ExpectedState, StringComparison.Ordinal))
        {
            return StructureFailure(
                "state_mismatch",
                "args.expectedState did not match the closed canonical state observed from the current visible WPF roots.",
                relativePath);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(JsonSerializer.Serialize(evidence, EvidenceJsonOptions));
        if (bytes.Length > MaximumArtifactBytes)
        {
            return StructureFailure(
                "not_available",
                "The bounded UI structure evidence exceeded its safe artifact limit.",
                relativePath);
        }

        return await Task.Run(
            () => WriteEvidenceAtomic(targetPath, relativePath, evidence, bytes, cancellationToken),
            cancellationToken);
    }

    internal bool TryResolveEvidencePath(
        string? requestedPath,
        out string path,
        out string relativePath,
        out string error)
    {
        path = "";
        relativePath = "";
        error = "";
        try
        {
            var root = Path.GetFullPath(evidenceRoot);
            var value = requestedPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                value = $"AI-Arena-ui-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
            }

            if (Path.IsPathRooted(value))
            {
                error = "args.path must be relative to the isolated UI evidence directory.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(value)))
            {
                value += ".json";
            }

            if (!Path.GetExtension(value).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                error = "args.path must name a JSON file.";
                return false;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, value));
            if (!IsWithinRoot(fullPath, root))
            {
                error = "A UI evidence path cannot leave the isolated UI evidence directory.";
                return false;
            }

            if (fullPath.Length > 32_000)
            {
                error = "args.path is too long.";
                return false;
            }

            path = fullPath;
            relativePath = Path.GetRelativePath(dataRoot, fullPath).Replace('\\', '/');
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "args.path is invalid.";
            return false;
        }
    }

    private AIArenaQaWindowSizeResult SetWindowSizeOnUiThread(
        int widthDip,
        int heightDip,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window.SizeToContent = SizeToContent.Manual;
        if (window.WindowState != WindowState.Normal)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Width = widthDip;
        window.Height = heightDip;
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var actualWidth = Round(window.ActualWidth);
        var actualHeight = Round(window.ActualHeight);
        var applied = Math.Abs(actualWidth - widthDip) <= 1
            && Math.Abs(actualHeight - heightDip) <= 1;
        return new AIArenaQaWindowSizeResult(
            applied,
            applied ? "" : "not_available",
            applied
                ? $"QA window set to {widthDip}x{heightDip} DIP."
                : "The desktop constrained the requested QA viewport; exact layout evidence is unavailable.",
            widthDip,
            heightDip,
            actualWidth,
            actualHeight,
            Round(dpi.DpiScaleX, 3),
            Round(dpi.DpiScaleY, 3));
    }

    private AIArenaQaFocusTraversalResult AdvanceKeyboardFocusOnUiThread(
        string direction,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window.UpdateLayout();
        var beforeElement = Keyboard.FocusedElement as DependencyObject;
        var before = DescribeFocus(beforeElement);
        var navigationDirection = direction == "previous"
            ? FocusNavigationDirection.Previous
            : FocusNavigationDirection.Next;
        var initialDirection = direction == "previous"
            ? FocusNavigationDirection.Last
            : FocusNavigationDirection.First;

        var moved = beforeElement switch
        {
            UIElement uiElement => uiElement.MoveFocus(new TraversalRequest(navigationDirection)),
            ContentElement contentElement => contentElement.MoveFocus(new TraversalRequest(navigationDirection)),
            _ => window.MoveFocus(new TraversalRequest(initialDirection))
        };

        window.UpdateLayout();
        cancellationToken.ThrowIfCancellationRequested();
        var after = DescribeFocus(Keyboard.FocusedElement as DependencyObject);
        var changed = !string.Equals(before.Identity, after.Identity, StringComparison.Ordinal)
            || !string.Equals(before.ControlType, after.ControlType, StringComparison.Ordinal);
        return new AIArenaQaFocusTraversalResult(
            true,
            "",
            moved
                ? $"Keyboard focus moved {direction}."
                : $"Keyboard focus could not move {direction} from the current element.",
            direction,
            before.Identity,
            before.ControlType,
            after.Identity,
            after.ControlType,
            moved,
            changed);
    }

    private AIArenaQaMotionPreferenceResult SetMotionPreferenceOnUiThread(
        string mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var animationsEnabled = mode switch
        {
            MotionModeNormal => true,
            MotionModeReduced => false,
            _ => (bool?)null
        };
        SystemMotionPreferences.SetQaAnimationsEnabledOverride(animationsEnabled);
        cancellationToken.ThrowIfCancellationRequested();
        return new AIArenaQaMotionPreferenceResult(
            true,
            "",
            mode == MotionModeSystem
                ? "QA motion override cleared; the process follows Windows."
                : $"Process-only QA motion mode set to {mode}.",
            mode,
            SystemMotionPreferences.PreferenceSource,
            SystemMotionPreferences.HasQaOverride,
            SystemMotionPreferences.AnimationsEnabled,
            SystemMotionPreferences.SystemAnimationsEnabled,
            isIsolatedQaProcess);
    }

    private FocusDescriptor DescribeFocus(DependencyObject? focusedElement)
    {
        if (focusedElement is null)
        {
            return new FocusDescriptor("none", "none");
        }

        if (focusedElement is not UIElement uiElement)
        {
            return new FocusDescriptor($"nonvisual-{SafeTypeName(focusedElement.GetType().Name)}", "Custom");
        }

        var identity = FindVisualIdentity(uiElement);
        return new FocusDescriptor(identity, AutomationControlType(uiElement));
    }

    private string FindVisualIdentity(UIElement target)
    {
        var sequence = 0;
        var entries = new List<VisualIdentityEntry>(Math.Min(MaximumNodes, 1024));
        var pending = new Stack<(DependencyObject Element, int Depth)>();
        pending.Push((window, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (depth > MaximumVisualDepth)
            {
                continue;
            }

            if (current is UIElement element)
            {
                if (!ReferenceEquals(element, window) && !element.IsVisible)
                {
                    continue;
                }

                if (sequence >= MaximumNodes)
                {
                    break;
                }

                entries.Add(new VisualIdentityEntry(
                    element,
                    sequence,
                    DescribeElementIdentity(element, sequence).BaseIdentity));
                sequence++;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = childCount - 1; index >= 0; index--)
            {
                pending.Push((VisualTreeHelper.GetChild(current, index), depth + 1));
            }
        }

        var targetEntry = entries.FirstOrDefault(entry => ReferenceEquals(entry.Element, target));
        if (targetEntry.Element is null)
        {
            return $"untracked-{SafeTypeName(target.GetType().Name)}";
        }

        var duplicateCount = entries.Count(entry => string.Equals(
            entry.BaseIdentity,
            targetEntry.BaseIdentity,
            StringComparison.Ordinal));
        return duplicateCount > 1
            ? DisambiguateIdentity(targetEntry.BaseIdentity, targetEntry.Sequence)
            : targetEntry.BaseIdentity;
    }

    private AIArenaUiStructureEvidence CaptureStructureOnUiThread(
        string treeFingerprint,
        double? renderDpiScale,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        window.UpdateLayout();
        var focusedElement = Keyboard.FocusedElement as DependencyObject;
        var nodes = new List<AIArenaUiStructureNodeEvidence>(Math.Min(MaximumNodes, 1024));
        var pending = new Stack<(DependencyObject Element, int Depth, int? ParentSequence)>();
        pending.Push((window, 0, null));
        var truncated = false;
        var focusIdentity = "none";
        int? focusedSequence = null;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth, parentSequence) = pending.Pop();
            if (depth > MaximumVisualDepth)
            {
                truncated = true;
                continue;
            }

            int? childParentSequence = parentSequence;
            if (current is UIElement element)
            {
                if (!ReferenceEquals(element, window) && !element.IsVisible)
                {
                    continue;
                }

                if (nodes.Count >= MaximumNodes)
                {
                    truncated = true;
                    break;
                }

                var sequence = nodes.Count;
                var describedIdentity = DescribeElementIdentity(element, sequence);
                var hasKeyboardFocus = ReferenceEquals(current, focusedElement) || element.IsKeyboardFocused;
                if (hasKeyboardFocus)
                {
                    focusedSequence = sequence;
                }

                nodes.Add(new AIArenaUiStructureNodeEvidence(
                    sequence,
                    parentSequence,
                    depth,
                    describedIdentity.BaseIdentity,
                    describedIdentity.AutomationId,
                    describedIdentity.AutomationIdRedacted,
                    SafeTypeName(element.GetType().Name),
                    AutomationControlType(element),
                    element.IsVisible,
                    element.IsEnabled,
                    element.Focusable,
                    hasKeyboardFocus));
                childParentSequence = sequence;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = childCount - 1; index >= 0; index--)
            {
                pending.Push((VisualTreeHelper.GetChild(current, index), depth + 1, childParentSequence));
            }
        }

        var identityCounts = nodes
            .GroupBy(node => node.Identity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (identityCounts[node.Identity] > 1)
            {
                nodes[index] = node with
                {
                    Identity = DisambiguateIdentity(node.Identity, node.Sequence)
                };
            }
        }
        if (focusedSequence is { } focusSequence
            && focusSequence >= 0
            && focusSequence < nodes.Count)
        {
            focusIdentity = nodes[focusSequence].Identity;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var configuredTheme = themeId();
        var safeTheme = ThemePalette.IsKnownId(configuredTheme)
            ? ThemePalette.NormalizeId(configuredTheme)
            : "unavailable";
        var actualWidth = Round(window.ActualWidth);
        var actualHeight = Round(window.ActualHeight);
        var dpiScaleX = Round(dpi.DpiScaleX, 3);
        var dpiScaleY = Round(dpi.DpiScaleY, 3);
        var effectiveRenderDpiScale = renderDpiScale ?? dpiScaleX;
        var visibleSurfaceRoots = SurfaceRootNames
            .Where(name => window.FindName(name) is UIElement { IsVisible: true })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var observedSurface = ObserveSurfaceState(visibleSurfaceRoots);
        var visibleRoots = visibleSurfaceRoots.Length == 0
            ? nodes.Where(node => node.IsVisible).Select(node => node.Identity).Take(1).ToArray()
            : visibleSurfaceRoots;
        var selectedView = SelectedView(observedSurface);
        var dialogState = ObserveDialogState();
        var motionMode = SystemMotionPreferences.PreferenceSource switch
        {
            "qa-normal" => "normal",
            "qa-reduced" => "reduced",
            _ => "system"
        };
        var observedState = BuildCanonicalState(
            observedSurface,
            dialogState,
            safeTheme,
            Math.Max(1, (int)Math.Round(actualWidth, MidpointRounding.AwayFromZero)),
            effectiveRenderDpiScale,
            motionMode);
        var requiredControls = observedSurface.StartsWith("arena-", StringComparison.Ordinal)
            ? ArenaRequiredControlNames
                .Where(name => nodes.Any(node => node.IsVisible && node.Identity == name))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : nodes.Where(node => node.IsVisible)
                .Select(node => node.Identity)
                .Where(identity => identity != "none")
                .Take(1)
                .ToArray();
        return new AIArenaUiStructureEvidence(
            EvidenceSchema,
            DateTimeOffset.UtcNow,
            CaptureMode,
            CaptureLimitation,
            treeFingerprint.ToLowerInvariant(),
            observedState,
            ExpectedStateSource,
            selectedView,
            observedSurface,
            dialogState,
            visibleRoots,
            requiredControls,
            safeTheme,
            Math.Max(1, (int)Math.Round(actualWidth, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(actualHeight, MidpointRounding.AwayFromZero)),
            effectiveRenderDpiScale,
            effectiveRenderDpiScale,
            renderDpiScale.HasValue,
            actualWidth,
            actualHeight,
            dpiScaleX,
            dpiScaleY,
            SystemMotionPreferences.PreferenceSource,
            SystemMotionPreferences.AnimationsEnabled,
            focusIdentity,
            nodes.Count,
            truncated,
            nodes.AsReadOnly());
    }

    private static (string Value, bool Redacted) PrivacySafeAutomationId(
        string? value,
        bool allowPlaintext)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return ("", false);
        }

        if (allowPlaintext
            && trimmed.Length <= 96
            && trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
        {
            return (trimmed, false);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))
            .ToLowerInvariant()[..16];
        return ($"redacted-{hash}", true);
    }

    private static ElementIdentityDescriptor DescribeElementIdentity(UIElement element, int sequence)
    {
        var elementName = element is FrameworkElement frameworkElement
            ? frameworkElement.Name
            : "";
        var staticName = PrivacySafeStaticIdentifier(elementName);
        var rawAutomationId = AutomationProperties.GetAutomationId(element);
        var automationIdMatchesStaticName = staticName.Length > 0
            && string.Equals(rawAutomationId, elementName, StringComparison.Ordinal);
        var (automationId, automationIdRedacted) = PrivacySafeAutomationId(
            rawAutomationId,
            automationIdMatchesStaticName);
        var baseIdentity = staticName.Length > 0
            ? staticName
            : string.IsNullOrWhiteSpace(automationId)
                ? $"{SafeTypeName(element.GetType().Name)}#{sequence:D4}"
                : automationId;
        return new ElementIdentityDescriptor(baseIdentity, automationId, automationIdRedacted);
    }

    private static string DisambiguateIdentity(string baseIdentity, int sequence)
        => $"{baseIdentity}#{sequence:D4}";

    private static string PrivacySafeStaticIdentifier(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length is > 0 and <= 96
            && (char.IsAsciiLetter(trimmed[0]) || trimmed[0] == '_')
            && trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
                ? trimmed
                : "";
    }

    private static string AutomationControlType(UIElement element)
    {
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(element);
            if (peer is not null)
            {
                return peer.GetAutomationControlType().ToString();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }

        return element switch
        {
            Window => System.Windows.Automation.Peers.AutomationControlType.Window.ToString(),
            CheckBox => System.Windows.Automation.Peers.AutomationControlType.CheckBox.ToString(),
            RadioButton => System.Windows.Automation.Peers.AutomationControlType.RadioButton.ToString(),
            ButtonBase => System.Windows.Automation.Peers.AutomationControlType.Button.ToString(),
            PasswordBox or TextBox => System.Windows.Automation.Peers.AutomationControlType.Edit.ToString(),
            ComboBox => System.Windows.Automation.Peers.AutomationControlType.ComboBox.ToString(),
            ListBox => System.Windows.Automation.Peers.AutomationControlType.List.ToString(),
            ListBoxItem => System.Windows.Automation.Peers.AutomationControlType.ListItem.ToString(),
            TabControl => System.Windows.Automation.Peers.AutomationControlType.Tab.ToString(),
            TabItem => System.Windows.Automation.Peers.AutomationControlType.TabItem.ToString(),
            TreeView => System.Windows.Automation.Peers.AutomationControlType.Tree.ToString(),
            TreeViewItem => System.Windows.Automation.Peers.AutomationControlType.TreeItem.ToString(),
            Slider => System.Windows.Automation.Peers.AutomationControlType.Slider.ToString(),
            ProgressBar => System.Windows.Automation.Peers.AutomationControlType.ProgressBar.ToString(),
            ScrollBar => System.Windows.Automation.Peers.AutomationControlType.ScrollBar.ToString(),
            ScrollViewer => System.Windows.Automation.Peers.AutomationControlType.Pane.ToString(),
            Image => System.Windows.Automation.Peers.AutomationControlType.Image.ToString(),
            TextBlock => System.Windows.Automation.Peers.AutomationControlType.Text.ToString(),
            _ => System.Windows.Automation.Peers.AutomationControlType.Custom.ToString()
        };
    }

    private AIArenaUiStructureCaptureResult WriteEvidenceAtomic(
        string targetPath,
        string relativePath,
        AIArenaUiStructureEvidence evidence,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return StructureFailure("invalid_argument", "UI evidence path has no parent directory.", relativePath);
            }

            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath, overwrite: false);
            tempPath = null;
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new AIArenaUiStructureCaptureResult(
                true,
                "",
                $"UI structure evidence saved to {relativePath}.",
                EvidenceArtifactKind,
                EvidencePathBase,
                relativePath,
                bytes.LongLength,
                sha256,
                evidence.CapturedAtUtc,
                evidence.Schema,
                evidence.CaptureMode,
                evidence.Limitation,
                evidence.TreeFingerprint,
                evidence.ExpectedState,
                evidence.ExpectedStateSource,
                evidence.SelectedView,
                evidence.ObservedSurfaceState,
                evidence.DialogState,
                evidence.VisibleRootIdentities,
                evidence.RequiredControlIdentities,
                evidence.Theme,
                evidence.ViewportWidthDip,
                evidence.ViewportHeightDip,
                evidence.DpiScale,
                evidence.RenderDpiScale,
                evidence.RenderDpiOverride,
                evidence.ActualWidthDip,
                evidence.ActualHeightDip,
                evidence.DpiScaleX,
                evidence.DpiScaleY,
                evidence.MotionPreferenceSource,
                evidence.AnimationsEnabled,
                evidence.FocusIdentity,
                evidence.NodeCount,
                evidence.Truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return File.Exists(targetPath)
                ? StructureFailure("already_exists", $"UI structure evidence already exists at {relativePath}.", relativePath)
                : StructureFailure("capture_failed", "UI structure evidence could not be written atomically.", relativePath);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeTypeName(string value)
    {
        return value.Length <= 96 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.')
            ? value
            : "Custom";
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(ch => char.IsAsciiHexDigit(ch));
    }

    private static bool IsSafeExpectedState(string? value)
    {
        return value is { Length: > 0 and <= 96 }
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
    }

    internal string DebugObservedExpectedState(double? renderDpiScale = null)
    {
        return window.Dispatcher.CheckAccess()
            ? ObserveExpectedStateOnUiThread(renderDpiScale)
            : window.Dispatcher.Invoke(() => ObserveExpectedStateOnUiThread(renderDpiScale));
    }

    private string ObserveExpectedStateOnUiThread(double? renderDpiScale)
    {
        window.UpdateLayout();
        var configuredTheme = themeId();
        var safeTheme = ThemePalette.IsKnownId(configuredTheme)
            ? ThemePalette.NormalizeId(configuredTheme)
            : "unavailable";
        var visibleRoots = SurfaceRootNames
            .Where(name => window.FindName(name) is UIElement { IsVisible: true })
            .Order(StringComparer.Ordinal)
            .ToArray();
        var source = SystemMotionPreferences.PreferenceSource;
        var motionMode = source == "qa-normal" ? "normal" : source == "qa-reduced" ? "reduced" : "system";
        var scale = renderDpiScale ?? VisualTreeHelper.GetDpi(window).DpiScaleX;
        return BuildCanonicalState(
            ObserveSurfaceState(visibleRoots),
            ObserveDialogState(),
            safeTheme,
            Math.Max(1, (int)Math.Round(window.ActualWidth, MidpointRounding.AwayFromZero)),
            scale,
            motionMode);
    }

    private string ObserveSurfaceState(IReadOnlyList<string> visibleRoots)
    {
        if (visibleRoots.Count != 1)
        {
            return visibleRoots.Count == 0 ? "verification-window" : "ambiguous";
        }

        return visibleRoots[0] switch
        {
            "TranscriptPanel" => ObserveTranscriptState(),
            "CustomMatchPanel" => "match-setup",
            "ExperimentLabPanel" => "experiment-lab",
            "AgentWorldPanel" => "agent-world",
            "AgentWorkspacePanel" => "agent",
            "CollaboratePanel" => "collaborate",
            _ => "ambiguous"
        };
    }

    private string ObserveTranscriptState()
    {
        var semanticCount = transcriptMessageCount?.Invoke();
        if (semanticCount is >= 0)
        {
            return semanticCount.Value == 0 ? "arena-empty" : "arena-populated";
        }

        return window.FindName("TranscriptItems") is ItemsControl transcript && transcript.Items.Count == 0
            ? "arena-empty"
            : "arena-populated";
    }

    private string ObserveDialogState()
    {
        if (window.FindName("AppSettingsPanel") is UIElement { IsVisible: true })
        {
            return "open";
        }

        return Application.Current?.Windows
            .OfType<Window>()
            .Any(candidate => !ReferenceEquals(candidate, window) && candidate.IsVisible && ReferenceEquals(candidate.Owner, window)) == true
                ? "open"
                : "closed";
    }

    private static string SelectedView(string surface) => surface switch
    {
        "arena-empty" or "arena-populated" => "arena",
        "match-setup" => "match-setup",
        "experiment-lab" => "experiment-lab",
        "agent-world" => "world",
        "agent" => "agent",
        "collaborate" => "collaborate",
        _ => "verification-window"
    };

    private static string BuildCanonicalState(
        string surface,
        string dialogState,
        string theme,
        int viewportWidthDip,
        double rasterDensityScale,
        string motionMode)
    {
        var density = Math.Abs(rasterDensityScale - 1.0) < 0.001
            ? "1-0"
            : Math.Abs(rasterDensityScale - 1.5) < 0.001
                ? "1-5"
                : Math.Abs(rasterDensityScale - 2.0) < 0.001
                    ? "2-0"
                    : rasterDensityScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '-');
        return $"{surface}.{dialogState}.{theme}.w{viewportWidthDip}.d{density}.{motionMode}";
    }

    private static bool TryNormalizeFocusDirection(string? value, out string direction)
    {
        direction = string.IsNullOrWhiteSpace(value)
            ? "next"
            : value.Trim().ToLowerInvariant();
        return direction is "next" or "previous";
    }

    private static bool TryNormalizeMotionMode(string? value, out string mode)
    {
        mode = value?.Trim().ToLowerInvariant() ?? "";
        return mode is MotionModeSystem or MotionModeNormal or MotionModeReduced;
    }

    private static double Round(double value, int digits = 2)
    {
        return double.IsFinite(value)
            ? Math.Round(Math.Max(0, value), digits, MidpointRounding.AwayFromZero)
            : 0;
    }

    private static AIArenaQaWindowSizeResult WindowSizeFailure(
        string code,
        string message,
        int widthDip,
        int heightDip)
    {
        return new AIArenaQaWindowSizeResult(false, code, message, widthDip, heightDip, 0, 0, 0, 0);
    }

    private static AIArenaQaFocusTraversalResult FocusFailure(
        string code,
        string message,
        string direction)
    {
        return new AIArenaQaFocusTraversalResult(
            false,
            code,
            message,
            direction,
            "none",
            "none",
            "none",
            "none",
            false,
            false);
    }

    private AIArenaQaMotionPreferenceResult MotionFailure(
        string code,
        string message,
        string requestedMode)
    {
        return new AIArenaQaMotionPreferenceResult(
            false,
            code,
            message,
            requestedMode,
            SystemMotionPreferences.PreferenceSource,
            SystemMotionPreferences.HasQaOverride,
            SystemMotionPreferences.AnimationsEnabled,
            SystemMotionPreferences.SystemAnimationsEnabled,
            isIsolatedQaProcess);
    }

    private static AIArenaUiStructureCaptureResult StructureFailure(
        string code,
        string message,
        string relativePath = "")
    {
        return new AIArenaUiStructureCaptureResult(
            false,
            code,
            message,
            EvidenceArtifactKind,
            EvidencePathBase,
            relativePath,
            0,
            "",
            null,
            EvidenceSchema,
            CaptureMode,
            CaptureLimitation,
            "",
            "",
            "unavailable",
            "unavailable",
            "unavailable",
            "unavailable",
            [],
            [],
            "unavailable",
            0,
            0,
            0,
            0,
            false,
            0,
            0,
            0,
            0,
            SystemMotionPreferences.PreferenceSource,
            SystemMotionPreferences.AnimationsEnabled,
            "none",
            0,
            false);
    }

    private readonly record struct FocusDescriptor(string Identity, string ControlType);

    private readonly record struct ElementIdentityDescriptor(
        string BaseIdentity,
        string AutomationId,
        bool AutomationIdRedacted);

    private readonly record struct VisualIdentityEntry(
        UIElement? Element,
        int Sequence,
        string BaseIdentity);
}
