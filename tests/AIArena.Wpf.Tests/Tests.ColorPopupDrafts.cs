using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIArena.Wpf.Services;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    static void ColorPopupDraftSurvivesInvalidInput()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            fixture.HexInput.Text = "zzzzzz";
            fixture.Choose("Apply");
            Require(PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision
                    && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision == fixture.OtherRevision,
                "Invalid color input must not save either session.");
            Require(popup.IsOpen && fixture.HexInput.Text == "zzzzzz" && fixture.HexInput.IsEnabled,
                "Invalid color input must retain its open, editable draft for correction.");
            Require(AutomationProperties.GetItemStatus(fixture.HexInput).Contains("#RRGGBB", StringComparison.Ordinal),
                "Invalid input must have accessible inline feedback.");
            CaptureColorDraftPopup(popup, "invalid-color");
            fixture.HexInput.Text = "aabbcc";
            Require(AutomationProperties.GetItemStatus(fixture.HexInput).Length == 0,
                "Editing an invalid draft must clear obsolete feedback.");
            fixture.Choose("Apply");
            Require(!popup.IsOpen && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.Engine.Agents[0].AccentColor == "#AABBCC",
                "Correcting the same open draft must save the normalized color.");
        }));
    }

    static void ColorPopupDraftSurvivesPrimarySaveFailure()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var choice in ColorPopupChoices)
            {
                using var fixture = new ColorPopupFixture();
                var popup = fixture.Open();
                fixture.State.ManageBusy = true;
                fixture.State.SaveFails = true;
                fixture.State.SaveFailure = new System.IO.IOException(@"private C:\secret\token sk-test-secret");
                fixture.Choose(choice);
                var expected = choice == "Reset" ? "" : choice == "Apply" ? "#778899" : AgentAccentService.PresetOptions[0].Hex;
                Require(fixture.State.Errors.Count == 1 && fixture.State.Refreshes.Count == 0
                        && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                    "A genuine primary save failure must remain failed without a snapshot change.");
                Require(popup.IsOpen && fixture.HexInput.Text == expected && fixture.HexInput.IsEnabled && fixture.Action("Apply").IsEnabled,
                    "A primary save failure must retain the open attempted value and recover its editing controls.");
                var message = AutomationProperties.GetItemStatus(fixture.HexInput);
                Require(message.Contains("AA-SETTINGS-IO", StringComparison.Ordinal)
                        && !message.Contains("secret", StringComparison.OrdinalIgnoreCase), "Save feedback must remain privacy-safe.");
                if (choice == "Apply") CaptureColorDraftPopup(popup, "failed-save");
                fixture.State.SaveFails = false;
                fixture.Choose("Apply");
                Require(!popup.IsOpen && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision + 1
                        && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.Engine.Agents[0].AccentColor == expected,
                    "Retrying the retained value must commit exactly one change and close the editor.");
            }
        }));
    }

    static void ColorPopupDraftPendingSaveBlocksDuplicateChoices()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.State.ManageBusy = true;
            fixture.State.SaveFails = true;
            fixture.State.BeforeSaveAsync = () => { entered.TrySetResult(); return release.Task; };
            try
            {
                fixture.StartChoice("Apply");
                PumpDocumentImportTask(entered.Task);
                Require(popup.IsOpen && !fixture.HexInput.IsEnabled && !fixture.Action("Apply").IsEnabled,
                    "The editor must remain visible with disabled choices during its own save.");
                fixture.StartChoice("Preset");
                fixture.HexInput.Text = "#ABCDEF";
                Require(popup.IsOpen && fixture.State.SaveAttempts == 1,
                    "A duplicate choice must not close the editor or start another save.");
            }
            finally { release.TrySetResult(); PumpDocumentImportTask(fixture.State.LastOperation); PumpLifecycleUi(); }
            Require(popup.IsOpen && fixture.HexInput.IsEnabled && fixture.HexInput.Text == "#ABCDEF"
                    && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                "A failed pending save must recover controls and retain a newer draft without writing it.");
        }));
    }

    static void ColorPopupDraftRetiredDuringSaveDoesNotReopen()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.State.ManageBusy = true;
            fixture.State.SaveFails = true;
            fixture.State.BeforeSaveAsync = () => { entered.TrySetResult(); return release.Task; };
            try
            {
                fixture.StartChoice("Apply");
                PumpDocumentImportTask(entered.Task);
                fixture.State.Active = fixture.Other;
                fixture.State.Coordinator.ClearControls();
                fixture.Host.Content = fixture.CreateCard();
                fixture.Host.UpdateLayout();
                Require(!popup.IsOpen, "Retiring the source card must still close a pending editor.");
            }
            finally { release.TrySetResult(); PumpDocumentImportTask(fixture.State.LastOperation); PumpLifecycleUi(); }
            Require(!popup.IsOpen && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision == fixture.OtherRevision,
                "Failed work from a retired editor must not reopen it or modify the current session.");
            var current = fixture.Open();
            Require(current.IsOpen, "The current session's editor must remain usable after retirement.");
        }));
    }

    static void ColorPopupDraftCommittedChangeClosesDespiteRefreshFailure()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            fixture.State.RefreshFails = true;
            fixture.Choose("Apply");
            Require(!popup.IsOpen && fixture.State.Errors.Count == 0
                    && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision + 1,
                "A committed change with optional refresh failure must close and stay successful, without inviting duplicate saves.");
        }));
    }

    static void ColorPopupDraftPresentationFailureDoesNotPreventSave()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            var descriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(TextBox));
            var injected = false;
            EventHandler failedView = (_, _) =>
            {
                if (!injected && !fixture.HexInput.IsEnabled) { injected = true; throw new InvalidOperationException("private view failure"); }
            };
            descriptor.AddValueChanged(fixture.HexInput, failedView);
            try { fixture.Choose("Apply"); }
            finally { descriptor.RemoveValueChanged(fixture.HexInput, failedView); }
            Require(injected && !popup.IsOpen && fixture.State.Errors.Count == 0
                    && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision + 1,
                "An optional pending-control view failure must not prevent or misreport the requested save.");
        }));
    }

    private static void CaptureColorDraftPopup(Popup popup, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AIARENA_COLOR_QA_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var root = System.IO.Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var fullDirectory = System.IO.Path.GetFullPath(directory);
        Require(fullDirectory.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Popup capture output must stay inside the current workspace.");
        var visual = (FrameworkElement)popup.Child;
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        System.IO.Directory.CreateDirectory(fullDirectory);
        using var file = new System.IO.FileStream(System.IO.Path.Combine(fullDirectory, name + ".png"), System.IO.FileMode.CreateNew);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }
}
