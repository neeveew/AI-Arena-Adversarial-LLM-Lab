using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AIArena.Wpf;

internal static class DialogChrome
{
    public static void ImportOwnerResources(Window owner, Window dialog)
    {
        if (owner is FrameworkElement ownerElement)
        {
            CopyResources(ownerElement.Resources, dialog.Resources);
        }

        if (Application.Current?.Resources is { } appResources)
        {
            CopyResources(appResources, dialog.Resources);
        }
    }

    public static void ApplyImplicitControlStyles(FrameworkElement root)
    {
        ApplyImplicitStyle<Button>(root, typeof(Button));
        ApplyImplicitStyle<TextBox>(root, typeof(TextBox));
        ApplyImplicitStyle<ComboBox>(root, typeof(ComboBox));
        ApplyImplicitStyle<CheckBox>(root, typeof(CheckBox));
    }

    public static void ApplyButtonStyle(Button button, Brush background, Brush border, Brush foreground)
    {
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.FontWeight = FontWeights.SemiBold;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.MinHeight = 38;
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
