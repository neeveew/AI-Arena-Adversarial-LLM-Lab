using System.ComponentModel;
using System.Windows;

namespace AIArena.Wpf.Services;

internal static class SystemMotionPreferences
{
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public static event PropertyChangedEventHandler PreferenceChanged
    {
        add => SystemParameters.StaticPropertyChanged += value;
        remove => SystemParameters.StaticPropertyChanged -= value;
    }

    internal static bool IsAnimationPreferenceChange(string? propertyName)
    {
        return string.IsNullOrEmpty(propertyName)
            || propertyName.Equals(nameof(SystemParameters.ClientAreaAnimation), StringComparison.Ordinal);
    }
}
