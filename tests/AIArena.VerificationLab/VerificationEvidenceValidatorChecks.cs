using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIArena.Core.Models;
using AIArena.Core.Services;

namespace AIArena.VerificationLab;

internal static class VerificationEvidenceValidatorChecks
{
    private const string ExpectedState = "verification-window.closed.dark-blue.w960.d1-0.system";

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ai-arena-evidence-validator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var mapRoot = Path.Combine(root, "map");
            Directory.CreateDirectory(mapRoot);
            await InitializeRepositoryAsync(mapRoot, "map.txt", "map-v1\n", cancellationToken);

            await RunGitAsync(root, ["init", "--quiet"], cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "evidence/\n", new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "outer-v1\n", new UTF8Encoding(false), cancellationToken);
            await RunGitAsync(root, ["add", ".gitignore", "root.txt", "map"], cancellationToken);
            await CommitAsync(root, cancellationToken);

            var outer = await VerificationRepositoryCurrentValidator.CaptureAsync(root, excludeArtifacts: true, cancellationToken);
            var map = await VerificationRepositoryCurrentValidator.CaptureAsync(mapRoot, excludeArtifacts: false, cancellationToken);
            var combined = Sha256Text($"outer={outer.TreeFingerprint}\nmap={map.TreeFingerprint}\n");
            var evidenceRoot = Path.Combine(root, "evidence");
            var automationPath = Path.Combine(evidenceRoot, "automation", "tree.json");
            var screenshotPath = Path.Combine(evidenceRoot, "screenshots", "view.png");
            Directory.CreateDirectory(Path.GetDirectoryName(automationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);

            var capturedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
            var automationBytes = BuildAutomationArtifact(combined, capturedAt);
            var legacyAutomationJson = Encoding.UTF8.GetString(automationBytes);
            Require(legacyAutomationJson.Contains("\"Schema\":\"ai_arena.ui_structure_evidence.v1\"", StringComparison.Ordinal)
                    && !legacyAutomationJson.Contains("\"BoundsX\"", StringComparison.Ordinal)
                    && !legacyAutomationJson.Contains("\"EffectiveOpacity\"", StringComparison.Ordinal)
                    && !legacyAutomationJson.Contains("\"IsRendered\"", StringComparison.Ordinal),
                "Historical V1 automation fixture no longer preserves the genuine pre-renderability node shape.");
            var screenshotBytes = BuildPngArtifact(960, 640, variant: 1);
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);
            var contract = BuildContract(outer, map, combined, capturedAt, automationBytes, screenshotBytes);
            var evidencePath = Path.Combine(evidenceRoot, "qa-evidence.json");
            await File.WriteAllTextAsync(
                evidencePath,
                ArenaContractCodec.Serialize(contract),
                new UTF8Encoding(false),
                cancellationToken);

