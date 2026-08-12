using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIArena.Wpf.Services;

namespace AIArena.Wpf.ViewModels;

/// <summary>
/// Presentation state for the persistent shell header and the application-wide
/// status center. Compatibility text targets feed the same typed status center.
/// </summary>
public sealed class ShellTopBarPresentationViewModel : INotifyPropertyChanged
{
    private string matchValue = "-";
    private string providerValue = "-";
    private string currentTurnValue = "-";
    private string turnCountValue = "0";
    private string arenaStatus = "Ready.";
    private string displayStatus = "Ready";
    private string displayStatusToolTip = "Ready";
    private string displayStatusHelpText = "Current application status: Ready.";
    private bool showStatusDock = true;
    private string viewButtonLabel = "View: Custom";
    private long nextLegacyTransientGeneration;
    private readonly Dictionary<long, ApplicationStatusReceipt> legacyTransientReceipts = [];
    private readonly Dictionary<string, ApplicationStatusReceipt> compatibilityReceipts = new(StringComparer.Ordinal);

    public ShellTopBarPresentationViewModel(ApplicationStatusCenter? statusCenter = null)
    {
        StatusCenter = statusCenter ?? new ApplicationStatusCenter();
        StatusCenter.Changed += StatusCenterChanged;
        ApplyStatusSnapshot(StatusCenter.Snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ApplicationStatusCenter StatusCenter { get; }

    public string MatchValue
    {
        get => matchValue;
        set => SetField(ref matchValue, value);
    }

    public string ProviderValue
    {
        get => providerValue;
        set => SetField(ref providerValue, value);
    }

    public string CurrentTurnValue
    {
        get => currentTurnValue;
        set => SetField(ref currentTurnValue, value);
    }

    public string TurnCountValue
    {
        get => turnCountValue;
        set => SetField(ref turnCountValue, value);
    }

    public string ArenaStatus
    {
        get => arenaStatus;
        set => SetPersistentStatus(value);
    }

    public string DisplayStatus
    {
        get => displayStatus;
        private set => SetField(ref displayStatus, value);
    }

    public string DisplayStatusToolTip
    {
        get => displayStatusToolTip;
        private set => SetField(ref displayStatusToolTip, value);
    }

    public string DisplayStatusHelpText
    {
        get => displayStatusHelpText;
        private set => SetField(ref displayStatusHelpText, value);
    }

    /// <summary>
    /// Compatibility property. The new four-row center always reserves its space.
    /// </summary>
    public bool ShowStatusDock
    {
        get => showStatusDock;
        private set => SetField(ref showStatusDock, value);
    }

    public string ViewButtonLabel
    {
        get => viewButtonLabel;
        set => SetField(ref viewButtonLabel, value);
    }

    internal long ShowTransientStatus(string status, string? toolTip = null, string? helpText = null)
    {
        var generation = Interlocked.Increment(ref nextLegacyTransientGeneration);
        var receipt = StatusCenter.PublishNotice(
            $"legacy.transient.{generation}",
            "App",
            ApplicationStatusState.Succeeded,
            NormalizeStatus(status),
            toolTip ?? helpText,
            lifetime: ApplicationStatusLifetime.Transient);
        legacyTransientReceipts[generation] = receipt;
        return generation;
    }

    internal bool ClearTransientStatus(long generation)
    {
        if (generation <= 0 || !legacyTransientReceipts.Remove(generation, out var receipt))
        {
            return false;
        }

        return StatusCenter.Resolve(receipt);
    }

    /// <summary>
    /// Bridges existing feature status callbacks into distinct typed operations
    /// while their coordinators are migrated incrementally. Repeated progress for
    /// one feature updates in place instead of replacing unrelated sources.
    /// </summary>
    internal void PublishCompatibilityStatus(
        string key,
        string source,
        string? status,
        string? detail = null,
        string? navigationTarget = null,
        ApplicationStatusIdentity? identity = null)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? "legacy.app" : key.Trim();
        var normalized = NormalizeStatus(status);
        if (IsRoutineStatus(normalized))
        {
            if (compatibilityReceipts.Remove(normalizedKey, out var routineReceipt))
            {
                StatusCenter.Resolve(routineReceipt);
            }
            else
            {
                StatusCenter.Resolve(normalizedKey);
            }

            return;
        }

        var state = LegacyState(normalized);
        if (state == ApplicationStatusState.Running)
        {
            if (compatibilityReceipts.TryGetValue(normalizedKey, out var active)
                && StatusCenter.Update(active, normalized, detail))
            {
                return;
            }

            compatibilityReceipts[normalizedKey] = StatusCenter.Begin(
                normalizedKey,
                source,
                normalized,
                detail,
                navigationTarget: navigationTarget,
                identity: identity);
            return;
        }

        if (compatibilityReceipts.Remove(normalizedKey, out var receipt))
        {
            var transitioned = state switch
            {
                ApplicationStatusState.Succeeded or ApplicationStatusState.Info =>
                    StatusCenter.Complete(receipt, normalized, detail),
                ApplicationStatusState.Cancelled =>
                    StatusCenter.Cancel(receipt, normalized, detail),
                ApplicationStatusState.Unconfirmed =>
                    StatusCenter.MarkUnconfirmed(receipt, normalized, detail),
                ApplicationStatusState.Blocked =>
                    StatusCenter.Fail(receipt, normalized, detail, blocked: true),
                ApplicationStatusState.Failed =>
                    StatusCenter.Fail(receipt, normalized, detail),
                _ => false
            };
            if (transitioned)
            {
                return;
            }
        }

        StatusCenter.PublishNotice(
            normalizedKey,
            source,
            state,
            normalized,
            detail,
            navigationTarget,
            identity,
            lifetime: LegacyLifetime(normalized));
    }

    private void SetPersistentStatus(string? status)
    {
        var normalized = NormalizeStatus(status);
        SetField(ref arenaStatus, normalized, nameof(ArenaStatus));
        PublishCompatibilityStatus(
            "legacy.arena",
            "Arena",
            normalized,
            navigationTarget: "arena");
    }

    private void StatusCenterChanged(object? sender, ApplicationStatusChangedEventArgs e) =>
        ApplyStatusSnapshot(e.Snapshot);

    private void ApplyStatusSnapshot(ApplicationStatusSnapshot snapshot)
    {
        DisplayStatus = snapshot.AppStatus;
        DisplayStatusToolTip = string.IsNullOrWhiteSpace(snapshot.Primary.Detail)
            ? snapshot.Primary.Summary
            : snapshot.Primary.Detail;
        DisplayStatusHelpText = $"Current application status: {snapshot.Primary.Source}, {snapshot.Primary.State}: {snapshot.Primary.Summary}";
        ShowStatusDock = true;
    }

    private static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? "Ready." : status.Trim();

    internal static bool IsRoutineStatus(string? status)
    {
        var normalized = status?.Trim() ?? string.Empty;
        return normalized.Length == 0
            || normalized.Equals("Ready.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online", StringComparison.OrdinalIgnoreCase);
    }

    internal static ApplicationStatusState LegacyState(string status)
    {
        if (status.Contains("context recovery", StringComparison.OrdinalIgnoreCase)
            || status.Contains("action required", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Blocked;
        }

        if (status.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Unconfirmed;
        }

        if (status.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Contains("stopped", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Cancelled;
        }

        if (status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Contains("could not", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Failed;
        }

        if (status.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || status.Contains("offline", StringComparison.OrdinalIgnoreCase)
            || status.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Select ", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Choose ", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("No active ", StringComparison.OrdinalIgnoreCase)
            || status.Contains("not selected", StringComparison.OrdinalIgnoreCase)
            || status.Contains("not loaded", StringComparison.OrdinalIgnoreCase)
            || status.Contains("no model", StringComparison.OrdinalIgnoreCase)
            || status.Contains(" is required", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Warning;
        }

        if (status.EndsWith("...", StringComparison.Ordinal)
            || status.Contains("running", StringComparison.OrdinalIgnoreCase)
            || status.Contains("loading", StringComparison.OrdinalIgnoreCase)
            || status.Contains("saving", StringComparison.OrdinalIgnoreCase)
            || status.Contains("stopping", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Running;
        }

        if (status.Contains("saved", StringComparison.OrdinalIgnoreCase)
            || status.Contains("completed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || status.Contains("copied", StringComparison.OrdinalIgnoreCase)
            || status.Contains("exported", StringComparison.OrdinalIgnoreCase)
            || status.Contains("loaded", StringComparison.OrdinalIgnoreCase)
            || status.Contains("sent", StringComparison.OrdinalIgnoreCase)
            || status.Contains("updated", StringComparison.OrdinalIgnoreCase)
            || status.Contains("applied", StringComparison.OrdinalIgnoreCase)
            || status.Contains("restored", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationStatusState.Succeeded;
        }

        return ApplicationStatusState.Info;
    }

    private static ApplicationStatusLifetime LegacyLifetime(string status) =>
        LegacyState(status) switch
        {
            ApplicationStatusState.Failed or
            ApplicationStatusState.Warning or
            ApplicationStatusState.Blocked or
            ApplicationStatusState.Unconfirmed => ApplicationStatusLifetime.UntilResolved,
            ApplicationStatusState.Running => ApplicationStatusLifetime.UntilSuperseded,
            _ => ApplicationStatusLifetime.Transient
        };

    private void SetField(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        var normalized = value ?? string.Empty;
        if (string.Equals(field, normalized, StringComparison.Ordinal))
        {
            return;
        }

        field = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
