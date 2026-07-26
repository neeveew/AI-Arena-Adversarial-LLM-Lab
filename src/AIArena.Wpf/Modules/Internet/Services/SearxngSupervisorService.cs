using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Services;

namespace AIArena.Wpf.Services;

public sealed record SearxngSupervisorStatus(
    bool Started,
    bool AlreadyRunning,
    bool PayloadFound,
    Uri BaseUri,
    string Message,
    string PayloadPath = "",
    string PayloadVersion = "");

public sealed record InternetSearchDiagnostic(
    bool Ok,
    TimeSpan Latency,
    int ResultCount,
    int? ResponsiveEngineCount,
    int? UnresponsiveEngineCount,
    string Error);

public sealed record InternetFetchDiagnostic(
    bool Ok,
    TimeSpan Latency,
    Uri? FinalUri,
    string Error);

public sealed record InternetDiagnosticsReport(
    SearxngSupervisorStatus Backend,
    InternetSearchDiagnostic Search,
    InternetFetchDiagnostic Fetch)
{
    public bool Ok => Search.Ok && Fetch.Ok;
}

public sealed class SearxngSupervisorService : IDisposable
{
    internal static readonly Uri BundledDefaultBaseUri = new("http://localhost:8081/");
    internal static readonly Uri DiagnosticPageUri = new("https://example.com/");
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DiagnosticSearchTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumProbeBytes = 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly Func<Process, bool> stopProcess;
    private readonly Func<CancellationToken, Task<InternetFetchDiagnostic>> fetchDiagnosticAsync;
    private readonly LocalInternetToolProvider? ownedDiagnosticProvider;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim startupLock = new(1, 1);
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? activeStartupCancellation;
    private Process? supervisedProcess;
    private long lifecycleGeneration;
    private int disposed;

    public SearxngSupervisorService(
        HttpClient? httpClient = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<CancellationToken, Task<InternetFetchDiagnostic>>? fetchDiagnosticAsync = null,
        Func<Process, bool>? stopProcess = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        this.startProcess = startProcess ?? Process.Start;
        this.stopProcess = stopProcess ?? TryStopProcess;
        ownsHttpClient = httpClient is null;
        if (fetchDiagnosticAsync is null)
        {
            ownedDiagnosticProvider = new LocalInternetToolProvider();
            this.fetchDiagnosticAsync = RunDefaultFetchDiagnosticAsync;
        }
        else
        {
            this.fetchDiagnosticAsync = fetchDiagnosticAsync;
        }
    }

    public async Task<SearxngSupervisorStatus> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var requestedGeneration = Volatile.Read(ref lifecycleGeneration);
        await startupLock.WaitAsync(cancellationToken);
        try
        {
            CancellationTokenSource startupCancellation;
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (requestedGeneration != lifecycleGeneration)
                {
                    throw new OperationCanceledException("The local-search start was superseded by a stop request.");
                }

                startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeStartupCancellation = startupCancellation;
            }

