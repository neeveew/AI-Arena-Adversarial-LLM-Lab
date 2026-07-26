using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIArena.Wpf;

internal static class DialogChrome
{
    private const double DialogInset = 48;
    private const double CompactDialogWidth = 300;
    private const double CompactDialogHeight = 280;

    public static void ImportOwnerResources(Window owner, Window dialog)
    {
        if (Application.Current?.Resources is { } appResources)
        {
            CopyResources(appResources, dialog.Resources);
        }

        if (owner is FrameworkElement ownerElement)
        {
            CopyResources(ownerElement.Resources, dialog.Resources);
        }
    }

    public static void ApplyImplicitControlStyles(FrameworkElement root)
    {
        ApplyImplicitStyle<Button>(root, typeof(Button));
        ApplyImplicitStyle<TextBox>(root, typeof(TextBox));
        ApplyImplicitStyle<ComboBox>(root, typeof(ComboBox));
        ApplyImplicitStyle<CheckBox>(root, typeof(CheckBox));
    }

    public static void PrepareModalWindow(
        Window dialog,
        Window owner,
        FrameworkElement focusScope,
        UIElement initialFocus,
        string automationName,
        string automationHelpText)
    {
        dialog.Owner = owner;
        ConfigureModalSurface(dialog, focusScope, automationName, automationHelpText);
        ApplyResponsiveBounds(
            dialog,
            owner.ActualWidth,
            owner.ActualHeight,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);

        dialog.ContentRendered += OnContentRendered;

        void OnContentRendered(object? sender, EventArgs e)
        {
            dialog.ContentRendered -= OnContentRendered;
            ApplyResponsiveBounds(
                dialog,
                owner.ActualWidth,
                owner.ActualHeight,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);

            dialog.Dispatcher.BeginInvoke(() =>
            {
                if (dialog.IsVisible
                    && initialFocus.IsVisible
                    && initialFocus.IsEnabled
                    && initialFocus.Focusable)
                {
                    Keyboard.Focus(initialFocus);
                }
            }, DispatcherPriority.Input);
        }
    }

    internal static void ConfigureModalSurface(
        Window dialog,
        FrameworkElement focusScope,
        string automationName,
        string automationHelpText)
    {
        FocusManager.SetIsFocusScope(focusScope, true);
        KeyboardNavigation.SetTabNavigation(focusScope, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(focusScope, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(focusScope, KeyboardNavigationMode.Contained);
        AutomationProperties.SetName(dialog, automationName);
        AutomationProperties.SetHelpText(dialog, automationHelpText);
        AutomationProperties.SetName(focusScope, automationName);
        AutomationProperties.SetHelpText(focusScope, automationHelpText);
    }

    internal static void ApplyResponsiveBounds(
        Window dialog,
        double ownerWidth,
        double ownerHeight,
        double fallbackWidth,
        double fallbackHeight)
    {
        var responsiveMaxWidth = ResponsiveMaximum(ownerWidth, fallbackWidth, CompactDialogWidth);
        var responsiveMaxHeight = ResponsiveMaximum(ownerHeight, fallbackHeight, CompactDialogHeight);
        var maxWidth = Math.Min(dialog.MaxWidth, responsiveMaxWidth);
        var maxHeight = Math.Min(dialog.MaxHeight, responsiveMaxHeight);

        dialog.MinWidth = Math.Min(dialog.MinWidth, maxWidth);
        dialog.MinHeight = Math.Min(dialog.MinHeight, maxHeight);
        dialog.MaxWidth = maxWidth;
        dialog.MaxHeight = maxHeight;

        if (double.IsFinite(dialog.Width) && dialog.Width > maxWidth)
        {
            dialog.Width = maxWidth;
        }

        if (double.IsFinite(dialog.Height) && dialog.Height > maxHeight)
        {
            dialog.Height = maxHeight;
        }
    }

    internal static double ResponsiveMaximum(double ownerLength, double fallbackLength, double minimumUsableLength)
    {
        var viewportLength = double.IsFinite(ownerLength) && ownerLength > 0
            ? ownerLength
            : fallbackLength;
        if (!double.IsFinite(viewportLength) || viewportLength <= 0)
        {
            return minimumUsableLength;
        }

        return Math.Max(Math.Min(minimumUsableLength, viewportLength), viewportLength - DialogInset);
    }

    public static void ApplyButtonStyle(Button button, Brush background, Brush border, Brush foreground)
    {
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.FontWeight = FontWeights.SemiBold;
        button.FontSize = 12;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.MinHeight = 38;
    }

    public static void ApplyCloseButtonStyle(Button button, Brush background, Brush border, Brush foreground)
    {
        ApplyButtonStyle(button, background, border, foreground);
        button.Content = "\uE8BB";
        button.FontFamily = ArenaTokens.IconFontFamily;
        button.FontSize = 10;
        button.Padding = new Thickness(0);
        button.MinHeight = 30;
        button.MinWidth = 34;
        button.Width = 34;
        button.Height = 30;
        AutomationProperties.SetName(button, button.ToolTip?.ToString() is { Length: > 0 } toolTip ? toolTip : "Close");
    }

    public static void DragMoveIfPossible(Window window, MouseButtonEventArgs e, bool ignoreTextInputs = false)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !StartedOnInteractiveElement(e.OriginalSource as DependencyObject, ignoreTextInputs))
        {
            window.DragMove();
        }
    }

    private static void CopyResources(ResourceDictionary source, ResourceDictionary target)
    {
        foreach (DictionaryEntry entry in source)
        {
            target[entry.Key] = entry.Value;
        }

        foreach (var merged in source.MergedDictionaries)
        {
            CopyResources(merged, target);
        }
    }

    private static void ApplyImplicitStyle<TControl>(FrameworkElement root, object key)
        where TControl : FrameworkElement
    {
        if (!root.Resources.Contains(key) || root.Resources[key] is not Style style)
        {
            return;
        }

        foreach (var control in Descendants<TControl>(root))
        {
            if (control.Style is null)
            {
                control.Style = style;
            }
        }
    }

    private static IEnumerable<TControl> Descendants<TControl>(DependencyObject root)
        where TControl : DependencyObject
    {
        if (root is TControl typed)
        {
            yield return typed;
        }

        var visualCount = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        if (visualCount > 0)
        {
            for (var i = 0; i < visualCount; i++)
            {
                foreach (var child in Descendants<TControl>(VisualTreeHelper.GetChild(root, i)))
                {
                    yield return child;
                }
            }

            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in Descendants<TControl>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool StartedOnInteractiveElement(DependencyObject? source, bool includeTextInputs)
    {
        while (source is not null)
        {
            if (source is ButtonBase || includeTextInputs && source is TextBoxBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
