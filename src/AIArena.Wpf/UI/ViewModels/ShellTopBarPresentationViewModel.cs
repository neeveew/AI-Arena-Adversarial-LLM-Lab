using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIArena.Wpf.ViewModels;

/// <summary>
/// Presentation-only state for the persistent shell header. Compatibility targets in
/// <see cref="Controls.ShellTopBarControl"/> mirror the existing coordinators into
/// these bindable values while the shell completes its MVVM migration.
/// </summary>
public sealed class ShellTopBarPresentationViewModel : INotifyPropertyChanged
{
    private string matchValue = "-";
    private string providerValue = "-";
    private string currentTurnValue = "-";
    private string turnCountValue = "0";
    private string arenaStatus = "Ready.";
    private string displayStatus = "Ready.";
    private string displayStatusToolTip = "Ready.";
    private string displayStatusHelpText = "Current arena status: Ready.";
    private bool showStatusDock;
    private string viewButtonLabel = "View: Custom";
    private string transientStatus = "";
    private string transientStatusToolTip = "";
    private string transientStatusHelpText = "";
    private long nextTransientStatusGeneration;
    private long activeTransientStatusGeneration;

    public event PropertyChangedEventHandler? PropertyChanged;

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
        var normalized = NormalizeStatus(status);
        var generation = ++nextTransientStatusGeneration;
        activeTransientStatusGeneration = generation;
        transientStatus = normalized;
        transientStatusToolTip = NormalizeDetail(toolTip, normalized);
        transientStatusHelpText = NormalizeDetail(helpText, transientStatusToolTip);
        UpdateDisplayStatus();
        return generation;
    }

    internal bool ClearTransientStatus(long generation)
    {
        if (generation <= 0 || generation != activeTransientStatusGeneration)
        {
            return false;
        }

        activeTransientStatusGeneration = 0;
        transientStatus = "";
        transientStatusToolTip = "";
        transientStatusHelpText = "";
        UpdateDisplayStatus();
        return true;
    }

    private void SetPersistentStatus(string? status)
    {
        var normalized = NormalizeStatus(status);
        SetField(ref arenaStatus, normalized, nameof(ArenaStatus));
        if (activeTransientStatusGeneration == 0)
        {
            UpdateDisplayStatus();
        }
    }

    private void UpdateDisplayStatus()
    {
        var transientActive = activeTransientStatusGeneration != 0;
        var status = transientActive ? transientStatus : arenaStatus;
        var toolTip = transientActive ? transientStatusToolTip : status;
        var helpText = transientActive
            ? transientStatusHelpText
            : $"Current arena status: {status}";
        var shouldShowStatusDock = transientActive || !IsRoutineStatus(status);
        if (shouldShowStatusDock && !ShowStatusDock)
        {
            // Reveal first so a Polite live region is present when its text changes.
            ShowStatusDock = true;
        }

        DisplayStatus = status;
        DisplayStatusToolTip = toolTip;
        DisplayStatusHelpText = helpText;
        if (!shouldShowStatusDock && ShowStatusDock)
        {
            // Project the routine state before releasing the bottom-rail space.
            ShowStatusDock = false;
        }
    }

    private static string NormalizeStatus(string? status)
    {
        return string.IsNullOrWhiteSpace(status) ? "Ready." : status.Trim();
    }

    private static string NormalizeDetail(string? detail, string fallback)
    {
        return string.IsNullOrWhiteSpace(detail) ? fallback : detail.Trim();
    }

    internal static bool IsRoutineStatus(string? status)
    {
        var normalized = status?.Trim() ?? string.Empty;
        return normalized.Length == 0
            || normalized.Equals("Ready.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online", StringComparison.OrdinalIgnoreCase);
    }

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
