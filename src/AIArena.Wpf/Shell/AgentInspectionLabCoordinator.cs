using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf;

/// <summary>
/// Standalone product coordinator for Context &amp; Prompt Inspector and Agent
/// Memory Debugger. Shell integration supplies only the current session id and,
/// optionally, the shell's existing load/save delegates. Every mutation method
/// is deterministic and reusable by a later control-plane adapter.
/// </summary>
internal sealed class AgentInspectionLabCoordinator : IDisposable
{
    private const int MaximumRenderedPromptTraces = 256;
    private const int MaximumRenderedMemoryEntries = StructuredMemoryService.MaximumEntriesPerAgent;
    private const int MaximumEditorCharacters = 4000;

    private readonly AgentInspectionLabControl view;
    private readonly ProviderRequestTraceStore traceStore;
    private readonly SessionStore sessionStore;
    private readonly Func<string?> currentSessionId;
    private readonly Func<string, CancellationToken, Task<ArenaSnapshot?>> loadSnapshotAsync;
    private readonly Func<ArenaSnapshot, string, CancellationToken, Task> saveSnapshotAsync;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim memoryGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ArenaSnapshot? memorySnapshot;
    private string loadedSessionId = "";
    private string authorizedAgentId = "";
    private string selectedMemoryId = "";
    private long memoryScopeGeneration;
    private MemoryEntryStateFilter memoryFilter = MemoryEntryStateFilter.Active;
    private bool disposed;

    internal AgentInspectionLabCoordinator(
        AgentInspectionLabControl view,
        ProviderRequestTraceStore traceStore,
        SessionStore sessionStore,
        Func<string?> currentSessionId,
        Func<string, CancellationToken, Task<ArenaSnapshot?>>? loadSnapshotAsync = null,
        Func<ArenaSnapshot, string, CancellationToken, Task>? saveSnapshotAsync = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.traceStore = traceStore ?? throw new ArgumentNullException(nameof(traceStore));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.currentSessionId = currentSessionId ?? throw new ArgumentNullException(nameof(currentSessionId));
        this.loadSnapshotAsync = loadSnapshotAsync
            ?? ((sessionId, cancellationToken) => this.sessionStore.LoadSnapshotAsync(sessionId, cancellationToken));
        this.saveSnapshotAsync = saveSnapshotAsync
            ?? ((snapshot, sessionId, cancellationToken) => this.sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);

        view.PromptRefreshRequested += OnPromptRefreshRequested;
        view.PromptClearRequested += OnPromptClearRequested;
        view.PromptSelectionChanged += OnPromptSelectionChanged;
        view.MemoryRefreshRequested += OnMemoryRefreshRequested;
        view.MemoryAgentChanged += OnMemoryAgentChanged;
        view.MemoryStateChanged += OnMemoryStateChanged;
        view.MemorySelectionChanged += OnMemorySelectionChanged;
        view.MemoryAddRequested += OnMemoryAddRequested;
        view.MemoryCorrectRequested += OnMemoryCorrectRequested;
        view.MemoryExpireRequested += OnMemoryExpireRequested;

        RefreshPromptTraces();
        RenderMemoryUnavailable("Select an agent to authorize a scoped private-memory view.");
    }

