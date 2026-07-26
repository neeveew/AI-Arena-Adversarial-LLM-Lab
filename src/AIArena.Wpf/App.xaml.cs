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

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
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
