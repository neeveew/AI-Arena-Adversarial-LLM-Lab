using AIArena.Wpf;
using AIArena.Wpf.Services;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static partial class Program
{
    static void NativeServicesWindowRendersAccessibleActions() =>
        RunStaTest(() => RenderNativeServicesWindowFixture(Environment.GetEnvironmentVariable("AIARENA_NATIVE_UI_OUTPUT")));

    static int RunNativeServicesWindowSmoke(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        RunStaTest(() => RenderNativeServicesWindowFixture(output));
        Console.WriteLine("Native services UI: standard and compact off-screen renders passed; fake client only.");
        return 0;
    }

    private static void RenderNativeServicesWindowFixture(string? outputDirectory)
    {
        if (outputDirectory is not null) Directory.CreateDirectory(outputDirectory);
        var temporary = Directory.CreateTempSubdirectory("AIArena-native-services-ui-");
        var owner = new Window
        {
            Width = 1200,
            Height = 1000,
            Left = -30000,
            Top = -30000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Content = new Border()
        };
        AttachArenaPresentationResources(owner);
        var client = new NativeWindowFixtureClient();
        using var coordinator = new NativeServicesCoordinator(client, new ApplicationStatusCenter(), temporary.FullName);
        NativeServicesWindow? window = null;
        try
        {
            owner.Show();
            owner.UpdateLayout();
            coordinator.Show(owner);
            window = owner.OwnedWindows.OfType<NativeServicesWindow>().Single();
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
            NativeWindowDrain(window);

            Require(((TabControl)window.FindName("NativeServicesTabs")).SelectedIndex == 0,
                "Native services should initially open Models and inference.");
            ((TabItem)window.FindName("DiagnosticsTab")).IsSelected = true;
            NativeWindowDrain(window);
            var buttons = NativeWindowDescendants<Button>(window).ToArray();
            var actionNames = new[]
            {
                "Connect or refresh native services",
                "Browse for a new diagnostic ZIP file",
                "Create diagnostic bundle",
                "Retry the same unconfirmed request",
                "Observe native operation progress",
                "Stop observing without canceling",
                "Refresh and reconcile native operation status",
                "Request native operation cancellation",
                "Close native services"
            };
            foreach (var name in actionNames)
                Require(buttons.Count(button => AutomationProperties.GetName(button) == name) == 1,
                    "native service action lacks one accessible rendered button: " + name);

            Button Action(string name) => buttons.Single(button => AutomationProperties.GetName(button) == name);
            var connect = Action(actionNames[0]);
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NativeWindowDrain(window);
            Require(client.ConnectCount == 1 && coordinator.CanCreateBundle,
                "Connect should call only the fake client and enable diagnostic creation");
            Require(coordinator.HealthSummary.Contains("healthy", StringComparison.Ordinal)
                    && coordinator.ModelsSummary.Contains("2", StringComparison.Ordinal),
                "connection evidence should flow through the production coordinator");

            Action("Create diagnostic bundle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NativeWindowDrain(window);
            Require(client.CreateCount == 1 && client.WaitCount > 0 && coordinator.IsObserving,
                "Create should bind the accepted fake operation and observe its progress");
            Require(Action("Stop observing without canceling").IsEnabled
                    && Action("Request native operation cancellation").IsEnabled
                    && !((TextBox)window.FindName("DestinationBox")).IsEnabled,
                "running work should expose distinct stop/cancel actions while locking its destination");
            var operationId = (TextBox)window.FindName("OperationIdBox");
            Require(operationId.Text == client.Operation.OperationId && operationId.IsReadOnly,
                "the accepted operation identity should be rendered in a readable field");

            foreach (var viewport in new[] { (Name: "standard", Width: 850, Height: 800), (Name: "compact", Width: 680, Height: 640) })
            {
                window.Width = viewport.Width;
                window.Height = viewport.Height;
                NativeWindowDrain(window);
                var scroll = (ScrollViewer)window.FindName("DiagnosticsScrollViewer");
                scroll.ScrollToTop();
                NativeWindowDrain(window);
                Require(NativeWindowIntersects(connect, window), "connection action should stay visible at " + viewport.Name);
                CaptureNativeWindow(window, outputDirectory, viewport.Name + "-connection.png");

                scroll.ScrollToBottom();
                NativeWindowDrain(window);
                foreach (var name in actionNames.Skip(4))
                {
                    var button = Action(name);
                    Require(button.ActualWidth > 0 && button.ActualHeight >= 30
                            && NativeWindowIntersects(button, window),
                        "operation action should remain rendered and reachable at " + viewport.Name + ": " + name);
                }
                Require(operationId.ActualHeight > 0
                        && NativeWindowDescendants<TextBlock>(window).Any(text =>
                            AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite
                            && !string.IsNullOrWhiteSpace(text.Text)),
                    "operation status should render alongside a polite live status region");
                CaptureNativeWindow(window, outputDirectory, viewport.Name + "-operation.png");
            }

            Action("Stop observing without canceling").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            NativeWindowDrain(window);
            Require(!coordinator.IsObserving && client.CancelCount == 0 && coordinator.Operation?.State == "running",
                "Stop observing must not request cancellation or invent a terminal operation");
            RenderNativeInferenceFixture(window, coordinator, client, outputDirectory);
            window.Close();
            window = null;
            coordinator.Show(owner);
            window = owner.OwnedWindows.OfType<NativeServicesWindow>().Single();
            NativeWindowDrain(window);
            Require(((TextBox)window.FindName("NativeOutputBox")).Text.Contains("Fixture partial response", StringComparison.Ordinal),
                "reopening should preserve the inference output independently of diagnostics");
            ((TabItem)window.FindName("DiagnosticsTab")).IsSelected = true;
            NativeWindowDrain(window);
            Require(((TextBox)window.FindName("OperationIdBox")).Text == client.Operation.OperationId,
                "reopening the modeless window should preserve the coordinator's accepted operation");
        }
        finally
        {
            coordinator.StopObserving();
            window?.Close();
            owner.Close();
            temporary.Delete(recursive: true);
        }
    }

    private static void RenderNativeInferenceFixture(
        NativeServicesWindow window,
        NativeServicesCoordinator coordinator,
        NativeWindowFixtureClient client,
        string? outputDirectory)
    {
        ((TabItem)window.FindName("ModelsTab")).IsSelected = true;
        NativeWindowDrain(window);
        var picker = (ComboBox)window.FindName("NativeModelPicker");
        Require(picker.Items.Count == 2, "the native model catalog should populate the actual picker");
        picker.SelectedIndex = 0;
        var prompt = (TextBox)window.FindName("NativePromptBox");
        var output = (TextBox)window.FindName("NativeOutputBox");
        prompt.Text = "Explain what the native service fixture is demonstrating.";
        NativeWindowDrain(window);
        var selectedModel = (NativeModelOption)picker.SelectedItem;
        var selectedText = NativeWindowDescendants<TextBlock>(picker)
            .Where(text => text.IsVisible && text.ActualWidth > 0)
            .Select(text => text.Text)
            .ToArray();
        Require(selectedText.Any(text => text.Contains(selectedModel.DisplayName, StringComparison.Ordinal))
                && selectedText.All(text => !text.Contains("NativeModelOption {", StringComparison.Ordinal)),
            "the selected native model must render its display name rather than the record representation");
        Require(output.IsReadOnly && ((Button)window.FindName("StartNativeInferenceButton")).IsEnabled,
            "a serviceable native model and prompt should enable inference with a read-only output surface");
        Require(!((Button)window.FindName("ConfirmNativeModelLoadButton")).IsVisible && client.ConfirmCount == 0,
            "load confirmation must not be displayed or executed without an explicit capacity warning");
        ((Button)window.FindName("StartNativeInferenceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        NativeWindowAwait(window, () => client.InferenceStartCount == 1 && coordinator.Models.IsObserving
            && output.Text.Contains("Fixture partial response", StringComparison.Ordinal),
            "starting inference should observe and display the partial native response");
        Require(!picker.IsEnabled && !prompt.IsEnabled && client.LastInferencePrompt == prompt.Text,
            "the model and exact submitted prompt should stay locked while inference is active");

        var fixedActions = new[]
        {
            "StartNativeInferenceButton", "RetryNativeModelButton", "ObserveNativeModelButton",
            "StopObservingNativeModelButton", "RefreshNativeModelButton", "CancelNativeModelButton"
        };
        foreach (var name in fixedActions.Concat(new[] { "LoadNativeModelButton", "UnloadNativeModelButton" }))
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName((Button)window.FindName(name))),
                "native model action must have a stable accessible name: " + name);
        foreach (var viewport in new[] { (Name: "standard", Width: 850, Height: 800), (Name: "compact", Width: 680, Height: 640) })
        {
            window.Width = viewport.Width;
            window.Height = viewport.Height;
            NativeWindowDrain(window);
            var scroll = (ScrollViewer)window.FindName("ModelsScrollViewer");
            foreach (var scrollToResponse in new[] { false, true })
            {
                if (scrollToResponse) scroll.ScrollToBottom(); else scroll.ScrollToTop();
                NativeWindowDrain(window);
                foreach (var name in fixedActions)
                {
                    var action = (Button)window.FindName(name);
                    Require(action.ActualHeight >= 30 && NativeWindowIntersects(action, window),
                        "inference actions should remain visible while its content scrolls: " + viewport.Name + " " + name);
                }
                Require(NativeWindowIntersects((FrameworkElement)window.FindName("NativeInferenceMessage"), window),
                    "inference status should remain visible with its action controls");
                if (!scrollToResponse) Require(NativeWindowIntersects(picker, window), "model picker should render at the top of its workspace");
                else Require(NativeWindowIntersects(output, window), "inference response should remain reachable by scrolling");
                CaptureNativeWindow(window, outputDirectory, viewport.Name + (scrollToResponse ? "-inference-response.png" : "-native-models.png"));
            }
        }

        ((Button)window.FindName("CancelNativeModelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        NativeWindowAwait(window, () => client.InferenceCancelCount == 1 && !coordinator.Models.IsObserving,
            "cancel should reconcile the dedicated inference generation");
        Require(client.CancelCount == 0 && coordinator.Operation?.OperationId == client.Operation.OperationId
                && coordinator.Operation.State == "running",
            "inference cancellation must not cancel or overwrite the diagnostic operation");
        Require(output.Text.Contains("Fixture partial response", StringComparison.Ordinal),
            "canceled inference should retain its observed partial output");
    }

    private static void NativeWindowAwait(Window window, Func<bool> condition, string message)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(3))
        {
            NativeWindowDrain(window);
            if (condition()) return;
            Thread.Sleep(10);
        }
        Require(condition(), message);
    }
    private static void NativeWindowDrain(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static bool NativeWindowIntersects(FrameworkElement element, Window window)
    {
        var bounds = element.TransformToAncestor(window)
            .TransformBounds(new Rect(new Point(), element.RenderSize));
        bounds.Intersect(new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        return element.IsVisible && !bounds.IsEmpty && bounds.Width > 4 && bounds.Height > 4;
    }

    private static IEnumerable<T> NativeWindowDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T typed) yield return typed;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var descendant in NativeWindowDescendants<T>(VisualTreeHelper.GetChild(root, index)))
                yield return descendant;
    }

    private static void CaptureNativeWindow(Window window, string? outputDirectory, string filename)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var distinct = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4)
            distinct.Add(pixels[offset] | pixels[offset + 1] << 8 | pixels[offset + 2] << 16 | pixels[offset + 3] << 24);
        Require(distinct.Count > 32, "native service window render should contain material text and control detail");
        if (outputDirectory is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(Path.Combine(outputDirectory, filename), FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private sealed class NativeWindowFixtureClient : INativeServicesClient, INativeModelClient, INativeInferenceClient
    {
        internal int ConnectCount { get; private set; }
        internal int CreateCount { get; private set; }
        internal int WaitCount { get; private set; }
        internal int CancelCount { get; private set; }
        internal int InferenceStartCount { get; private set; }
        internal int InferenceCancelCount { get; private set; }
        internal int ConfirmCount { get; private set; }
        internal string LastInferencePrompt { get; private set; } = "";
        private const string ServiceableModelId = "fixture-native-model";
        private readonly IReadOnlyList<NativeModelOption> modelItems =
        [
            new(ServiceableModelId, "ready", true, true, "healthy", "cpu", "", "ready", null, false, false),
            new("fixture-unloaded-model", "unloaded", false, false, "unknown", "cpu", "", "", null, false, false)
        ];
        private NativeGenerationSnapshot generation = new(
            "01990000-0000-7000-8000-000000000002", ServiceableModelId, "running", false, 1,
            "Fixture partial response: the selected native model is producing text. This retained response is still incomplete.", "");
        internal NativeOperationSnapshot Operation { get; } = new(
            "01990000-0000-7000-8000-000000000001", "running", 2,
            "Collecting service diagnostics. Progress measurement is unavailable.");

        public Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            return Task.FromResult(new NativeConnectionSnapshot("Native services are healthy.",
                "2 models are available; routing is unchanged.", "Diagnostic and model operations are available.",
                ["diagnostics.bundle.create", "inference.models.state", "inference.models.preflight",
                 "inference.models.load", "inference.models.eject", "inference.models.load.confirm",
                 "operation.state", "operation.wait", "operation.cancel",
                 "inference.generate.start", "inference.generate.read", "inference.generate.cancel"], true));
        }

        public Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string idempotencyKey, CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(Operation);
        }

        public Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken) => Task.FromResult(Operation);

        public async Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken)
        {
            WaitCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Operation;
        }

        public Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.FromResult(Operation with { State = "canceled", Transition = 3 });
        }

        public Task<NativeModelCatalog> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new NativeModelCatalog(modelItems, ServiceableModelId, false));

        public Task<NativeModelPreflight> PreflightModelAsync(string deploymentId, CancellationToken cancellationToken) =>
            Task.FromResult(new NativeModelPreflight(deploymentId, "Fixture capacity is available.", false, "fixture-plan"));

        public Task<NativeOperationSnapshot> LoadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(new NativeOperationSnapshot("01990000-0000-7000-8000-000000000003", "succeeded", 2, "Fixture model loaded."));

        public Task<NativeOperationSnapshot> UnloadModelAsync(string deploymentId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(new NativeOperationSnapshot("01990000-0000-7000-8000-000000000004", "succeeded", 2, "Fixture model unloaded."));

        public Task<NativeOperationSnapshot> ConfirmModelLoadAsync(string operationId, string planDigest, string capacitySampleId,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            ConfirmCount++;
            return Task.FromResult(new NativeOperationSnapshot(operationId, "succeeded", 3, "Fixture model load confirmed."));
        }

        public Task<NativeGenerationSnapshot> StartInferenceAsync(string deploymentId, string prompt, int maximumTokens,
            double temperature, string idempotencyKey, CancellationToken cancellationToken)
        {
            InferenceStartCount++;
            LastInferencePrompt = prompt;
            Require(deploymentId == ServiceableModelId, "the inference request must use the exact selected native deployment");
            return Task.FromResult(generation);
        }

        public Task<NativeGenerationSnapshot> ReadInferenceAsync(string operationId, CancellationToken cancellationToken) =>
            Task.FromResult(generation);

        public Task<NativeGenerationSnapshot> CancelInferenceAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken)
        {
            Require(operationId == generation.OperationId, "inference cancel must target its generation identity");
            InferenceCancelCount++;
            generation = generation with { State = "canceled", IsTerminal = true, Sequence = 2 };
            return Task.FromResult(generation);
        }
    }
}