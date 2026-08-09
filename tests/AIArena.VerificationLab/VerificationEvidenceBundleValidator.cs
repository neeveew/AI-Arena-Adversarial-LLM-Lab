using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Services;

namespace AIArena.VerificationLab;

internal sealed record VerificationEvidenceIssue(string Code, int ArtifactIndex = -1)
{
    public string SafeDisplay => ArtifactIndex < 0
        ? $"FAIL {Code}"
        : $"FAIL {Code} artifact[{ArtifactIndex}]";
}

internal static partial class VerificationEvidenceBundleValidator
{
    private const long MaximumArtifactBytes = 64L * 1024 * 1024;
    private const long MaximumAutomationArtifactBytes = 4L * 1024 * 1024;
    private const long MaximumUiMatrixArtifactBytes = 1L * 1024 * 1024;
    private const long MaximumBundleBytes = 256L * 1024 * 1024;
    private const int MaximumAutomationNodes = 5_000;
    private const long MaximumPngPixels = 100_000_000;
    private const long MaximumDecodedPngBytes = 128L * 1024 * 1024;
    private const string AutomationSchema = "ai_arena.ui_structure_evidence.v1";
    private const string UiMatrixSchema = "ai_arena.qa_ui_matrix.v1";
    private const string UiMatrixArtifactKind = "qa-ui-matrix";
    private const string AutomationCaptureMode = "wpf-visual-tree-accessibility";
    private const string AutomationLimitation = "OS UI Automation and OS input are not queried; focus traversal and the privacy-safe visual-tree snapshot are programmatic and in-process. RenderDpiScale is off-screen raster density, not physical or per-monitor display DPI. Motion fields prove preference plumbing, not rendered animation playback. Accessible names, help text, and all dynamic control content are omitted.";
    private const string ExpectedStateSource = "observed-visible-roots";