            try
            {
                return await EnsureStartedCoreAsync(requestedGeneration, startupCancellation.Token);
            }
            finally
            {
                lock (lifecycleGate)
                {
                    if (ReferenceEquals(activeStartupCancellation, startupCancellation))
                    {
                        activeStartupCancellation = null;
                    }
                }

                startupCancellation.Dispose();
            }
        }
        finally
        {
            startupLock.Release();
        }
    }

    private async Task<SearxngSupervisorStatus> EnsureStartedCoreAsync(
        long requestedGeneration,
        CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
        var payloadAppDirectory = ResolvePayloadAppDirectory();
        SearxngSupervisorStatus Status(bool started, bool alreadyRunning, bool payloadFound, Uri uri, string message) =>
            CreateStatus(started, alreadyRunning, payloadFound, uri, message, payloadAppDirectory);

        if (!TryResolveBaseUri(configured, out var baseUri, out var configurationError))
        {
            return Status(false, false, BundledPayloadExists(payloadAppDirectory), baseUri, configurationError);
        }
        var isServing = await IsHealthyAsync(httpClient, baseUri, cancellationToken);
        EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
        if (isServing)
        {
            var ownedProcess = GetSupervisedProcess();
            if (ownedProcess is null)
            {
                EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                return Status(false, true, BundledPayloadExists(payloadAppDirectory), baseUri, "Local SearXNG is already running.");
            }

            // Never accept the initial fast-path response for an app-owned child.
            // It can outlive the process that produced it, so require a fresh
            // response and then re-check the owned handle before returning ready.
            isServing = await IsHealthyAsync(httpClient, baseUri, cancellationToken);
            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
            if (isServing && IsSupervisedProcessRunning(ownedProcess))
            {
                EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                return Status(false, true, BundledPayloadExists(payloadAppDirectory), baseUri, "Local SearXNG is already running.");
            }

            if (!IsSupervisedProcessRunning(ownedProcess))
            {
                // Release only a confirmed-exited handle. If the fresh response
                // was successful, use one more probe to prove another instance
                // remains after the owned child disappeared.
                if (!StopSupervisedProcess())
                {
                    return Status(
                        false,
                        false,
                        BundledPayloadExists(payloadAppDirectory),
                        baseUri,
                        "The previous bundled SearXNG process could not be confirmed stopped; readiness was not accepted.");
                }

                EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                if (isServing && await IsHealthyAsync(httpClient, baseUri, cancellationToken))
                {
                    EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                    return Status(false, true, BundledPayloadExists(payloadAppDirectory), baseUri, "Local SearXNG is already running from another app instance.");
                }
            }

            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
        }

        if (!ShouldUseBundledForBaseUrl(configured, baseUri))
        {
            return Status(false, false, BundledPayloadExists(payloadAppDirectory), baseUri, "Configured SearXNG URL is unavailable; bundled local search was not started because an override is set.");
        }

        if (!BundledPayloadExists(payloadAppDirectory))
        {
            return Status(false, false, false, baseUri, "Bundled SearXNG payload is not installed.");
        }

        // The gateway health check already failed. Stop a live unhealthy child or
        // dispose a previous exited child before replacing the process handle.
        if (!StopSupervisedProcess())
        {
            return Status(
                false,
                false,
                true,
                baseUri,
                "The previous bundled SearXNG process could not be confirmed stopped; restart was not attempted.");
        }

        EnsureLifecycleCurrent(requestedGeneration, cancellationToken);

        Process? startedProcess;
        try
        {
            startedProcess = startProcess(CreateStartInfo(payloadAppDirectory));
        }
        catch (Exception ex)
        {
            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return Status(false, false, true, baseUri, $"Bundled SearXNG process could not start: {ex.Message}");
        }
        if (startedProcess is null)
        {
            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return Status(false, false, true, baseUri, "Bundled SearXNG process did not start.");
        }

        var tracked = false;
        var adopted = false;
        lock (lifecycleGate)
        {
            if (disposed == 0 && supervisedProcess is null)
            {
                supervisedProcess = startedProcess;
                tracked = true;
                adopted = requestedGeneration == lifecycleGeneration
                    && !cancellationToken.IsCancellationRequested;
            }
        }

        if (!adopted)
        {
            if (tracked)
            {
                StopSupervisedProcess();
            }
            else
            {
                StopDetachedProcess(startedProcess);
            }

            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
            throw new ObjectDisposedException(nameof(SearxngSupervisorService));
        }

        try
        {
            var deadline = DateTimeOffset.UtcNow + StartupTimeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                isServing = await IsHealthyAsync(httpClient, baseUri, cancellationToken);
                EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                if (isServing)
                {
                    if (!IsSupervisedProcessRunning())
                    {
                        if (!StopSupervisedProcess())
                        {
                            return Status(false, false, true, baseUri, "The bundled SearXNG process could not be confirmed stopped; readiness was not accepted.");
                        }

                        EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                        // The successful response may have raced the owned child's
                        // exit (or a concurrent Stop). Do not infer another instance
                        // from that stale response; only a fresh probe can prove it.
                        if (await IsHealthyAsync(httpClient, baseUri, cancellationToken))
                        {
                            EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                            return Status(false, true, true, baseUri, "Local SearXNG became ready from another app instance.");
                        }

                        EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                        return Status(false, false, true, baseUri, "Bundled SearXNG exited after its health check; no search backend is responding.");
                    }

                    EnsureLifecycleCurrent(requestedGeneration, cancellationToken);
                    return Status(true, false, true, baseUri, "Bundled SearXNG started.");
                }

                if (!IsSupervisedProcessRunning())
                {
                    if (!StopSupervisedProcess())
                    {
                        return Status(false, false, true, baseUri, "Bundled SearXNG exited, but its process handle could not be reconciled.");
                    }

                    return Status(false, false, true, baseUri, "Bundled SearXNG exited before becoming ready.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            StopSupervisedProcess();
            return Status(false, false, true, baseUri, "Bundled SearXNG did not become ready before timeout.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A canceled startup must not leave a half-started search process behind
            // or make the next health check report that an unready child is ready.
            StopSupervisedProcess();
            throw;
        }
    }

    public Task<SearxngSupervisorStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return ProbeAsync(httpClient, cancellationToken: cancellationToken);
    }

    public async Task<InternetDiagnosticsReport> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var backend = await EnsureStartedAsync(cancellationToken);
        var searchTask = backend.Started || backend.AlreadyRunning
            ? RunSearchDiagnosticAsync(httpClient, backend.BaseUri, cancellationToken)
            : Task.FromResult(new InternetSearchDiagnostic(
                false,
                TimeSpan.Zero,
                0,
                null,
                null,
                backend.Message));
        var fetchTask = RunFetchDiagnosticSafelyAsync(cancellationToken);

        await Task.WhenAll(searchTask, fetchTask);
        return new InternetDiagnosticsReport(backend, await searchTask, await fetchTask);
    }

    internal static async Task<SearxngSupervisorStatus> ProbeAsync(
        HttpClient httpClient,
        string? configured = null,
        string? appDirectory = null,
        CancellationToken cancellationToken = default)
    {
        configured ??= Environment.GetEnvironmentVariable("AIARENA_SEARXNG_URL");
        var payloadAppDirectory = ResolvePayloadAppDirectory(appDirectory);
        if (!TryResolveBaseUri(configured, out var baseUri, out var configurationError))
        {
            return CreateStatus(false, false, BundledPayloadExists(payloadAppDirectory), baseUri, configurationError, payloadAppDirectory);
        }

        var payloadFound = BundledPayloadExists(payloadAppDirectory);
        if (await IsHealthyAsync(httpClient, baseUri, cancellationToken))
        {
            return CreateStatus(false, true, payloadFound, baseUri, "Local search backend is ready.", payloadAppDirectory);
        }

        var usesBundledPayload = ShouldUseBundledForBaseUrl(configured, baseUri);
        var message = !usesBundledPayload
            ? "Configured SearXNG URL is unavailable."
            : payloadFound
                ? "Local search backend is not responding yet."
                : "Local search backend is unavailable; bundled payload is not installed.";
        return CreateStatus(false, false, payloadFound, baseUri, message, payloadAppDirectory);
    }

    internal static async Task<InternetSearchDiagnostic> RunSearchDiagnosticAsync(
        HttpClient httpClient,
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DiagnosticSearchTimeout);
            var builder = new UriBuilder(new Uri(NormalizeBaseUri(baseUri), "search"))
            {
                Query = $"q={Uri.EscapeDataString("AI Arena internet diagnostic")}&format=json"
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new InternetSearchDiagnostic(
                    false,
                    stopwatch.Elapsed,
                    0,
                    null,
                    null,
                    $"Local search returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "error"}).");
            }

            var content = await ReadBoundedAsync(response.Content, MaximumProbeBytes, timeout.Token);
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return new InternetSearchDiagnostic(
                    false,
                    stopwatch.Elapsed,
                    0,
                    null,
                    null,
                    "Local search returned JSON without a results array.");
            }

            var responsiveEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (result.TryGetProperty("engine", out var engine)
                    && engine.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(engine.GetString()))
                {
                    responsiveEngines.Add(engine.GetString()!);
                }

                if (!result.TryGetProperty("engines", out var engines)
                    || engines.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in engines.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        responsiveEngines.Add(item.GetString()!);
                    }
                }
            }

            int? unresponsiveEngineCount = document.RootElement.TryGetProperty("unresponsive_engines", out var unresponsive)
                && unresponsive.ValueKind == JsonValueKind.Array
                    ? unresponsive.GetArrayLength()
                    : null;
            var resultCount = results.GetArrayLength();
            return new InternetSearchDiagnostic(
                resultCount > 0,
                stopwatch.Elapsed,
                resultCount,
                responsiveEngines.Count > 0 ? responsiveEngines.Count : null,
                unresponsiveEngineCount,
                resultCount > 0
                    ? ""
                    : "Local search answered but returned no results; check enabled engines and outbound network access.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new InternetSearchDiagnostic(false, stopwatch.Elapsed, 0, null, null, "Local search timed out after 15 seconds.");
        }
        catch (Exception ex)
        {
            return new InternetSearchDiagnostic(false, stopwatch.Elapsed, 0, null, null, $"Local search diagnostic failed: {ex.Message}");
        }
    }

    internal static Uri ResolveBaseUri(string? configured)
    {
        return TryResolveBaseUri(configured, out var uri, out _) ? uri : BundledDefaultBaseUri;
    }

    internal static bool TryResolveBaseUri(string? configured, out Uri baseUri, out string error)
    {
        baseUri = BundledDefaultBaseUri;
        error = "";
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            error = "Configured SearXNG URL must be a credential-free absolute HTTP or HTTPS URL.";
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(candidate.Host))
        {
            error = "Configured remote SearXNG URLs must use HTTPS; HTTP is allowed only on loopback.";
            return false;
        }

        if (!string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment))
        {
            error = "Configured SearXNG URL must not include a query string or fragment.";
            return false;
        }

        baseUri = NormalizeBaseUri(candidate);
        return true;
    }

    internal static bool ShouldUseBundledForBaseUrl(string? configured, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        return IsBundledDefaultUri(baseUri);
    }

    internal static bool IsBundledDefaultUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var isSupportedHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal);
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.Port == BundledDefaultBaseUri.Port
            && isSupportedHost
            && (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    internal static bool BundledPayloadExists(string appDirectory)
    {
        var root = Path.Combine(appDirectory, "searxng");
        return (File.Exists(Path.Combine(root, "python", "pythonw.exe"))
                || File.Exists(Path.Combine(root, "python", "python.exe")))
            && File.Exists(Path.Combine(root, "runtime", "searx", "webapp.py"))
            && File.Exists(Path.Combine(root, "runtime", "arena_searxng_wsgi.py"))
            && File.Exists(Path.Combine(root, "runtime", "site-packages", "granian", "__init__.py"))
            && File.Exists(Path.Combine(root, "settings.yml"));
    }

    internal static string ResolvePayloadAppDirectory(string? appDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(appDirectory))
        {
            return Path.GetFullPath(appDirectory);
        }

        if (BundledPayloadExists(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        var configuredPayload = Environment.GetEnvironmentVariable("AIARENA_SEARXNG_PAYLOAD_DIR");
        if (!string.IsNullOrWhiteSpace(configuredPayload))
        {
            var configuredFullPath = Path.GetFullPath(configuredPayload);
            if (BundledPayloadExists(configuredFullPath))
            {
                return configuredFullPath;
            }

            if (PayloadRootExists(configuredFullPath))
            {
                return Directory.GetParent(configuredFullPath)?.FullName ?? configuredFullPath;
            }
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is not null)
        {
            var distRoot = Path.Combine(repositoryRoot, "dist");
            if (Directory.Exists(distRoot))
            {
                var release = Directory.GetDirectories(distRoot, "AI Arena - *", SearchOption.TopDirectoryOnly)
                    .Where(BundledPayloadExists)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (release is not null)
                {
                    return release;
                }
            }
        }

        return AppContext.BaseDirectory;
    }

    internal static string ResolvePayloadVersion(string appDirectory)
    {
        var manifestPath = Path.Combine(appDirectory, "searxng", "payload-manifest.txt");
        try
        {
            if (!File.Exists(manifestPath))
            {
                return "";
            }

            const string revisionPrefix = "SearXNG revision:";
            var revision = File.ReadLines(manifestPath)
                .FirstOrDefault(line => line.StartsWith(revisionPrefix, StringComparison.OrdinalIgnoreCase));
            return revision is null ? "" : revision[revisionPrefix.Length..].Trim();
        }
        catch
        {
            return "";
        }
    }

    private static SearxngSupervisorStatus CreateStatus(
        bool started,
        bool alreadyRunning,
        bool payloadFound,
        Uri baseUri,
        string message,
        string payloadAppDirectory)
    {
        return new SearxngSupervisorStatus(
            started,
            alreadyRunning,
            payloadFound,
            baseUri,
            message,
            Path.Combine(payloadAppDirectory, "searxng"),
            payloadFound ? ResolvePayloadVersion(payloadAppDirectory) : "");
    }

    private static bool PayloadRootExists(string root)
    {
        return (File.Exists(Path.Combine(root, "python", "pythonw.exe"))
                || File.Exists(Path.Combine(root, "python", "python.exe")))
            && File.Exists(Path.Combine(root, "runtime", "searx", "webapp.py"))
            && File.Exists(Path.Combine(root, "runtime", "arena_searxng_wsgi.py"))
            && File.Exists(Path.Combine(root, "runtime", "site-packages", "granian", "__init__.py"))
            && File.Exists(Path.Combine(root, "settings.yml"));
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "scripts", "build-searxng-payload.ps1")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string appDirectory)
    {
        var root = Path.Combine(appDirectory, "searxng");
        var pythonw = Path.Combine(root, "python", "pythonw.exe");
        var python = File.Exists(pythonw) ? pythonw : Path.Combine(root, "python", "python.exe");
        var runtime = Path.Combine(root, "runtime");
        var sitePackages = Path.Combine(runtime, "site-packages");
        var settings = Path.Combine(root, "settings.yml");
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = runtime
        };
        foreach (var argument in new[]
        {
            "-m",
            "granian",
            "--interface",
            "wsgi",
            "--workers",
            "2",
            "--respawn-failed-workers",
            "--host",
            "127.0.0.1",
            "--port",
            "8081",
            "arena_searxng_wsgi:application"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONPATH"] = $"{runtime};{sitePackages}";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["SEARXNG_SETTINGS_PATH"] = settings;
        startInfo.Environment["SEARXNG_DEBUG"] = "false";
        startInfo.Environment["SEARXNG_LIMITER"] = "false";
        startInfo.Environment["SEARXNG_PUBLIC_INSTANCE"] = "false";
        startInfo.Environment["SEARXNG_BIND_ADDRESS"] = "127.0.0.1";
        startInfo.Environment["SEARXNG_PORT"] = "8081";
        return startInfo;
    }

    private static async Task<bool> IsHealthyAsync(HttpClient httpClient, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var healthUri = new Uri(NormalizeBaseUri(baseUri), "healthz");
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException("Local search probe response is too large.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Local search probe response is too large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    private async Task<InternetFetchDiagnostic> RunFetchDiagnosticSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await fetchDiagnosticAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InternetFetchDiagnostic(false, TimeSpan.Zero, null, $"Direct page fetch diagnostic failed: {ex.Message}");
        }
    }

    private async Task<InternetFetchDiagnostic> RunDefaultFetchDiagnosticAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var provider = ownedDiagnosticProvider
            ?? throw new InvalidOperationException("The direct page diagnostic provider is unavailable.");
        var result = await provider.ExecuteAsync(
            new InternetToolRequest
            {
                Tool = InternetToolNames.FetchUrl,
                RequesterId = "internet-diagnostics",
                Url = DiagnosticPageUri.AbsoluteUri,
                Reason = "Verify direct public HTTPS page access."
            },
            new InternetSettings { UseInternet = true },
            cancellationToken);
        var sourceUrl = result.Sources.FirstOrDefault()?.Url;
        var finalUri = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsedFinalUri)
            ? parsedFinalUri
            : result.Ok ? DiagnosticPageUri : null;
        return new InternetFetchDiagnostic(
            result.Ok,
            stopwatch.Elapsed,
            finalUri,
            result.Ok ? "" : string.IsNullOrWhiteSpace(result.Error) ? "The diagnostic page returned no readable content." : result.Error);
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private void EnsureLifecycleCurrent(long requestedGeneration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (requestedGeneration != lifecycleGeneration)
            {
                throw new OperationCanceledException(
                    "The local-search start was superseded by a stop request.",
                    cancellationToken);
            }
        }
    }

    private Process? GetSupervisedProcess()
    {
        lock (lifecycleGate)
        {
            return supervisedProcess;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? startupCancellation;
        lock (lifecycleGate)
        {
            lifecycleGeneration++;
            startupCancellation = activeStartupCancellation;
        }

        CancelBestEffort(startupCancellation);
        StopSupervisedProcess();
    }

    private bool StopSupervisedProcess()
    {
        lock (lifecycleGate)
        {
            var process = supervisedProcess;
            if (process is null)
            {
                return true;
            }

            bool stopped;
            try
            {
                stopped = stopProcess(process);
            }
            catch
            {
                stopped = false;
            }

            if (!stopped)
            {
                // Keep the undisposed handle so Stop or the next Ensure can retry.
                // Dropping it here can orphan a still-live app-owned process.
                return false;
            }

            supervisedProcess = null;
            process.Dispose();
            return true;
        }
    }

    private void StopDetachedProcess(Process process)
    {
        bool stopped;
        try
        {
            stopped = stopProcess(process);
        }
        catch
        {
            stopped = false;
        }

        if (stopped)
        {
            process.Dispose();
            return;
        }

        lock (lifecycleGate)
        {
            if (supervisedProcess is null)
            {
                supervisedProcess = process;
                return;
            }
        }

        // This should be unreachable because startup is serialized and a new
        // child is not launched until the prior handle is confirmed stopped.
        // Leave the live handle undisposed rather than pretending it exited.
    }

    private static bool TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(checked((int)ProcessExitTimeout.TotalMilliseconds)))
                {
                    return false;
                }
            }

            return process.HasExited;
        }
        catch
        {
            // Best-effort cleanup only; the search client already degrades gracefully.
            return false;
        }
    }

    private bool IsSupervisedProcessRunning()
    {
        Process? process;
        lock (lifecycleGate)
        {
            process = supervisedProcess;
        }

        if (process is null)
        {
            return false;
        }

        return IsProcessRunning(process);
    }

    private bool IsSupervisedProcessRunning(Process expectedProcess)
    {
        lock (lifecycleGate)
        {
            return ReferenceEquals(supervisedProcess, expectedProcess)
                && IsProcessRunning(expectedProcess);
        }
    }

    private static bool IsProcessRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? startupCancellation;
        var retryOnly = false;
        lock (lifecycleGate)
        {
            if (disposed != 0)
            {
                startupCancellation = null;
                retryOnly = true;
            }
            else
            {
                Volatile.Write(ref disposed, 1);
                lifecycleGeneration++;
                startupCancellation = activeStartupCancellation;
                activeStartupCancellation = null;
            }
        }

        if (retryOnly)
        {
            StopSupervisedProcess();
            return;
        }

        CancelBestEffort(startupCancellation);
        StopSupervisedProcess();
        try
        {
            ownedDiagnosticProvider?.Dispose();
        }
        finally
        {
            if (ownsHttpClient)
            {
                httpClient.Dispose();
            }
        }
    }

    private static void CancelBestEffort(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // A racing startup owns disposal of its cancellation source.
        }
    }
}
