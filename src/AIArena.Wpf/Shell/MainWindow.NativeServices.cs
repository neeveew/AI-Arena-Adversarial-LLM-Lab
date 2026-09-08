using System.IO;
using System.Windows;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow
{
    private NativeServicesCoordinator? nativeServices;

    private void OpenNativeServicesButton_Click(object sender, RoutedEventArgs e)
    {
        nativeServices ??= new NativeServicesCoordinator(
            new NativeControlClient(),
            ShellTopBar.Presentation.StatusCenter,
            Path.Combine(_coreSessionStore.DataRoot, "exports", "native-services"));
        nativeServices.Show(this);
    }
}