            var validOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(evidencePath, validOutput, cancellationToken) == 0,
                $"Valid evidence bundle was rejected: {validOutput.ToString().Trim()}");
            Require(await VerificationEvidenceValidator.ValidateCurrentFileAsync(evidencePath, root, new StringWriter(), cancellationToken) == 0,
                "Current matching repository evidence was rejected.");
            Require(await VerificationEvidenceValidator.ValidateCurrentFileAsync(evidencePath, " ", new StringWriter(), cancellationToken) == 2,
                "Blank current-repository roots should fail before validation.");

            var invalidMotionBytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(automationBytes).Replace(
                    "\"MotionPreferenceSource\":\"system\"",
                    "\"MotionPreferenceSource\":\"qa-reduced\"",
                    StringComparison.Ordinal));
            await File.WriteAllBytesAsync(automationPath, invalidMotionBytes, cancellationToken);
            var invalidMotionContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                invalidMotionBytes,
                screenshotBytes);
            var invalidMotionPath = Path.Combine(evidenceRoot, "invalid-motion.json");
            await File.WriteAllTextAsync(
                invalidMotionPath,
                ArenaContractCodec.Serialize(invalidMotionContract),
                new UTF8Encoding(false),
                cancellationToken);
            var invalidMotionOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(invalidMotionPath, invalidMotionOutput, cancellationToken) != 0
                && invalidMotionOutput.ToString().Contains("bundle.automation_motion", StringComparison.Ordinal),
                "Contradictory reduced-motion evidence was not rejected.");
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);

            var callerEchoAutomationBytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(automationBytes).Replace(
                    "\"ObservedSurfaceState\":\"verification-window\"",
                    "\"ObservedSurfaceState\":\"arena-empty\"",
                    StringComparison.Ordinal));
            await File.WriteAllBytesAsync(automationPath, callerEchoAutomationBytes, cancellationToken);
            var callerEchoContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                callerEchoAutomationBytes,
                screenshotBytes);
            var callerEchoPath = Path.Combine(evidenceRoot, "caller-echo-state.json");
            await File.WriteAllTextAsync(
                callerEchoPath,
                ArenaContractCodec.Serialize(callerEchoContract),
                new UTF8Encoding(false),
                cancellationToken);
            var callerEchoOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(callerEchoPath, callerEchoOutput, cancellationToken) != 0
                && callerEchoOutput.ToString().Contains("bundle.automation_observed_state", StringComparison.Ordinal),
                "A caller-like surface label that disagreed with reconstructed state was not rejected.");
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);

            var truncatedAutomationBytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(automationBytes).Replace(
                    "\"Truncated\":false",
                    "\"Truncated\":true",
                    StringComparison.Ordinal));
            await File.WriteAllBytesAsync(automationPath, truncatedAutomationBytes, cancellationToken);
            var truncatedAutomationContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                truncatedAutomationBytes,
                screenshotBytes);
            var truncatedAutomationPath = Path.Combine(evidenceRoot, "truncated-automation.json");
            await File.WriteAllTextAsync(
                truncatedAutomationPath,
                ArenaContractCodec.Serialize(truncatedAutomationContract),
                new UTF8Encoding(false),
                cancellationToken);
            var truncatedAutomationOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(truncatedAutomationPath, truncatedAutomationOutput, cancellationToken) != 0
                && truncatedAutomationOutput.ToString().Contains("bundle.automation_truncated", StringComparison.Ordinal),
                "Truncated automation evidence was not rejected.");
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);

            var duplicateIdentityAutomationBytes = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(automationBytes).Replace(
                    "QaPrimaryButton",
                    "QaRoot",
                    StringComparison.Ordinal));
            await File.WriteAllBytesAsync(automationPath, duplicateIdentityAutomationBytes, cancellationToken);
            var duplicateIdentityContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                duplicateIdentityAutomationBytes,
                screenshotBytes);
            var duplicateIdentityPath = Path.Combine(evidenceRoot, "duplicate-automation-identity.json");
            await File.WriteAllTextAsync(
                duplicateIdentityPath,
                ArenaContractCodec.Serialize(duplicateIdentityContract),
                new UTF8Encoding(false),
                cancellationToken);
            var duplicateIdentityOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(duplicateIdentityPath, duplicateIdentityOutput, cancellationToken) != 0
                && duplicateIdentityOutput.ToString().Contains("bundle.automation_nodes", StringComparison.Ordinal),
                "Duplicate privacy-safe automation node identities were not rejected.");
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);

            var corruptPngBytes = screenshotBytes.ToArray();
            var idatTypeOffset = FindSequence(corruptPngBytes, "IDAT"u8);
            Require(idatTypeOffset >= 0 && idatTypeOffset + 4 < corruptPngBytes.Length, "Valid PNG fixture did not contain image data.");
            corruptPngBytes[idatTypeOffset + 4] ^= 0x01;
            await File.WriteAllBytesAsync(screenshotPath, corruptPngBytes, cancellationToken);
            var corruptPngContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                corruptPngBytes);
            var corruptPngPath = Path.Combine(evidenceRoot, "corrupt-png.json");
            await File.WriteAllTextAsync(
                corruptPngPath,
                ArenaContractCodec.Serialize(corruptPngContract),
                new UTF8Encoding(false),
                cancellationToken);
            var corruptPngOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(corruptPngPath, corruptPngOutput, cancellationToken) != 0
                && corruptPngOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "CRC-invalid PNG image data was not rejected after its artifact hash was updated.");
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            var uniformPngBytes = BuildPngArtifact(960, 640, uniform: true);
            await File.WriteAllBytesAsync(screenshotPath, uniformPngBytes, cancellationToken);
            var uniformPngContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                uniformPngBytes);
            var uniformPngPath = Path.Combine(evidenceRoot, "uniform-png.json");
            await File.WriteAllTextAsync(
                uniformPngPath,
                ArenaContractCodec.Serialize(uniformPngContract),
                new UTF8Encoding(false),
                cancellationToken);
            var uniformPngOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(uniformPngPath, uniformPngOutput, cancellationToken) != 0
                && uniformPngOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A uniform PNG was not rejected after its artifact hash was updated.");

            var nearUniformPngBytes = BuildPngArtifact(960, 640, nearUniform: true);
            await File.WriteAllBytesAsync(screenshotPath, nearUniformPngBytes, cancellationToken);
            var nearUniformPngContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                nearUniformPngBytes);
            var nearUniformPngPath = Path.Combine(evidenceRoot, "near-uniform-png.json");
            await File.WriteAllTextAsync(
                nearUniformPngPath,
                ArenaContractCodec.Serialize(nearUniformPngContract),
                new UTF8Encoding(false),
                cancellationToken);
            var nearUniformPngOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(nearUniformPngPath, nearUniformPngOutput, cancellationToken) != 0
                && nearUniformPngOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A near-uniform PNG was not rejected after its artifact hash was updated.");

            var sparseVariationPngBytes = BuildPngArtifact(960, 640, sparseAdversarialVariation: true);
            await File.WriteAllBytesAsync(screenshotPath, sparseVariationPngBytes, cancellationToken);
            var sparseVariationContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                sparseVariationPngBytes);
            var sparseVariationPath = Path.Combine(evidenceRoot, "sparse-variation-png.json");
            await File.WriteAllTextAsync(
                sparseVariationPath,
                ArenaContractCodec.Serialize(sparseVariationContract),
                new UTF8Encoding(false),
                cancellationToken);
            var sparseVariationOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(sparseVariationPath, sparseVariationOutput, cancellationToken) != 0
                && sparseVariationOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "Four adversarial sampled pixels were allowed to disguise an otherwise uniform PNG.");

            var subPercentVariationPngBytes = BuildPngArtifact(960, 640, subPercentAdversarialVariation: true);
            await File.WriteAllBytesAsync(screenshotPath, subPercentVariationPngBytes, cancellationToken);
            var subPercentVariationContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                subPercentVariationPngBytes);
            var subPercentVariationPath = Path.Combine(evidenceRoot, "sub-percent-variation-png.json");
            await File.WriteAllTextAsync(
                subPercentVariationPath,
                ArenaContractCodec.Serialize(subPercentVariationContract),
                new UTF8Encoding(false),
                cancellationToken);
            var subPercentVariationOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(subPercentVariationPath, subPercentVariationOutput, cancellationToken) != 0
                && subPercentVariationOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A PNG with only 0.51% varied pixels was not rejected as near-uniform.");

            var sparseAlphaPngBytes = BuildPngArtifact(960, 640, sparseOpaqueAdversarial: true);
            await File.WriteAllBytesAsync(screenshotPath, sparseAlphaPngBytes, cancellationToken);
            var sparseAlphaContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                sparseAlphaPngBytes);
            var sparseAlphaPath = Path.Combine(evidenceRoot, "sparse-alpha-png.json");
            await File.WriteAllTextAsync(
                sparseAlphaPath,
                ArenaContractCodec.Serialize(sparseAlphaContract),
                new UTF8Encoding(false),
                cancellationToken);
            var sparseAlphaOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(sparseAlphaPath, sparseAlphaOutput, cancellationToken) != 0
                && sparseAlphaOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A nearly transparent PNG with only sparse varied opaque samples was not rejected.");

            var lowAlphaPngBytes = BuildPngArtifact(960, 640, lowAlphaAdversarial: true);
            await File.WriteAllBytesAsync(screenshotPath, lowAlphaPngBytes, cancellationToken);
            var lowAlphaContract = BuildContract(outer, map, combined, capturedAt, automationBytes, lowAlphaPngBytes);
            var lowAlphaPath = Path.Combine(evidenceRoot, "low-alpha-png.json");
            await File.WriteAllTextAsync(
                lowAlphaPath,
                ArenaContractCodec.Serialize(lowAlphaContract),
                new UTF8Encoding(false),
                cancellationToken);
            var lowAlphaOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(lowAlphaPath, lowAlphaOutput, cancellationToken) != 0
                && lowAlphaOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A varied PNG with alpha 16 at every pixel was accepted as a full-window capture.");

            var halfOpaquePngBytes = BuildPngArtifact(960, 640, halfOpaqueAdversarial: true);
            await File.WriteAllBytesAsync(screenshotPath, halfOpaquePngBytes, cancellationToken);
            var halfOpaqueContract = BuildContract(outer, map, combined, capturedAt, automationBytes, halfOpaquePngBytes);
            var halfOpaquePath = Path.Combine(evidenceRoot, "half-opaque-png.json");
            await File.WriteAllTextAsync(
                halfOpaquePath,
                ArenaContractCodec.Serialize(halfOpaqueContract),
                new UTF8Encoding(false),
                cancellationToken);
            var halfOpaqueOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(halfOpaquePath, halfOpaqueOutput, cancellationToken) != 0
                && halfOpaqueOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A PNG with every other pixel transparent was accepted as a full-window capture.");

            var hiddenRgbPngBytes = BuildPngArtifact(960, 640, variant: 1, hiddenRgbAdversarial: true);
            await File.WriteAllBytesAsync(screenshotPath, hiddenRgbPngBytes, cancellationToken);
            var hiddenRgbContract = BuildContract(outer, map, combined, capturedAt, automationBytes, hiddenRgbPngBytes);
            var hiddenRgbPath = Path.Combine(evidenceRoot, "hidden-rgb-png.json");
            await File.WriteAllTextAsync(
                hiddenRgbPath,
                ArenaContractCodec.Serialize(hiddenRgbContract),
                new UTF8Encoding(false),
                cancellationToken);
            var hiddenRgbOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(hiddenRgbPath, hiddenRgbOutput, cancellationToken) != 0
                && hiddenRgbOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "Hidden RGB under one transparent pixel was accepted into decoded visual evidence.");

            var trailingIdatPngBytes = BuildPngArtifact(960, 640, variant: 1, trailingIdatMetadata: true);
            await File.WriteAllBytesAsync(screenshotPath, trailingIdatPngBytes, cancellationToken);
            var trailingIdatContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                trailingIdatPngBytes);
            var trailingIdatPath = Path.Combine(evidenceRoot, "trailing-idat-png.json");
            await File.WriteAllTextAsync(
                trailingIdatPath,
                ArenaContractCodec.Serialize(trailingIdatContract),
                new UTF8Encoding(false),
                cancellationToken);
            var trailingIdatOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(trailingIdatPath, trailingIdatOutput, cancellationToken) != 0
                && trailingIdatOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A valid zlib member followed by SECRET-METADATA-IN-IDAT bytes was not rejected.");

            var metadataPngBytes = BuildPngArtifact(960, 640, variant: 1, includeTextMetadata: true);
            await File.WriteAllBytesAsync(screenshotPath, metadataPngBytes, cancellationToken);
            var metadataPngContract = BuildContract(
                outer,
                map,
                combined,
                capturedAt,
                automationBytes,
                metadataPngBytes);
            var metadataPngPath = Path.Combine(evidenceRoot, "metadata-png.json");
            await File.WriteAllTextAsync(
                metadataPngPath,
                ArenaContractCodec.Serialize(metadataPngContract),
                new UTF8Encoding(false),
                cancellationToken);
            var metadataPngOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(metadataPngPath, metadataPngOutput, cancellationToken) != 0
                && metadataPngOutput.ToString().Contains("bundle.screenshot_png", StringComparison.Ordinal),
                "A PNG carrying ancillary text metadata was not rejected after its artifact hash was updated.");
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            File.Delete(screenshotPath);
            var missingOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(evidencePath, missingOutput, cancellationToken) != 0
                && missingOutput.ToString().Contains("bundle.missing", StringComparison.Ordinal),
                "Missing bundle artifact was not rejected.");
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            var replaced = screenshotBytes.ToArray();
            replaced[25] ^= 1;
            await File.WriteAllBytesAsync(screenshotPath, replaced, cancellationToken);
            var replacedOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(evidencePath, replacedOutput, cancellationToken) != 0
                && replacedOutput.ToString().Contains("bundle.hash", StringComparison.Ordinal),
                "Replaced bundle artifact was not rejected.");
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            var traversalJson = ArenaContractCodec.Serialize(contract)
                .Replace("automation/tree.json", "../outside.json", StringComparison.Ordinal);
            var traversalPath = Path.Combine(evidenceRoot, "traversal.json");
            await File.WriteAllTextAsync(traversalPath, traversalJson, new UTF8Encoding(false), cancellationToken);
            var traversalOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(traversalPath, traversalOutput, cancellationToken) != 0
                && traversalOutput.ToString().Contains("privacy.relative_path", StringComparison.Ordinal),
                "Traversal artifact reference was not rejected at the contract boundary.");

            await RunUiMatrixValidationChecksAsync(
                evidenceRoot,
                outer,
                map,
                combined,
                capturedAt,
                cancellationToken);
            await RunFeatureSurfaceMatrixValidationChecksAsync(
                evidenceRoot,
                outer,
                map,
                combined,
                capturedAt,
                cancellationToken);

            await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "outer-v2\n", new UTF8Encoding(false), cancellationToken);
            var staleOuterOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateCurrentFileAsync(evidencePath, root, staleOuterOutput, cancellationToken) != 0
                && staleOuterOutput.ToString().Contains("current.tree_fingerprint", StringComparison.Ordinal)
                && staleOuterOutput.ToString().Contains("current.clean_state", StringComparison.Ordinal),
                "Stale outer repository evidence was not rejected.");
            await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "outer-v1\n", new UTF8Encoding(false), cancellationToken);

            await File.WriteAllTextAsync(Path.Combine(mapRoot, "map.txt"), "map-v2\n", new UTF8Encoding(false), cancellationToken);
            var staleMapOutput = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateCurrentFileAsync(evidencePath, root, staleMapOutput, cancellationToken) != 0
                && staleMapOutput.ToString().Contains("current.map_fingerprint", StringComparison.Ordinal)
                && staleMapOutput.ToString().Contains("current.map_clean_state", StringComparison.Ordinal),
                "Stale nested Map evidence was not rejected.");

            var combinedOutput = string.Concat(
                validOutput,
                invalidMotionOutput,
                callerEchoOutput,
                truncatedAutomationOutput,
                duplicateIdentityOutput,
                corruptPngOutput,
                uniformPngOutput,
                nearUniformPngOutput,
                sparseVariationOutput,
                subPercentVariationOutput,
                sparseAlphaOutput,
                lowAlphaOutput,
                halfOpaqueOutput,
                hiddenRgbOutput,
                trailingIdatOutput,
                metadataPngOutput,
                missingOutput,
                replacedOutput,
                traversalOutput,
                staleOuterOutput,
                staleMapOutput);
            Require(!combinedOutput.Contains(root, StringComparison.OrdinalIgnoreCase)
                && !combinedOutput.Contains("outer-v2", StringComparison.Ordinal)
                && !combinedOutput.Contains("map-v2", StringComparison.Ordinal),
                "Validator output leaked repository paths or source content.");
        }
        finally
        {
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullRoot).StartsWith("ai-arena-evidence-validator-", StringComparison.Ordinal)
                && Directory.Exists(fullRoot))
            {
                foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static async Task RunUiMatrixValidationChecksAsync(
        string evidenceRoot,
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var fixture = await BuildUiMatrixFixtureAsync(
            evidenceRoot,
            outer,
            map,
            combinedFingerprint,
            capturedAt,
            enhancedAutomation: false,
            cancellationToken: cancellationToken);
        var validPath = Path.Combine(evidenceRoot, "valid-ui-matrix.json");
        await WriteContractAsync(validPath, fixture.Contract, cancellationToken);
        Require(await VerificationEvidenceValidator.ValidateFileAsync(validPath, new StringWriter(), cancellationToken) == 0,
            "A complete 36-cell UI matrix bundle was rejected.");

        var darkScreenshot = fixture.Contract.Artifacts.First(artifact =>
            artifact.Id.Contains(".dark-blue.w960.d1-0.normal.screenshot", StringComparison.Ordinal));
        var lightScreenshot = fixture.Contract.Artifacts.First(artifact =>
            artifact.Id.Contains(".light.w960.d1-0.normal.screenshot", StringComparison.Ordinal));
        var darkScreenshotPath = Path.Combine(evidenceRoot, darkScreenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var lightScreenshotPath = Path.Combine(evidenceRoot, lightScreenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalLightBytes = await File.ReadAllBytesAsync(lightScreenshotPath, cancellationToken);
        var duplicatedThemeBytes = await File.ReadAllBytesAsync(darkScreenshotPath, cancellationToken);
        await File.WriteAllBytesAsync(lightScreenshotPath, duplicatedThemeBytes, cancellationToken);
        var duplicateThemeContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Select(artifact => artifact.Id == lightScreenshot.Id
                ? artifact with { Sha256 = Sha256(duplicatedThemeBytes) }
                : artifact)]
        };
        var duplicateThemePath = Path.Combine(evidenceRoot, "duplicate-theme-render.json");
        await WriteContractAsync(duplicateThemePath, duplicateThemeContract, cancellationToken);
        var duplicateThemeOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(duplicateThemePath, duplicateThemeOutput, cancellationToken) != 0
                && duplicateThemeOutput.ToString().Contains("bundle.ui_matrix_theme_render", StringComparison.Ordinal),
            "Identical Dark Blue and Light screenshots for one matrix coordinate were not rejected.");
        await File.WriteAllBytesAsync(lightScreenshotPath, originalLightBytes, cancellationToken);

        var reencodedDarkPixels = BuildPngArtifact(960, 640, variant: 1, splitImageData: true);
        Require(!Sha256(reencodedDarkPixels).Equals(darkScreenshot.Sha256, StringComparison.OrdinalIgnoreCase),
            "The cross-theme decoded-pixel fixture must use a distinct PNG container hash.");
        await File.WriteAllBytesAsync(lightScreenshotPath, reencodedDarkPixels, cancellationToken);
        var duplicateDecodedThemeContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Select(artifact => artifact.Id == lightScreenshot.Id
                ? artifact with { Sha256 = Sha256(reencodedDarkPixels) }
                : artifact)]
        };
        var duplicateDecodedThemePath = Path.Combine(evidenceRoot, "duplicate-decoded-theme-render.json");
        await WriteContractAsync(duplicateDecodedThemePath, duplicateDecodedThemeContract, cancellationToken);
        var duplicateDecodedThemeOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(duplicateDecodedThemePath, duplicateDecodedThemeOutput, cancellationToken) != 0
                && duplicateDecodedThemeOutput.ToString().Contains("bundle.ui_matrix_theme_render", StringComparison.Ordinal),
            "Identical decoded Dark Blue and Light pixels in distinct PNG containers were not rejected.");
        await File.WriteAllBytesAsync(lightScreenshotPath, originalLightBytes, cancellationToken);

        var onePixelThemeDelta = BuildPngArtifact(
            960,
            640,
            variant: 1,
            splitImageData: true,
            singlePixelThemeDelta: true);
        Require(!Sha256(onePixelThemeDelta).Equals(darkScreenshot.Sha256, StringComparison.OrdinalIgnoreCase),
            "The one-pixel theme fixture must use a distinct PNG container hash.");
        await File.WriteAllBytesAsync(lightScreenshotPath, onePixelThemeDelta, cancellationToken);
        var onePixelThemeContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Select(artifact => artifact.Id == lightScreenshot.Id
                ? artifact with { Sha256 = Sha256(onePixelThemeDelta) }
                : artifact)]
        };
        var onePixelThemePath = Path.Combine(evidenceRoot, "one-pixel-theme-render.json");
        await WriteContractAsync(onePixelThemePath, onePixelThemeContract, cancellationToken);
        var onePixelThemeOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(onePixelThemePath, onePixelThemeOutput, cancellationToken) != 0
                && onePixelThemeOutput.ToString().Contains("bundle.ui_matrix_theme_render", StringComparison.Ordinal),
            "A one-visible-pixel delta was accepted as material Dark Blue versus Light theme evidence.");
        await File.WriteAllBytesAsync(lightScreenshotPath, originalLightBytes, cancellationToken);

        var targetedPointThemeDelta = BuildPngArtifact(
            960,
            640,
            variant: 1,
            splitImageData: true,
            targetedSamplePointThemeDelta: true);
        await File.WriteAllBytesAsync(lightScreenshotPath, targetedPointThemeDelta, cancellationToken);
        var targetedPointThemeContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Select(artifact => artifact.Id == lightScreenshot.Id
                ? artifact with { Sha256 = Sha256(targetedPointThemeDelta) }
                : artifact)]
        };
        var targetedPointThemePath = Path.Combine(evidenceRoot, "targeted-point-theme-render.json");
        await WriteContractAsync(targetedPointThemePath, targetedPointThemeContract, cancellationToken);
        var targetedPointThemeOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(targetedPointThemePath, targetedPointThemeOutput, cancellationToken) != 0
                && targetedPointThemeOutput.ToString().Contains("bundle.ui_matrix_theme_render", StringComparison.Ordinal),
            "Sparse changes aimed at the former point-sample grid were accepted as material theme evidence.");
        await File.WriteAllBytesAsync(lightScreenshotPath, originalLightBytes, cancellationToken);

        var missingContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Where(artifact => artifact.Kind != "qa-ui-matrix")]
        };
        var missingPath = Path.Combine(evidenceRoot, "missing-ui-matrix.json");
        await WriteContractAsync(missingPath, missingContract, cancellationToken);
        var missingOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(missingPath, missingOutput, cancellationToken) != 0
                && missingOutput.ToString().Contains("bundle.ui_matrix_missing", StringComparison.Ordinal),
            "Passing UI matrix gates without a matrix document were not rejected.");

        var duplicateRelativePath = "metadata/pass-01.ui-matrix.duplicate.json";
        var duplicatePath = Path.Combine(evidenceRoot, duplicateRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(duplicatePath, fixture.MatrixBytes, cancellationToken);
        var duplicateArtifact = fixture.MatrixArtifact with
        {
            Id = "artifact.pass-01.ui-matrix.duplicate",
            RelativePath = duplicateRelativePath
        };
        var duplicateContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts, duplicateArtifact]
        };
        var duplicateEvidencePath = Path.Combine(evidenceRoot, "duplicate-ui-matrix.json");
        await WriteContractAsync(duplicateEvidencePath, duplicateContract, cancellationToken);
        var duplicateOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(duplicateEvidencePath, duplicateOutput, cancellationToken) != 0
                && duplicateOutput.ToString().Contains("bundle.ui_matrix_duplicate", StringComparison.Ordinal),
            "Two matrix documents claiming the same rendered pass were not rejected.");

        var corruptBytes = "{"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.MatrixPath, corruptBytes, cancellationToken);
        var corruptContract = ReplaceMatrixArtifact(fixture.Contract, fixture.MatrixArtifact with { Sha256 = Sha256(corruptBytes) });
        var corruptEvidencePath = Path.Combine(evidenceRoot, "corrupt-ui-matrix.json");
        await WriteContractAsync(corruptEvidencePath, corruptContract, cancellationToken);
        var corruptOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(corruptEvidencePath, corruptOutput, cancellationToken) != 0
                && corruptOutput.ToString().Contains("bundle.ui_matrix_schema", StringComparison.Ordinal),
            $"Corrupt matrix JSON was not rejected after its artifact hash was updated: {corruptOutput.ToString().Trim()}");

        var mismatchedJson = ReplaceFirst(
            Encoding.UTF8.GetString(fixture.MatrixBytes),
            "\"animationsEnabled\":true",
            "\"animationsEnabled\":false");
        var mismatchedBytes = Encoding.UTF8.GetBytes(mismatchedJson);
        await File.WriteAllBytesAsync(fixture.MatrixPath, mismatchedBytes, cancellationToken);
        var mismatchedContract = ReplaceMatrixArtifact(fixture.Contract, fixture.MatrixArtifact with { Sha256 = Sha256(mismatchedBytes) });
        var mismatchedEvidencePath = Path.Combine(evidenceRoot, "mismatched-ui-matrix.json");
        await WriteContractAsync(mismatchedEvidencePath, mismatchedContract, cancellationToken);
        var mismatchedOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(mismatchedEvidencePath, mismatchedOutput, cancellationToken) != 0
                && mismatchedOutput.ToString().Contains("bundle.ui_matrix_motion", StringComparison.Ordinal),
            "Matrix motion metadata that disagreed with its automation capture was not rejected.");

        var focusJson = ReplaceFirst(
            Encoding.UTF8.GetString(fixture.MatrixBytes),
            "\"afterIdentity\":\"QaFocusSecond\"",
            "\"afterIdentity\":\"QaFocusThird\"");
        var focusBytes = Encoding.UTF8.GetBytes(focusJson);
        await File.WriteAllBytesAsync(fixture.MatrixPath, focusBytes, cancellationToken);
        var focusContract = ReplaceMatrixArtifact(fixture.Contract, fixture.MatrixArtifact with { Sha256 = Sha256(focusBytes) });
        var focusEvidencePath = Path.Combine(evidenceRoot, "focus-ui-matrix.json");
        await WriteContractAsync(focusEvidencePath, focusContract, cancellationToken);
        var focusOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(focusEvidencePath, focusOutput, cancellationToken) != 0
                && focusOutput.ToString().Contains("bundle.ui_matrix_focus", StringComparison.Ordinal),
            "Matrix focus metadata that did not round-trip or match capture focus was not rejected.");

        var aliasedFocusJson = Encoding.UTF8.GetString(fixture.MatrixBytes).Replace(
            "QaFocusFirst",
            "QaFocusSecond",
            StringComparison.Ordinal);
        var aliasedFocusBytes = Encoding.UTF8.GetBytes(aliasedFocusJson);
        await File.WriteAllBytesAsync(fixture.MatrixPath, aliasedFocusBytes, cancellationToken);
        var aliasedFocusContract = ReplaceMatrixArtifact(
            fixture.Contract,
            fixture.MatrixArtifact with { Sha256 = Sha256(aliasedFocusBytes) });
        var aliasedFocusPath = Path.Combine(evidenceRoot, "aliased-focus-ui-matrix.json");
        await WriteContractAsync(aliasedFocusPath, aliasedFocusContract, cancellationToken);
        var aliasedFocusOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(aliasedFocusPath, aliasedFocusOutput, cancellationToken) != 0
                && aliasedFocusOutput.ToString().Contains("bundle.ui_matrix_focus", StringComparison.Ordinal),
            "Matrix focus steps that claimed a change while retaining one aliased identity were not rejected.");

        var lostFocusJson = Encoding.UTF8.GetString(fixture.MatrixBytes).Replace(
            "QaFocusFirst",
            "none",
            StringComparison.Ordinal);
        var lostFocusBytes = Encoding.UTF8.GetBytes(lostFocusJson);
        await File.WriteAllBytesAsync(fixture.MatrixPath, lostFocusBytes, cancellationToken);
        var lostFocusContract = ReplaceMatrixArtifact(
            fixture.Contract,
            fixture.MatrixArtifact with { Sha256 = Sha256(lostFocusBytes) });
        var lostFocusPath = Path.Combine(evidenceRoot, "lost-focus-ui-matrix.json");
        await WriteContractAsync(lostFocusPath, lostFocusContract, cancellationToken);
        var lostFocusOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(lostFocusPath, lostFocusOutput, cancellationToken) != 0
                && lostFocusOutput.ToString().Contains("bundle.ui_matrix_focus", StringComparison.Ordinal),
            "A closed matrix cycle that lost focus to none on an intermediate step was not rejected.");

        var staleFingerprint = new string('a', 64);
        if (string.Equals(staleFingerprint, combinedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            staleFingerprint = new string('b', 64);
        }
        var staleJson = ReplaceFirst(
            Encoding.UTF8.GetString(fixture.MatrixBytes),
            combinedFingerprint,
            staleFingerprint);
        var staleBytes = Encoding.UTF8.GetBytes(staleJson);
        await File.WriteAllBytesAsync(fixture.MatrixPath, staleBytes, cancellationToken);
        var staleContract = ReplaceMatrixArtifact(fixture.Contract, fixture.MatrixArtifact with { Sha256 = Sha256(staleBytes) });
        var staleEvidencePath = Path.Combine(evidenceRoot, "stale-ui-matrix.json");
        await WriteContractAsync(staleEvidencePath, staleContract, cancellationToken);
        var staleOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(staleEvidencePath, staleOutput, cancellationToken) != 0
                && staleOutput.ToString().Contains("bundle.ui_matrix_provenance", StringComparison.Ordinal),
            "A matrix document bound to a different source tree was not rejected.");

        await File.WriteAllBytesAsync(fixture.MatrixPath, fixture.MatrixBytes, cancellationToken);
    }

    private static async Task RunFeatureSurfaceMatrixValidationChecksAsync(
        string evidenceRoot,
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var fixture = await BuildFeatureSurfaceMatrixFixtureAsync(
            evidenceRoot,
            outer,
            map,
            combinedFingerprint,
            capturedAt,
            enhancedAutomation: true,
            cancellationToken: cancellationToken);
        var validPath = Path.Combine(evidenceRoot, "valid-feature-surface-matrix.json");
        await WriteContractAsync(validPath, fixture.Contract, cancellationToken);
        var validOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(validPath, validOutput, cancellationToken) == 0,
            $"A complete 60-cell feature-surface matrix was rejected: {validOutput.ToString().Trim()}");

        var missingContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Where(artifact =>
                artifact.Kind != ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind)]
        };
        var missingPath = Path.Combine(evidenceRoot, "missing-feature-surface-matrix.json");
        await WriteContractAsync(missingPath, missingContract, cancellationToken);
        var missingOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(missingPath, missingOutput, cancellationToken) != 0
                && missingOutput.ToString().Contains("bundle.feature_matrix_missing", StringComparison.Ordinal),
            "A passing feature-surface gate without its matrix document was not rejected.");

        async Task RequireContractMutationAsync(
            string name,
            ArenaQaEvidenceContract contract,
            string issueCode,
            string message)
        {
            var path = Path.Combine(evidenceRoot, $"{name}-feature-surface-matrix.json");
            await WriteContractAsync(path, contract, cancellationToken);
            var output = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(path, output, cancellationToken) != 0
                    && output.ToString().Contains(issueCode, StringComparison.Ordinal),
                $"{message}: {output.ToString().Trim()}");
        }

        var migrationGateIndex = fixture.Contract.Gates.IndexOf(fixture.Contract.Gates.Single(gate =>
            gate.Id == ArenaQaSealManifestV2.ExplicitMigrationGateId));
        var migrationSchemaIndex = fixture.Contract.SchemaChecks.IndexOf(fixture.Contract.SchemaChecks.Single(check =>
            check.Schema == ArenaContractSchemas.ScenarioPack));
        var migrationArtifactIndex = fixture.Contract.Artifacts.IndexOf(fixture.Contract.Artifacts.Single(artifact =>
            artifact.Id == ArenaQaSealManifestV2.ExplicitMigrationArtifactId));
        await RequireContractMutationAsync(
            "migration-gate-deleted",
            fixture.Contract with
            {
                Gates = fixture.Contract.Gates.RemoveAt(migrationGateIndex)
            },
            "bundle.v2_migration_gate",
            "A V2 feature matrix without the explicit v0 migration gate was accepted");
        await RequireContractMutationAsync(
            "migration-gate-not-required",
            fixture.Contract with
            {
                Gates = fixture.Contract.Gates.SetItem(
                    migrationGateIndex,
                    fixture.Contract.Gates[migrationGateIndex] with { Required = false })
            },
            "bundle.v2_migration_gate",
            "A V2 feature matrix whose migration gate was not required was accepted");
        await RequireContractMutationAsync(
            "migration-gate-outcome",
            fixture.Contract with
            {
                Gates = fixture.Contract.Gates.SetItem(
                    migrationGateIndex,
                    fixture.Contract.Gates[migrationGateIndex] with { Outcome = ArenaQaGateOutcome.Partial })
            },
            "bundle.v2_migration_gate",
            "A V2 feature matrix whose migration gate did not pass was accepted");
        await RequireContractMutationAsync(
            "migration-schema-source",
            fixture.Contract with
            {
                SchemaChecks = fixture.Contract.SchemaChecks.SetItem(
                    migrationSchemaIndex,
                    fixture.Contract.SchemaChecks[migrationSchemaIndex] with
                    {
                        MigratedFromSchema = ArenaContractSchemas.BenchmarkPack
                    })
            },
            "bundle.v2_migration_schema",
            "A V2 feature matrix with a substituted migration source schema was accepted");
        await RequireContractMutationAsync(
            "migration-artifact-kind",
            fixture.Contract with
            {
                Artifacts = fixture.Contract.Artifacts.SetItem(
                    migrationArtifactIndex,
                    fixture.Contract.Artifacts[migrationArtifactIndex] with { Kind = "generic-log" })
            },
            "bundle.v2_migration_artifact",
            "A V2 feature matrix with a substituted migration artifact kind was accepted");

        async Task RequireMutationAsync(string name, string json, string issueCode, string message)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await File.WriteAllBytesAsync(fixture.MatrixPath, bytes, cancellationToken);
            var contract = ReplaceFeatureMatrixArtifact(
                fixture.Contract,
                fixture.MatrixArtifact with { Sha256 = Sha256(bytes) });
            var path = Path.Combine(evidenceRoot, $"{name}-feature-surface-matrix.json");
            await WriteContractAsync(path, contract, cancellationToken);
            var output = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(path, output, cancellationToken) != 0
                    && output.ToString().Contains(issueCode, StringComparison.Ordinal),
                $"{message}: {output.ToString().Trim()}");
        }

        var featureAutomation = fixture.Contract.Artifacts.First(artifact =>
            artifact.Kind == "automation-tree"
            && artifact.Id.StartsWith("artifact.p01.feature.matrix.", StringComparison.Ordinal));
        var featureAutomationPath = Path.Combine(
            evidenceRoot,
            featureAutomation.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalAutomationBytes = await File.ReadAllBytesAsync(featureAutomationPath, cancellationToken);
        async Task RequireAutomationMutationAsync(
            string name,
            Action<JsonObject> mutation,
            string issueCode,
            string message)
        {
            var root = JsonNode.Parse(originalAutomationBytes)?.AsObject()
                ?? throw new InvalidOperationException("Feature automation fixture was unavailable.");
            mutation(root);
            var mutatedAutomationBytes = JsonSerializer.SerializeToUtf8Bytes(root);
            var mutatedAutomationHash = Sha256(mutatedAutomationBytes);
            var mutatedMatrixJson = ReplaceFirst(
                Encoding.UTF8.GetString(fixture.MatrixBytes),
                $"\"automationSha256\":\"{featureAutomation.Sha256}\"",
                $"\"automationSha256\":\"{mutatedAutomationHash}\"");
            var mutatedMatrixBytes = Encoding.UTF8.GetBytes(mutatedMatrixJson);
            await File.WriteAllBytesAsync(featureAutomationPath, mutatedAutomationBytes, cancellationToken);
            await File.WriteAllBytesAsync(fixture.MatrixPath, mutatedMatrixBytes, cancellationToken);
            var contract = fixture.Contract with
            {
                Artifacts = [.. fixture.Contract.Artifacts.Select(artifact =>
                    artifact.Id == featureAutomation.Id
                        ? artifact with { Sha256 = mutatedAutomationHash }
                        : artifact.Kind == ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind
                            ? artifact with { Sha256 = Sha256(mutatedMatrixBytes) }
                            : artifact)]
            };
            var path = Path.Combine(evidenceRoot, $"{name}-feature-surface-matrix.json");
            await WriteContractAsync(path, contract, cancellationToken);
            var output = new StringWriter();
            Require(await VerificationEvidenceValidator.ValidateFileAsync(path, output, cancellationToken) != 0
                    && output.ToString().Contains(issueCode, StringComparison.Ordinal),
                $"{message}: {output.ToString().Trim()}");
            await File.WriteAllBytesAsync(featureAutomationPath, originalAutomationBytes, cancellationToken);
            await File.WriteAllBytesAsync(fixture.MatrixPath, fixture.MatrixBytes, cancellationToken);
        }

        var matrixJson = Encoding.UTF8.GetString(fixture.MatrixBytes);
        await RequireMutationAsync(
            "refresh-failed",
            ReplaceFirst(matrixJson, "\"selectedFeatureStatus\":\"ready\"", "\"selectedFeatureStatus\":\"refresh-failed\""),
            "bundle.feature_matrix_cross_product",
            "A feature refresh failure was accepted as ready evidence");
        await RequireMutationAsync(
            "registry-tamper",
            ReplaceFirst(matrixJson, "\"registeredFeatureKeys\":[\"matrix\"", "\"registeredFeatureKeys\":[\"not-registered\""),
            "bundle.feature_matrix_schema",
            "A matrix with a substituted registered feature key was accepted");
        await RequireMutationAsync(
            "visible-root-tamper",
            ReplaceFirst(matrixJson, "\"visibleRootIdentity\":\"ExperimentLabPanel\"", "\"visibleRootIdentity\":\"TranscriptPanel\""),
            "bundle.feature_matrix_cross_product",
            "A matrix cell bound to the wrong visible root was accepted");
        await RequireMutationAsync(
            "content-identity-tamper",
            ReplaceFirst(matrixJson, "\"requiredContentIdentity\":\"MemoryFeatureRoot\"", "\"requiredContentIdentity\":\"MatrixPanel\""),
            "bundle.feature_matrix_cross_product",
            "A matrix cell bound to another feature's content identity was accepted");
        await RequireMutationAsync(
            "focus-tamper",
            ReplaceFirst(matrixJson, "\"afterIdentity\":\"QaFocusSecond\"", "\"afterIdentity\":\"QaFocusThird\""),
            "bundle.feature_matrix_focus",
            "A feature-surface focus cycle that did not round-trip was accepted");
        await RequireMutationAsync(
            "hash-tamper",
            ReplaceFirst(
                matrixJson,
                $"\"automationSha256\":\"{fixture.FirstAutomationHash}\"",
                $"\"automationSha256\":\"{new string('f', 64)}\""),
            "bundle.feature_matrix_provenance",
            "A matrix cell carrying a false automation hash was accepted");
        await RequireMutationAsync(
            "state-tamper",
            ReplaceFirst(
                matrixJson,
                "experiment-lab.feature-agent-memory-debugger.closed.",
                "experiment-lab.feature-matrix.closed."),
            "bundle.feature_matrix_cross_product",
            "A matrix cell whose canonical state named another feature was accepted");
        await RequireMutationAsync(
            "corrupt",
            "{",
            "bundle.feature_matrix_schema",
            "Corrupt feature-surface matrix JSON was accepted");
        await RequireAutomationMutationAsync(
            "missing-content",
            root => FeatureNode(root, "MatrixPanel")["Identity"] = "MissingPanel",
            "bundle.feature_matrix_observed_state",
            "A selected feature whose required content identity was absent from the rendered tree was accepted");
        await RequireAutomationMutationAsync(
            "extra-feature-root",
            root =>
            {
                var nodes = root["Nodes"]!.AsArray();
                nodes.Add(new JsonObject
                {
                    ["Sequence"] = nodes.Count,
                    ["ParentSequence"] = 1,
                    ["VisualDepth"] = 2,
                    ["Identity"] = "ForkPanel",
                    ["AutomationId"] = "ForkPanel",
                    ["AutomationIdRedacted"] = false,
                    ["FrameworkType"] = "Grid",
                    ["ControlType"] = "Custom",
                    ["IsVisible"] = true,
                    ["IsEnabled"] = true,
                    ["IsFocusable"] = false,
                    ["HasKeyboardFocus"] = false,
                    ["BoundsX"] = 300,
                    ["BoundsY"] = 120,
                    ["BoundsWidth"] = 180,
                    ["BoundsHeight"] = 120,
                    ["EffectiveOpacity"] = 1,
                    ["IntersectsViewport"] = true,
                    ["IsRendered"] = true
                });
                root["NodeCount"] = nodes.Count;
            },
            "bundle.feature_matrix_observed_state",
            "A tree rendering two registered feature roots was accepted");
        await RequireAutomationMutationAsync(
            "zero-size-feature-root",
            root =>
            {
                var node = FeatureNode(root, "MatrixPanel");
                node["BoundsWidth"] = 0;
                node["IntersectsViewport"] = false;
                node["IsRendered"] = false;
            },
            "bundle.feature_matrix_observed_state",
            "A zero-size feature root was accepted as rendered");
        await RequireAutomationMutationAsync(
            "insufficient-feature-comparison-region",
            root =>
            {
                var node = FeatureNode(root, "MatrixPanel");
                node["BoundsWidth"] = 100;
                node["BoundsHeight"] = 100;
            },
            "bundle.feature_matrix_feature_render",
            "A feature root covering less than the bounded comparison area was accepted");
        await RequireAutomationMutationAsync(
            "transparent-feature-root",
            root =>
            {
                var node = FeatureNode(root, "MatrixPanel");
                node["EffectiveOpacity"] = 0;
                node["IsRendered"] = false;
            },
            "bundle.feature_matrix_observed_state",
            "A fully transparent feature root was accepted as rendered");
        await RequireAutomationMutationAsync(
            "relocated-feature-root",
            root =>
            {
                var node = FeatureNode(root, "MatrixPanel");
                node["ParentSequence"] = null;
                node["VisualDepth"] = 0;
            },
            "bundle.feature_matrix_observed_state",
            "A rendered feature root outside ExperimentLabPanel was accepted");
        await RequireAutomationMutationAsync(
            "outside-feature-focus",
            root =>
            {
                FeatureNode(root, "QaFocusFirst")["ParentSequence"] = 0;
                FeatureNode(root, "QaFocusSecond")["ParentSequence"] = 0;
            },
            "bundle.feature_matrix_focus",
            "A shell-level focus cycle outside the selected feature was accepted");

        var firstFeatureScreenshot = fixture.Contract.Artifacts.First(artifact =>
            artifact.Id == "artifact.p01.feature.matrix.dark-blue.w960.screenshot");
        var secondFeatureScreenshot = fixture.Contract.Artifacts.First(artifact =>
            artifact.Id == "artifact.p01.feature.fork.dark-blue.w960.screenshot");
        var firstFeatureScreenshotPath = Path.Combine(evidenceRoot, firstFeatureScreenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var secondFeatureScreenshotPath = Path.Combine(evidenceRoot, secondFeatureScreenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalSecondScreenshotBytes = await File.ReadAllBytesAsync(secondFeatureScreenshotPath, cancellationToken);
        var identicalScreenshotBytes = await File.ReadAllBytesAsync(firstFeatureScreenshotPath, cancellationToken);
        var matrixFeatureIndex = ArenaQaSealManifestV2.RequiredExperimentFeatureKeys.IndexOf("matrix");
        var matrixDarkBlueVariant = checked(matrixFeatureIndex * 3 + 1);
        Require(Sha256(identicalScreenshotBytes).Equals(firstFeatureScreenshot.Sha256, StringComparison.OrdinalIgnoreCase),
            "The base feature screenshot fixture did not match its contract hash.");

        async Task RequireFeatureScreenshotMutationAsync(
            string name,
            byte[] replacementBytes,
            bool expectedValid,
            string message)
        {
            var replacementHash = Sha256(replacementBytes);
            var replacementMatrixJson = ReplaceFirst(
                matrixJson,
                $"\"screenshotSha256\":\"{secondFeatureScreenshot.Sha256}\"",
                $"\"screenshotSha256\":\"{replacementHash}\"");
            var replacementMatrixBytes = Encoding.UTF8.GetBytes(replacementMatrixJson);
            await File.WriteAllBytesAsync(secondFeatureScreenshotPath, replacementBytes, cancellationToken);
            await File.WriteAllBytesAsync(fixture.MatrixPath, replacementMatrixBytes, cancellationToken);
            var replacementContract = fixture.Contract with
            {
                Artifacts = [.. fixture.Contract.Artifacts.Select(artifact =>
                artifact.Id == secondFeatureScreenshot.Id
                    ? artifact with { Sha256 = replacementHash }
                    : artifact.Kind == ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind
                        ? artifact with { Sha256 = Sha256(replacementMatrixBytes) }
                        : artifact)]
            };
            var path = Path.Combine(evidenceRoot, $"{name}-feature-surface-matrix.json");
            await WriteContractAsync(path, replacementContract, cancellationToken);
            var output = new StringWriter();
            var exitCode = await VerificationEvidenceValidator.ValidateFileAsync(path, output, cancellationToken);
            Require(expectedValid
                    ? exitCode == 0
                    : exitCode != 0
                      && output.ToString().Contains("bundle.feature_matrix_feature_render", StringComparison.Ordinal),
                $"{message}: {output.ToString().Trim()}");
            await File.WriteAllBytesAsync(secondFeatureScreenshotPath, originalSecondScreenshotBytes, cancellationToken);
            await File.WriteAllBytesAsync(fixture.MatrixPath, fixture.MatrixBytes, cancellationToken);
        }

        await RequireFeatureScreenshotMutationAsync(
            "same-pixels",
            identicalScreenshotBytes,
            expectedValid: false,
            "Two features with identical PNG bytes at the same theme and width were accepted");
        await RequireFeatureScreenshotMutationAsync(
            "same-decoded-pixels",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, splitImageData: true),
            expectedValid: false,
            "Two features with identical decoded pixels in different PNG containers were accepted");
        await RequireFeatureScreenshotMutationAsync(
            "one-pixel-feature-delta",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, singlePixelThemeDelta: true),
            expectedValid: false,
            "A one-pixel feature delta was accepted as a materially different feature surface");
        await RequireFeatureScreenshotMutationAsync(
            "targeted-grid-feature-delta",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, targetedSamplePointThemeDelta: true),
            expectedValid: false,
            "Sparse changes aimed at the pooled grid were accepted as a materially different feature surface");
        await RequireFeatureScreenshotMutationAsync(
            "shell-only-feature-delta",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, shellOnlyFeatureDelta: true),
            expectedValid: false,
            "A shell/status-only change outside identical feature roots was accepted as feature evidence");
        await RequireFeatureScreenshotMutationAsync(
            "outside-root-boundary-strip",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, outsideFeatureBoundaryDelta: true),
            expectedValid: false,
            "A high-contrast strip immediately outside the feature root bled into pooled feature evidence");
        await RequireFeatureScreenshotMutationAsync(
            "subthreshold-feature-delta",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, featureRootSubthresholdDelta: true),
            expectedValid: false,
            "A root-wide RGB delta below the per-cell material threshold was accepted");
        await RequireFeatureScreenshotMutationAsync(
            "material-root-feature-delta",
            BuildPngArtifact(960, 640, variant: matrixDarkBlueVariant, featureRootMaterialDelta: true),
            expectedValid: true,
            "A material feature-root change with identical shell pixels was rejected");

        var matrixLightScreenshot = fixture.Contract.Artifacts.First(artifact =>
            artifact.Id == "artifact.p01.feature.matrix.light.w960.screenshot");
        var matrixLightScreenshotPath = Path.Combine(
            evidenceRoot,
            matrixLightScreenshot.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var originalMatrixLightBytes = await File.ReadAllBytesAsync(matrixLightScreenshotPath, cancellationToken);
        var shellOnlyThemeBytes = BuildPngArtifact(
            960,
            640,
            variant: matrixDarkBlueVariant,
            shellOnlyFeatureDelta: true);
        var shellOnlyThemeHash = Sha256(shellOnlyThemeBytes);
        var shellOnlyThemeMatrixJson = ReplaceFirst(
            matrixJson,
            $"\"screenshotSha256\":\"{matrixLightScreenshot.Sha256}\"",
            $"\"screenshotSha256\":\"{shellOnlyThemeHash}\"");
        var shellOnlyThemeMatrixBytes = Encoding.UTF8.GetBytes(shellOnlyThemeMatrixJson);
        await File.WriteAllBytesAsync(matrixLightScreenshotPath, shellOnlyThemeBytes, cancellationToken);
        await File.WriteAllBytesAsync(fixture.MatrixPath, shellOnlyThemeMatrixBytes, cancellationToken);
        var shellOnlyThemeContract = fixture.Contract with
        {
            Artifacts = [.. fixture.Contract.Artifacts.Select(artifact =>
                artifact.Id == matrixLightScreenshot.Id
                    ? artifact with { Sha256 = shellOnlyThemeHash }
                    : artifact.Kind == ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind
                        ? artifact with { Sha256 = Sha256(shellOnlyThemeMatrixBytes) }
                        : artifact)]
        };
        var shellOnlyThemePath = Path.Combine(evidenceRoot, "shell-only-feature-theme-render.json");
        await WriteContractAsync(shellOnlyThemePath, shellOnlyThemeContract, cancellationToken);
        var shellOnlyThemeOutput = new StringWriter();
        Require(await VerificationEvidenceValidator.ValidateFileAsync(
                    shellOnlyThemePath,
                    shellOnlyThemeOutput,
                    cancellationToken) != 0
                && shellOnlyThemeOutput.ToString().Contains("bundle.feature_matrix_theme_render", StringComparison.Ordinal),
            $"Shell/status colour changes outside identical feature-root pixels were accepted as cross-theme feature evidence: {shellOnlyThemeOutput.ToString().Trim()}");
        await File.WriteAllBytesAsync(matrixLightScreenshotPath, originalMatrixLightBytes, cancellationToken);
        await File.WriteAllBytesAsync(secondFeatureScreenshotPath, originalSecondScreenshotBytes, cancellationToken);
        await File.WriteAllBytesAsync(fixture.MatrixPath, fixture.MatrixBytes, cancellationToken);
    }

    private static JsonObject FeatureNode(JsonObject root, string identity) =>
        root["Nodes"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals((string?)node["Identity"], identity, StringComparison.Ordinal));

    private static async Task<FeatureSurfaceMatrixFixture> BuildFeatureSurfaceMatrixFixtureAsync(
        string evidenceRoot,
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
        bool enhancedAutomation,
        CancellationToken cancellationToken)
    {
        var uiMatrix = await BuildUiMatrixFixtureAsync(
            evidenceRoot,
            outer,
            map,
            combinedFingerprint,
            capturedAt,
            enhancedAutomation,
            cancellationToken);
        var artifacts = uiMatrix.Contract.Artifacts.ToList();
        var cells = new List<object>();
        var firstFeatureAutomationHash = "";
        foreach (var featureKey in ArenaQaSealManifestV2.RequiredExperimentFeatureKeys)
        foreach (var theme in new[] { "dark-blue", "light", "high-contrast" })
        foreach (var viewport in new[] { (Width: 960, Height: 640), (Width: 1500, Height: 960) })
        {
            var key = $"p01.feature.{featureKey}.{theme}.w{viewport.Width}";
            var expectedState = $"experiment-lab.feature-{featureKey}.closed.{theme}.w{viewport.Width}.d1-0.normal";
            var automationId = $"artifact.{key}.automation";
            var screenshotId = $"artifact.{key}.screenshot";
            var automationRelativePath = $"automation/{key}.automation-tree.json";
            var screenshotRelativePath = $"screenshots/{key}.rendered-ui.png";
            var automationPath = Path.Combine(evidenceRoot, automationRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var screenshotPath = Path.Combine(evidenceRoot, screenshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(automationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);

            var automationBytes = BuildAutomationArtifact(
                combinedFingerprint,
                capturedAt,
                expectedState,
                theme,
                viewport.Width,
                viewport.Height,
                1m,
                "qa-normal",
                true,
                "QaFocusSecond",
                enhancedAutomation: true);
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);
            var featureIndex = ArenaQaSealManifestV2.RequiredExperimentFeatureKeys.IndexOf(featureKey);
            var themeIndex = theme == "dark-blue" ? 1 : theme == "light" ? 2 : 3;
            var screenshotBytes = BuildPngArtifact(
                viewport.Width,
                viewport.Height,
                variant: checked(featureIndex * 3 + themeIndex));
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            var automationHash = Sha256(automationBytes);
            if (firstFeatureAutomationHash.Length == 0) firstFeatureAutomationHash = automationHash;
            var screenshotHash = Sha256(screenshotBytes);
            var provenance = new ArenaQaArtifactProvenance(
                combinedFingerprint,
                capturedAt,
                theme,
                viewport.Width,
                viewport.Height,
                1m,
                expectedState,
                null,
                null);
            artifacts.Add(new(automationId, "automation-tree", automationRelativePath, automationHash, provenance));
            artifacts.Add(new(screenshotId, "rendered-ui-screenshot", screenshotRelativePath, screenshotHash,
                provenance with { LinkedAutomationArtifactId = automationId }));
            cells.Add(new
            {
                key,
                featureKey,
                selectedFeatureStatus = "ready",
                controlPlaneBusy = false,
                theme,
                viewportWidthDip = viewport.Width,
                viewportHeightDip = viewport.Height,
                renderDpiScale = 1m,
                motionMode = "normal",
                motionPreferenceSource = "qa-normal",
                animationsEnabled = true,
                expectedState,
                visibleRootIdentity = "ExperimentLabPanel",
                requiredContentIdentity = ArenaQaSealManifestV2.RequiredExperimentFeatureAutomationIdentities[featureKey],
                focusNext = MatrixFocus("next", "QaFocusFirst", "QaFocusSecond"),
                focusPrevious = MatrixFocus("previous", "QaFocusSecond", "QaFocusFirst"),
                focusCapture = MatrixFocus("next", "QaFocusFirst", "QaFocusSecond"),
                automationArtifactId = automationId,
                automationSha256 = automationHash,
                screenshotArtifactId = screenshotId,
                screenshotSha256 = screenshotHash
            });
        }

        var orderedCells = cells
            .OrderBy(cell => (string)cell.GetType().GetProperty("key")!.GetValue(cell)!, StringComparer.Ordinal)
            .ToArray();
        var matrixBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = ArenaQaSealManifestV2.FeatureSurfaceMatrixSchema,
            passNumber = 1,
            treeFingerprint = combinedFingerprint,
            registeredFeatureKeys = ArenaQaSealManifestV2.RequiredExperimentFeatureKeys,
            cellCount = orderedCells.Length,
            cells = orderedCells
        });
        var matrixRelativePath = "metadata/pass-01.feature-surface-matrix.json";
        var matrixPath = Path.Combine(evidenceRoot, matrixRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(matrixPath, matrixBytes, cancellationToken);
        var matrixArtifact = new ArenaQaArtifact(
            "artifact.pass-01.feature-surface-matrix",
            ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind,
            matrixRelativePath,
            Sha256(matrixBytes),
            null);
        artifacts.Add(matrixArtifact);

        var migrationRelativePath = ArenaQaSealManifestV2.ExplicitMigrationArtifactPath;
        var migrationPath = Path.Combine(evidenceRoot, migrationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(migrationPath)!);
        var migrationBytes = Encoding.UTF8.GetBytes("AI Arena QA gate evidence\ngate=schema.explicit-v0-pack-migration\noutcome=pass\n");
        await File.WriteAllBytesAsync(migrationPath, migrationBytes, cancellationToken);
        artifacts.Add(new ArenaQaArtifact(
            ArenaQaSealManifestV2.ExplicitMigrationArtifactId,
            ArenaQaSealManifestV2.ExplicitMigrationArtifactKind,
            migrationRelativePath,
            Sha256(migrationBytes),
            null));

        var firstScreenshot = artifacts.First(artifact => artifact.Kind == "rendered-ui-screenshot");
        var observed = new ArenaEvidenceAssertion(
            "evidence:feature-surface-matrix",
            ArenaEvidenceState.Observed,
            "Recorded by deterministic local feature-surface QA.",
            firstScreenshot.Id);
        var unavailable = new ArenaEvidenceAssertion(
            "evidence:feature-surface-unavailable",
            ArenaEvidenceState.Unavailable,
            "External provider evidence is not required for this fixture.",
            Limitation: "No external provider is required.");
        var gates = uiMatrix.Contract.Gates
            .Append(new ArenaQaGateEvidence(
                "ui.feature-surface-matrix",
                ArenaQaGateOutcome.Pass,
                true,
                1,
                new(1, 0, 0, 1),
                observed))
            .Append(new ArenaQaGateEvidence(
                ArenaQaSealManifestV2.ExplicitMigrationGateId,
                ArenaQaGateOutcome.Pass,
                true,
                1,
                new(1, 0, 0, 1),
                observed with { ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId }))
            .ToImmutableArray();
        var contract = new ArenaQaEvidenceContract(
            ArenaContractSchemas.QaEvidence,
            "qa:feature-surface-validator",
            capturedAt,
            outer.SourceRevision,
            combinedFingerprint,
            ArenaQaSealManifestV2.Id,
            true,
            [new("map", map.SourceRevision, map.TreeFingerprint, true)],
            capturedAt.AddMinutes(-1),
            capturedAt.AddMinutes(1),
            ArenaQaVerdict.Partial,
            1,
            new("Windows", "x64", "10.0.0", "10.0.100", "Release", true),
            [new("dotnet", "10.0.100"), new("powershell", "7.5.2")],
            gates,
            [.. artifacts],
            [new("performance:feature-surface", "validator-duration", 1m, "milliseconds", ArenaQaThresholdKind.Maximum, 10m, observed)],
            [
                new("schema:feature-surface", ArenaContractSchemas.QaEvidence, null, ArenaQaGateOutcome.Pass, observed),
                new(
                    "schema:scenario-migration",
                    ArenaContractSchemas.ScenarioPack,
                    ArenaQaSealManifestV2.ScenarioPackV0Schema,
                    ArenaQaGateOutcome.Pass,
                    observed with
                    {
                        Id = ArenaQaSealManifestV2.ScenarioMigrationEvidenceId,
                        ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId
                    }),
                new(
                    "schema:benchmark-migration",
                    ArenaContractSchemas.BenchmarkPack,
                    ArenaQaSealManifestV2.BenchmarkPackV0Schema,
                    ArenaQaGateOutcome.Pass,
                    observed with
                    {
                        Id = ArenaQaSealManifestV2.BenchmarkMigrationEvidenceId,
                        ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId
                    })
            ],
            new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider is required."),
            RequiredQaLimitations(),
            new(false, null, null, [], [], unavailable),
            [observed]);
        return new(contract, matrixArtifact, matrixPath, matrixBytes, firstFeatureAutomationHash);
    }

    private static async Task<UiMatrixFixture> BuildUiMatrixFixtureAsync(
        string evidenceRoot,
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
        bool enhancedAutomation,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<ArenaQaArtifact>();
        var cells = new List<object>();
        var pngCache = new Dictionary<(int Width, int Height, string Theme), byte[]>();
        foreach (var theme in new[] { "dark-blue", "light", "high-contrast" })
        foreach (var viewport in new[] { (Width: 960, Height: 640), (Width: 1500, Height: 960) })
        foreach (var renderDpi in new[] { (Value: 1m, Label: "1-0"), (Value: 1.5m, Label: "1-5"), (Value: 2m, Label: "2-0") })
        foreach (var motion in new[] { "normal", "reduced" })
        {
            var key = $"p01.{theme}.w{viewport.Width}.d{renderDpi.Label}.{motion}";
            var expectedState = $"arena-empty.closed.{theme}.w{viewport.Width}.d{renderDpi.Label}.{motion}";
            var motionSource = motion == "normal" ? "qa-normal" : "qa-reduced";
            var animationsEnabled = motion == "normal";
            var automationId = $"artifact.{key}.automation";
            var screenshotId = $"artifact.{key}.screenshot";
            var automationRelativePath = $"automation/{key}.json";
            var screenshotRelativePath = $"screenshots/{key}.png";
            var automationPath = Path.Combine(evidenceRoot, automationRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var screenshotPath = Path.Combine(evidenceRoot, screenshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(automationPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);

            var automationBytes = BuildAutomationArtifact(
                combinedFingerprint,
                capturedAt,
                expectedState,
                theme,
                viewport.Width,
                viewport.Height,
                renderDpi.Value,
                motionSource,
                animationsEnabled,
                "QaFocusSecond",
                enhancedAutomation);
            await File.WriteAllBytesAsync(automationPath, automationBytes, cancellationToken);
            var physicalSize = (
                Width: (int)Math.Ceiling(viewport.Width * renderDpi.Value),
                Height: (int)Math.Ceiling(viewport.Height * renderDpi.Value));
            var pngKey = (physicalSize.Width, physicalSize.Height, theme);
            if (!pngCache.TryGetValue(pngKey, out var screenshotBytes))
            {
                var variant = theme == "dark-blue" ? 1 : theme == "light" ? 2 : 3;
                screenshotBytes = BuildPngArtifact(physicalSize.Width, physicalSize.Height, variant: variant);
                pngCache.Add(pngKey, screenshotBytes);
            }
            await File.WriteAllBytesAsync(screenshotPath, screenshotBytes, cancellationToken);

            var provenance = new ArenaQaArtifactProvenance(
                combinedFingerprint,
                capturedAt,
                theme,
                viewport.Width,
                viewport.Height,
                renderDpi.Value,
                expectedState,
                null,
                null);
            artifacts.Add(new(automationId, "automation-tree", automationRelativePath, Sha256(automationBytes), provenance));
            artifacts.Add(new(screenshotId, "rendered-ui-screenshot", screenshotRelativePath, Sha256(screenshotBytes),
                provenance with { LinkedAutomationArtifactId = automationId }));

            cells.Add(new
            {
                key,
                theme,
                viewportWidthDip = viewport.Width,
                viewportHeightDip = viewport.Height,
                renderDpiScale = renderDpi.Value,
                motionMode = motion,
                motionPreferenceSource = motionSource,
                animationsEnabled,
                focusNext = MatrixFocus("next", "QaFocusFirst", "QaFocusSecond"),
                focusPrevious = MatrixFocus("previous", "QaFocusSecond", "QaFocusFirst"),
                focusCapture = MatrixFocus("next", "QaFocusFirst", "QaFocusSecond"),
                automationArtifactId = automationId,
                screenshotArtifactId = screenshotId
            });
        }

        var orderedCells = cells
            .OrderBy(cell => (string)cell.GetType().GetProperty("key")!.GetValue(cell)!, StringComparer.Ordinal)
            .ToArray();
        var matrixBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "ai_arena.qa_ui_matrix.v1",
            passNumber = 1,
            treeFingerprint = combinedFingerprint,
            cellCount = orderedCells.Length,
            cells = orderedCells
        });
        var matrixRelativePath = "metadata/pass-01.ui-matrix.json";
        var matrixPath = Path.Combine(evidenceRoot, matrixRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(matrixPath)!);
        await File.WriteAllBytesAsync(matrixPath, matrixBytes, cancellationToken);
        var matrixArtifact = new ArenaQaArtifact(
            "artifact.pass-01.ui-matrix",
            "qa-ui-matrix",
            matrixRelativePath,
            Sha256(matrixBytes),
            null);
        artifacts.Add(matrixArtifact);

        var firstScreenshotId = artifacts.First(artifact => artifact.Kind == "rendered-ui-screenshot").Id;
        var observed = new ArenaEvidenceAssertion(
            "evidence:ui-matrix",
            ArenaEvidenceState.Observed,
            "Recorded by deterministic local UI matrix QA.",
            firstScreenshotId);
        var unavailable = new ArenaEvidenceAssertion(
            "evidence:ui-matrix-unavailable",
            ArenaEvidenceState.Unavailable,
            "External provider evidence is not required for this fixture.",
            Limitation: "No external provider is required.");
        var matrixGates = new[]
        {
            "pass-01.rendered-ui",
            "ui.keyboard-automation-matrix",
            "ui.reduced-motion-matrix",
            "ui.theme-contrast-matrix",
            "ui.viewport-dpi-matrix"
        };
        var contract = new ArenaQaEvidenceContract(
            ArenaContractSchemas.QaEvidence,
            "qa:ui-matrix-validator",
            capturedAt,
            outer.SourceRevision,
            combinedFingerprint,
            ArenaQaSealManifestV1.Id,
            true,
            [new("map", map.SourceRevision, map.TreeFingerprint, true)],
            capturedAt.AddMinutes(-1),
            capturedAt.AddMinutes(1),
            ArenaQaVerdict.Partial,
            1,
            new("Windows", "x64", "10.0.0", "10.0.100", "Release", true),
            [new("dotnet", "10.0.100"), new("powershell", "7.5.2")],
            [.. matrixGates.Select(id => new ArenaQaGateEvidence(
                id,
                ArenaQaGateOutcome.Pass,
                true,
                1,
                new(1, 0, 0, 1),
                observed))],
            [.. artifacts],
            [new("performance:ui-matrix", "validator-duration", 1m, "milliseconds", ArenaQaThresholdKind.Maximum, 10m, observed)],
            [new("schema:qa-ui-matrix", ArenaContractSchemas.QaEvidence, null, ArenaQaGateOutcome.Pass, observed)],
            new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider is required."),
            RequiredQaLimitations(),
            new(false, null, null, [], [], unavailable),
            [observed]);
        return new(contract, matrixArtifact, matrixPath, matrixBytes);
    }

    private static object MatrixFocus(string direction, string beforeIdentity, string afterIdentity) => new
    {
        direction,
        beforeIdentity,
        beforeControlType = "Button",
        afterIdentity,
        afterControlType = "Button",
        moved = true,
        focusChanged = true
    };

    private static ArenaQaEvidenceContract ReplaceMatrixArtifact(
        ArenaQaEvidenceContract contract,
        ArenaQaArtifact replacement) =>
        contract with
        {
            Artifacts = [.. contract.Artifacts.Select(artifact => artifact.Kind == "qa-ui-matrix" ? replacement : artifact)]
        };

    private static ArenaQaEvidenceContract ReplaceFeatureMatrixArtifact(
        ArenaQaEvidenceContract contract,
        ArenaQaArtifact replacement) =>
        contract with
        {
            Artifacts = [.. contract.Artifacts.Select(artifact =>
                artifact.Kind == ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind ? replacement : artifact)]
        };

    private static Task WriteContractAsync(
        string path,
        ArenaQaEvidenceContract contract,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, ArenaContractCodec.Serialize(contract), new UTF8Encoding(false), cancellationToken);

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("Matrix fixture mutation target was unavailable.");
        return string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
    }

    private static ArenaQaEvidenceContract BuildContract(
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
        byte[] automationBytes,
        byte[] screenshotBytes)
    {
        var automationId = "artifact:automation";
        var screenshotId = "artifact:screenshot";
        var observed = new ArenaEvidenceAssertion(
            "evidence:observed",
            ArenaEvidenceState.Observed,
            "Recorded by deterministic local QA.",
            screenshotId);
        var unavailable = new ArenaEvidenceAssertion(
            "evidence:unavailable",
            ArenaEvidenceState.Unavailable,
            "Evidence is not required for this validator fixture.",
            Limitation: "No external provider is required.");
        var provenance = new ArenaQaArtifactProvenance(
            combinedFingerprint,
            capturedAt,
            "dark-blue",
            960,
            640,
            1m,
            ExpectedState,
            null,
            null);
        return new ArenaQaEvidenceContract(
            ArenaContractSchemas.QaEvidence,
            "qa:validator",
            capturedAt,
            outer.SourceRevision,
            combinedFingerprint,
            ArenaQaSealManifestV1.Id,
            true,
            [new("map", map.SourceRevision, map.TreeFingerprint, true)],
            capturedAt.AddMinutes(-1),
            capturedAt.AddMinutes(1),
            ArenaQaVerdict.Partial,
            1,
            new("Windows", "x64", "10.0.0", "10.0.100", "Release", true),
            [new("dotnet", "10.0.100"), new("powershell", "7.5.2")],
            [new("gate:validator", ArenaQaGateOutcome.Pass, true, 1, new(1, 0, 0, 1), observed)],
            [new(automationId, "automation-tree", "automation/tree.json", Sha256(automationBytes), provenance),
                new(screenshotId, "rendered-ui-screenshot", "screenshots/view.png", Sha256(screenshotBytes),
                    provenance with { LinkedAutomationArtifactId = automationId })],
            [new("performance:validator", "validator-duration", 1m, "milliseconds", ArenaQaThresholdKind.Maximum, 10m, observed)],
            [new("schema:qa", ArenaContractSchemas.QaEvidence, null, ArenaQaGateOutcome.Pass, observed)],
            new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider is required."),
            RequiredQaLimitations(),
            new(false, null, null, [], [], unavailable),
            [observed]);
    }

    private static ImmutableArray<ArenaQaAcceptedLimitation> RequiredQaLimitations() =>
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

    private static byte[] BuildAutomationArtifact(string treeFingerprint, DateTimeOffset capturedAt) =>
        BuildAutomationArtifactCore(
            treeFingerprint,
            capturedAt,
            ExpectedState,
            "dark-blue",
            960,
            640,
            1m,
            false,
            "system",
            true,
            "QaPrimaryButton",
            enhancedAutomation: false);

    private static byte[] BuildAutomationArtifact(
        string treeFingerprint,
        DateTimeOffset capturedAt,
        string expectedState,
        string theme,
        int viewportWidthDip,
        int viewportHeightDip,
        decimal renderDpiScale,
        string motionPreferenceSource,
        bool animationsEnabled,
        string focusIdentity,
        bool enhancedAutomation) =>
        BuildAutomationArtifactCore(
            treeFingerprint,
            capturedAt,
            expectedState,
            theme,
            viewportWidthDip,
            viewportHeightDip,
            renderDpiScale,
            true,
            motionPreferenceSource,
            animationsEnabled,
            focusIdentity,
            enhancedAutomation);

    private static byte[] BuildAutomationArtifactCore(
        string treeFingerprint,
        DateTimeOffset capturedAt,
        string expectedState,
        string theme,
        int viewportWidthDip,
        int viewportHeightDip,
        decimal renderDpiScale,
        bool renderDpiOverride,
        string motionPreferenceSource,
        bool animationsEnabled,
        string focusIdentity,
        bool enhancedAutomation)
    {
        var isArena = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal);
        var isFeatureSurface = expectedState.StartsWith("experiment-lab.feature-", StringComparison.Ordinal);
        var featureKey = isFeatureSurface
            ? ArenaQaSealManifestV2.RequiredExperimentFeatureKeys.First(key =>
                expectedState.StartsWith($"experiment-lab.feature-{key}.", StringComparison.Ordinal))
            : "";
        var featureContentIdentity = isFeatureSurface
            ? ArenaQaSealManifestV2.RequiredExperimentFeatureAutomationIdentities[featureKey]
            : "";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = enhancedAutomation
                ? "ai_arena.ui_structure_evidence.v2"
                : "ai_arena.ui_structure_evidence.v1",
            CapturedAtUtc = capturedAt,
            CaptureMode = "wpf-visual-tree-accessibility",
            Limitation = "OS UI Automation and OS input are not queried; focus traversal and the privacy-safe visual-tree snapshot are programmatic and in-process. RenderDpiScale is off-screen raster density, not physical or per-monitor display DPI. Motion fields prove preference plumbing, not rendered animation playback. Accessible names, help text, and all dynamic control content are omitted.",
            TreeFingerprint = treeFingerprint,
            ExpectedState = expectedState,
            ExpectedStateSource = "observed-visible-roots",
            SelectedView = isArena ? "arena" : isFeatureSurface ? "experiment-lab" : "verification-window",
            ObservedSurfaceState = isArena ? "arena-empty" : isFeatureSurface ? "experiment-lab" : "verification-window",
            DialogState = "closed",
            VisibleRootIdentities = isArena
                ? new[] { "TranscriptPanel" }
                : isFeatureSurface
                    ? new[] { "ExperimentLabPanel" }
                : new[] { "QaRoot" },
            RequiredControlIdentities = isArena
                ? new[] { "RootLayout", "ShellNavigationRail", "ShellTopBar", "TranscriptItems", "TranscriptPanel" }
                : isFeatureSurface
                    ? new[] { "ExperimentLabPanel" }
                : new[] { "QaRoot" },
            Theme = theme,
            ViewportWidthDip = viewportWidthDip,
            ViewportHeightDip = viewportHeightDip,
            DpiScale = renderDpiScale,
            RenderDpiScale = renderDpiScale,
            RenderDpiOverride = renderDpiOverride,
            ActualWidthDip = (double)viewportWidthDip,
            ActualHeightDip = (double)viewportHeightDip,
            DpiScaleX = 1d,
            DpiScaleY = 1d,
            MotionPreferenceSource = motionPreferenceSource,
            AnimationsEnabled = animationsEnabled,
            FocusIdentity = focusIdentity,
            NodeCount = isArena ? 6 : isFeatureSurface ? 4 : 2,
            Truncated = false,
            Nodes = enhancedAutomation
                ? BuildAutomationNodes(
                    expectedState,
                    focusIdentity,
                    featureContentIdentity,
                    viewportWidthDip,
                    viewportHeightDip)
                : BuildLegacyAutomationNodes(expectedState, focusIdentity, featureContentIdentity)
        });
    }

    private static object[] BuildLegacyAutomationNodes(
        string expectedState,
        string focusIdentity,
        string featureContentIdentity)
    {
        static object Node(
            int sequence,
            int? parent,
            int depth,
            string identity,
            string frameworkType,
            string controlType,
            bool focusable = false,
            bool focused = false) => new
        {
            Sequence = sequence,
            ParentSequence = parent,
            VisualDepth = depth,
            Identity = identity,
            AutomationId = identity,
            AutomationIdRedacted = false,
            FrameworkType = frameworkType,
            ControlType = controlType,
            IsVisible = true,
            IsEnabled = true,
            IsFocusable = focusable,
            HasKeyboardFocus = focused
        };

        return expectedState.StartsWith("arena-empty.", StringComparison.Ordinal)
            ?
            [
                Node(0, null, 0, "RootLayout", "Grid", "Custom"),
                Node(1, 0, 1, "ShellNavigationRail", "ShellNavigationRailControl", "Custom"),
                Node(2, 0, 1, "ShellTopBar", "ShellTopBarControl", "Custom"),
                Node(3, 0, 1, "TranscriptPanel", "Grid", "Custom"),
                Node(4, 3, 2, "TranscriptItems", "TranscriptListBox", "List"),
                Node(5, 3, 2, focusIdentity, "Button", "Button", true, true)
            ]
            : expectedState.StartsWith("experiment-lab.feature-", StringComparison.Ordinal)
                ?
                [
                    Node(0, null, 0, "ExperimentLabPanel", "ExperimentLabControl", "Custom"),
                    Node(1, 0, 1, featureContentIdentity, "Grid", "Custom"),
                    Node(2, 1, 2, "QaFocusFirst", "Button", "Button", true, focusIdentity == "QaFocusFirst"),
                    Node(3, 1, 2, "QaFocusSecond", "Button", "Button", true, focusIdentity == "QaFocusSecond")
                ]
                :
                [
                    Node(0, null, 0, "QaRoot", "Grid", "Custom"),
                    Node(1, 0, 1, focusIdentity, "Button", "Button", true, true)
                ];
    }

    private static object[] BuildAutomationNodes(
        string expectedState,
        string focusIdentity,
        string featureContentIdentity,
        int viewportWidthDip,
        int viewportHeightDip)
    {
        static object Node(
            int sequence,
            int? parent,
            int depth,
            string identity,
            string frameworkType,
            string controlType,
            double x,
            double y,
            double width,
            double height,
            bool focusable = false,
            bool focused = false) => new
        {
            Sequence = sequence,
            ParentSequence = parent,
            VisualDepth = depth,
            Identity = identity,
            AutomationId = identity,
            AutomationIdRedacted = false,
            FrameworkType = frameworkType,
            ControlType = controlType,
            IsVisible = true,
            IsEnabled = true,
            IsFocusable = focusable,
            HasKeyboardFocus = focused,
            BoundsX = x,
            BoundsY = y,
            BoundsWidth = width,
            BoundsHeight = height,
            EffectiveOpacity = 1d,
            IntersectsViewport = true,
            IsRendered = true
        };

        var contentWidth = Math.Max(100, viewportWidthDip - 240);
        var contentHeight = Math.Max(100, viewportHeightDip - 180);

        return expectedState.StartsWith("arena-empty.", StringComparison.Ordinal)
            ?
            [
                Node(0, null, 0, "RootLayout", "Grid", "Custom", 0, 0, viewportWidthDip, viewportHeightDip),
                Node(1, 0, 1, "ShellNavigationRail", "ShellNavigationRailControl", "Custom", 0, 0, 220, viewportHeightDip),
                Node(2, 0, 1, "ShellTopBar", "ShellTopBarControl", "Custom", 220, 0, viewportWidthDip - 220, 80),
                Node(3, 0, 1, "TranscriptPanel", "Grid", "Custom", 220, 80, viewportWidthDip - 220, viewportHeightDip - 80),
                Node(4, 3, 2, "TranscriptItems", "TranscriptListBox", "List", 240, 100, contentWidth, contentHeight),
                Node(5, 3, 2, focusIdentity, "Button", "Button", 260, 120, 120, 36, true, true)
            ]
            : expectedState.StartsWith("experiment-lab.feature-", StringComparison.Ordinal)
                ?
                [
                    Node(0, null, 0, "ExperimentLabPanel", "ExperimentLabControl", "Custom", 0, 0, viewportWidthDip, viewportHeightDip),
                    Node(1, 0, 1, featureContentIdentity, "Grid", "Custom", 80, 80, contentWidth, contentHeight),
                    Node(2, 1, 2, "QaFocusFirst", "Button", "Button", 120, 120, 120, 36, true, focusIdentity == "QaFocusFirst"),
                    Node(3, 1, 2, "QaFocusSecond", "Button", "Button", 120, 168, 120, 36, true, focusIdentity == "QaFocusSecond")
                ]
                :
            [
                Node(0, null, 0, "QaRoot", "Grid", "Custom", 0, 0, viewportWidthDip, viewportHeightDip),
                Node(1, 0, 1, focusIdentity, "Button", "Button", 40, 40, 120, 36, true, true)
            ];
    }

    private static byte[] BuildPngArtifact(
        int width,
        int height,
        int variant = 0,
        bool uniform = false,
        bool nearUniform = false,
        bool includeTextMetadata = false,
        bool sparseAdversarialVariation = false,
        bool subPercentAdversarialVariation = false,
        bool sparseOpaqueAdversarial = false,
        bool lowAlphaAdversarial = false,
        bool halfOpaqueAdversarial = false,
        bool hiddenRgbAdversarial = false,
        bool trailingIdatMetadata = false,
        bool splitImageData = false,
        bool singlePixelThemeDelta = false,
        bool targetedSamplePointThemeDelta = false,
        bool shellOnlyFeatureDelta = false,
        bool outsideFeatureBoundaryDelta = false,
        bool featureRootMaterialDelta = false,
        bool featureRootSubthresholdDelta = false)
    {
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;

        var targetedPixels = new HashSet<int>();
        if (targetedSamplePointThemeDelta)
        {
            for (var index = 0; index < 328; index++)
            {
                var sampleRow = index / 128;
                var sampleColumn = index % 128;
                targetedPixels.Add((sampleRow * height / 128) * width + sampleColumn * width / 128);
            }
        }

        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[checked(1 + width * 4)];
            row[0] = 0;
            for (var rowIndex = 0; rowIndex < height; rowIndex++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = 1 + x * 4;
                    var linearPixel = rowIndex * width + x;
                    var band = uniform || nearUniform || sparseAdversarialVariation || subPercentAdversarialVariation || sparseOpaqueAdversarial
                        ? 0
                        : ((x / 47) + (rowIndex / 31)) % 7;
                    if (nearUniform && x < 2 && rowIndex < 2)
                    {
                        band = 6;
                    }
                    row[offset] = (byte)(18 + variant * 17 + band * 19);
                    row[offset + 1] = (byte)(42 + variant * 11 + band * 13);
                    row[offset + 2] = (byte)(66 + variant * 7 + band * 9);
                    row[offset + 3] = sparseOpaqueAdversarial
                        ? (byte)0
                        : lowAlphaAdversarial
                            ? (byte)16
                            : halfOpaqueAdversarial && linearPixel % 2 != 0
                                ? (byte)0
                                : (byte)255;
                    if (sparseAdversarialVariation && rowIndex == 0 && x is 0 or 2 or 4 or 6)
                    {
                        var value = (byte)(x / 2 * 64);
                        row[offset] = row[offset + 1] = row[offset + 2] = value;
                    }
                    if (subPercentAdversarialVariation && linearPixel < 6_268 && linearPixel % 2 == 0)
                    {
                        var value = (byte)((linearPixel / 2 % 4) * 64);
                        row[offset] = row[offset + 1] = row[offset + 2] = value;
                    }
                    if (sparseOpaqueAdversarial && rowIndex == 0 && x < 256 && x % 2 == 0)
                    {
                        var value = (byte)((x / 2 % 4) * 64);
                        row[offset] = row[offset + 1] = row[offset + 2] = value;
                        row[offset + 3] = 255;
                    }
                    if (singlePixelThemeDelta && rowIndex == height / 2 && x == width / 2)
                    {
                        row[offset] ^= 0x40;
                    }
                    if (hiddenRgbAdversarial && rowIndex == height / 2 && x == width / 2)
                    {
                        row[offset] = 255;
                        row[offset + 1] = 0;
                        row[offset + 2] = 255;
                        row[offset + 3] = 0;
                    }
                    if (targetedPixels.Contains(linearPixel))
                    {
                        row[offset] = row[offset] < 128 ? (byte)255 : (byte)0;
                        row[offset + 1] = row[offset + 1] < 128 ? (byte)255 : (byte)0;
                        row[offset + 2] = row[offset + 2] < 128 ? (byte)255 : (byte)0;
                    }

                    // Feature-matrix automation roots use this deterministic
                    // rectangle. These variants let the validator checks prove
                    // that shell-only pixels do not stand in for feature content.
                    var featureRight = width - 160;
                    var featureBottom = height - 100;
                    var insideFeatureRoot = x >= 80 && x < featureRight
                        && rowIndex >= 80 && rowIndex < featureBottom;
                    var immediatelyOutsideFeatureBoundary =
                        (x >= featureRight && x < Math.Min(width, featureRight + 8)
                         && rowIndex >= 80 && rowIndex < featureBottom)
                        || (rowIndex >= featureBottom && rowIndex < Math.Min(height, featureBottom + 8)
                            && x >= 80 && x < featureRight);
                    if ((shellOnlyFeatureDelta && !insideFeatureRoot)
                        || (outsideFeatureBoundaryDelta && immediatelyOutsideFeatureBoundary)
                        || (featureRootMaterialDelta && insideFeatureRoot))
                    {
                        row[offset] = (byte)(255 - row[offset]);
                        row[offset + 1] = (byte)(255 - row[offset + 1]);
                        row[offset + 2] = (byte)(255 - row[offset + 2]);
                    }
                    if (featureRootSubthresholdDelta && insideFeatureRoot)
                    {
                        row[offset] = row[offset] <= 247 ? (byte)(row[offset] + 7) : (byte)(row[offset] - 7);
                        row[offset + 1] = row[offset + 1] <= 247 ? (byte)(row[offset + 1] + 7) : (byte)(row[offset + 1] - 7);
                        row[offset + 2] = row[offset + 2] <= 247 ? (byte)(row[offset + 2] + 7) : (byte)(row[offset + 2] - 7);
                    }
                }
                encoder.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WritePngChunk(png, "IHDR"u8, header);
        if (includeTextMetadata)
        {
            WritePngChunk(png, "tEXt"u8, "Comment\0QA metadata must be rejected"u8);
        }
        var imageData = compressed.ToArray();
        if (trailingIdatMetadata)
        {
            imageData = [.. imageData, .. "SECRET-METADATA-IN-IDAT!!"u8.ToArray()];
        }
        if (splitImageData)
        {
            var split = imageData.Length / 2;
            WritePngChunk(png, "IDAT"u8, imageData.AsSpan(0, split));
            WritePngChunk(png, "IDAT"u8, imageData.AsSpan(split));
        }
        else
        {
            WritePngChunk(png, "IDAT"u8, imageData);
        }
        WritePngChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, PngCrc32(crcInput));
        output.Write(crc);
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

    private static int FindSequence(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> sequence)
    {
        for (var index = 0; index <= bytes.Length - sequence.Length; index++)
        {
            if (bytes.Slice(index, sequence.Length).SequenceEqual(sequence))
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task InitializeRepositoryAsync(
        string root,
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        await RunGitAsync(root, ["init", "--quiet"], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, fileName), content, new UTF8Encoding(false), cancellationToken);
        await RunGitAsync(root, ["add", fileName], cancellationToken);
        await CommitAsync(root, cancellationToken);
    }

    private static Task CommitAsync(string root, CancellationToken cancellationToken) =>
        RunGitAsync(
            root,
            ["-c", "user.name=AI Arena QA", "-c", "user.email=qa@invalid.local", "commit", "--quiet", "-m", "fixture"],
            cancellationToken);

    private static async Task RunGitAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(root);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Git fixture setup failed.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture setup failed.");
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256Text(string value) =>
        Sha256(Encoding.UTF8.GetBytes(value));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record UiMatrixFixture(
        ArenaQaEvidenceContract Contract,
        ArenaQaArtifact MatrixArtifact,
        string MatrixPath,
        byte[] MatrixBytes);

    private sealed record FeatureSurfaceMatrixFixture(
        ArenaQaEvidenceContract Contract,
        ArenaQaArtifact MatrixArtifact,
        string MatrixPath,
        byte[] MatrixBytes,
        string FirstAutomationHash);
}
