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
    private string viewButtonLabel = "View: Custom";
    private bool showStatusLine;
    private bool forceStatusLine;

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
        set
        {
            SetField(ref arenaStatus, value);
            UpdateStatusLineVisibility();
        }
    }

    public bool ShowStatusLine
    {
        get => showStatusLine;
        private set => SetField(ref showStatusLine, value);
    }

    public string ViewButtonLabel
    {
        get => viewButtonLabel;
        set => SetField(ref viewButtonLabel, value);
    }

    public void SetTransientStatusVisible(bool visible)
    {
        forceStatusLine = visible;
        UpdateStatusLineVisibility();
    }

    internal static bool IsRoutineStatus(string? status)
    {
        var normalized = status?.Trim() ?? string.Empty;
        return normalized.Length == 0
            || normalized.Equals("Ready.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online.", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Provider online", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatusLineVisibility()
    {
        ShowStatusLine = forceStatusLine || !IsRoutineStatus(arenaStatus);
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
