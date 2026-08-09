using System.ComponentModel;
using System.Windows;

namespace AIArena.Wpf.Services;

internal static class SystemMotionPreferences
{
    private static readonly object Gate = new();
    private static PropertyChangedEventHandler? qaPreferenceChanged;
    private static bool? qaAnimationsEnabledOverride;

    /// <summary>
    /// The effective process motion preference. Normal application runs follow
    /// Windows. An isolated QA process can temporarily override this value in
    /// memory; the override is never written to application settings.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            lock (Gate)
            {
                return qaAnimationsEnabledOverride ?? SystemParameters.ClientAreaAnimation;
            }
        }
    }

    internal static bool SystemAnimationsEnabled => SystemParameters.ClientAreaAnimation;

    internal static string PreferenceSource
    {
        get
        {
            lock (Gate)
            {
                return qaAnimationsEnabledOverride switch
                {
                    true => "qa-normal",
                    false => "qa-reduced",
                    null => "system"
                };
            }
        }
    }

    internal static bool HasQaOverride
    {
        get
        {
            lock (Gate)
            {
                return qaAnimationsEnabledOverride.HasValue;
            }
        }
    }

    public static event PropertyChangedEventHandler PreferenceChanged
    {
        add
        {
            SystemParameters.StaticPropertyChanged += value;
            lock (Gate)
            {
                qaPreferenceChanged += value;
            }
        }
        remove
        {
            SystemParameters.StaticPropertyChanged -= value;
            lock (Gate)
            {
                qaPreferenceChanged -= value;
            }
        }
    }

    internal static void SetQaAnimationsEnabledOverride(bool? animationsEnabled)
    {
        PropertyChangedEventHandler? handlers;
        lock (Gate)
        {
            if (qaAnimationsEnabledOverride == animationsEnabled)
            {
                return;
            }

            qaAnimationsEnabledOverride = animationsEnabled;
            handlers = qaPreferenceChanged;
        }

        handlers?.Invoke(
            sender: null,
            new PropertyChangedEventArgs(nameof(AnimationsEnabled)));
    }

    internal static void ClearQaOverride()
    {
        SetQaAnimationsEnabledOverride(null);
    }

    internal static bool IsAnimationPreferenceChange(string? propertyName)
    {
        return string.IsNullOrEmpty(propertyName)
            || propertyName.Equals(nameof(SystemParameters.ClientAreaAnimation), StringComparison.Ordinal)
            || propertyName.Equals(nameof(AnimationsEnabled), StringComparison.Ordinal);
    }
}
