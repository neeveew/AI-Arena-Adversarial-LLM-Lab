using System.Configuration;
using System.Data;
using System.Windows;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SearxngSupervisorService? searxngSupervisor;
    private readonly CancellationTokenSource shutdownCancellation = new();

    /// <summary>0 until a crash has been shown to the reader; see InstallCrashHandlers.</summary>
    private int crashReported;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Installed first: a failure during the rest of startup should still
            // leave a report rather than closing the window silently.
            InstallCrashHandlers();

            // Protect provider API tokens at rest with DPAPI before any snapshot I/O happens.
            SessionStore.ProtectSecret = SecretProtection.Protect;
            SessionStore.UnprotectSecret = SecretProtection.Unprotect;
            searxngSupervisor = new SearxngSupervisorService();
            base.OnStartup(e);
        }
        catch
        {
            var supervisor = Interlocked.Exchange(ref searxngSupervisor, null);
            ShutdownServices(shutdownCancellation, supervisor);
            throw;
        }
    }

    internal Task<SearxngSupervisorStatus> EnsureInternetSearchAsync(CancellationToken cancellationToken = default)
    {
        return searxngSupervisor?.EnsureStartedAsync(cancellationToken)
            ?? Task.FromResult(new SearxngSupervisorStatus(
                false,
                false,
                false,
                SearxngSupervisorService.ResolveBaseUri(null),
                "Local search backend is not initialized."));
    }

    internal void StopInternetSearch()
    {
        searxngSupervisor?.Stop();
    }

    internal Task<InternetDiagnosticsReport> TestInternetAsync(CancellationToken cancellationToken = default)
    {
        if (searxngSupervisor is not null)
        {
            return searxngSupervisor.RunDiagnosticsAsync(cancellationToken);
        }

        var backend = new SearxngSupervisorStatus(
            false,
            false,
            false,
            SearxngSupervisorService.ResolveBaseUri(null),
            "Internet diagnostics are not initialized.");
        return Task.FromResult(new InternetDiagnosticsReport(
            backend,
            new InternetSearchDiagnostic(false, TimeSpan.Zero, 0, null, null, backend.Message),
            new InternetFetchDiagnostic(false, TimeSpan.Zero, null, backend.Message)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var supervisor = Interlocked.Exchange(ref searxngSupervisor, null);
        try
        {
            ShutdownServices(shutdownCancellation, supervisor);
        }
        finally
        {
            base.OnExit(e);
        }
    }

    /// <summary>
    /// Before this, an unhandled exception in a release build closed the window
    /// and left nothing behind - Debug.WriteLine is compiled out of the build
    /// people actually run, so there was no dialog, no log and no way to say
    /// what had happened.
    ///
    /// The dispatcher case shuts down rather than continuing: the app already
    /// died there, and exiting through OnExit at least disposes the search
    /// backend and the control plane instead of abandoning them.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var path = CrashReporter.Write("Dispatcher", args.Exception);
            args.Handled = true;

            // Only the first failure gets a dialog and a shutdown. The message
            // box is modal and pumps messages, and Shutdown runs OnExit on this
            // same dispatcher, so anything failing during either would re-enter
            // here: a second box stacked on the first, another shutdown asked
            // for, and a reader holding down Enter on error dialogs while the
            // app dies. Later failures still leave a report.
            if (Interlocked.Exchange(ref crashReported, 1) == 0)
            {
                ReportToUser(args.Exception, path);
                Shutdown(1);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // Nothing can stop this one; the point is only that it leaves a trace.
            CrashReporter.Write("AppDomain", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashReporter.Write("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    private static void ReportToUser(Exception exception, string? reportPath)
    {
        try
        {
            var where = reportPath is null
                ? "A crash report could not be written."
                : $"A report was saved to:{Environment.NewLine}{reportPath}";
            MessageBox.Show(
                $"AI Arena has to close.{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}{where}",
                "AI Arena",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If even the message box fails the report is already on disk, which
            // is the part that matters.
        }
    }

    internal static void ShutdownServices(CancellationTokenSource shutdownCancellation, IDisposable? service)
    {
        ArgumentNullException.ThrowIfNull(shutdownCancellation);
        try
        {
            shutdownCancellation.Cancel();
        }
        catch
        {
            // Cancellation callbacks are outside the app's control. App shutdown
            // must still dispose the owned service and cancellation source.
        }

        try
        {
            service?.Dispose();
        }
        catch
        {
            // Process/service cleanup is best-effort during application exit.
        }

        try
        {
            shutdownCancellation.Dispose();
        }
        catch
        {
        }
    }

}
