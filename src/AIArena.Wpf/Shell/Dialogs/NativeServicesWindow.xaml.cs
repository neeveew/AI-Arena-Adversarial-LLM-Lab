using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AIArena.Wpf;

public partial class NativeServicesWindow : Window
{
    private readonly NativeServicesCoordinator coordinator;

    internal NativeServicesWindow(Window owner, NativeServicesCoordinator coordinator)
    {
        this.coordinator = coordinator;
        InitializeComponent();
        Owner = owner;
        DialogChrome.ImportOwnerResources(owner, this);
        DataContext = coordinator;
        DialogChrome.ApplyResponsiveBounds(this, owner.ActualWidth, owner.ActualHeight,
            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        Loaded += (_, _) => ConnectButton.Focus();
        Closed += (_, _) => coordinator.Models.StopObserving();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.ConnectAsync();
        if (coordinator.Operation is not null) await coordinator.ReconcileAsync();
    }

    private async void LoadModel_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.Models.LoadSelectedModelAsync();
        await ObserveModelIfVisibleAsync();
    }

    private async void ConfirmModelLoad_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.Models.ConfirmLoadAsync();
        await ObserveModelIfVisibleAsync();
    }
    private async void UnloadModel_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.Models.UnloadSelectedModelAsync();
        await ObserveModelIfVisibleAsync();
    }

    private async void StartInference_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.Models.StartInferenceAsync();
        await ObserveModelIfVisibleAsync();
    }

    private async void RetryModelRequest_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.Models.RetryPendingRequestAsync();
        await ObserveModelIfVisibleAsync();
    }

    private async Task ObserveModelIfVisibleAsync()
    {
        if (IsVisible && coordinator.Models.CanObserve) await coordinator.Models.ObserveAsync();
    }

    private async void ObserveModel_Click(object sender, RoutedEventArgs e) => await coordinator.Models.ObserveAsync();
    private void StopObservingModel_Click(object sender, RoutedEventArgs e) => coordinator.Models.StopObserving();
    private async void RefreshModel_Click(object sender, RoutedEventArgs e) => await coordinator.Models.RefreshAsync();
    private async void CancelModel_Click(object sender, RoutedEventArgs e) => await coordinator.Models.CancelAsync();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(coordinator.OutputDirectory);
            var picker = new SaveFileDialog
            {
                Title = "Choose a new diagnostic bundle ZIP",
                Filter = "ZIP archive (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true,
                InitialDirectory = coordinator.OutputDirectory,
                FileName = $"native-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip"
            };
            picker.FileOk += (_, args) =>
            {
                if (File.Exists(picker.FileName) || Directory.Exists(picker.FileName))
                {
                    args.Cancel = true;
                    MessageBox.Show(this, "Choose a new filename. Existing files cannot be replaced.",
                        "Diagnostic bundle", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            if (picker.ShowDialog(this) == true) coordinator.Destination = picker.FileName;
        }
        catch (Exception)
        {
            MessageBox.Show(this, "The destination picker could not be opened. Enter a new ZIP path in an existing folder.",
                "Diagnostic bundle", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.CreateBundleAsync(coordinator.Destination);
        if (IsVisible && coordinator.CanObserve) await coordinator.ObserveAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await coordinator.RetryPendingRequestAsync();
        if (IsVisible && coordinator.CanObserve) await coordinator.ObserveAsync();
    }

    private async void Observe_Click(object sender, RoutedEventArgs e) => await coordinator.ObserveAsync();
    private void StopObserving_Click(object sender, RoutedEventArgs e) => coordinator.StopObserving();
    private async void Reconcile_Click(object sender, RoutedEventArgs e) => await coordinator.ReconcileAsync();
    private async void CancelOperation_Click(object sender, RoutedEventArgs e) => await coordinator.CancelOperationAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
