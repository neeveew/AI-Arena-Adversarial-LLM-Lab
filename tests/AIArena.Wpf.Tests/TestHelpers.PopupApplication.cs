using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using AIArena.Core.Persistence;
using AIArena.Wpf;

internal static partial class Program
{
    private sealed class SharedPopupFixtureApplication
    {
        private ExceptionDispatchInfo? dispatcherFailure;
        private readonly DispatcherOperation startupOperation;
        private readonly Delegate? originalProtectSecret = SessionStore.ProtectSecret;
        private readonly Delegate? originalUnprotectSecret = SessionStore.UnprotectSecret;
        private bool startupObserved;

        internal App Application { get; }

        internal SharedPopupFixtureApplication()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var posted = new List<DispatcherOperation>();
            DispatcherHookEventHandler capture = (_, args) => posted.Add(args.Operation);
            dispatcher.Hooks.OperationPosted += capture;
            try { Application = new App(); }
            finally { dispatcher.Hooks.OperationPosted -= capture; }

            // .NET 10 Application's constructor posts one Send-priority startup
            // callback, even without Run(). Abort precisely that operation before
            // any pump: this is a compiled-resource fixture, not an app startup.
            // Fail closed if the framework changes rather than canceling other work.
            // https://github.com/dotnet/wpf/blob/release/10.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Application.cs
            Require(posted.Count == 1 && posted[0].Priority == DispatcherPriority.Send
                    && posted[0].Status == DispatcherOperationStatus.Pending,
                $"Unexpected WPF Application constructor dispatcher shape ({string.Join(", ", posted.Select(item => $"{item.Priority}:{item.Status}"))}).");
            startupOperation = posted[0];
            Require(startupOperation.Abort() && startupOperation.Status == DispatcherOperationStatus.Aborted,
                "The fixture could not isolate WPF Application startup before resource loading.");
            Application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Application.Startup += (_, _) => startupObserved = true;
            dispatcher.UnhandledException += (_, args) =>
            {
                dispatcherFailure ??= ExceptionDispatchInfo.Capture(args.Exception);
                // Route failure back to the console harness after the dispatcher
                // drain; production crash handling and modal UI are not installed.
                args.Handled = true;
            };
        }

        internal void AssertIsolatedStartup(Window host)
        {
            ThrowDispatcherFailure();
            Require(startupOperation.Status == DispatcherOperationStatus.Aborted && !startupObserved
                    && Application.StartupUri?.OriginalString == "Shell/MainWindow.xaml"
                    && Application.Windows.Count == 1 && ReferenceEquals(Application.Windows[0], host)
                    && Equals(SessionStore.ProtectSecret, originalProtectSecret)
                    && Equals(SessionStore.UnprotectSecret, originalUnprotectSecret),
                "The resource fixture must retain its isolated startup, original persistence delegates, and only its own host window.");
        }

        internal void ThrowDispatcherFailure() => dispatcherFailure?.Throw();
    }
}
