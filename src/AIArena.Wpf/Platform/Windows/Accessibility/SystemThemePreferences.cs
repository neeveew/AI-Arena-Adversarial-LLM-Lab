using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace AIArena.Wpf.Services;

internal static class SystemThemePreferences
{
    private const string PersonalizeKeyPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static PropertyChangedEventHandler? _appThemeHandlers;
    private static bool _systemEventsHooked;

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>
    /// Windows app-mode preference. Defaults to dark when the value is missing
    /// or unreadable so the established dark palettes remain the fallback.
    /// </summary>
    public static bool AppsUseLightTheme
    {
        get
        {
            try
            {
                return Registry.GetValue(PersonalizeKeyPath, "AppsUseLightTheme", 0) is int value && value != 0;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or System.IO.IOException)
            {
                return false;
            }
        }
    }

    public static event PropertyChangedEventHandler PreferenceChanged
    {
        add
        {
            SystemParameters.StaticPropertyChanged += value;
            _appThemeHandlers += value;
            EnsureSystemEventsHook();
        }
        remove
        {
            SystemParameters.StaticPropertyChanged -= value;
            _appThemeHandlers -= value;
        }
    }

    internal static bool IsThemePreferenceChange(string? propertyName)
    {
        return string.IsNullOrEmpty(propertyName)
            || propertyName.Equals(nameof(SystemParameters.HighContrast), StringComparison.Ordinal)
            || propertyName.Equals(nameof(AppsUseLightTheme), StringComparison.Ordinal);
    }

    private static void EnsureSystemEventsHook()
    {
        if (_systemEventsHooked)
        {
            return;
        }

        _systemEventsHooked = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows raises General when the app light/dark mode flips.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
        {
            _appThemeHandlers?.Invoke(null, new PropertyChangedEventArgs(nameof(AppsUseLightTheme)));
        }
    }
}