    internal async Task<InspectionOperationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshPromptTraces();
        return await RefreshMemoryAsync(cancellationToken);
    }

    internal void RefreshPromptTraces()
    {
        ThrowIfDisposed();
        var selectedRequestId = (view.PromptList.SelectedItem as PromptTraceListItem)?.RequestId ?? "";
        var traces = traceStore.Snapshot()
            .OrderByDescending(trace => trace.ObservedAtUtc)
            .ThenByDescending(trace => trace.Attempt)
            .Take(MaximumRenderedPromptTraces)
            .Select(PromptTraceListItem.From)
            .ToArray();
        var collection = new ListCollectionView(traces.ToList());
        collection.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PromptTraceListItem.CorrelationId)));
        view.PromptList.ItemsSource = collection;
        view.PromptClear.IsEnabled = traces.Length > 0;

        var preserved = traces.FirstOrDefault(item => item.RequestId.Equals(selectedRequestId, StringComparison.Ordinal));
        if (preserved is not null)
        {
            view.PromptList.SelectedItem = preserved;
            RenderPromptTrace(preserved.Trace);
        }
        else
        {
            RenderPromptTrace(null);
        }

        SetPromptStatus(traces.Length == 0
            ? "No provider requests have been observed in this process."
            : $"{traces.Length.ToString(CultureInfo.InvariantCulture)} physical provider request attempt(s), grouped by correlation id. Traces are process-memory only.");
    }

    internal InspectionOperationResult ClearPromptTraces()
    {
        ThrowIfDisposed();
        traceStore.Clear();
        RefreshPromptTraces();
        SetPromptStatus("Cleared process-memory provider traces. Session data was unchanged.");
        return InspectionOperationResult.Success("prompt_traces_cleared", "Process-memory provider traces cleared.");
    }

    internal InspectionOperationResult SelectPromptTrace(string requestId)
    {
        ThrowIfDisposed();
        var requested = (requestId ?? "").Trim();
        var match = EnumerateItems<PromptTraceListItem>(view.PromptList)
            .FirstOrDefault(item => item.RequestId.Equals(requested, StringComparison.Ordinal));
        if (match is null)
        {
            return InspectionOperationResult.Failure("prompt_trace_not_found", "The requested provider trace is unavailable in process memory.");
        }

        view.PromptList.SelectedItem = match;
        RenderPromptTrace(match.Trace);
        return InspectionOperationResult.Success("prompt_trace_selected", "Provider trace selected.", match.RequestId);
    }

    internal async Task<InspectionOperationResult> RefreshMemoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var acquired = false;
        try
        {
            // A refresh can be queued behind an in-flight mutation/save. Clear
            // the prior session's private content before waiting on that gate;
            // ResetMemorySession also advances the scope generation observed by
            // the mutation's post-save recheck.
            await OnViewAsync(() =>
            {
                var activeSessionId = NormalizeSessionId(currentSessionId());
                if (!loadedSessionId.Equals(activeSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    ResetMemorySession();
                    SetMemoryStatus(activeSessionId.Length == 0
                        ? "No active session is available; the previous private-memory view was cleared."
                        : "Loading the new session; the previous private-memory view was cleared.");
                }
            });

            await memoryGate.WaitAsync(linked.Token);
            acquired = true;
            var sessionId = NormalizeSessionId(currentSessionId());
            if (sessionId.Length == 0)
            {
                return await OnViewAsync(() =>
                {
                    ResetMemorySession();
                    return InspectionOperationResult.Failure("session_unavailable", "No active session is available.");
                });
            }

            if (!loadedSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                // Recheck after gate acquisition because the active session may
                // have changed again while this refresh was queued.
                await OnViewAsync(() =>
                {
                    ResetMemorySession();
                    SetMemoryStatus("Loading the new session; the previous private-memory view was cleared.");
                });
            }

            var snapshot = await loadSnapshotAsync(sessionId, linked.Token);
            if (!sessionId.Equals(NormalizeSessionId(currentSessionId()), StringComparison.OrdinalIgnoreCase))
            {
                return await OnViewAsync(() =>
                {
                    ResetMemorySession();
                    SetMemoryStatus("The active session changed while memory was loading; the private-memory view remains cleared.");
                    return InspectionOperationResult.Failure("session_changed", "The active session changed while memory was loading; no stale evidence was displayed.");
                });
            }

            if (snapshot is null)
            {
                return await OnViewAsync(() =>
                {
                    ResetMemorySession();
                    SetMemoryStatus("The active session snapshot is unavailable; memory evidence was not inferred.");
                    return InspectionOperationResult.Failure("snapshot_unavailable", "The active session snapshot is unavailable.");
                });
            }

            StructuredMemoryService.NormalizeSnapshot(snapshot);
            return await OnViewAsync(() =>
            {
                if (!loadedSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    ClearMemoryAuthorization();
                }

                loadedSessionId = sessionId;
                memorySnapshot = snapshot;
                PopulateAgentChoices(snapshot);
                RenderMemoryEntries();
                return InspectionOperationResult.Success("memory_refreshed", "Structured memory refreshed.");
            });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return await OnViewAsync(() =>
            {
                ResetMemorySession();
                SetMemoryStatus("Memory refresh was cancelled; the private-memory view remains cleared.");
                return InspectionOperationResult.Failure("operation_cancelled", "Memory refresh was cancelled.");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotConcurrencyException)
        {
            return await OnViewAsync(() =>
            {
                ResetMemorySession();
                SetMemoryStatus(MemoryFailureMessage(ex, "refresh"));
                return InspectionOperationResult.Failure("memory_refresh_failed", "Structured memory could not be refreshed.");
            });
        }
        catch (Exception)
        {
            const string message = "Memory refresh did not complete. Exception details were withheld from the privacy-safe UI.";
            return await OnViewAsync(() =>
            {
                ResetMemorySession();
                SetMemoryStatus(message);
                return InspectionOperationResult.Failure("memory_refresh_failed", message);
            });
        }
        finally
        {
            if (acquired)
            {
                memoryGate.Release();
            }
        }
    }

    internal InspectionOperationResult SelectAuthorizedAgent(string? agentId)
    {
        ThrowIfDisposed();
        var requested = (agentId ?? "").Trim();
        if (memorySnapshot is null)
        {
            return InspectionOperationResult.Failure("snapshot_unavailable", "Refresh the active session before selecting an agent.");
        }

        var agent = memorySnapshot.Engine.Agents.FirstOrDefault(candidate =>
            candidate.Id.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            ClearMemoryAuthorization();
            view.MemoryAgent.SelectedIndex = -1;
            RenderMemoryEntries();
            return InspectionOperationResult.Failure("agent_not_found", "The requested agent is unavailable in the active session.");
        }

        AuthorizeMemoryAgent(agent.Id);
        view.MemoryAgent.SelectedValue = agent.Id;
        RenderMemoryEntries();
        return InspectionOperationResult.Success("memory_agent_selected", "Scoped memory view authorized for the selected agent.", agent.Id);
    }

    internal InspectionOperationResult SetMemoryFilter(string? filter)
    {
        ThrowIfDisposed();
        if (!TryParseFilter(filter, out var parsed))
        {
            return InspectionOperationResult.Failure("invalid_memory_filter", "Memory filter must be active, expired, superseded, or all.");
        }

        memoryFilter = parsed;
        SelectComboTag(view.MemoryState, FilterTag(parsed));
        RenderMemoryEntries();
        return InspectionOperationResult.Success("memory_filter_set", $"Memory filter set to {FilterTag(parsed)}.");
    }

    internal InspectionOperationResult SelectMemory(string? memoryId)
    {
        ThrowIfDisposed();
        var requested = (memoryId ?? "").Trim();
        var match = EnumerateItems<MemoryEntryListItem>(view.MemoryList)
            .FirstOrDefault(item => item.MemoryId.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            selectedMemoryId = "";
            RenderSelectedMemory(null);
            return InspectionOperationResult.Failure("memory_not_found", "The requested memory is not present in the current scoped filter.");
        }

        selectedMemoryId = match.MemoryId;
        view.MemoryList.SelectedItem = match;
        RenderSelectedMemory(match);
        return InspectionOperationResult.Success("memory_selected", "Structured memory selected.", match.MemoryId);
    }

    internal Task<InspectionOperationResult> AddMemoryAsync(
        string text,
        string visibility,
        TimeSpan? expiresAfter,
        CancellationToken cancellationToken = default) =>
        MutateMemoryAsync(
            "add",
            text,
            visibility,
            expiresAfter,
            targetMemoryId: null,
            cancellationToken);

    internal Task<InspectionOperationResult> CorrectMemoryAsync(
        string memoryId,
        string correctedText,
        string visibility,
        TimeSpan? expiresAfter,
        CancellationToken cancellationToken = default) =>
        MutateMemoryAsync(
            "correct",
            correctedText,
            visibility,
            expiresAfter,
            memoryId,
            cancellationToken);

    internal Task<InspectionOperationResult> ExpireMemoryAsync(
        string memoryId,
        CancellationToken cancellationToken = default) =>
        MutateMemoryAsync(
            "expire",
            text: "",
            visibility: StructuredMemoryVisibilities.Private,
            expiresAfter: null,
            memoryId,
            cancellationToken);

    internal IReadOnlyList<PromptTraceListItem> DebugPromptItems => EnumerateItems<PromptTraceListItem>(view.PromptList).ToArray();
    internal IReadOnlyList<MemoryEntryListItem> DebugMemoryItems => EnumerateItems<MemoryEntryListItem>(view.MemoryList).ToArray();
    internal string DebugAuthorizedAgentId => authorizedAgentId;
    internal string DebugLoadedSessionId => loadedSessionId;

    internal static string FormatTokenEvidence(string label, ProviderTokenEvidence? evidence)
    {
        if (evidence is null)
        {
            return $"{label}: unavailable — no token evidence record was supplied.";
        }

        var explanation = InspectionTextSafety.RedactAndBound(evidence.Explanation, 600, "No explanation was supplied.");
        if (evidence.Kind == ProviderTokenEvidenceKind.Unavailable || evidence.Value is null || evidence.Value < 0)
        {
            return $"{label}: unavailable — {explanation}";
        }

        var evidenceLabel = evidence.Kind switch
        {
            ProviderTokenEvidenceKind.Measured => "measured",
            ProviderTokenEvidenceKind.ProviderReported => "provider-reported",
            ProviderTokenEvidenceKind.Estimated => "estimated",
            _ => "unavailable"
        };
        return $"{label}: {evidence.Value.Value.ToString("N0", CultureInfo.InvariantCulture)} ({evidenceLabel}) — {explanation}";
    }

    internal static IReadOnlyList<MemoryEntryListItem> BuildMemoryItems(
        ArenaSnapshot snapshot,
        string? authorizedAgentId,
        MemoryEntryStateFilter filter,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var agentId = (authorizedAgentId ?? "").Trim();
        if (agentId.Length == 0)
        {
            return [];
        }

        var agent = snapshot.Engine.Agents.FirstOrDefault(candidate =>
            candidate.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return [];
        }

        // Deliberately operate on one owner only. This prevents an aggregate
        // memory view from leaking other-agent private values or counts.
        var retired = agent.MemoryEntries
            .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = nowUtc.ToUnixTimeSeconds();
        return agent.MemoryEntries
            .Select(entry => MemoryEntryListItem.From(agent, entry, ClassifyMemory(entry, retired, now)))
            .Where(item => filter == MemoryEntryStateFilter.All || item.State == filter)
            .OrderByDescending(item => item.RevisedAt)
            .ThenBy(item => item.MemoryId, StringComparer.Ordinal)
            .Take(MaximumRenderedMemoryEntries)
            .ToArray();
    }

    internal static MemoryEntryStateFilter ClassifyMemory(
        StructuredMemoryEntry entry,
        IReadOnlySet<string> retiredIds,
        double nowUnixSeconds)
    {
        if (retiredIds.Contains(entry.MemoryId))
        {
            return MemoryEntryStateFilter.Superseded;
        }

        return entry.ExpiresAt is not null && entry.ExpiresAt.Value <= nowUnixSeconds
            ? MemoryEntryStateFilter.Expired
            : MemoryEntryStateFilter.Active;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();
        view.PromptRefreshRequested -= OnPromptRefreshRequested;
        view.PromptClearRequested -= OnPromptClearRequested;
        view.PromptSelectionChanged -= OnPromptSelectionChanged;
        view.MemoryRefreshRequested -= OnMemoryRefreshRequested;
        view.MemoryAgentChanged -= OnMemoryAgentChanged;
        view.MemoryStateChanged -= OnMemoryStateChanged;
        view.MemorySelectionChanged -= OnMemorySelectionChanged;
        view.MemoryAddRequested -= OnMemoryAddRequested;
        view.MemoryCorrectRequested -= OnMemoryCorrectRequested;
        view.MemoryExpireRequested -= OnMemoryExpireRequested;
        lifetime.Dispose();
    }

    private async Task<InspectionOperationResult> MutateMemoryAsync(
        string operation,
        string text,
        string visibility,
        TimeSpan? expiresAfter,
        string? targetMemoryId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var acquired = false;
        try
        {
            await memoryGate.WaitAsync(linked.Token);
            acquired = true;
            var sessionId = NormalizeSessionId(currentSessionId());
            var scopedAgentId = authorizedAgentId;
            var scopedGeneration = Volatile.Read(ref memoryScopeGeneration);
            if (sessionId.Length == 0 || scopedAgentId.Length == 0)
            {
                return InspectionOperationResult.Failure("memory_scope_required", "Select an active session and one authorized agent before changing memory.");
            }

            var snapshot = await loadSnapshotAsync(sessionId, linked.Token);
            if (snapshot is null)
            {
                return InspectionOperationResult.Failure("snapshot_unavailable", "The active session snapshot is unavailable; no change was applied.");
            }

            if (!sessionId.Equals(NormalizeSessionId(currentSessionId()), StringComparison.OrdinalIgnoreCase)
                || !scopedAgentId.Equals(authorizedAgentId, StringComparison.OrdinalIgnoreCase)
                || scopedGeneration != Volatile.Read(ref memoryScopeGeneration))
            {
                return InspectionOperationResult.Failure("memory_scope_changed", "The session or authorized agent changed; no stale change was applied.");
            }

            var agent = snapshot.Engine.Agents.FirstOrDefault(candidate =>
                candidate.Id.Equals(scopedAgentId, StringComparison.OrdinalIgnoreCase));
            if (agent is null)
            {
                return InspectionOperationResult.Failure("agent_not_found", "The authorized agent is no longer present; no change was applied.");
            }

            var now = clock();
            DateTimeOffset? expiry = expiresAfter is null ? null : now + expiresAfter.Value;
            StructuredMemoryEntry changed;
            switch (operation)
            {
                case "add":
                    changed = StructuredMemoryService.AddManualMemory(snapshot, agent, text, visibility, now, expiry);
                    break;
                case "correct":
                    changed = StructuredMemoryService.CorrectMemory(
                        snapshot,
                        agent,
                        targetMemoryId ?? "",
                        text,
                        visibility,
                        now,
                        expiry);
                    break;
                case "expire":
                    changed = StructuredMemoryService.ExpireMemory(snapshot, agent, targetMemoryId ?? "", now);
                    break;
                default:
                    return InspectionOperationResult.Failure("unsupported_memory_operation", "The requested memory operation is unsupported.");
            }

            await saveSnapshotAsync(snapshot, sessionId, linked.Token);
            var message = operation switch
            {
                "add" => "Manual memory added and persisted.",
                "correct" => "Correction added; the previous record remains as superseded evidence.",
                _ => "Memory expired; the record remains available as lifecycle evidence."
            };
            return await OnViewAsync(() =>
            {
                if (!IsMemoryScopeCurrent(sessionId, scopedAgentId, scopedGeneration))
                {
                    const string savedToOriginalScope = "The memory change was persisted to the session that was active when the operation began. The active session or agent authorization changed afterward, so the private-memory view was cleared.";
                    ResetMemorySession();
                    SetMemoryStatus(savedToOriginalScope);
                    return InspectionOperationResult.Success(
                        $"memory_{operation}_persisted_scope_changed",
                        savedToOriginalScope);
                }

                memorySnapshot = snapshot;
                loadedSessionId = sessionId;
                selectedMemoryId = changed.MemoryId;
                PopulateAgentChoices(snapshot);
                RenderMemoryEntries();
                view.MemoryEditor.Clear();
                SetMemoryStatus(message);
                return InspectionOperationResult.Success($"memory_{operation}_applied", message, changed.MemoryId);
            });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return InspectionOperationResult.Failure("operation_cancelled", "The memory operation was cancelled; completion was not claimed.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SnapshotConcurrencyException)
        {
            var safe = MemoryFailureMessage(ex, "change");
            return await OnViewAsync(() =>
            {
                SetMemoryStatus(safe);
                return InspectionOperationResult.Failure("memory_change_rejected", safe);
            });
        }
        catch (Exception)
        {
            const string message = "Memory change did not complete. Exception details were withheld from the privacy-safe UI.";
            return await OnViewAsync(() =>
            {
                SetMemoryStatus(message);
                return InspectionOperationResult.Failure("memory_change_failed", message);
            });
        }
        finally
        {
            if (acquired)
            {
                memoryGate.Release();
            }
        }
    }

    private Task OnViewAsync(Action action)
    {
        if (view.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return view.Dispatcher.InvokeAsync(action).Task;
    }

    private Task<T> OnViewAsync<T>(Func<T> action)
    {
        if (view.Dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return view.Dispatcher.InvokeAsync(action).Task;
    }

    private void PopulateAgentChoices(ArenaSnapshot snapshot)
    {
        var choices = snapshot.Engine.Agents
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Select(agent => new MemoryAgentChoice(
                agent.Id,
                string.IsNullOrWhiteSpace(agent.Name)
                    ? InspectionTextSafety.RedactAndBound(agent.Id, 100, "Unnamed agent")
                    : $"{InspectionTextSafety.RedactAndBound(agent.Name, 100, "Unnamed agent")} ({InspectionTextSafety.RedactAndBound(agent.Id, 100, "unavailable")})"))
            .ToArray();
        view.MemoryAgent.ItemsSource = choices;
        view.MemoryAgent.SelectedValue = choices.Any(choice =>
            choice.AgentId.Equals(authorizedAgentId, StringComparison.OrdinalIgnoreCase))
            ? authorizedAgentId
            : null;
    }

    private void RenderMemoryEntries()
    {
        if (memorySnapshot is null)
        {
            RenderMemoryUnavailable("The active session snapshot is unavailable; memory evidence was not inferred.");
            return;
        }

        if (authorizedAgentId.Length == 0)
        {
            view.MemoryList.ItemsSource = Array.Empty<MemoryEntryListItem>();
            RenderMemoryUnavailable("Select an agent to authorize a scoped private-memory view.");
            return;
        }

        var agent = memorySnapshot.Engine.Agents.FirstOrDefault(candidate =>
            candidate.Id.Equals(authorizedAgentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            ClearMemoryAuthorization();
            view.MemoryList.ItemsSource = Array.Empty<MemoryEntryListItem>();
            RenderMemoryUnavailable("The previously authorized agent is no longer present. No private content is displayed.");
            return;
        }

        var items = BuildMemoryItems(memorySnapshot, authorizedAgentId, memoryFilter, clock());
        view.MemoryList.ItemsSource = items;
        view.MemoryPrivacy.Text = $"Scoped to {SafeAgentLabel(agent)} only. Private entries belonging to other agents are neither counted nor displayed.";
        view.MemoryAdd.IsEnabled = true;
        var selected = items.FirstOrDefault(item =>
            item.MemoryId.Equals(selectedMemoryId, StringComparison.OrdinalIgnoreCase));
        view.MemoryList.SelectedItem = selected;
        RenderSelectedMemory(selected);
        SetMemoryStatus(items.Count == 0
            ? $"No {FilterTag(memoryFilter)} memory records are available for the selected agent."
            : $"Showing {items.Count.ToString(CultureInfo.InvariantCulture)} {FilterTag(memoryFilter)} record(s) for the selected agent only.");
    }

    private void RenderMemoryUnavailable(string status)
    {
        view.MemoryList.ItemsSource = Array.Empty<MemoryEntryListItem>();
        view.MemoryPrivacy.Text = "No agent is authorized in this view. Other-agent private content is not aggregated or previewed.";
        view.MemoryMetadata.Text = "No memory selected.";
        view.MemoryEditor.Clear();
        view.MemoryAdd.IsEnabled = false;
        view.MemoryCorrect.IsEnabled = false;
        view.MemoryExpire.IsEnabled = false;
        SetMemoryStatus(status);
    }

    private void RenderSelectedMemory(MemoryEntryListItem? item)
    {
        if (item is null)
        {
            selectedMemoryId = "";
            view.MemoryMetadata.Text = "No memory selected.";
            view.MemoryCorrect.IsEnabled = false;
            view.MemoryExpire.IsEnabled = false;
            return;
        }

        selectedMemoryId = item.MemoryId;
        view.MemoryMetadata.Text = item.Metadata;
        view.MemoryVisibility.SelectedValue = item.Visibility is StructuredMemoryVisibilities.Private or StructuredMemoryVisibilities.Shared
            ? item.Visibility
            : StructuredMemoryVisibilities.Private;
        if (item.Text.Length <= MaximumEditorCharacters)
        {
            view.MemoryEditor.Text = item.Text;
        }
        else
        {
            view.MemoryEditor.Clear();
            SetMemoryStatus("The selected legacy value exceeds the safe editor bound. Enter replacement text explicitly to create a correction.");
        }

        var current = item.State == MemoryEntryStateFilter.Active;
        view.MemoryCorrect.IsEnabled = current;
        view.MemoryExpire.IsEnabled = current;
    }

    private void RenderPromptTrace(ProviderRequestTrace? trace)
    {
        if (trace is null)
        {
            view.PromptMetadata.Text = "No request selected.";
            view.PromptHash.Text = "Unavailable — no request selected.";
            view.PromptTokens.Text = "Unavailable — no request selected.";
            view.PromptPayload.Text = "No redacted preview is available.";
            view.PromptRoles.ItemsSource = Array.Empty<string>();
            view.PromptContext.ItemsSource = new[]
            {
                "unavailable · Select a request to inspect context evidence and explicit omissions."
            };
            return;
        }

        view.PromptMetadata.Text = string.Join(
            Environment.NewLine,
            $"Phase: {InspectionTextSafety.RedactAndBound(trace.Phase, 100, "unavailable")}",
            $"Model: {InspectionTextSafety.RedactAndBound(trace.Model, 200, "unavailable")}",
            $"API mode / transport: {InspectionTextSafety.RedactAndBound(trace.ApiMode, 80, "unavailable")} / {InspectionTextSafety.RedactAndBound(trace.Transport, 80, "unavailable")}",
            $"Attempt: {Math.Max(1, trace.Attempt).ToString(CultureInfo.InvariantCulture)}",
            $"Streaming requested / serialized: {trace.RequestedStreaming} / {trace.PayloadStreaming}",
            $"Outcome: {InspectionTextSafety.RedactAndBound(trace.Outcome, 300, "unavailable")}",
            $"Observed UTC: {trace.ObservedAtUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff 'UTC'}");
        view.PromptHash.Text = IsSha256(trace.PayloadSha256)
            ? $"sha256 {trace.PayloadSha256.ToLowerInvariant()} · {Math.Max(0, trace.PayloadByteCount).ToString("N0", CultureInfo.InvariantCulture)} exact UTF-8 byte(s)"
            : $"Unavailable — no valid exact-body hash was retained. Byte count alone is not presented as correspondence evidence.";
        view.PromptTokens.Text = string.Join(
            Environment.NewLine,
            FormatTokenEvidence("Prompt", trace.PromptTokens),
            FormatTokenEvidence("Completion", trace.CompletionTokens),
            FormatTokenEvidence("Total", trace.TotalTokens));
        view.PromptPayload.Text = string.IsNullOrWhiteSpace(trace.RedactedPayload)
            ? "[OMITTED:REDACTED_PREVIEW_UNAVAILABLE]"
            : InspectionTextSafety.RedactAndBound(trace.RedactedPayload, 32 * 1024, "[OMITTED:REDACTED_PREVIEW_UNAVAILABLE]");
        AutomationProperties.SetHelpText(
            view.PromptPayload,
            trace.RedactedPayloadTruncated
                ? "Bounded redacted preview; truncation or safe-size omission is explicitly recorded. The original exact body is represented only by its measured hash and byte count."
                : "Complete stored redacted preview. Redaction means the preview is not byte-identical to the original exact body represented by the measured hash.");
        view.PromptRoles.ItemsSource = (trace.Roles ?? [])
            .Take(128)
            .Select(role =>
                $"#{Math.Max(0, role.Index).ToString(CultureInfo.InvariantCulture)} {InspectionTextSafety.RedactAndBound(role.Role, 64, "unavailable")} · {InspectionTextSafety.RedactAndBound(role.Transformation, 1024, "Transformation unavailable.")}\n{InspectionTextSafety.RedactAndBound(role.RedactedContent, 2200, "[OMITTED:CONTENT_UNAVAILABLE]")}")
            .ToArray();
        var contexts = (trace.Context ?? [])
            .Take(64)
            .Select(context =>
                $"{NormalizeEvidenceState(context.EvidenceState)} · {InspectionTextSafety.RedactAndBound(context.Subject, 120, "unavailable")}: {InspectionTextSafety.RedactAndBound(context.Explanation, 1100, "Explanation unavailable.")}")
            .ToArray();
        view.PromptContext.ItemsSource = contexts.Length > 0
            ? contexts
            : new[] { "unavailable · No upstream context evidence was supplied." };
    }

    private void ResetMemorySession()
    {
        loadedSessionId = "";
        ClearMemoryAuthorization();
        memorySnapshot = null;
        view.MemoryAgent.ItemsSource = Array.Empty<MemoryAgentChoice>();
        RenderMemoryUnavailable("No active session is available; memory evidence was not inferred.");
    }

    private void AuthorizeMemoryAgent(string agentId)
    {
        authorizedAgentId = agentId;
        selectedMemoryId = "";
        Interlocked.Increment(ref memoryScopeGeneration);
    }

    private void ClearMemoryAuthorization()
    {
        authorizedAgentId = "";
        selectedMemoryId = "";
        Interlocked.Increment(ref memoryScopeGeneration);
    }

    private bool IsMemoryScopeCurrent(string sessionId, string agentId, long generation)
    {
        try
        {
            return sessionId.Equals(NormalizeSessionId(currentSessionId()), StringComparison.OrdinalIgnoreCase)
                && agentId.Equals(authorizedAgentId, StringComparison.OrdinalIgnoreCase)
                && generation == Volatile.Read(ref memoryScopeGeneration);
        }
        catch
        {
            return false;
        }
    }

    private void SetPromptStatus(string message)
    {
        view.PromptStatus.Text = message;
        AutomationProperties.SetHelpText(view.PromptStatus, message);
    }

    private void SetMemoryStatus(string message)
    {
        view.MemoryStatus.Text = message;
        AutomationProperties.SetHelpText(view.MemoryStatus, message);
    }

    private void OnPromptRefreshRequested(object? sender, EventArgs e) => RefreshPromptTraces();

    private void OnPromptClearRequested(object? sender, EventArgs e) => ClearPromptTraces();

    private void OnPromptSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderPromptTrace((view.PromptList.SelectedItem as PromptTraceListItem)?.Trace);

    private async void OnMemoryRefreshRequested(object? sender, EventArgs e) =>
        await ObserveUiOperationAsync(RefreshMemoryAsync());

    private void OnMemoryAgentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (view.MemoryAgent.SelectedValue is string agentId
            && !agentId.Equals(authorizedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            SelectAuthorizedAgent(agentId);
        }
    }

    private void OnMemoryStateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedComboTag(view.MemoryState) is { } filter)
        {
            SetMemoryFilter(filter);
        }
    }

    private void OnMemorySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RenderSelectedMemory(view.MemoryList.SelectedItem as MemoryEntryListItem);

    private async void OnMemoryAddRequested(object? sender, EventArgs e) =>
        await ObserveUiOperationAsync(AddMemoryAsync(
            view.MemoryEditor.Text,
            SelectedComboTag(view.MemoryVisibility) ?? StructuredMemoryVisibilities.Private,
            SelectedExpiry()));

    private async void OnMemoryCorrectRequested(object? sender, EventArgs e) =>
        await ObserveUiOperationAsync(CorrectMemoryAsync(
            selectedMemoryId,
            view.MemoryEditor.Text,
            SelectedComboTag(view.MemoryVisibility) ?? StructuredMemoryVisibilities.Private,
            SelectedExpiry()));

    private async void OnMemoryExpireRequested(object? sender, EventArgs e) =>
        await ObserveUiOperationAsync(ExpireMemoryAsync(selectedMemoryId));

    private async Task ObserveUiOperationAsync(Task<InspectionOperationResult> operation)
    {
        try
        {
            var result = await operation;
            if (!result.Ok)
            {
                SetMemoryStatus(result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            SetMemoryStatus("The operation was cancelled; completion was not claimed.");
        }
        catch (Exception)
        {
            SetMemoryStatus("The operation did not complete. Exception details were withheld from the privacy-safe UI; refresh before retrying.");
        }
    }

    private TimeSpan? SelectedExpiry()
    {
        var tag = SelectedComboTag(view.MemoryExpiry);
        return int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? TimeSpan.FromHours(hours)
            : null;
    }

    private static IEnumerable<T> EnumerateItems<T>(ItemsControl control)
    {
        foreach (var item in control.Items)
        {
            if (item is T typed)
            {
                yield return typed;
            }
        }
    }

    private static bool TryParseFilter(string? value, out MemoryEntryStateFilter filter)
    {
        filter = (value ?? "").Trim().ToLowerInvariant() switch
        {
            "active" => MemoryEntryStateFilter.Active,
            "expired" => MemoryEntryStateFilter.Expired,
            "superseded" => MemoryEntryStateFilter.Superseded,
            "all" => MemoryEntryStateFilter.All,
            _ => (MemoryEntryStateFilter)(-1)
        };
        return filter is MemoryEntryStateFilter.Active
            or MemoryEntryStateFilter.Expired
            or MemoryEntryStateFilter.Superseded
            or MemoryEntryStateFilter.All;
    }

    private static string FilterTag(MemoryEntryStateFilter filter) => filter switch
    {
        MemoryEntryStateFilter.Active => "active",
        MemoryEntryStateFilter.Expired => "expired",
        MemoryEntryStateFilter.Superseded => "superseded",
        _ => "all"
    };

    private static void SelectComboTag(ComboBox picker, string tag)
    {
        picker.SelectedItem = picker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? SelectedComboTag(ComboBox picker) =>
        picker.SelectedValue?.ToString()
        ?? (picker.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string NormalizeSessionId(string? value) => (value ?? "").Trim();

    private static string NormalizeEvidenceState(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "observed" => "observed",
        "inferred" => "inferred",
        _ => "unavailable"
    };

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string SafeAgentLabel(DialogueAgent agent)
    {
        var name = InspectionTextSafety.RedactAndBound(agent.Name, 80, "Unnamed agent");
        var id = InspectionTextSafety.RedactAndBound(agent.Id, 80, "unavailable");
        return $"{name} ({id})";
    }

    private static string MemoryFailureMessage(Exception exception, string operation) => exception switch
    {
        SnapshotConcurrencyException => "Memory change not applied because the session changed concurrently. Refresh and retry.",
        UnauthorizedAccessException => "Memory evidence is unavailable because the session store denied access. No private path details are displayed.",
        IOException => "Memory evidence is unavailable because session storage could not be read or written. No private path details are displayed.",
        ArgumentException => "Memory change not applied because the supplied values failed validation.",
        InvalidOperationException => "Memory change not applied because the selected record is missing, superseded, or no longer editable.",
        _ when operation.Equals("refresh", StringComparison.Ordinal) => "Memory refresh did not complete. Exception details were withheld from the privacy-safe UI.",
        _ => "Memory change did not complete. Exception details were withheld from the privacy-safe UI."
    };

    private static string BoundDisplay(string? value, int maximum, string fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        return text.Length <= maximum ? text : $"{text[..maximum]}… [display truncated]";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

internal sealed record InspectionOperationResult(bool Ok, string Code, string Message, string EntityId = "")
{
    internal static InspectionOperationResult Success(string code, string message, string? entityId = null) =>
        new(true, code, message, entityId ?? "");

    internal static InspectionOperationResult Failure(string code, string message) =>
        new(false, code, message);
}

internal enum MemoryEntryStateFilter
{
    Active,
    Expired,
    Superseded,
    All
}

internal sealed record MemoryAgentChoice(string AgentId, string DisplayName);

internal sealed record PromptTraceListItem(
    string RequestId,
    string CorrelationId,
    string Phase,
    string AttemptLabel,
    string ModelTransport,
    string OutcomeLabel,
    string AutomationName,
    string AutomationHelp,
    ProviderRequestTrace Trace)
{
    internal static PromptTraceListItem From(ProviderRequestTrace trace)
    {
        var phase = InspectionTextSafety.RedactAndBound(trace.Phase, 100, "unavailable phase");
        var model = InspectionTextSafety.RedactAndBound(trace.Model, 200, "model unavailable");
        var transport = InspectionTextSafety.RedactAndBound(trace.Transport, 100, "transport unavailable");
        var outcome = InspectionTextSafety.RedactAndBound(trace.Outcome, 300, "outcome unavailable");
        var request = InspectionTextSafety.RedactAndBound(trace.RequestId, 200, "unavailable");
        var correlation = InspectionTextSafety.RedactAndBound(trace.CorrelationId, 200, "unavailable");
        var attempt = Math.Max(1, trace.Attempt);
        var bytes = Math.Max(0, trace.PayloadByteCount);
        return new PromptTraceListItem(
            request,
            correlation,
            phase,
            $"attempt {attempt.ToString(CultureInfo.InvariantCulture)}",
            $"{model} · {transport}",
            outcome,
            $"{phase}, {model}, attempt {attempt.ToString(CultureInfo.InvariantCulture)}, {outcome}",
            $"Exact outbound body evidence: {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes. Select for hash, token evidence, redacted payload, role transformations, and context omissions.",
            trace);
    }
}

internal static partial class InspectionTextSafety
{
    [GeneratedRegex("""(?im)^.*\bvisibility\s*=\s*private\b.*$""", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateMemoryLineRegex();

    [GeneratedRegex("""(?i)("?(?:api[_-]?key|api[_-]?token|authorization|password|private[_-]?key|refresh[_-]?token|secret|session[_-]?token|token)"?\s*[:=]\s*"?)[^",\s}\]]+""", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex("""(?i)\b(?:sk|pk|api)[-_][A-Za-z0-9_-]{8,}\b""", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex("""(?i)\bhttps?://[^\s"'<>]+""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("""(?i)(?<![A-Za-z0-9_])[A-Za-z]:\\(?:[^\s"'<>|]+\\)*[^\s"'<>|]*""", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("""(?i)\\\\[^\s\\/]+\\[^\s"'<>|]+(?:\\[^\s"'<>|]+)*""", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex("""(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b""", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    internal static string RedactAndBound(string? value, int maximum, string fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        // ProviderRequestTraceStore is the authoritative boundary-aware
        // redactor for scoped-memory sections. Reapplying a greedy UI regex
        // here would erase later public transcript content from an already-safe
        // preview, so this layer only applies idempotent field/line redactions.
        text = PrivateMemoryLineRegex().Replace(text, "[REDACTED:PRIVATE_MEMORY]");
        text = NamedSecretRegex().Replace(text, "$1[REDACTED:SECRET]");
        text = CredentialRegex().Replace(text, "[REDACTED:SECRET]");
        text = UrlRegex().Replace(text, "[REDACTED:URL]");
        text = WindowsPathRegex().Replace(text, "[REDACTED:PATH]");
        text = UncPathRegex().Replace(text, "[REDACTED:PATH]");
        text = EmailRegex().Replace(text, "[REDACTED:EMAIL]");
        maximum = Math.Clamp(maximum, 1, 64 * 1024);
        return text.Length <= maximum ? text : $"{text[..maximum]}… [display truncated]";
    }
}

internal sealed record MemoryEntryListItem(
    string MemoryId,
    string Text,
    string Origin,
    string Visibility,
    MemoryEntryStateFilter State,
    double RevisedAt,
    string OriginLabel,
    string StateLabel,
    string ScopeLabel,
    string Metadata,
    string AutomationName,
    string AutomationHelp)
{
    internal static MemoryEntryListItem From(
        DialogueAgent owner,
        StructuredMemoryEntry entry,
        MemoryEntryStateFilter state)
    {
        var normalizedOrigin = StructuredMemoryOrigins.IsKnown(entry.Origin)
            ? entry.Origin
            : StructuredMemoryOrigins.LegacyUnknown;
        var legacy = normalizedOrigin.Equals(StructuredMemoryOrigins.LegacyUnknown, StringComparison.OrdinalIgnoreCase);
        var originLabel = legacy
            ? "Legacy / provenance unavailable"
            : entry.IsCorrection
                ? $"Correction · {normalizedOrigin}"
                : normalizedOrigin;
        var stateLabel = state switch
        {
            MemoryEntryStateFilter.Active => "ACTIVE",
            MemoryEntryStateFilter.Expired => "EXPIRED",
            MemoryEntryStateFilter.Superseded => "SUPERSEDED",
            _ => "UNAVAILABLE"
        };
        var visibility = StructuredMemoryVisibilities.IsKnown(entry.Visibility)
            ? entry.Visibility
            : StructuredMemoryVisibilities.Private;
        var branch = string.IsNullOrWhiteSpace(entry.BranchId)
            ? "root / unlabelled branch"
            : InspectionTextSafety.RedactAndBound(entry.BranchId, 180, "unavailable branch");
        var source = FormatSource(entry, legacy);
        var ownerName = InspectionTextSafety.RedactAndBound(owner.Name, 100, "Unnamed agent");
        var ownerId = InspectionTextSafety.RedactAndBound(owner.Id, 100, "unavailable");
        var displayMemoryId = InspectionTextSafety.RedactAndBound(entry.MemoryId, 200, "unavailable");
        var supersedesId = string.IsNullOrWhiteSpace(entry.SupersedesMemoryId)
            ? "none"
            : InspectionTextSafety.RedactAndBound(entry.SupersedesMemoryId, 200, "unavailable");
        var correctionOfId = string.IsNullOrWhiteSpace(entry.CorrectionOfMemoryId)
            ? "none"
            : InspectionTextSafety.RedactAndBound(entry.CorrectionOfMemoryId, 200, "unavailable");
        var metadata = string.Join(
            Environment.NewLine,
            $"Owner: {ownerName} ({ownerId})",
            $"State: {stateLabel}",
            $"Origin: {(legacy ? "legacy_unknown — original provenance is unprovable" : normalizedOrigin)}",
            $"Visibility: {visibility}",
            $"Created: {FormatTimestamp(entry.CreatedAt)}",
            $"Revised: {FormatTimestamp(entry.RevisedAt)}",
            $"Expires: {(entry.ExpiresAt is null ? "never" : FormatTimestamp(entry.ExpiresAt.Value))}",
            $"Branch: {branch}",
            $"Source: {source}",
            $"Correction: {(entry.IsCorrection ? "yes" : "no")}",
            $"Supersedes: {supersedesId}",
            $"Correction of: {correctionOfId}",
            $"Memory id: {displayMemoryId}");
        return new MemoryEntryListItem(
            entry.MemoryId,
            entry.Text ?? "",
            normalizedOrigin,
            visibility,
            state,
            Math.Max(entry.RevisedAt, entry.CreatedAt),
            originLabel,
            stateLabel,
            $"{visibility} · {branch}",
            metadata,
            $"{originLabel}, {stateLabel}, {visibility} memory",
            legacy
                ? "Legacy memory. Content is scoped to the selected owner, but its original source and creation context may be unprovable."
                : $"{stateLabel} {visibility} memory with {normalizedOrigin} provenance. Select for source, branch, expiry, and correction evidence.");
    }

    private static string FormatSource(StructuredMemoryEntry entry, bool legacy)
    {
        var parts = new List<string>();
        if (entry.SourceTurn is not null)
        {
            parts.Add($"turn {entry.SourceTurn.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(entry.SourceMessageId))
        {
            parts.Add($"message {InspectionTextSafety.RedactAndBound(entry.SourceMessageId, 200, "unavailable")}");
        }
        if (parts.Count == 0)
        {
            return legacy ? "unavailable — legacy origin cannot prove a source" : "unavailable — no source was recorded";
        }

        var joined = string.Join(", ", parts);
        return legacy ? $"retained hint: {joined}; legacy origin remains unprovable" : joined;
    }

    private static string FormatTimestamp(double unixSeconds)
    {
        if (unixSeconds <= 0 || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return "unavailable";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(unixSeconds))
                .ToUniversalTime()
                .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "unavailable";
        }
    }
}