    private static readonly string[] RequiredArenaControls =
    [
        "RootLayout",
        "ShellNavigationRail",
        "ShellTopBar",
        "TranscriptItems",
        "TranscriptPanel"
    ];

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly IReadOnlySet<string> AutomationRootProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "Schema", "CapturedAtUtc", "CaptureMode", "Limitation", "TreeFingerprint", "ExpectedState",
        "ExpectedStateSource", "SelectedView", "ObservedSurfaceState", "DialogState", "VisibleRootIdentities", "RequiredControlIdentities",
        "Theme", "ViewportWidthDip", "ViewportHeightDip", "DpiScale", "RenderDpiScale", "RenderDpiOverride", "ActualWidthDip", "ActualHeightDip",
        "DpiScaleX", "DpiScaleY", "MotionPreferenceSource", "AnimationsEnabled", "FocusIdentity", "NodeCount", "Truncated", "Nodes"
    };

    private static readonly IReadOnlySet<string> AutomationNodeProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "Sequence", "ParentSequence", "VisualDepth", "Identity", "AutomationId", "AutomationIdRedacted",
        "FrameworkType", "ControlType", "IsVisible", "IsEnabled", "IsFocusable", "HasKeyboardFocus"
    };

    private static readonly IReadOnlySet<string> UiMatrixRootProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "schema", "passNumber", "treeFingerprint", "cellCount", "cells"
    };

    private static readonly IReadOnlySet<string> UiMatrixCellProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "key", "theme", "viewportWidthDip", "viewportHeightDip", "renderDpiScale", "motionMode",
        "motionPreferenceSource", "animationsEnabled", "focusNext", "focusPrevious", "focusCapture",
        "automationArtifactId", "screenshotArtifactId"
    };

    private static readonly IReadOnlySet<string> UiMatrixFocusProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "direction", "beforeIdentity", "beforeControlType", "afterIdentity", "afterControlType", "moved", "focusChanged"
    };

    private static readonly IReadOnlySet<string> UiMatrixGateIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "ui.keyboard-automation-matrix",
        "ui.reduced-motion-matrix",
        "ui.theme-contrast-matrix",
        "ui.viewport-dpi-matrix"
    };

    public static async Task<ImmutableArray<VerificationEvidenceIssue>> ValidateAsync(
        string evidencePath,
        ArenaQaEvidenceContract contract,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<VerificationEvidenceIssue>();
        string bundleRoot;
        try
        {
            var fullEvidencePath = Path.GetFullPath(evidencePath);
            bundleRoot = Path.GetDirectoryName(fullEvidencePath) ?? "";
            if (bundleRoot.Length == 0 || !Directory.Exists(bundleRoot))
            {
                issues.Add(new("bundle.root_unavailable"));
                return Sort(issues);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new("bundle.root_unavailable"));
            return Sort(issues);
        }

        var artifactBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long bundleBytes = 0;
        for (var index = 0; index < contract.Artifacts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = contract.Artifacts[index];
            if (!TryResolveArtifact(bundleRoot, artifact.RelativePath, out var artifactPath)
                || ContainsReparsePoint(bundleRoot, artifact.RelativePath))
            {
                issues.Add(new("bundle.path", index));
                continue;
            }

            if (!File.Exists(artifactPath))
            {
                issues.Add(new("bundle.missing", index));
                continue;
            }

            var maximumBytes = artifact.Kind switch
            {
                "automation-tree" => MaximumAutomationArtifactBytes,
                UiMatrixArtifactKind => MaximumUiMatrixArtifactBytes,
                _ => MaximumArtifactBytes
            };
            ArtifactReadResult read;
            try
            {
                read = await ReadAndHashAsync(
                    artifactPath,
                    maximumBytes,
                    captureBytes: artifact.Kind is "automation-tree" or "rendered-ui-screenshot" or UiMatrixArtifactKind,
                    cancellationToken);
            }
            catch (InvalidDataException)
            {
                issues.Add(new("bundle.oversize", index));
                continue;
            }
            catch (DecoderFallbackException)
            {
                issues.Add(new("bundle.utf8", index));
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new("bundle.read", index));
                continue;
            }

            bundleBytes += read.Length;
            if (bundleBytes > MaximumBundleBytes)
            {
                issues.Add(new("bundle.oversize"));
                break;
            }

            if (!string.Equals(read.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("bundle.hash", index));
            }

            if (read.Bytes is not null)
            {
                artifactBytes[artifact.Id] = read.Bytes;
            }

            if (artifact.Kind != "rendered-ui-screenshot")
            {
                await ValidateTextPrivacyAsync(artifactPath, read.Length, issues, index, cancellationToken);
            }
        }

        var byId = contract.Artifacts
            .Select((artifact, index) => (artifact, index))
            .ToDictionary(item => item.artifact.Id, item => item, StringComparer.Ordinal);
        var decodedPngEvidence = new Dictionary<string, DecodedPngEvidence>(StringComparer.Ordinal);
        foreach (var (artifact, index) in contract.Artifacts.Select((value, index) => (value, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!artifactBytes.TryGetValue(artifact.Id, out var bytes))
            {
                continue;
            }

            if (artifact.Kind == "automation-tree")
            {
                ValidateAutomationArtifact(bytes, artifact, index, issues);
            }
            else if (artifact.Kind == "rendered-ui-screenshot")
            {
                ValidateScreenshotArtifact(bytes, artifact, index, decodedPngEvidence, issues);
                ValidateScreenshotLink(artifact, index, byId, issues);
            }
        }

        ValidateUiMatrices(contract, artifactBytes, byId, decodedPngEvidence, issues);

        return Sort(issues);
    }

    private static async Task ValidateTextPrivacyAsync(
        string path,
        long length,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues,
        int artifactIndex,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new("bundle.read", artifactIndex));
            return;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Optional binary artifacts are hash-checked but not interpreted as text.
            return;
        }

        if (UnsafeEvidenceTextRegex().IsMatch(text))
        {
            issues.Add(new("bundle.privacy", artifactIndex));
        }
    }

    private static void ValidateAutomationArtifact(
        byte[] bytes,
        ArenaQaArtifact artifact,
        int artifactIndex,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        if (artifact.Provenance is null)
        {
            issues.Add(new("bundle.automation_provenance", artifactIndex));
            return;
        }

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new("bundle.automation_utf8", artifactIndex));
            return;
        }

        if (!ArenaContractPrivacyRules.InspectJson(json).IsEmpty)
        {
            issues.Add(new("bundle.automation_privacy", artifactIndex));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 160
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(root, AutomationRootProperties)
                || RequiredString(root, "Schema") != AutomationSchema
                || RequiredString(root, "CaptureMode") != AutomationCaptureMode
                || RequiredString(root, "Limitation") != AutomationLimitation)
            {
                issues.Add(new("bundle.automation_schema", artifactIndex));
                return;
            }

            var provenance = artifact.Provenance;
            var capturedAt = root.GetProperty("CapturedAtUtc").GetDateTimeOffset();
            var treeFingerprint = RequiredString(root, "TreeFingerprint");
            var expectedState = RequiredString(root, "ExpectedState");
            var expectedStateSource = RequiredString(root, "ExpectedStateSource");
            var selectedView = RequiredString(root, "SelectedView");
            var observedSurfaceState = RequiredString(root, "ObservedSurfaceState");
            var dialogState = RequiredString(root, "DialogState");
            var visibleRootIdentities = ReadSafeStringArray(root, "VisibleRootIdentities", 16);
            var requiredControlIdentities = ReadSafeStringArray(root, "RequiredControlIdentities", 64);
            var theme = RequiredString(root, "Theme");
            var viewportWidth = root.GetProperty("ViewportWidthDip").GetInt32();
            var viewportHeight = root.GetProperty("ViewportHeightDip").GetInt32();
            var dpiScale = root.GetProperty("DpiScale").GetDecimal();
            var renderDpiScale = root.GetProperty("RenderDpiScale").GetDouble();
            var renderDpiOverride = root.GetProperty("RenderDpiOverride").GetBoolean();
            var actualWidth = root.GetProperty("ActualWidthDip").GetDouble();
            var actualHeight = root.GetProperty("ActualHeightDip").GetDouble();
            var dpiScaleX = root.GetProperty("DpiScaleX").GetDouble();
            var dpiScaleY = root.GetProperty("DpiScaleY").GetDouble();
            var motionPreferenceSource = RequiredString(root, "MotionPreferenceSource");
            var animationsEnabled = root.GetProperty("AnimationsEnabled").GetBoolean();
            if (!string.Equals(treeFingerprint, provenance.TreeFingerprint, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expectedState, provenance.ExpectedState, StringComparison.Ordinal)
                || expectedStateSource != ExpectedStateSource
                || !IsSafeAutomationValue(selectedView)
                || !IsSafeAutomationValue(observedSurfaceState)
                || dialogState is not ("closed" or "open")
                || visibleRootIdentities.Length == 0
                || requiredControlIdentities.Length == 0
                || !string.Equals(theme, provenance.Theme, StringComparison.Ordinal)
                || treeFingerprint.Length != 64
                || !treeFingerprint.All(char.IsAsciiHexDigit)
                || !IsSafeStateIdentifier(expectedState)
                || !IsSafeAutomationValue(theme)
                || viewportWidth != provenance.ViewportWidthDip
                || viewportHeight != provenance.ViewportHeightDip
                || Math.Abs(dpiScale - provenance.DpiScale) > 0.001m
                || capturedAt != provenance.CapturedAtUtc
                || !double.IsFinite(actualWidth)
                || !double.IsFinite(actualHeight)
                || !double.IsFinite(dpiScaleX)
                || !double.IsFinite(dpiScaleY)
                || actualWidth <= 0
                || actualHeight <= 0
                || Math.Round(actualWidth, MidpointRounding.AwayFromZero) != viewportWidth
                || Math.Round(actualHeight, MidpointRounding.AwayFromZero) != viewportHeight
                || dpiScaleX is < 0.5 or > 8
                || dpiScaleY is < 0.5 or > 8
                || !double.IsFinite(renderDpiScale)
                || renderDpiScale is < 0.5 or > 8
                || Math.Abs((decimal)renderDpiScale - provenance.DpiScale) > 0.001m
                || Math.Abs((decimal)renderDpiScale - dpiScale) > 0.001m
                || (renderDpiOverride
                    ? !IsSupportedQaRenderDpiScale(renderDpiScale)
                    : Math.Abs(renderDpiScale - dpiScaleX) > 0.001))
            {
                issues.Add(new("bundle.automation_provenance", artifactIndex));
            }

            if (motionPreferenceSource is not ("system" or "qa-normal" or "qa-reduced")
                || (motionPreferenceSource == "qa-normal" && !animationsEnabled)
                || (motionPreferenceSource == "qa-reduced" && animationsEnabled))
            {
                issues.Add(new("bundle.automation_motion", artifactIndex));
            }

            var nodes = root.GetProperty("Nodes");
            var nodeCount = root.GetProperty("NodeCount").GetInt32();
            if (root.GetProperty("Truncated").GetBoolean())
            {
                issues.Add(new("bundle.automation_truncated", artifactIndex));
            }
            if (nodes.ValueKind != JsonValueKind.Array
                || nodeCount < 0
                || nodeCount > MaximumAutomationNodes
                || nodes.GetArrayLength() != nodeCount)
            {
                issues.Add(new("bundle.automation_nodes", artifactIndex));
                return;
            }

            var focused = 0;
            var focusIdentity = RequiredString(root, "FocusIdentity");
            var sequence = 0;
            var visibleNodeIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                var parent = node.GetProperty("ParentSequence");
                var parentSequence = parent.ValueKind == JsonValueKind.Null
                    ? (int?)null
                    : parent.GetInt32();
                if (node.ValueKind != JsonValueKind.Object
                    || !HasOnlyProperties(node, AutomationNodeProperties)
                    || node.GetProperty("Sequence").GetInt32() != sequence
                    || node.GetProperty("VisualDepth").GetInt32() is < 0 or > 128
                    || (sequence == 0 ? parentSequence is not null : parentSequence is null || parentSequence >= sequence)
                    || !IsSafeAutomationValue(RequiredString(node, "Identity"))
                    || !IsSafeAutomationValue(RequiredString(node, "AutomationId"), allowEmpty: true)
                    || !IsSafeAutomationValue(RequiredString(node, "FrameworkType"))
                    || !IsSafeAutomationValue(RequiredString(node, "ControlType")))
                {
                    issues.Add(new("bundle.automation_nodes", artifactIndex));
                    return;
                }

                _ = node.GetProperty("AutomationIdRedacted").GetBoolean();
                var isVisible = node.GetProperty("IsVisible").GetBoolean();
                _ = node.GetProperty("IsEnabled").GetBoolean();
                _ = node.GetProperty("IsFocusable").GetBoolean();
                if (isVisible)
                {
                    visibleNodeIdentities.Add(RequiredString(node, "Identity"));
                }

                if (node.GetProperty("HasKeyboardFocus").GetBoolean())
                {
                    focused++;
                    if (!string.Equals(RequiredString(node, "Identity"), focusIdentity, StringComparison.Ordinal))
                    {
                        issues.Add(new("bundle.automation_focus", artifactIndex));
                    }
                }
                sequence++;
            }

            if (focused > 1 || (focused == 0) != string.Equals(focusIdentity, "none", StringComparison.Ordinal))
            {
                issues.Add(new("bundle.automation_focus", artifactIndex));
            }

            if (requiredControlIdentities.Any(identity => !visibleNodeIdentities.Contains(identity))
                || visibleRootIdentities.Any(identity => !visibleNodeIdentities.Contains(identity)))
            {
                issues.Add(new("bundle.automation_observed_state", artifactIndex));
            }

            var reconstructedState = BuildCanonicalState(
                observedSurfaceState,
                dialogState,
                theme,
                viewportWidth,
                (decimal)renderDpiScale,
                motionPreferenceSource);
            if (expectedState != reconstructedState)
            {
                issues.Add(new("bundle.automation_observed_state", artifactIndex));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            issues.Add(new("bundle.automation_schema", artifactIndex));
        }
    }

    private static void ValidateUiMatrices(
        ArenaQaEvidenceContract contract,
        IReadOnlyDictionary<string, byte[]> artifactBytes,
        IReadOnlyDictionary<string, (ArenaQaArtifact artifact, int index)> byId,
        IReadOnlyDictionary<string, DecodedPngEvidence> decodedPngEvidence,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        var passingRenderedPasses = contract.Gates
            .Where(gate => gate.Outcome == ArenaQaGateOutcome.Pass)
            .Select(gate => TryParseRenderedUiPass(gate.Id, out var passNumber) ? passNumber : 0)
            .Where(passNumber => passNumber > 0)
            .ToHashSet();
        var passingMatrixGateCount = contract.Gates.Count(gate =>
            gate.Outcome == ArenaQaGateOutcome.Pass && UiMatrixGateIds.Contains(gate.Id));
        if (passingMatrixGateCount is > 0 and < 4 ||
            (passingMatrixGateCount == 4 && passingRenderedPasses.Count == 0))
        {
            issues.Add(new("bundle.ui_matrix_gates"));
        }

        var documents = new List<(UiMatrixDocument document, ArenaQaArtifact artifact, int index)>();
        foreach (var entry in byId.Values
                     .Where(entry => entry.artifact.Kind == UiMatrixArtifactKind)
                     .OrderBy(entry => entry.index))
        {
            if (entry.artifact.Provenance is not null)
            {
                issues.Add(new("bundle.ui_matrix_schema", entry.index));
            }
            if (!artifactBytes.TryGetValue(entry.artifact.Id, out var bytes))
            {
                continue;
            }

            if (TryReadUiMatrixDocument(bytes, entry.artifact, entry.index, issues, out var document))
            {
                documents.Add((document!, entry.artifact, entry.index));
            }
        }

        foreach (var passNumber in passingRenderedPasses.Order())
        {
            var matches = documents.Where(item => item.document.PassNumber == passNumber).ToArray();
            if (matches.Length == 0)
            {
                issues.Add(new("bundle.ui_matrix_missing"));
            }
            else if (matches.Length != 1)
            {
                issues.Add(new("bundle.ui_matrix_duplicate"));
            }
        }

        foreach (var item in documents)
        {
            if (!passingRenderedPasses.Contains(item.document.PassNumber))
            {
                issues.Add(new("bundle.ui_matrix_orphan", item.index));
                continue;
            }

            ValidateUiMatrixDocument(
                contract,
                item.document,
                item.artifact,
                item.index,
                artifactBytes,
                byId,
                decodedPngEvidence,
                issues);
        }
    }

    private static bool TryReadUiMatrixDocument(
        byte[] bytes,
        ArenaQaArtifact artifact,
        int artifactIndex,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues,
        out UiMatrixDocument? value)
    {
        value = null;
        try
        {
            var json = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            if (!ArenaContractPrivacyRules.InspectJson(json).IsEmpty)
            {
                issues.Add(new("bundle.ui_matrix_privacy", artifactIndex));
                return false;
            }
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(root, UiMatrixRootProperties)
                || RequiredString(root, "schema") != UiMatrixSchema)
            {
                issues.Add(new("bundle.ui_matrix_schema", artifactIndex));
                return false;
            }

            var passNumber = root.GetProperty("passNumber").GetInt32();
            var treeFingerprint = RequiredString(root, "treeFingerprint");
            var cellCount = root.GetProperty("cellCount").GetInt32();
            var cellsElement = root.GetProperty("cells");
            if (passNumber is < 1 or > 5
                || treeFingerprint.Length != 64
                || !treeFingerprint.All(char.IsAsciiHexDigit)
                || cellCount != 36
                || cellsElement.ValueKind != JsonValueKind.Array
                || cellsElement.GetArrayLength() != cellCount)
            {
                issues.Add(new("bundle.ui_matrix_schema", artifactIndex));
                return false;
            }

            var cells = ImmutableArray.CreateBuilder<UiMatrixCell>(cellCount);
            foreach (var cellElement in cellsElement.EnumerateArray())
            {
                if (!TryReadUiMatrixCell(cellElement, out var cell))
                {
                    issues.Add(new("bundle.ui_matrix_schema", artifactIndex));
                    return false;
                }
                cells.Add(cell!);
            }

            value = new(passNumber, treeFingerprint, [.. cells]);
            if (artifact.Id != $"artifact.pass-{passNumber:D2}.ui-matrix"
                || artifact.RelativePath != $"metadata/pass-{passNumber:D2}.ui-matrix.json")
            {
                // Keep the parsed pass identity so a second document cannot hide
                // behind a non-canonical artifact ID/path and evade duplicate detection.
                issues.Add(new("bundle.ui_matrix_schema", artifactIndex));
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException
                                   or FormatException or OverflowException or KeyNotFoundException)
        {
            issues.Add(new("bundle.ui_matrix_schema", artifactIndex));
            return false;
        }
    }

    private static bool TryReadUiMatrixCell(JsonElement element, out UiMatrixCell? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !HasOnlyProperties(element, UiMatrixCellProperties)
            || !TryReadUiMatrixFocus(element.GetProperty("focusNext"), out var focusNext)
            || !TryReadUiMatrixFocus(element.GetProperty("focusPrevious"), out var focusPrevious)
            || !TryReadUiMatrixFocus(element.GetProperty("focusCapture"), out var focusCapture))
        {
            return false;
        }

        value = new(
            RequiredString(element, "key"),
            RequiredString(element, "theme"),
            element.GetProperty("viewportWidthDip").GetInt32(),
            element.GetProperty("viewportHeightDip").GetInt32(),
            element.GetProperty("renderDpiScale").GetDecimal(),
            RequiredString(element, "motionMode"),
            RequiredString(element, "motionPreferenceSource"),
            element.GetProperty("animationsEnabled").GetBoolean(),
            focusNext!,
            focusPrevious!,
            focusCapture!,
            RequiredString(element, "automationArtifactId"),
            RequiredString(element, "screenshotArtifactId"));
        return true;
    }

    private static bool TryReadUiMatrixFocus(JsonElement element, out UiMatrixFocus? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !HasOnlyProperties(element, UiMatrixFocusProperties))
        {
            return false;
        }

        value = new(
            RequiredString(element, "direction"),
            RequiredString(element, "beforeIdentity"),
            RequiredString(element, "beforeControlType"),
            RequiredString(element, "afterIdentity"),
            RequiredString(element, "afterControlType"),
            element.GetProperty("moved").GetBoolean(),
            element.GetProperty("focusChanged").GetBoolean());
        return true;
    }

    private static void ValidateUiMatrixDocument(
        ArenaQaEvidenceContract contract,
        UiMatrixDocument document,
        ArenaQaArtifact matrixArtifact,
        int matrixArtifactIndex,
        IReadOnlyDictionary<string, byte[]> artifactBytes,
        IReadOnlyDictionary<string, (ArenaQaArtifact artifact, int index)> byId,
        IReadOnlyDictionary<string, DecodedPngEvidence> decodedPngEvidence,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        if (!string.Equals(document.TreeFingerprint, contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("bundle.ui_matrix_provenance", matrixArtifactIndex));
        }

        var expectedKeys = ExpectedUiMatrixKeys(document.PassNumber);
        var actualKeys = document.Cells.Select(cell => cell.Key).ToArray();
        if (actualKeys.Distinct(StringComparer.Ordinal).Count() != 36
            || !actualKeys.SequenceEqual(actualKeys.OrderBy(key => key, StringComparer.Ordinal), StringComparer.Ordinal)
            || !actualKeys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys))
        {
            issues.Add(new("bundle.ui_matrix_cross_product", matrixArtifactIndex));
        }

        var automationIds = new HashSet<string>(StringComparer.Ordinal);
        var screenshotIds = new HashSet<string>(StringComparer.Ordinal);
        var artifactPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in document.Cells)
        {
            ValidateUiMatrixCell(
                contract,
                document.PassNumber,
                cell,
                matrixArtifactIndex,
                artifactBytes,
                byId,
                automationIds,
                screenshotIds,
                artifactPaths,
                issues);
        }

        if (automationIds.Count != 36 || screenshotIds.Count != 36 || artifactPaths.Count != 72
            || automationIds.Overlaps(screenshotIds))
        {
            issues.Add(new("bundle.ui_matrix_artifacts", matrixArtifactIndex));
        }

        foreach (var group in document.Cells.GroupBy(cell =>
                     (cell.ViewportWidthDip, cell.ViewportHeightDip, cell.RenderDpiScale, cell.MotionMode)))
        {
            var screenshotHashes = group
                .Select(cell => byId.TryGetValue(cell.ScreenshotArtifactId, out var entry)
                    ? entry.artifact.Sha256
                    : "")
                .Where(hash => hash.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var decoded = group
                .Select(cell => decodedPngEvidence.GetValueOrDefault(cell.ScreenshotArtifactId))
                .Where(value => value is not null)
                .Cast<DecodedPngEvidence>()
                .ToArray();
            var decodedHashes = decoded
                .Select(value => value.PixelSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var materialPairCount = 0;
            for (var left = 0; left < decoded.Length; left++)
            for (var right = left + 1; right < decoded.Length; right++)
            {
                if (HasMaterialThemeDifference(decoded[left], decoded[right]))
                {
                    materialPairCount++;
                }
            }
            if (group.Count() != 3
                || screenshotHashes != 3
                || decoded.Length != 3
                || decodedHashes != 3
                || materialPairCount != 3)
            {
                issues.Add(new("bundle.ui_matrix_theme_render", matrixArtifactIndex));
            }
        }
    }

    private static void ValidateUiMatrixCell(
        ArenaQaEvidenceContract contract,
        int passNumber,
        UiMatrixCell cell,
        int matrixArtifactIndex,
        IReadOnlyDictionary<string, byte[]> artifactBytes,
        IReadOnlyDictionary<string, (ArenaQaArtifact artifact, int index)> byId,
        HashSet<string> automationIds,
        HashSet<string> screenshotIds,
        HashSet<string> artifactPaths,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        var dpiLabel = cell.RenderDpiScale switch
        {
            1.0m => "1-0",
            1.5m => "1-5",
            2.0m => "2-0",
            _ => "invalid"
        };
        var expectedHeight = cell.ViewportWidthDip switch
        {
            960 => 640,
            1500 => 960,
            _ => 0
        };
        var expectedKey = $"p{passNumber:D2}.{cell.Theme}.w{cell.ViewportWidthDip}.d{dpiLabel}.{cell.MotionMode}";
        var expectedState = $"arena-empty.closed.{cell.Theme}.w{cell.ViewportWidthDip}.d{dpiLabel}.{cell.MotionMode}";
        var expectedMotionSource = cell.MotionMode switch
        {
            "normal" => "qa-normal",
            "reduced" => "qa-reduced",
            _ => "invalid"
        };
        var expectedAnimations = cell.MotionMode == "normal";
        if (cell.Theme is not ("dark-blue" or "light" or "high-contrast")
            || expectedHeight == 0
            || cell.ViewportHeightDip != expectedHeight
            || dpiLabel == "invalid"
            || expectedMotionSource == "invalid"
            || cell.Key != expectedKey)
        {
            issues.Add(new("bundle.ui_matrix_cross_product", matrixArtifactIndex));
        }

        if (cell.MotionPreferenceSource != expectedMotionSource
            || cell.AnimationsEnabled != expectedAnimations)
        {
            issues.Add(new("bundle.ui_matrix_motion", matrixArtifactIndex));
        }

        if (!IsValidFocusSequence(cell.FocusNext, cell.FocusPrevious, cell.FocusCapture))
        {
            issues.Add(new("bundle.ui_matrix_focus", matrixArtifactIndex));
        }

        var expectedAutomationId = $"artifact.{cell.Key}.automation";
        var expectedScreenshotId = $"artifact.{cell.Key}.screenshot";
        if (cell.AutomationArtifactId != expectedAutomationId
            || cell.ScreenshotArtifactId != expectedScreenshotId
            || !automationIds.Add(cell.AutomationArtifactId)
            || !screenshotIds.Add(cell.ScreenshotArtifactId)
            || !byId.TryGetValue(cell.AutomationArtifactId, out var automationEntry)
            || !byId.TryGetValue(cell.ScreenshotArtifactId, out var screenshotEntry)
            || automationEntry.artifact.Kind != "automation-tree"
            || screenshotEntry.artifact.Kind != "rendered-ui-screenshot")
        {
            issues.Add(new("bundle.ui_matrix_artifacts", matrixArtifactIndex));
            return;
        }

        _ = artifactPaths.Add(automationEntry.artifact.RelativePath);
        _ = artifactPaths.Add(screenshotEntry.artifact.RelativePath);
        var automation = automationEntry.artifact;
        var screenshot = screenshotEntry.artifact;
        var automationProvenance = automation.Provenance;
        var screenshotProvenance = screenshot.Provenance;
        if (!MatrixProvenanceMatches(automationProvenance, contract.TreeFingerprint, cell, expectedState)
            || !MatrixProvenanceMatches(screenshotProvenance, contract.TreeFingerprint, cell, expectedState)
            || screenshotProvenance?.LinkedAutomationArtifactId != cell.AutomationArtifactId)
        {
            issues.Add(new("bundle.ui_matrix_provenance", matrixArtifactIndex));
        }

        if (!artifactBytes.TryGetValue(cell.AutomationArtifactId, out var automationBytes)
            || !TryReadAutomationMatrixState(automationBytes, out var automationState)
            || automationState!.TreeFingerprint != contract.TreeFingerprint
            || automationState.ExpectedState != expectedState
            || automationState.Theme != cell.Theme
            || automationState.ViewportWidthDip != cell.ViewportWidthDip
            || automationState.ViewportHeightDip != cell.ViewportHeightDip
            || Math.Abs(automationState.RenderDpiScale - cell.RenderDpiScale) > 0.001m
            || !automationState.RenderDpiOverride
            || automationState.MotionPreferenceSource != cell.MotionPreferenceSource
            || automationState.AnimationsEnabled != cell.AnimationsEnabled)
        {
            issues.Add(new("bundle.ui_matrix_provenance", matrixArtifactIndex));
        }
        else if (automationState.FocusIdentity != cell.FocusCapture.AfterIdentity
                 || !automationState.HasVisibleKeyboardFocus)
        {
            issues.Add(new("bundle.ui_matrix_focus", matrixArtifactIndex));
        }
        else if (automationState.ExpectedStateSource != ExpectedStateSource
                 || automationState.SelectedView != "arena"
                 || automationState.ObservedSurfaceState != "arena-empty"
                 || automationState.DialogState != "closed"
                 || !automationState.VisibleRootIdentities.SequenceEqual(["TranscriptPanel"], StringComparer.Ordinal)
                 || !automationState.RequiredControlIdentities.SequenceEqual(RequiredArenaControls, StringComparer.Ordinal))
        {
            issues.Add(new("bundle.ui_matrix_observed_state", matrixArtifactIndex));
        }
    }

    private static bool MatrixProvenanceMatches(
        ArenaQaArtifactProvenance? provenance,
        string treeFingerprint,
        UiMatrixCell cell,
        string expectedState) =>
        provenance is not null
        && string.Equals(provenance.TreeFingerprint, treeFingerprint, StringComparison.OrdinalIgnoreCase)
        && provenance.Theme == cell.Theme
        && provenance.ViewportWidthDip == cell.ViewportWidthDip
        && provenance.ViewportHeightDip == cell.ViewportHeightDip
        && Math.Abs(provenance.DpiScale - cell.RenderDpiScale) <= 0.001m
        && provenance.ExpectedState == expectedState;

    private static bool IsValidFocusSequence(UiMatrixFocus next, UiMatrixFocus previous, UiMatrixFocus capture)
    {
        if (next.Direction != "next" || previous.Direction != "previous" || capture.Direction != "next"
            || !next.Moved || !next.FocusChanged
            || !previous.Moved || !previous.FocusChanged
            || !capture.Moved || !capture.FocusChanged
            || !IsSafeFocus(next) || !IsSafeFocus(previous) || !IsSafeFocus(capture)
            || capture.AfterIdentity == "none")
        {
            return false;
        }

        return SameFocus(next.AfterIdentity, next.AfterControlType, previous.BeforeIdentity, previous.BeforeControlType)
            && SameFocus(previous.AfterIdentity, previous.AfterControlType, capture.BeforeIdentity, capture.BeforeControlType)
            && SameFocus(next.BeforeIdentity, next.BeforeControlType, previous.AfterIdentity, previous.AfterControlType)
            && SameFocus(next.AfterIdentity, next.AfterControlType, capture.AfterIdentity, capture.AfterControlType);
    }

    private static bool IsSafeFocus(UiMatrixFocus value) =>
        IsSafeAutomationValue(value.BeforeIdentity)
        && IsSafeAutomationValue(value.BeforeControlType)
        && IsSafeAutomationValue(value.AfterIdentity)
        && IsSafeAutomationValue(value.AfterControlType)
        && !SameFocus(value.BeforeIdentity, value.BeforeControlType, value.AfterIdentity, value.AfterControlType);

    private static bool SameFocus(string leftIdentity, string leftType, string rightIdentity, string rightType) =>
        leftIdentity == rightIdentity && leftType == rightType;

    private static bool TryReadAutomationMatrixState(byte[] bytes, out AutomationMatrixState? value)
    {
        value = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 160
            });
            var root = document.RootElement;
            var focusIdentity = RequiredString(root, "FocusIdentity");
            var visibleFocusedNodes = root.GetProperty("Nodes").EnumerateArray().Count(node =>
                node.GetProperty("HasKeyboardFocus").GetBoolean()
                && node.GetProperty("IsVisible").GetBoolean()
                && node.GetProperty("IsFocusable").GetBoolean()
                && RequiredString(node, "Identity") == focusIdentity);
            value = new(
                RequiredString(root, "TreeFingerprint"),
                RequiredString(root, "ExpectedState"),
                RequiredString(root, "ExpectedStateSource"),
                RequiredString(root, "SelectedView"),
                RequiredString(root, "ObservedSurfaceState"),
                RequiredString(root, "DialogState"),
                ReadSafeStringArray(root, "VisibleRootIdentities", 16),
                ReadSafeStringArray(root, "RequiredControlIdentities", 64),
                RequiredString(root, "Theme"),
                root.GetProperty("ViewportWidthDip").GetInt32(),
                root.GetProperty("ViewportHeightDip").GetInt32(),
                root.GetProperty("RenderDpiScale").GetDecimal(),
                root.GetProperty("RenderDpiOverride").GetBoolean(),
                RequiredString(root, "MotionPreferenceSource"),
                root.GetProperty("AnimationsEnabled").GetBoolean(),
                focusIdentity,
                visibleFocusedNodes == 1);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException
                                   or OverflowException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static HashSet<string> ExpectedUiMatrixKeys(int passNumber)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in new[] { "dark-blue", "light", "high-contrast" })
        foreach (var width in new[] { 960, 1500 })
        foreach (var dpi in new[] { "1-0", "1-5", "2-0" })
        foreach (var motion in new[] { "normal", "reduced" })
        {
            result.Add($"p{passNumber:D2}.{theme}.w{width}.d{dpi}.{motion}");
        }
        return result;
    }

    private static bool TryParseRenderedUiPass(string gateId, out int passNumber)
    {
        passNumber = 0;
        const string suffix = ".rendered-ui";
        if (gateId.Length != "pass-00.rendered-ui".Length
            || !gateId.StartsWith("pass-", StringComparison.Ordinal)
            || !gateId.EndsWith(suffix, StringComparison.Ordinal)
            || !int.TryParse(gateId.AsSpan(5, 2), out passNumber)
            || passNumber is < 1 or > 5)
        {
            passNumber = 0;
            return false;
        }
        return true;
    }

    private static void ValidateScreenshotArtifact(
        byte[] bytes,
        ArenaQaArtifact artifact,
        int artifactIndex,
        IDictionary<string, DecodedPngEvidence> decodedPngEvidence,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        if (artifact.Provenance is null)
        {
            issues.Add(new("bundle.screenshot_provenance", artifactIndex));
            return;
        }

        if (!TryDecodePng(bytes, out var pixelWidth, out var pixelHeight, out var decoded))
        {
            issues.Add(new("bundle.screenshot_png", artifactIndex));
            return;
        }
        decodedPngEvidence[artifact.Id] = decoded!;

        var expectedWidth = (int)Math.Ceiling(artifact.Provenance.ViewportWidthDip * artifact.Provenance.DpiScale);
        var expectedHeight = (int)Math.Ceiling(artifact.Provenance.ViewportHeightDip * artifact.Provenance.DpiScale);
        if (pixelWidth <= 0
            || pixelHeight <= 0
            || Math.Abs(pixelWidth - expectedWidth) > 2
            || Math.Abs(pixelHeight - expectedHeight) > 2)
        {
            issues.Add(new("bundle.screenshot_dimensions", artifactIndex));
        }
    }

    private static void ValidateScreenshotLink(
        ArenaQaArtifact screenshot,
        int artifactIndex,
        IReadOnlyDictionary<string, (ArenaQaArtifact artifact, int index)> byId,
        ImmutableArray<VerificationEvidenceIssue>.Builder issues)
    {
        var screenshotProvenance = screenshot.Provenance;
        if (screenshotProvenance?.LinkedAutomationArtifactId is not { } automationId
            || !byId.TryGetValue(automationId, out var linked)
            || linked.artifact.Kind != "automation-tree"
            || linked.artifact.Provenance is null)
        {
            issues.Add(new("bundle.screenshot_link", artifactIndex));
            return;
        }

        var automationProvenance = linked.artifact.Provenance;
        if (!string.Equals(screenshotProvenance.TreeFingerprint, automationProvenance.TreeFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(screenshotProvenance.Theme, automationProvenance.Theme, StringComparison.Ordinal)
            || screenshotProvenance.ViewportWidthDip != automationProvenance.ViewportWidthDip
            || screenshotProvenance.ViewportHeightDip != automationProvenance.ViewportHeightDip
            || Math.Abs(screenshotProvenance.DpiScale - automationProvenance.DpiScale) > 0.001m
            || !string.Equals(screenshotProvenance.ExpectedState, automationProvenance.ExpectedState, StringComparison.Ordinal))
        {
            issues.Add(new("bundle.screenshot_link_provenance", artifactIndex));
        }
    }

    private static bool TryDecodePng(
        byte[] bytes,
        out int width,
        out int height,
        out DecodedPngEvidence? decodedEvidence)
    {
        width = 0;
        height = 0;
        decodedEvidence = null;
        if (bytes.Length < PngSignature.Length + 12
            || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return false;
        }

        var offset = PngSignature.Length;
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        byte colorType = 0;
        var bytesPerPixel = 0;
        using var compressed = new MemoryStream();

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > int.MaxValue)
            {
                return false;
            }

            var dataLength = (int)length;
            var chunkEnd = (long)offset + 12 + dataLength;
            if (chunkEnd > bytes.Length)
            {
                return false;
            }

            var type = bytes.AsSpan(offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, dataLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + dataLength, 4));
            if (!IsPngChunkType(type)
                || PngCrc32(bytes.AsSpan(offset + 4, 4 + dataLength)) != expectedCrc)
            {
                return false;
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || offset != PngSignature.Length || dataLength != 13)
                {
                    return false;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                var bitDepth = data[8];
                colorType = data[9];
                var compressionMethod = data[10];
                var filterMethod = data[11];
                var interlaceMethod = data[12];
                bytesPerPixel = colorType switch
                {
                    0 when bitDepth == 8 => 1,
                    2 when bitDepth == 8 => 3,
                    4 when bitDepth == 8 => 2,
                    6 when bitDepth == 8 => 4,
                    _ => 0
                };
                if (width <= 0
                    || height <= 0
                    || width > 16_384
                    || height > 16_384
                    || (long)width * height > MaximumPngPixels
                    || bytesPerPixel == 0
                    || compressionMethod != 0
                    || filterMethod != 0
                    || interlaceMethod != 0)
                {
                    return false;
                }

                sawHeader = true;
            }
            else if (!sawHeader || sawEnd)
            {
                return false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (compressed.Length + dataLength > MaximumArtifactBytes)
                {
                    return false;
                }

                compressed.Write(data);
                sawImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!sawImageData || dataLength != 0 || chunkEnd != bytes.Length)
                {
                    return false;
                }

                sawEnd = true;
            }
            else
            {
                // QA screenshots intentionally allow only the three chunks
                // required by the supported non-indexed, non-interlaced PNG
                // profile. Text, ICC, EXIF, time, and every other ancillary
                // channel are rejected even when their CRC is valid.
                return false;
            }

            offset = checked((int)chunkEnd);
            if (sawEnd)
            {
                break;
            }
        }

        if (!sawEnd || compressed.Length == 0)
        {
            return false;
        }

        try
        {
            var rowBytesLong = checked((long)width * bytesPerPixel);
            var rowStride = checked(rowBytesLong + 1);
            var expectedDecodedBytes = checked(rowStride * height);
            if (expectedDecodedBytes <= 0
                || expectedDecodedBytes > MaximumDecodedPngBytes
                || rowBytesLong > int.MaxValue)
            {
                return false;
            }

            var compressedBytes = compressed.ToArray();
            using var compressedInput = new MemoryStream(compressedBytes, writable: false);
            using var decoder = new ZLibStream(compressedInput, CompressionMode.Decompress, leaveOpen: false);
            using var pixelHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rowBytes = (int)rowBytesLong;
            var encodedRow = new byte[rowBytes + 1];
            var priorRow = new byte[rowBytes];
            var currentRow = new byte[rowBytes];
            var canonicalRgbaRow = new byte[checked(width * 4)];
            Span<byte> canonicalHeader = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(canonicalHeader[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(canonicalHeader[4..], height);
            pixelHasher.AppendData(canonicalHeader);
            var totalPixels = checked((long)width * height);
            var sampleWidth = Math.Min(width, 128);
            var sampleHeight = Math.Min(height, 128);
            var sampleColumns = new int[width];
            for (var index = 0; index < sampleColumns.Length; index++)
            {
                sampleColumns[index] = index * sampleWidth / width;
            }
            var sampleRows = new int[height];
            for (var index = 0; index < sampleRows.Length; index++)
            {
                sampleRows[index] = index * sampleHeight / height;
            }
            var sampleCellCount = checked(sampleWidth * sampleHeight);
            var sampleRedSums = new long[sampleCellCount];
            var sampleGreenSums = new long[sampleCellCount];
            var sampleBlueSums = new long[sampleCellCount];
            var samplePixelCounts = new int[sampleCellCount];
            var quantizedColorCounts = new long[4_096];
            var distinctQuantizedColors = 0;
            long opaquePixels = 0;
            byte minimumR = byte.MaxValue;
            byte minimumG = byte.MaxValue;
            byte minimumB = byte.MaxValue;
            byte maximumR = byte.MinValue;
            byte maximumG = byte.MinValue;
            byte maximumB = byte.MinValue;
            uint adlerA = 1;
            uint adlerB = 0;
            for (var rowIndex = 0; rowIndex < height; rowIndex++)
            {
                decoder.ReadExactly(encodedRow);
                UpdateAdler32(ref adlerA, ref adlerB, encodedRow);
                var filter = encodedRow[0];
                if (filter > 4)
                {
                    return false;
                }

                UnfilterPngRow(filter, encodedRow.AsSpan(1), currentRow, priorRow, bytesPerPixel);
                for (var x = 0; x < width; x++)
                {
                    var offsetInRow = x * bytesPerPixel;
                    byte red;
                    byte green;
                    byte blue;
                    byte alpha;
                    switch (colorType)
                    {
                        case 0:
                            red = green = blue = currentRow[offsetInRow];
                            alpha = byte.MaxValue;
                            break;
                        case 2:
                            red = currentRow[offsetInRow];
                            green = currentRow[offsetInRow + 1];
                            blue = currentRow[offsetInRow + 2];
                            alpha = byte.MaxValue;
                            break;
                        case 4:
                            red = green = blue = currentRow[offsetInRow];
                            alpha = currentRow[offsetInRow + 1];
                            break;
                        case 6:
                            red = currentRow[offsetInRow];
                            green = currentRow[offsetInRow + 1];
                            blue = currentRow[offsetInRow + 2];
                            alpha = currentRow[offsetInRow + 3];
                            break;
                        default:
                            return false;
                    }

                    var canonicalOffset = x * 4;
                    canonicalRgbaRow[canonicalOffset] = red;
                    canonicalRgbaRow[canonicalOffset + 1] = green;
                    canonicalRgbaRow[canonicalOffset + 2] = blue;
                    canonicalRgbaRow[canonicalOffset + 3] = alpha;
                    var sampleCell = sampleRows[rowIndex] * sampleWidth + sampleColumns[x];
                    sampleRedSums[sampleCell] += red;
                    sampleGreenSums[sampleCell] += green;
                    sampleBlueSums[sampleCell] += blue;
                    samplePixelCounts[sampleCell]++;
                    if (alpha == byte.MaxValue)
                    {
                        opaquePixels++;
                    }
                    // A full-window WPF capture is overwhelmingly opaque. Sparse
                    // opaque pixels over a transparent canvas cannot prove a
                    // rendered application surface.
                    if (alpha != byte.MaxValue)
                    {
                        continue;
                    }

                    minimumR = Math.Min(minimumR, red);
                    minimumG = Math.Min(minimumG, green);
                    minimumB = Math.Min(minimumB, blue);
                    maximumR = Math.Max(maximumR, red);
                    maximumG = Math.Max(maximumG, green);
                    maximumB = Math.Max(maximumB, blue);
                    var quantizedColor = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                    if (quantizedColorCounts[quantizedColor] == 0)
                    {
                        distinctQuantizedColors++;
                    }
                    quantizedColorCounts[quantizedColor]++;
                }

                pixelHasher.AppendData(canonicalRgbaRow);
                (priorRow, currentRow) = (currentRow, priorRow);
            }

            Span<byte> trailing = stackalloc byte[1];
            if (decoder.Read(trailing) != 0 || opaquePixels < 64)
            {
                return false;
            }

            var maximumChannelSpan = Math.Max(
                Math.Max(maximumR - minimumR, maximumG - minimumG),
                maximumB - minimumB);
            var dominantColorRatio = distinctQuantizedColors == 0
                ? 1d
                : (double)quantizedColorCounts.Max() / opaquePixels;
            var adler32 = (adlerB << 16) | adlerA;
            if (opaquePixels != totalPixels
                || distinctQuantizedColors < 4
                || maximumChannelSpan < 12
                || dominantColorRatio >= 0.98
                || !IsExactSingleZlibMember(compressedBytes, expectedDecodedBytes, adler32))
            {
                return false;
            }

            var visualSamples = new byte[checked(sampleCellCount * 3)];
            for (var cell = 0; cell < sampleCellCount; cell++)
            {
                var count = samplePixelCounts[cell];
                if (count <= 0)
                {
                    return false;
                }
                var sampleOffset = cell * 3;
                visualSamples[sampleOffset] = (byte)((sampleRedSums[cell] + count / 2) / count);
                visualSamples[sampleOffset + 1] = (byte)((sampleGreenSums[cell] + count / 2) / count);
                visualSamples[sampleOffset + 2] = (byte)((sampleBlueSums[cell] + count / 2) / count);
            }

            decodedEvidence = new DecodedPngEvidence(
                Convert.ToHexString(pixelHasher.GetHashAndReset()).ToLowerInvariant(),
                sampleWidth,
                sampleHeight,
                visualSamples);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static void UpdateAdler32(ref uint a, ref uint b, ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65_521;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(5_552, bytes.Length - offset);
            ulong nextA = a;
            ulong nextB = b;
            for (var index = 0; index < count; index++)
            {
                nextA += bytes[offset + index];
                nextB += nextA;
            }

            a = (uint)(nextA % modulus);
            b = (uint)(nextB % modulus);
            offset += count;
        }
    }

    private static bool IsExactSingleZlibMember(
        byte[] compressedBytes,
        long expectedDecodedBytes,
        uint decodedAdler32)
    {
        if (compressedBytes.Length < 6
            || BinaryPrimitives.ReadUInt32BigEndian(compressedBytes.AsSpan(^4)) != decodedAdler32)
        {
            return false;
        }

        // ZLibStream validates a member but permits opaque bytes after its Adler-32
        // trailer. The real trailer is still present inside such a payload. Locate
        // every earlier occurrence of the decoded checksum and reject if any prefix
        // through that occurrence is itself a complete zlib member. This detects
        // arbitrary appended IDAT bytes without relying on the decoder's read-ahead
        // position, while allowing harmless checksum byte patterns that do not form
        // a valid member boundary.
        for (var index = 2; index <= compressedBytes.Length - 8; index++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(compressedBytes.AsSpan(index, 4)) == decodedAdler32
                && CanInflateExactZlibPrefix(compressedBytes, index + 4, expectedDecodedBytes))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanInflateExactZlibPrefix(byte[] bytes, int length, long expectedDecodedBytes)
    {
        try
        {
            using var source = new MemoryStream(bytes, 0, length, writable: false, publiclyVisible: true);
            using var decoder = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false);
            var buffer = new byte[16 * 1024];
            long decoded = 0;
            while (true)
            {
                var read = decoder.Read(buffer);
                if (read == 0)
                {
                    return decoded == expectedDecodedBytes;
                }

                decoded = checked(decoded + read);
                if (decoded > expectedDecodedBytes)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasMaterialThemeDifference(DecodedPngEvidence left, DecodedPngEvidence right)
    {
        if (left.SampleWidth != right.SampleWidth
            || left.SampleHeight != right.SampleHeight
            || left.VisualRgbSamples.Length != right.VisualRgbSamples.Length
            || left.VisualRgbSamples.Length == 0)
        {
            return false;
        }

        var pixelCount = left.VisualRgbSamples.Length / 3;
        var changedPixels = 0;
        long totalChannelDelta = 0;
        for (var offset = 0; offset < left.VisualRgbSamples.Length; offset += 3)
        {
            var redDelta = Math.Abs(left.VisualRgbSamples[offset] - right.VisualRgbSamples[offset]);
            var greenDelta = Math.Abs(left.VisualRgbSamples[offset + 1] - right.VisualRgbSamples[offset + 1]);
            var blueDelta = Math.Abs(left.VisualRgbSamples[offset + 2] - right.VisualRgbSamples[offset + 2]);
            if (Math.Max(redDelta, Math.Max(greenDelta, blueDelta)) >= 8)
            {
                changedPixels++;
            }
            totalChannelDelta += redDelta + greenDelta + blueDelta;
        }

        return changedPixels >= Math.Max(1, (int)Math.Ceiling(pixelCount * 0.02))
            && (double)totalChannelDelta / (pixelCount * 3) >= 2;
    }

    private static void UnfilterPngRow(
        byte filter,
        ReadOnlySpan<byte> encoded,
        Span<byte> current,
        ReadOnlySpan<byte> prior,
        int bytesPerPixel)
    {
        for (var index = 0; index < encoded.Length; index++)
        {
            var left = index >= bytesPerPixel ? current[index - bytesPerPixel] : (byte)0;
            var above = prior[index];
            var upperLeft = index >= bytesPerPixel ? prior[index - bytesPerPixel] : (byte)0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => above,
                3 => (left + above) / 2,
                4 => Paeth(left, above, upperLeft),
                _ => throw new InvalidDataException("Unsupported PNG filter.")
            };
            current[index] = unchecked((byte)(encoded[index] + predictor));
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var prediction = left + above - upperLeft;
        var leftDistance = Math.Abs(prediction - left);
        var aboveDistance = Math.Abs(prediction - above);
        var upperLeftDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance
                ? above
                : upperLeft;
    }

    private static bool IsPngChunkType(ReadOnlySpan<byte> type)
    {
        if (type.Length != 4)
        {
            return false;
        }

        foreach (var value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z')
                and not (>= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return true;
    }

    private static uint PngCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }
        }

        return crc ^ uint.MaxValue;
    }

    private static async Task<ArtifactReadResult> ReadAndHashAsync(
        string path,
        long maximumBytes,
        bool captureBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Artifact exceeds its bounded read limit.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffer = captureBytes ? new MemoryStream((int)Math.Min(stream.Length, maximumBytes)) : null;
        var chunk = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            length += read;
            if (length > maximumBytes)
            {
                throw new InvalidDataException("Artifact exceeds its bounded read limit.");
            }
            hash.AppendData(chunk, 0, read);
            buffer?.Write(chunk, 0, read);
        }

        return new(
            length,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            buffer?.ToArray());
    }

    private static bool TryResolveArtifact(string root, string relativePath, out string fullPath)
    {
        fullPath = "";
        if (!ArenaContractPrivacyRules.IsSafeRelativePath(relativePath)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return IsWithinRoot(fullPath, normalizedRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string root, string relativePath)
    {
        try
        {
            var current = Path.GetFullPath(root);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var segment in relativePath.Split('/'))
            {
                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
        return false;
    }

    private static bool HasOnlyProperties(JsonElement element, IReadOnlySet<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) return false;
        }
        return seen.SetEquals(allowed);
    }

    private static ImmutableArray<string> ReadSafeStringArray(JsonElement root, string property, int maximum)
    {
        var element = root.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 || element.GetArrayLength() > maximum)
        {
            throw new InvalidOperationException();
        }

        var values = element.EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidOperationException())
            .ToImmutableArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length
            || !values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || values.Any(value => !IsSafeAutomationValue(value)))
        {
            throw new InvalidOperationException();
        }
        return values;
    }

    private static string BuildCanonicalState(
        string surface,
        string dialogState,
        string theme,
        int viewportWidthDip,
        decimal rasterDensityScale,
        string motionPreferenceSource)
    {
        var density = rasterDensityScale switch
        {
            1.0m => "1-0",
            1.5m => "1-5",
            2.0m => "2-0",
            _ => rasterDensityScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '-')
        };
        var motion = motionPreferenceSource switch
        {
            "qa-normal" => "normal",
            "qa-reduced" => "reduced",
            "system" => "system",
            _ => "invalid"
        };
        return $"{surface}.{dialogState}.{theme}.w{viewportWidthDip}.d{density}.{motion}";
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? throw new InvalidOperationException();

    private static bool IsSafeAutomationValue(string value, bool allowEmpty = false)
    {
        if (value.Length == 0) return allowEmpty;
        return value.Length <= 128
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '#');
    }

    private static bool IsSafeStateIdentifier(string value) =>
        value.Length is > 0 and <= 96
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');

    private static bool IsSupportedQaRenderDpiScale(double value) =>
        Math.Abs(value - 1.0) < 0.0001
        || Math.Abs(value - 1.5) < 0.0001
        || Math.Abs(value - 2.0) < 0.0001;

    private static bool IsWithinRoot(string path, string root)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<VerificationEvidenceIssue> Sort(
        ImmutableArray<VerificationEvidenceIssue>.Builder issues) =>
        [.. issues.Distinct()
            .OrderBy(issue => issue.ArtifactIndex)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)];

    [GeneratedRegex(
        "(?ix)(?:[a-z]:[\\\\/]|\\\\\\\\[^\\\\/\\s]+[\\\\/]|/(?:users|home|root|opt|mnt|private|var|tmp|etc)(?:/[^\\s\\\"']*)?|\\b(?:sk|pk|rk)-[a-z0-9_-]{10,}\\b|\\bgh[pousr]_[a-z0-9]{8,}\\b|\\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|client[_-]?secret|password|secret)\\s*[:=]|\\bbearer\\s+[a-z0-9._~+/=-]{10,}|\\\"(?:rawOutput|rawResponse|rawTranscript|sourceCode|sourceContent|sourceText|transcriptContent|promptContent|responseContent)\\\"\\s*:)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeEvidenceTextRegex();

    private sealed record UiMatrixDocument(
        int PassNumber,
        string TreeFingerprint,
        ImmutableArray<UiMatrixCell> Cells);

    private sealed record UiMatrixCell(
        string Key,
        string Theme,
        int ViewportWidthDip,
        int ViewportHeightDip,
        decimal RenderDpiScale,
        string MotionMode,
        string MotionPreferenceSource,
        bool AnimationsEnabled,
        UiMatrixFocus FocusNext,
        UiMatrixFocus FocusPrevious,
        UiMatrixFocus FocusCapture,
        string AutomationArtifactId,
        string ScreenshotArtifactId);

    private sealed record UiMatrixFocus(
        string Direction,
        string BeforeIdentity,
        string BeforeControlType,
        string AfterIdentity,
        string AfterControlType,
        bool Moved,
        bool FocusChanged);

    private sealed record AutomationMatrixState(
        string TreeFingerprint,
        string ExpectedState,
        string ExpectedStateSource,
        string SelectedView,
        string ObservedSurfaceState,
        string DialogState,
        ImmutableArray<string> VisibleRootIdentities,
        ImmutableArray<string> RequiredControlIdentities,
        string Theme,
        int ViewportWidthDip,
        int ViewportHeightDip,
        decimal RenderDpiScale,
        bool RenderDpiOverride,
        string MotionPreferenceSource,
        bool AnimationsEnabled,
        string FocusIdentity,
        bool HasVisibleKeyboardFocus);

    private sealed record DecodedPngEvidence(
        string PixelSha256,
        int SampleWidth,
        int SampleHeight,
        byte[] VisualRgbSamples);

    private sealed record ArtifactReadResult(long Length, string Sha256, byte[]? Bytes);
}
