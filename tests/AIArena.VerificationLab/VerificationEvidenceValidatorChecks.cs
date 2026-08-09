using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            cancellationToken);
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

    private static async Task<UiMatrixFixture> BuildUiMatrixFixtureAsync(
        string evidenceRoot,
        VerificationRepositorySnapshot outer,
        VerificationRepositorySnapshot map,
        string combinedFingerprint,
        DateTimeOffset capturedAt,
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
                "QaFocusSecond");
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
            "QaPrimaryButton");

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
        string focusIdentity) =>
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
            focusIdentity);

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
        string focusIdentity) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "ai_arena.ui_structure_evidence.v1",
            CapturedAtUtc = capturedAt,
            CaptureMode = "wpf-visual-tree-accessibility",
            Limitation = "OS UI Automation and OS input are not queried; focus traversal and the privacy-safe visual-tree snapshot are programmatic and in-process. RenderDpiScale is off-screen raster density, not physical or per-monitor display DPI. Motion fields prove preference plumbing, not rendered animation playback. Accessible names, help text, and all dynamic control content are omitted.",
            TreeFingerprint = treeFingerprint,
            ExpectedState = expectedState,
            ExpectedStateSource = "observed-visible-roots",
            SelectedView = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal) ? "arena" : "verification-window",
            ObservedSurfaceState = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal) ? "arena-empty" : "verification-window",
            DialogState = "closed",
            VisibleRootIdentities = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal)
                ? new[] { "TranscriptPanel" }
                : new[] { "QaRoot" },
            RequiredControlIdentities = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal)
                ? new[] { "RootLayout", "ShellNavigationRail", "ShellTopBar", "TranscriptItems", "TranscriptPanel" }
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
            NodeCount = expectedState.StartsWith("arena-empty.", StringComparison.Ordinal) ? 6 : 2,
            Truncated = false,
            Nodes = BuildAutomationNodes(expectedState, focusIdentity)
        });

    private static object[] BuildAutomationNodes(string expectedState, string focusIdentity)
    {
        static object Node(int sequence, int? parent, int depth, string identity, string frameworkType, string controlType, bool focused = false) => new
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
            IsFocusable = focused,
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
                Node(5, 3, 2, focusIdentity, "Button", "Button", true)
            ]
            :
            [
                Node(0, null, 0, "QaRoot", "Grid", "Custom"),
                Node(1, 0, 1, focusIdentity, "Button", "Button", true)
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
        bool targetedSamplePointThemeDelta = false)
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
}
