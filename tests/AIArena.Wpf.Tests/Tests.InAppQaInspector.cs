using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void QaInspectorRejectsMaliciousEvidencePathsAndContent()
    {
        WithQaRoot(root =>
        {
            var repository = new QaEvidenceRepository(root, new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()));
            var traversal = repository.LoadAsync("artifacts/qa/../qa-evidence.json").GetAwaiter().GetResult();
            Require(traversal.State == QaInspectorState.Blocked && traversal.Code == "qa.evidence_path", "QA Inspector accepted traversal");

            var contract = CreateQaContract();
            var evidencePath = WriteQaContract(root, "malicious", contract);
            var validBytes = File.ReadAllBytes(evidencePath);
            File.WriteAllBytes(evidencePath, [0xEF, 0xBB, 0xBF, .. validBytes]);
            var bom = repository.LoadAsync("artifacts/qa/malicious/qa-evidence.json").GetAwaiter().GetResult();
            Require(bom.State == QaInspectorState.Blocked && bom.Code == "qa.evidence_bom", "QA Inspector accepted UTF-8 BOM evidence");

            File.WriteAllBytes(evidencePath, validBytes);
            var json = Encoding.UTF8.GetString(validBytes).Replace(
                "Recorded by bounded local QA.",
                "C:\\\\Users\\\\Cyber\\\\private.txt",
                StringComparison.Ordinal);
            File.WriteAllText(evidencePath, json, new UTF8Encoding(false));
            var privateContent = repository.LoadAsync("artifacts/qa/malicious/qa-evidence.json").GetAwaiter().GetResult();
            Require(privateContent.State == QaInspectorState.Blocked && privateContent.Code == "qa.contract_invalid", "QA Inspector accepted an absolute private path in evidence");

            using (var stream = new FileStream(evidencePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(QaEvidenceRepository.MaximumEvidenceBytes + 1L);
            }
            var oversized = repository.LoadAsync("artifacts/qa/malicious/qa-evidence.json").GetAwaiter().GetResult();
            Require(oversized.State == QaInspectorState.Blocked && oversized.Code == "qa.evidence_size", "QA Inspector accepted oversized evidence");

            var reparseTargetEvidence = WriteQaContract(root, "reparse-target", contract);
            var reparseTarget = Path.GetDirectoryName(reparseTargetEvidence)!;
            var reparseLink = Path.Combine(root, "artifacts", "qa", "reparse-link");
            var reparseExercised = false;
            try
            {
                Directory.CreateSymbolicLink(reparseLink, reparseTarget);
                reparseExercised = (File.GetAttributes(reparseLink) & FileAttributes.ReparsePoint) != 0;
                Require(reparseExercised, "test link was not marked as a reparse point");
                var reparse = repository.LoadAsync("artifacts/qa/reparse-link/qa-evidence.json").GetAwaiter().GetResult();
                Require(reparse.State == QaInspectorState.Blocked && reparse.Code == "qa.evidence_path", "QA Inspector followed a reparse-point bundle");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
            {
                // Some Windows CI policies deny creating links. The structural
                // assertion below still guards the production reparse check.
            }
            finally
            {
                if (Directory.Exists(reparseLink)
                    && (File.GetAttributes(reparseLink) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(reparseLink);
                }
            }

            var source = FindWorkspaceFile("src/AIArena.Wpf/Services/InAppQaInspectorCoordinator.cs");
            var sourceText = File.ReadAllText(source);
            Require(sourceText.Contains("FileAttributes.ReparsePoint", StringComparison.Ordinal)
                && sourceText.Contains("MaximumBundleBytes", StringComparison.Ordinal),
                "QA Inspector evidence boundary lost reparse or aggregate-size protection");
        });
    }

    static void QaInspectorPresentsAllEvidenceStatesAccessibly()
    {
        var contract = CreateQaContract() with
        {
            Gates =
            [
                Gate("gate:pass", ArenaQaGateOutcome.Pass, QaObserved("evidence:pass"), 1, 0),
                Gate("gate:partial", ArenaQaGateOutcome.Partial, QaInferred("evidence:partial"), 0, 0),
                Gate("gate:blocked", ArenaQaGateOutcome.Blocked, QaUnavailable("evidence:blocked"), 0, 0),
                Gate("gate:unavailable", ArenaQaGateOutcome.Unavailable, QaUnavailable("evidence:unavailable"), 0, 0)
            ]
        };
        var snapshot = new QaEvidenceSnapshot(
            contract,
            "artifacts/qa/presentation/qa-evidence.json",
            "private-path-not-for-presentation",
            "private-bundle-not-for-presentation",
            new string('e', 64),
            QaCurrentnessResult.Current(),
            [],
            QaInspectorState.Partial,
            "qa.loaded",
            "Loaded.");
        var presentation = QaInspectorPresentation.Create(new(QaInspectorState.Partial, "qa.loaded", "Loaded.", snapshot));
        Require(
            presentation.Gates.Select(item => item.State).SequenceEqual(["PASS", "PARTIAL", "BLOCKED", "UNAVAILABLE"]),
            "QA gate states were not rendered with non-colour labels");
        Require(presentation.TestTotals.Contains("total", StringComparison.Ordinal), "QA aggregate test counts were omitted");
        Require(presentation.LiveProvider.Contains("Limitation:", StringComparison.Ordinal), "live-provider limitation was omitted");

        static SolidColorBrush ThemeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static void ApplyInspectorTheme(FrameworkElement element, ThemePalette theme)
        {
            element.Resources["PanelBrush"] = ThemeBrush(theme.Panel);
            element.Resources["ControlBorderBrush"] = ThemeBrush(theme.Border);
            element.Resources["TextBrush"] = ThemeBrush(theme.Text);
            element.Resources["MutedTextBrush"] = ThemeBrush(theme.MutedText);
            element.Resources["PrimaryBorderBrush"] = ThemeBrush(theme.PrimaryBorder);
            element.Resources["HoverBorderBrush"] = ThemeBrush(theme.HoverBorder);
            element.Resources["NavHoverBrush"] = ThemeBrush(theme.NavHover);
            element.Resources["NavActiveBrush"] = ThemeBrush(theme.NavActive);
            element.Resources["DisabledBorderBrush"] = ThemeBrush(theme.DisabledBorder);
            element.Resources["DisabledTextBrush"] = ThemeBrush(theme.DisabledText);
        }

        RunStaTest(() =>
        {
            var control = new InAppQaInspectorControl();
            const string evidenceLabel = "20260809t042925z-ea67c771";
            const string evidencePath = "artifacts/qa/20260809t042925z-ea67c771/qa-evidence.json";
            var suite = QaLocalSuiteCatalog.All.Single(item => item.Id == QaLocalSuite.Wpf);
            control.SetEvidenceChoices(
                [new QaEvidenceChoice(evidencePath, evidenceLabel, DateTimeOffset.UtcNow)],
                evidencePath);
            control.SetSuiteChoices([suite]);
            Require(control.FeatureRegistration.Key == "in-app-qa-inspector", "QA Inspector feature key changed");
            Require(!string.IsNullOrWhiteSpace(control.FeatureRegistration.HelpText), "QA Inspector feature help is missing");
            Require(AutomationProperties.GetName(control) == "In-App QA Inspector", "QA Inspector root automation name is missing");
            Require(!InAppQaInspectorControl.UsesCompactLayout(960)
                && InAppQaInspectorControl.UsesCompactLayout(620)
                && !InAppQaInspectorControl.UsesCompactLayout(double.NaN),
                "QA Inspector responsive tier is not deterministic");
            control.ApplyPresentation(presentation);
            control.ApplyResponsiveLayout(compact: true);
            control.ApplyResponsiveLayout(compact: false);
            Require(control.RefreshEvidenceButton.MinHeight >= 34 && control.RunSuiteButton.MinHeight >= 34, "QA Inspector pointer targets are undersized");
            Require(AutomationProperties.GetLiveSetting(control.QaStatusText) == AutomationLiveSetting.Polite, "QA status is not a polite live region");
            Require(AutomationProperties.GetName(control.AcceptInspectionButton) == "Accept rendered inspection", "inspection acceptance lacks an automation name");
            var host = new Window
            {
                Content = control,
                Width = 960,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0
            };
            host.Show();
            try
            {
                host.Activate();
                host.UpdateLayout();
                var evidenceLabelText = DescendantTextBlocks(control.EvidenceRunPicker)
                    .SingleOrDefault(item => item.Text == evidenceLabel);
                var suiteLabelText = DescendantTextBlocks(control.SuitePicker)
                    .SingleOrDefault(item => item.Text == suite.Label);
                Require(evidenceLabelText is { TextTrimming: TextTrimming.CharacterEllipsis }
                    && suiteLabelText is { TextTrimming: TextTrimming.CharacterEllipsis }
                    && control.SelectedEvidencePath == evidencePath
                    && control.SelectedSuite == QaLocalSuite.Wpf
                    && !DescendantTextBlocks(control.EvidenceRunPicker).Any(item => item.Text.Contains(nameof(QaEvidenceChoice), StringComparison.Ordinal))
                    && !DescendantTextBlocks(control.SuitePicker).Any(item => item.Text.Contains(nameof(QaSuiteDefinition), StringComparison.Ordinal)),
                    "QA Inspector pickers exposed record ToString values instead of bounded user labels");

                control.EvidenceTabs.ApplyTemplate();
                var tabs = control.EvidenceTabs.Items.Cast<TabItem>().ToArray();
                Require(tabs.Length == 5, "QA Inspector evidence tabs changed unexpectedly");
                foreach (var tab in tabs) tab.ApplyTemplate();
                var selectedTabChrome = tabs[0].Template.FindName("TabChrome", tabs[0]) as Border
                    ?? throw new InvalidOperationException("QA Inspector did not instantiate selected tab chrome.");
                var unselectedTabChrome = tabs[1].Template.FindName("TabChrome", tabs[1]) as Border
                    ?? throw new InvalidOperationException("QA Inspector did not instantiate unselected tab chrome.");
                var contentChrome = control.EvidenceTabs.Template.FindName("ContentChrome", control.EvidenceTabs) as Border
                    ?? throw new InvalidOperationException("QA Inspector did not instantiate content tab chrome.");
                foreach (var themeId in new[] { "dark-blue", "light", "high-contrast" })
                {
                    var theme = ThemePalette.Resolve(themeId);
                    ApplyInspectorTheme(control, theme);
                    host.UpdateLayout();
                    Require(selectedTabChrome.Background is SolidColorBrush selectedBackground
                            && selectedBackground.Color == theme.NavActive
                            && selectedTabChrome.BorderBrush is SolidColorBrush selectedBorder
                            && selectedBorder.Color == theme.PrimaryBorder
                            && contentChrome.Background is SolidColorBrush contentBackground
                            && contentBackground.Color == theme.Panel
                            && contentChrome.BorderBrush is SolidColorBrush contentBorder
                            && contentBorder.Color == theme.Border
                            && unselectedTabChrome.Background == Brushes.Transparent
                            && unselectedTabChrome.BorderBrush is SolidColorBrush unselectedBorder
                            && unselectedBorder.Color == theme.Border,
                        $"QA Inspector tab chrome did not resolve the {themeId} palette");
                }
                Require(control.RefreshEvidenceButton.Focus(), "QA Inspector refresh action was not keyboard focusable at 960 DIP");
                Require(control.RefreshEvidenceButton.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)),
                    "QA Inspector keyboard traversal did not reach the next action");
                host.Width = 620;
                host.UpdateLayout();
                control.ApplyResponsiveLayout(compact: true);
                Require(control.ArtifactGapColumn.Width.Value == 0
                    && control.SchemaGapColumn.Width.Value == 0
                    && control.CommandGapColumn.Width.Value == 0
                    && Grid.GetRow(control.SuiteCommandPane) == 2,
                    "QA Inspector narrow layout retained wide-only gaps");
            }
            finally
            {
                host.Close();
            }
            var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/InAppQaInspectorControl.xaml"));
            Require(xaml.Contains("DynamicResource", StringComparison.Ordinal)
                && xaml.Contains("QaPickerLabelTemplate", StringComparison.Ordinal)
                && xaml.Contains("QaEvidenceTabControl", StringComparison.Ordinal)
                && !xaml.Contains("DisplayMemberPath=\"Label\"", StringComparison.Ordinal)
                && !xaml.Contains("Storyboard", StringComparison.Ordinal)
                && !xaml.Contains("DoubleAnimation", StringComparison.Ordinal),
                "QA Inspector is not static reduced-motion/theme-resource safe");
        });
    }

    static void QaInspectorAllowsOnlyFixedSuitesAndCancels()
    {
        RunStaTest(() =>
        {
            WithQaRoot(root =>
            {
                var control = new InAppQaInspectorControl();
                var runner = new FakeQaSuiteRunner();
                using var coordinator = new InAppQaInspectorCoordinator(
                    control,
                    root,
                    new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()),
                    runner,
                    new FakeQaAcceptanceRunner(),
                    new FakeQaClipboard(),
                    () => true);

                QaSuiteRunResult? unknown = null;
                RunExperimentDispatcherTask(async () => unknown = await coordinator.RunSuiteAsync((QaLocalSuite)999));
                Require(unknown?.Code == "qa.suite_not_allowlisted" && runner.CallCount == 0, "unknown QA suite reached the process runner");

                QaSuiteRunResult? seal = null;
                RunExperimentDispatcherTask(async () => seal = await coordinator.RunSuiteAsync(QaLocalSuite.FullSeal));
                Require(seal?.State == QaInspectorState.Unavailable
                    && seal.Code == "qa.full_seal_requires_close"
                    && runner.CallCount == 0,
                    "in-app full seal was not held behind the close boundary");
                Require(control.PostCloseCommandText.Text == QaLocalSuiteCatalog.PostCloseSealCommand, "exact post-close seal command was not exposed");

                runner.CancelOnStart = coordinator.CancelSuite;
                QaSuiteRunResult? cancelled = null;
                RunExperimentDispatcherTask(async () => cancelled = await coordinator.RunSuiteAsync(QaLocalSuite.Core));
                Require(cancelled?.Code == "qa.suite_cancelled" && runner.CallCount == 1, "focused QA cancellation was not propagated");

                Require(QaLocalSuiteCatalog.All.Count == 6
                    && QaLocalSuiteCatalog.All.Select(item => item.Id).Distinct().Count() == 6
                    && QaLocalSuiteCatalog.All.SelectMany(item => item.Commands).All(command => QaSuiteCommandSafety.IsAllowed(
                        QaLocalSuiteCatalog.All.Single(suite => suite.Commands.Contains(command)).Id,
                        command)),
                    "QA suite allowlist is incomplete or internally inconsistent");
            });
        });
    }

    static void QaInspectorReportRemainsPrivacySafe()
    {
        RunStaTest(() =>
        {
            WithQaRoot(root =>
            {
                var contract = CreateQaContract() with
                {
                    Evidence = [new("evidence:private-narrative", ArenaEvidenceState.Observed, "A prompt-like private narrative that must not enter Copy Report.", "artifact:trace")]
                };
                WriteQaContract(root, "report", contract);
                var clipboard = new FakeQaClipboard();
                var control = new InAppQaInspectorControl();
                using var coordinator = new InAppQaInspectorCoordinator(
                    control,
                    root,
                    new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()),
                    new FakeQaSuiteRunner(),
                    new FakeQaAcceptanceRunner(),
                    clipboard,
                    () => true);
                RunExperimentDispatcherTask(() => coordinator.InitializeAsync());
                var report = coordinator.BuildPrivacySafeReport();
                Require(!report.Contains(root, StringComparison.OrdinalIgnoreCase), "Copy Report leaked the absolute repository path");
                Require(!report.Contains("prompt-like", StringComparison.OrdinalIgnoreCase), "Copy Report leaked raw evidence narrative");
                Require(!report.Contains("raw command", StringComparison.OrdinalIgnoreCase), "Copy Report claimed or included raw command output");
                Require(report.Contains("Gates:", StringComparison.Ordinal) && report.Length <= 16 * 1024, "Copy Report omitted bounded aggregate evidence");
                Require(coordinator.CopyReport() && clipboard.Text == report, "privacy-safe report was not sent through the injected clipboard boundary");
            });
        });
    }

    static void QaInspectorAcceptanceRequiresCurrentVerifiedVisualEvidence()
    {
        RunStaTest(() =>
        {
            WithQaRoot(root =>
            {
                var bundle = CreateAcceptanceReadyBundle(root, "acceptance");
                var currentness = new FakeQaCurrentnessValidator(QaCurrentnessResult.Current());
                var acceptance = new FakeQaAcceptanceRunner(SealQaEvidenceFile);
                var control = new InAppQaInspectorControl();
                using var coordinator = new InAppQaInspectorCoordinator(
                    control,
                    root,
                    currentness,
                    new FakeQaSuiteRunner(),
                    acceptance,
                    new FakeQaClipboard(),
                    () => true);
                QaEvidenceLoadResult? loaded = null;
                RunExperimentDispatcherTask(async () => loaded = await coordinator.RefreshAsync(bundle.RelativeEvidencePath));
                Require(loaded?.Snapshot is not null, "current clean linked visual evidence did not load");
                var snapshot = loaded!.Snapshot!;
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(snapshot, null)
                    && !control.AcceptInspectionButton.IsEnabled
                    && control.CurrentPreviewImage.Source is null,
                    "refresh auto-reviewed a screenshot or enabled acceptance without explicit previews");

                var screenshots = snapshot.Artifacts
                    .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
                    .OrderBy(item => item.Artifact.Id, StringComparer.Ordinal)
                    .ToArray();
                Require(screenshots.Length == 2, "acceptance fixture should exercise an all-screenshot review boundary");
                RunExperimentDispatcherTask(() => coordinator.SelectScreenshotAsync(screenshots[0].Artifact.Id));
                Require(!control.AcceptInspectionButton.IsEnabled, "one reviewed screenshot enabled acceptance while another remained unreviewed");
                RunExperimentDispatcherTask(() => coordinator.SelectScreenshotAsync(screenshots[1].Artifact.Id));
                Require(control.AcceptInspectionButton.IsEnabled, "reviewing every exact screenshot did not enable acceptance");
                var reviewPath = Path.Combine(snapshot.BundlePath, QaEvidenceRepository.ReviewManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Require(File.Exists(reviewPath), "the exact in-app review manifest was not persisted");
                var reviewText = File.ReadAllText(reviewPath, Encoding.UTF8);
                Require(!reviewText.Contains(root, StringComparison.OrdinalIgnoreCase)
                    && reviewText.Contains(snapshot.EvidenceSha256, StringComparison.Ordinal)
                    && reviewText.Contains(snapshot.Contract.TreeFingerprint, StringComparison.Ordinal)
                    && screenshots.All(item => reviewText.Contains(item.Artifact.Id, StringComparison.Ordinal)
                        && reviewText.Contains(item.Artifact.Sha256, StringComparison.Ordinal)),
                    "the review manifest was not privacy-safe or exactly bound to evidence, tree, and every screenshot");

                RunExperimentDispatcherTask(async () => loaded = await coordinator.RefreshAsync(bundle.RelativeEvidencePath));
                Require(!File.Exists(reviewPath)
                    && !control.AcceptInspectionButton.IsEnabled
                    && control.CurrentPreviewImage.Source is null,
                    "refresh did not clear the persisted and visible screenshot review state");
                snapshot = loaded!.Snapshot!;
                screenshots = snapshot.Artifacts
                    .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
                    .OrderBy(item => item.Artifact.Id, StringComparer.Ordinal)
                    .ToArray();
                foreach (var screenshot in screenshots)
                {
                    RunExperimentDispatcherTask(() => coordinator.SelectScreenshotAsync(screenshot.Artifact.Id));
                }
                Require(control.AcceptInspectionButton.IsEnabled, "re-reviewing every screenshot after refresh did not restore readiness");

                var changedScreenshotPath = Path.Combine(
                    snapshot.BundlePath,
                    screenshots[1].Artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var originalScreenshotBytes = File.ReadAllBytes(changedScreenshotPath);
                File.AppendAllText(changedScreenshotPath, "changed", Encoding.UTF8);
                QaAcceptanceResult? changed = null;
                RunExperimentDispatcherTask(async () => changed = await coordinator.AcceptInspectionAsync());
                Require(changed?.State == QaInspectorState.Blocked
                    && acceptance.CallCount == 0
                    && !File.Exists(reviewPath)
                    && !control.AcceptInspectionButton.IsEnabled,
                    "an artifact change did not clear reviews and block in-app acceptance");
                File.WriteAllBytes(changedScreenshotPath, originalScreenshotBytes);
                RunExperimentDispatcherTask(async () => loaded = await coordinator.RefreshAsync(bundle.RelativeEvidencePath));
                snapshot = loaded!.Snapshot!;
                screenshots = snapshot.Artifacts
                    .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
                    .OrderBy(item => item.Artifact.Id, StringComparer.Ordinal)
                    .ToArray();
                foreach (var screenshot in screenshots)
                {
                    RunExperimentDispatcherTask(() => coordinator.SelectScreenshotAsync(screenshot.Artifact.Id));
                }
                Require(control.AcceptInspectionButton.IsEnabled, "restored exact evidence did not permit a new explicit review session");

                QaAcceptanceResult? accepted = null;
                RunExperimentDispatcherTask(async () => accepted = await coordinator.AcceptInspectionAsync());
                Require(accepted?.State == QaInspectorState.Pass && acceptance.CallCount == 1, "explicit acceptance did not invoke the injected authoritative runner once");
                Require(acceptance.ReviewedManifestPath == reviewPath
                    && acceptance.ReviewedManifestSha256?.Length == 64,
                    "in-app acceptance did not pass the exact review-manifest path and hash");
                Require(currentness.CallCount >= 4, "authoritative evidence was not reloaded across refresh, artifact reset, and acceptance");
                Require(control.InspectionText.Text.StartsWith("Accepted at", StringComparison.Ordinal)
                    && !control.AcceptInspectionButton.IsEnabled,
                    "post-acceptance UI did not reload the authoritative sealed evidence");

                var arguments = PowerShellQaInspectionAcceptanceRunner.BuildArguments("qa-accept-inspection.ps1", "qa-evidence.json", "repository", "review.json", new string('a', 64));
                Require(arguments.Contains("qa-accept-inspection.ps1")
                    && arguments.Contains("InAppReviewedManifest")
                    && arguments.Contains("review.json")
                    && arguments.Contains(new string('a', 64))
                    && !arguments.Any(argument => argument.Contains("preaccept", StringComparison.OrdinalIgnoreCase)),
                    "inspection acceptance introduced a preaccept bypass");

                var staleSnapshot = snapshot with { Currentness = QaCurrentnessResult.Stale() };
                var completedReview = CompleteReviewManifest(snapshot);
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(staleSnapshot, completedReview), "stale source evidence enabled acceptance");
                var dirtySnapshot = snapshot with { Contract = snapshot.Contract with { IsWorkingTreeClean = false } };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(dirtySnapshot, completedReview), "dirty-tree evidence enabled acceptance");
                var historicalManifest = snapshot with
                {
                    Contract = snapshot.Contract with { SealManifestId = ArenaQaSealManifestV1.Id }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(historicalManifest, completedReview),
                    "historical V1 evidence enabled current in-app acceptance");

                var migrationGateIndex = snapshot.Contract.Gates.IndexOf(snapshot.Contract.Gates.Single(gate =>
                    gate.Id == ArenaQaSealManifestV2.ExplicitMigrationGateId));
                var deletedMigrationGate = snapshot with
                {
                    Contract = snapshot.Contract with { Gates = snapshot.Contract.Gates.RemoveAt(migrationGateIndex) }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(deletedMigrationGate, completedReview),
                    "deleted explicit migration gate enabled acceptance");
                var optionalMigrationGate = snapshot with
                {
                    Contract = snapshot.Contract with
                    {
                        Gates = snapshot.Contract.Gates.SetItem(
                            migrationGateIndex,
                            snapshot.Contract.Gates[migrationGateIndex] with { Required = false })
                    }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(optionalMigrationGate, completedReview),
                    "optional explicit migration gate enabled acceptance");
                var partialMigrationGate = snapshot with
                {
                    Contract = snapshot.Contract with
                    {
                        Gates = snapshot.Contract.Gates.SetItem(
                            migrationGateIndex,
                            snapshot.Contract.Gates[migrationGateIndex] with { Outcome = ArenaQaGateOutcome.Partial })
                    }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(partialMigrationGate, completedReview),
                    "non-passing explicit migration gate enabled acceptance");

                var scenarioSchemaIndex = snapshot.Contract.SchemaChecks.IndexOf(snapshot.Contract.SchemaChecks.Single(check =>
                    check.Schema == ArenaContractSchemas.ScenarioPack));
                var substitutedMigrationSource = snapshot with
                {
                    Contract = snapshot.Contract with
                    {
                        SchemaChecks = snapshot.Contract.SchemaChecks.SetItem(
                            scenarioSchemaIndex,
                            snapshot.Contract.SchemaChecks[scenarioSchemaIndex] with
                            {
                                MigratedFromSchema = "ai_arena.scenario_pack.v0-renamed"
                            })
                    }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(substitutedMigrationSource, completedReview),
                    "substituted migration source schema enabled acceptance");

                var migrationArtifactIndex = snapshot.Artifacts.IndexOf(snapshot.Artifacts.Single(item =>
                    item.Artifact.Id == ArenaQaSealManifestV2.ExplicitMigrationArtifactId));
                var substitutedMigrationArtifact = snapshot with
                {
                    Artifacts = snapshot.Artifacts.SetItem(
                        migrationArtifactIndex,
                        snapshot.Artifacts[migrationArtifactIndex] with
                        {
                            Artifact = snapshot.Artifacts[migrationArtifactIndex].Artifact with { Kind = "generic-log" }
                        })
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(substitutedMigrationArtifact, completedReview),
                    "substituted migration artifact kind enabled acceptance");
                var missingFeatureMatrix = snapshot with
                {
                    Artifacts = [.. snapshot.Artifacts.Where(item =>
                        item.Artifact.Id != "artifact.pass-02.feature-surface-matrix")]
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(missingFeatureMatrix, completedReview),
                    "missing clean-pass feature matrix enabled acceptance");

                var missingLimitation = snapshot with
                {
                    Contract = snapshot.Contract with { AcceptedLimitations = snapshot.Contract.AcceptedLimitations.RemoveAt(0) }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(missingLimitation, completedReview), "deleted required limitation enabled acceptance");
                var renamedItems = snapshot.Contract.AcceptedLimitations.ToBuilder();
                renamedItems[0] = renamedItems[0] with { Id = "limitation.source-boundary-renamed" };
                var renamedLimitation = snapshot with
                {
                    Contract = snapshot.Contract with { AcceptedLimitations = renamedItems.ToImmutable() }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(renamedLimitation, completedReview), "renamed required limitation enabled acceptance");
                var stateItems = snapshot.Contract.AcceptedLimitations.ToBuilder();
                stateItems[0] = stateItems[0] with
                {
                    Evidence = stateItems[0].Evidence with
                    {
                        State = ArenaEvidenceState.Inferred,
                        Basis = "Tampered state must remain unacceptable.",
                        Limitation = null
                    }
                };
                var stateTamper = snapshot with
                {
                    Contract = snapshot.Contract with { AcceptedLimitations = stateItems.ToImmutable() }
                };
                Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(stateTamper, completedReview), "required limitation evidence-state tamper enabled acceptance");

                var canonical = snapshot.Contract.AcceptedLimitations[0];
                var semanticTampers = new[]
                {
                    canonical with { Summary = "OS input and external UIA were fully tested." },
                    canonical with { Evidence = canonical.Evidence with { Summary = "All external interaction evidence was observed." } },
                    canonical with { Evidence = canonical.Evidence with { ReferenceId = "artifact.unrelated.log" } },
                    canonical with { Evidence = canonical.Evidence with { Limitation = "No limitation." } },
                    canonical with { Evidence = canonical.Evidence with { Basis = "Tampered inferred basis." } }
                };
                foreach (var semanticTamper in semanticTampers)
                {
                    var tamperedItems = snapshot.Contract.AcceptedLimitations.SetItem(0, semanticTamper);
                    var tamperedSnapshot = snapshot with
                    {
                        Contract = snapshot.Contract with { AcceptedLimitations = tamperedItems }
                    };
                    Require(!InAppQaInspectorCoordinator.IsInspectionAcceptanceReady(tamperedSnapshot, completedReview),
                        "required limitation semantic tamper enabled acceptance");
                }
            });
        });
    }

    static void QaInspectorIsolatedCapturePreservesExistingReviewsReadOnly()
    {
        RunStaTest(() =>
        {
            WithQaRoot(root =>
            {
                var bundle = CreateAcceptanceReadyBundle(root, "read-only-capture");
                var bundlePath = Path.GetDirectoryName(bundle.EvidencePath)
                    ?? throw new InvalidOperationException("QA fixture bundle path is unavailable.");
                var reviewPath = Path.Combine(
                    bundlePath,
                    QaEvidenceRepository.ReviewManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var repository = new QaEvidenceRepository(
                    root,
                    new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()));
                var initialLoad = repository.LoadAsync(bundle.RelativeEvidencePath).GetAwaiter().GetResult();
                var initialSnapshot = initialLoad.Snapshot
                    ?? throw new InvalidOperationException("Read-only QA fixture could not load its valid evidence.");
                var reviewedIds = initialSnapshot.Artifacts
                    .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
                    .Select(item => item.Artifact.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var reviewHandle = repository.WriteReviewManifestAsync(initialSnapshot, reviewedIds).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("Read-only QA fixture could not persist a valid completed review.");
                Require(InAppQaInspectorCoordinator.ReviewManifestMatches(initialSnapshot, reviewHandle.Manifest),
                    "read-only preservation fixture did not begin with a valid hash-bound completed review");
                var sentinel = File.ReadAllBytes(reviewPath);

                var isolatedDataRoot = Path.Combine(root, "isolated-data");
                Directory.CreateDirectory(isolatedDataRoot);
                var ownerMarker = Path.Combine(isolatedDataRoot, ".ai-arena-qa-owner");
                File.WriteAllText(ownerMarker, "fixture-owner\n", new UTF8Encoding(false));
                var previousDataRoot = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
                bool readOnlyPreserveReviews;
                try
                {
                    Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", isolatedDataRoot);
                    readOnlyPreserveReviews = InAppQaInspectorCoordinator.ShouldPreserveReviewsForIsolatedCapture(isolatedDataRoot);
                    Require(readOnlyPreserveReviews,
                        "owned isolated QA data root did not activate read-only review preservation");
                    File.Delete(ownerMarker);
                    Require(!InAppQaInspectorCoordinator.ShouldPreserveReviewsForIsolatedCapture(isolatedDataRoot),
                        "isolated QA data root without its owner marker activated read-only preservation");
                    File.WriteAllText(ownerMarker, "fixture-owner\n", new UTF8Encoding(false));
                }
                finally
                {
                    Environment.SetEnvironmentVariable("AI_ARENA_DATA_DIR", previousDataRoot);
                }

                var suiteRunner = new FakeQaSuiteRunner();
                var acceptanceRunner = new FakeQaAcceptanceRunner();
                var control = new InAppQaInspectorControl();
                using var coordinator = new InAppQaInspectorCoordinator(
                    control,
                    root,
                    new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()),
                    suiteRunner,
                    acceptanceRunner,
                    new FakeQaClipboard(),
                    () => true,
                    readOnlyPreserveReviews: readOnlyPreserveReviews);

                QaEvidenceLoadResult? loaded = null;
                RunExperimentDispatcherTask(async () => loaded = await coordinator.RefreshAsync(bundle.RelativeEvidencePath));
                var screenshot = loaded?.Snapshot?.Artifacts.First(item => item.Artifact.Kind == "rendered-ui-screenshot")
                    ?? throw new InvalidOperationException("Read-only QA fixture screenshot is unavailable.");
                RunExperimentDispatcherTask(() => coordinator.SelectScreenshotAsync(screenshot.Artifact.Id));
                QaSuiteRunResult? suite = null;
                RunExperimentDispatcherTask(async () => suite = await coordinator.RunSuiteAsync(QaLocalSuite.Core));
                QaAcceptanceResult? acceptance = null;
                RunExperimentDispatcherTask(async () => acceptance = await coordinator.AcceptInspectionAsync());
                RunExperimentDispatcherTask(async () => loaded = await coordinator.RefreshAsync(bundle.RelativeEvidencePath));

                Require(File.Exists(reviewPath)
                    && File.ReadAllBytes(reviewPath).SequenceEqual(sentinel),
                    "isolated QA capture deleted or rewrote an existing human inspection review");
                Require(suite?.Code == "qa.capture_read_only"
                    && acceptance?.Code == "qa.capture_read_only"
                    && suiteRunner.CallCount == 0
                    && acceptanceRunner.CallCount == 0,
                    "isolated QA capture reached a mutating suite or acceptance runner");
                Require(!control.RunSuiteButton.IsEnabled
                    && !control.CancelSuiteButton.IsEnabled
                    && !control.AcceptInspectionButton.IsEnabled,
                    "isolated QA capture exposed mutating QA Inspector commands");
            });
        });
    }

    static void QaInspectorRevalidatesScreenshotHashesBeforePreview()
    {
        WithQaRoot(root =>
        {
            var bundle = CreateAcceptanceReadyBundle(root, "preview");
            var repository = new QaEvidenceRepository(root, new FakeQaCurrentnessValidator(QaCurrentnessResult.Current()));
            var loaded = repository.LoadAsync(bundle.RelativeEvidencePath).GetAwaiter().GetResult();
            Require(loaded.Snapshot is not null && loaded.Snapshot.Artifacts.All(item => item.IsVerified), "valid QA artifacts failed initial verification");
            var snapshot = loaded.Snapshot!;
            var screenshot = snapshot.Artifacts.First(item => item.Artifact.Kind == "rendered-ui-screenshot");
            var preview = repository.LoadPreviewAsync(snapshot, screenshot.Artifact.Id).GetAwaiter().GetResult();
            Require(preview?.CurrentPng.Length > 0
                && preview.BaselinePng?.Length > 0
                && preview.AutomationStatus.Contains("verified", StringComparison.OrdinalIgnoreCase),
                "verified before/after and linked automation preview was unavailable");

            var screenshotPath = Path.Combine(snapshot.BundlePath, screenshot.Artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(screenshotPath, "tampered", Encoding.UTF8);
            var rejected = repository.LoadPreviewAsync(snapshot, screenshot.Artifact.Id).GetAwaiter().GetResult();
            Require(rejected is null, "screenshot preview reused stale verification after artifact tampering");
            var reloaded = repository.LoadAsync(bundle.RelativeEvidencePath).GetAwaiter().GetResult();
            Require(reloaded.State == QaInspectorState.Blocked
                && reloaded.Snapshot?.Artifacts.Any(item => item.Code == "qa.artifact_hash") == true,
                "artifact hash regression was not surfaced as blocked evidence");
        });
    }

    static void QaInspectorBuildsAndPinsFreshCurrentnessValidatorOutput()
    {
        WithQaRoot(root =>
        {
            var projectRoot = Path.Combine(root, "tests", "AIArena.VerificationLab");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(
                Path.Combine(projectRoot, "AIArena.VerificationLab.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                new UTF8Encoding(false));
            var source = """
                using System.Reflection;
                using System.Text;

                if (args.Length != 3 || args[0] != "--validate-evidence-current") return 91;
                var root = Path.GetFullPath(args[2]);
                var artifactRoot = Path.Combine(root, "artifacts");
                Directory.CreateDirectory(artifactRoot);
                var location = Assembly.GetExecutingAssembly().Location;
                File.AppendAllText(Path.Combine(artifactRoot, "currentness-validator-calls.log"), location + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(Path.Combine(artifactRoot, "tamper-currentness-output.flag")))
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(location)!, "tampered.marker"), "tampered", new UTF8Encoding(false));
                }
                return 0;
                """;
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), source, new UTF8Encoding(false));
            var projectPath = Path.Combine(projectRoot, "AIArena.VerificationLab.csproj");
            var restore = BoundedQaProcess.RunAsync(
                "dotnet",
                ["restore", projectPath, "--nologo", "--verbosity", "quiet"],
                root,
                TimeSpan.FromMinutes(2),
                CancellationToken.None).GetAwaiter().GetResult();
            Require(restore == BoundedQaProcessResult.Passed, "currentness fixture could not seed restored assets");
            var ignoredGeneratedTarget = Path.Combine(projectRoot, "obj", "AIArena.VerificationLab.csproj.nuget.g.targets");
            File.WriteAllText(
                ignoredGeneratedTarget,
                """
                <Project>
                  <Target Name="PoisonIgnoredRepoObj" BeforeTargets="CoreCompile">
                    <WriteLinesToFile File="$(MSBuildProjectDirectory)\..\..\artifacts\repo-obj-poison-consumed.flag" Lines="poisoned" Overwrite="true" />
                  </Target>
                </Project>
                """,
                new UTF8Encoding(false));
            var staleDll = Path.Combine(projectRoot, "bin", "Release", "net10.0", "AIArena.VerificationLab.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(staleDll)!);
            File.WriteAllText(staleDll, "ignored stale output", new UTF8Encoding(false));
            var evidence = Path.Combine(root, "artifacts", "qa", "currentness", "qa-evidence.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(evidence, "{}", new UTF8Encoding(false));

            var validator = new ReleaseQaEvidenceCurrentnessValidator();
            var productionSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Services/InAppQaInspectorCoordinator.cs"));
            Require(productionSource.Contains("\"restore\"", StringComparison.Ordinal)
                && productionSource.Contains("\"--configfile\", configPath", StringComparison.Ordinal)
                && productionSource.Contains("\"--packages\", packageRoot", StringComparison.Ordinal)
                && productionSource.Contains("\"--artifacts-path\", outputRoot", StringComparison.Ordinal)
                && productionSource.Contains("\"-p:NuGetAudit=false\"", StringComparison.Ordinal)
                && productionSource.Contains("\"--no-restore\"", StringComparison.Ordinal),
                "fresh currentness validation no longer enforces its local-only isolated restore/build flags");
            var current = validator.ValidateAsync(evidence, root, CancellationToken.None).GetAwaiter().GetResult();
            Require(current.State == QaInspectorState.Pass && current.Code == "current", $"fresh isolated currentness validation did not pass ({current.State}/{current.Code})");
            Require(!File.Exists(Path.Combine(root, "artifacts", "repo-obj-poison-consumed.flag")),
                "fresh currentness validation consumed the poisoned ignored repository obj target");
            var log = Path.Combine(root, "artifacts", "currentness-validator-calls.log");
            var firstLocation = File.ReadAllLines(log).Single();
            Require(!Path.GetFullPath(firstLocation).Equals(Path.GetFullPath(staleDll), StringComparison.OrdinalIgnoreCase)
                && firstLocation.Contains("ai-arena-qa-currentness-", StringComparison.Ordinal)
                && !File.Exists(firstLocation),
                "currentness validation trusted conventional output or failed to remove its isolated build");

            File.WriteAllText(Path.Combine(root, "artifacts", "tamper-currentness-output.flag"), "tamper", new UTF8Encoding(false));
            var tampered = validator.ValidateAsync(evidence, root, CancellationToken.None).GetAwaiter().GetResult();
            Require(tampered.State == QaInspectorState.Unavailable
                && tampered.Code == "qa.validator_tampered",
                "currentness validation did not fail closed when its isolated output changed during execution");
            var secondLocation = File.ReadAllLines(log).Last();
            Require(!File.Exists(secondLocation), "tampered isolated validator output was not cleaned safely");
        });
    }

    private static ArenaQaGateEvidence Gate(
        string id,
        ArenaQaGateOutcome outcome,
        ArenaEvidenceAssertion evidence,
        int passed,
        int failed) =>
        new(id, outcome, true, 10, new(passed, failed, 0, passed + failed), evidence);

    private static ArenaEvidenceAssertion QaObserved(string id) =>
        new(id, ArenaEvidenceState.Observed, "Recorded by bounded local QA.", "artifact:trace");

    private static ArenaEvidenceAssertion QaInferred(string id) =>
        new(id, ArenaEvidenceState.Inferred, "Bounded inference.", Basis: "Derived from a fixed local gate result.");

    private static ArenaEvidenceAssertion QaUnavailable(string id) =>
        new(id, ArenaEvidenceState.Unavailable, "Evidence unavailable.", Limitation: "No compatible local evidence was available.");

    private static ArenaQaEvidenceContract CreateQaContract()
    {
        var at = new DateTimeOffset(2036, 2, 3, 4, 5, 6, TimeSpan.Zero);
        return new(
            ArenaContractSchemas.QaEvidence,
            "qa:test",
            at,
            new string('b', 40),
            new string('a', 64),
            ArenaQaSealManifestV1.Id,
            true,
            [new("map", new string('c', 40), new string('d', 64), true)],
            at,
            at.AddMinutes(1),
            ArenaQaVerdict.Partial,
            0,
            new("Windows", "x64", "10.0.0", "10.0.100", "Release", true),
            [new("dotnet", "10.0.100")],
            [Gate("gate:core", ArenaQaGateOutcome.Pass, QaObserved("evidence:gate"), 1, 0)],
            [],
            [],
            [],
            new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider was required for this local evidence."),
            RequiredQaLimitations(),
            new(false, null, null, [], [], QaUnavailable("evidence:inspection")),
            [QaObserved("evidence:qa")]);
    }

    private static QaBundleFixture CreateAcceptanceReadyBundle(string root, string runId)
    {
        var bundlePath = Path.Combine(root, "artifacts", "qa", runId);
        Directory.CreateDirectory(Path.Combine(bundlePath, "screenshots"));
        Directory.CreateDirectory(Path.Combine(bundlePath, "automation"));
        Directory.CreateDirectory(Path.Combine(bundlePath, "logs"));
        Directory.CreateDirectory(Path.Combine(bundlePath, "metadata"));
        var screenshotBytes = CreatePngBytes(320, 240, 0x28, 0x74, 0xA1);
        var secondScreenshotBytes = CreatePngBytes(320, 240, 0x49, 0x8A, 0xB8);
        var baselineBytes = CreatePngBytes(320, 240, 0x16, 0x33, 0x4A);
        var automationBytes = Encoding.UTF8.GetBytes("{\"schema\":\"ai_arena.automation_tree.v1\",\"nodes\":[]}");
        var migrationLogBytes = Encoding.UTF8.GetBytes("AI Arena QA gate evidence\ngate=schema.explicit-v0-pack-migration\noutcome=pass\n");
        var featureMatrixBytes = Encoding.UTF8.GetBytes("{\"schema\":\"ai_arena.qa_feature_surface_matrix.v1\",\"fixture\":true}");
        File.WriteAllBytes(Path.Combine(bundlePath, "screenshots", "current.png"), screenshotBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "screenshots", "current-secondary.png"), secondScreenshotBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "screenshots", "baseline.png"), baselineBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "automation", "tree.json"), automationBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "logs", "schema.explicit-v0-pack-migration.log"), migrationLogBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "metadata", "pass-01.feature-surface-matrix.json"), featureMatrixBytes);
        File.WriteAllBytes(Path.Combine(bundlePath, "metadata", "pass-02.feature-surface-matrix.json"), featureMatrixBytes);

        var baseContract = CreateQaContract();
        var at = baseContract.StartedAtUtc.AddSeconds(10);
        var tree = baseContract.TreeFingerprint;
        var artifacts = ImmutableArray.Create(
            new ArenaQaArtifact(
                "artifact:automation",
                "automation-tree",
                "automation/tree.json",
                Hash(automationBytes),
                new(tree, at, "dark-blue", 1500, 960, 1m, "qa-inspector", null, null)),
            new ArenaQaArtifact(
                "artifact:baseline",
                "render-baseline",
                "screenshots/baseline.png",
                Hash(baselineBytes),
                null),
            new ArenaQaArtifact(
                "artifact:screenshot",
                "rendered-ui-screenshot",
                "screenshots/current.png",
                Hash(screenshotBytes),
                new(tree, at, "dark-blue", 1500, 960, 1m, "qa-inspector", "artifact:automation", "artifact:baseline")),
            new ArenaQaArtifact(
                "artifact:screenshot-secondary",
                "rendered-ui-screenshot",
                "screenshots/current-secondary.png",
                Hash(secondScreenshotBytes),
                new(tree, at, "light", 960, 700, 1.5m, "qa-inspector", "artifact:automation", null)),
            new ArenaQaArtifact(
                ArenaQaSealManifestV2.ExplicitMigrationArtifactId,
                ArenaQaSealManifestV2.ExplicitMigrationArtifactKind,
                ArenaQaSealManifestV2.ExplicitMigrationArtifactPath,
                Hash(migrationLogBytes),
                null),
            new ArenaQaArtifact(
                "artifact.pass-01.feature-surface-matrix",
                ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind,
                "metadata/pass-01.feature-surface-matrix.json",
                Hash(featureMatrixBytes),
                null),
            new ArenaQaArtifact(
                "artifact.pass-02.feature-surface-matrix",
                ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind,
                "metadata/pass-02.feature-surface-matrix.json",
                Hash(featureMatrixBytes),
                null));
        var gates = ArenaQaSealManifestV2.RequiredGateIds(ArenaQaSealManifestV2.RequiredCleanPasses)
            .Select((id, index) => id.Equals("inspection.user-acceptance", StringComparison.Ordinal)
                ? new ArenaQaGateEvidence(id, ArenaQaGateOutcome.Unavailable, true, 0, new(0, 0, 0, 0), QaUnavailable($"evidence:gate:{index}"))
                : new ArenaQaGateEvidence(
                    id,
                    ArenaQaGateOutcome.Pass,
                    true,
                    10,
                    new(1, 0, 0, 1),
                    id == ArenaQaSealManifestV2.ExplicitMigrationGateId
                        ? QaObserved($"evidence:gate:{index}") with { ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId }
                        : QaObserved($"evidence:gate:{index}")))
            .ToImmutableArray();
        var schemas = ArenaQaSealManifestV2.RequiredSchemaIds
            .Select((schema, index) => schema switch
            {
                ArenaContractSchemas.ScenarioPack => new ArenaQaSchemaCheck(
                    $"schema:check:{index}", schema, ArenaQaSealManifestV2.ScenarioPackV0Schema, ArenaQaGateOutcome.Pass,
                    QaObserved(ArenaQaSealManifestV2.ScenarioMigrationEvidenceId) with { ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId }),
                ArenaContractSchemas.BenchmarkPack => new ArenaQaSchemaCheck(
                    $"schema:check:{index}", schema, ArenaQaSealManifestV2.BenchmarkPackV0Schema, ArenaQaGateOutcome.Pass,
                    QaObserved(ArenaQaSealManifestV2.BenchmarkMigrationEvidenceId) with { ReferenceId = ArenaQaSealManifestV2.ExplicitMigrationArtifactId }),
                _ => new ArenaQaSchemaCheck(
                    $"schema:check:{index}", schema, null, ArenaQaGateOutcome.Pass, QaObserved($"evidence:schema:{index}"))
            })
            .ToImmutableArray();
        var performance = ArenaQaSealManifestV2.RequiredPerformanceMetrics
            .Select(metric => new ArenaQaPerformanceMeasurement(
                $"performance:{metric}",
                metric,
                10,
                "milliseconds",
                ArenaQaThresholdKind.Maximum,
                20,
                QaObserved($"evidence:performance:{metric}")))
            .ToImmutableArray();
        var contract = baseContract with
        {
            Id = $"qa:{runId}",
            SealManifestId = ArenaQaSealManifestV2.Id,
            CleanFullPasses = ArenaQaSealManifestV2.RequiredCleanPasses,
            Gates = gates,
            Artifacts = artifacts,
            SchemaChecks = schemas,
            Performance = performance,
            AcceptedLimitations = RequiredQaLimitations(),
            Inspection = new(
                false,
                null,
                null,
                ["artifact:screenshot", "artifact:screenshot-secondary"],
                ["artifact:automation"],
                QaUnavailable("evidence:inspection"))
        };
        var evidencePath = WriteQaContract(root, runId, contract);
        return new($"artifacts/qa/{runId}/qa-evidence.json", evidencePath);
    }

    private static ImmutableArray<ArenaQaAcceptedLimitation> RequiredQaLimitations() =>
        ArenaQaSealManifestV1.RequiredLimitations
            .Select(requirement => new ArenaQaAcceptedLimitation(
                requirement.Id,
                requirement.Summary,
                false,
                new ArenaEvidenceAssertion(
                    requirement.EvidenceId,
                    ArenaEvidenceState.Unavailable,
                    requirement.EvidenceSummary,
                    requirement.ReferenceId,
                    Basis: null,
                    Limitation: requirement.EvidenceLimitation)))
            .ToImmutableArray();

    private static QaInspectionReviewManifest CompleteReviewManifest(QaEvidenceSnapshot snapshot) =>
        new(
            "ai_arena.qa_inspection_review.v1",
            snapshot.EvidenceSha256,
            snapshot.Contract.TreeFingerprint,
            DateTimeOffset.UtcNow,
            snapshot.Artifacts
                .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
                .Select(item => new QaReviewedScreenshot(item.Artifact.Id, item.Artifact.Sha256, true))
                .ToImmutableArray());

    private static byte[] CreatePngBytes(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 0xFF;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string WriteQaContract(string root, string runId, ArenaQaEvidenceContract contract)
    {
        var directory = Path.Combine(root, "artifacts", "qa", runId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "qa-evidence.json");
        File.WriteAllText(path, ArenaContractCodec.Serialize(contract, indented: true), new UTF8Encoding(false));
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void SealQaEvidenceFile(string evidencePath)
    {
        var json = File.ReadAllText(evidencePath, Encoding.UTF8);
        Require(ArenaContractCodec.TryDeserialize<ArenaQaEvidenceContract>(json, out var contract, out var issues),
            $"acceptance fixture could not read evidence: {string.Join(", ", issues.Select(item => item.Code))}");
        var accepted = contract! with
        {
            Verdict = ArenaQaVerdict.Sealed,
            Gates = contract.Gates.Select(gate => gate.Id.Equals("inspection.user-acceptance", StringComparison.Ordinal)
                ? gate with
                {
                    Outcome = ArenaQaGateOutcome.Pass,
                    Tests = new ArenaQaTestCounts(1, 0, 0, 1),
                    Evidence = QaObserved("evidence:inspection-acceptance")
                }
                : gate).ToImmutableArray(),
            AcceptedLimitations = contract.AcceptedLimitations
                .Select(item => item with { UserAccepted = true })
                .ToImmutableArray(),
            Inspection = new(
                true,
                contract.CompletedAtUtc.AddMinutes(1),
                contract.TreeFingerprint,
                contract.Inspection.ScreenshotArtifactIds,
                contract.Inspection.AutomationArtifactIds,
                QaObserved("evidence:inspection-accepted"))
        };
        File.WriteAllText(evidencePath, ArenaContractCodec.Serialize(accepted, indented: true), new UTF8Encoding(false));
    }

    private static void WithQaRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-qa-inspector-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-qa-inspector-tests")) + Path.DirectorySeparatorChar;
            if (full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private sealed record QaBundleFixture(string RelativeEvidencePath, string EvidencePath);

    private sealed class FakeQaCurrentnessValidator(QaCurrentnessResult result) : IQaEvidenceCurrentnessValidator
    {
        public int CallCount { get; private set; }

        public Task<QaCurrentnessResult> ValidateAsync(string evidencePath, string repositoryRoot, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeQaSuiteRunner : IQaSuiteProcessRunner
    {
        public int CallCount { get; private set; }
        public Func<bool>? CancelOnStart { get; set; }

        public async Task<QaSuiteRunResult> RunAsync(
            QaSuiteDefinition suite,
            string repositoryRoot,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report("Running fixed fake suite");
            if (CancelOnStart is not null)
            {
                Require(CancelOnStart(), "coordinator did not expose an active cancellation boundary");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new(QaInspectorState.Pass, "qa.suite_passed", "Passed.", suite.Commands.Length, 0, suite.Commands.Length, 1);
        }
    }

    private sealed class FakeQaAcceptanceRunner(Action<string>? onAccept = null) : IQaInspectionAcceptanceRunner
    {
        public int CallCount { get; private set; }
        public string? ReviewedManifestPath { get; private set; }
        public string? ReviewedManifestSha256 { get; private set; }

        public Task<QaAcceptanceResult> AcceptAsync(
            string evidencePath,
            string repositoryRoot,
            string reviewedManifestPath,
            string reviewedManifestSha256,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ReviewedManifestPath = reviewedManifestPath;
            ReviewedManifestSha256 = reviewedManifestSha256;
            onAccept?.Invoke(evidencePath);
            return Task.FromResult(new QaAcceptanceResult(QaInspectorState.Pass, "qa.acceptance_recorded", "Accepted."));
        }
    }

    private sealed class FakeQaClipboard : IQaInspectorClipboard
    {
        public string Text { get; private set; } = "";

        public bool TrySetText(string text)
        {
            Text = text;
            return true;
        }
    }
}
