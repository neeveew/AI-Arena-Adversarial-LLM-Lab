using System.Collections.Specialized;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// A single-selection list whose visible and automation state always retain one
/// selected option while items are available.
/// </summary>
public sealed class RequiredSelectionListBox : ListBox
{
    private bool restoringSelection;

    public RequiredSelectionListBox()
    {
        SelectionMode = SelectionMode.Single;
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        EnsureSelection();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        EnsureSelection();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        EnsureSelection();
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new RequiredSelectionListBoxAutomationPeer(this);

    private void EnsureSelection()
    {
        if (restoringSelection || Items.Count == 0 || SelectedIndex >= 0)
        {
            return;
        }

        try
        {
            restoringSelection = true;
            SelectedIndex = 0;
        }
        finally
        {
            restoringSelection = false;
        }
    }

    private sealed class RequiredSelectionListBoxAutomationPeer(RequiredSelectionListBox owner)
        : ListBoxAutomationPeer(owner)
    {
        private ISelectionProvider? requiredSelectionProvider;

        public override object? GetPattern(PatternInterface patternInterface)
        {
            var pattern = base.GetPattern(patternInterface);
            if (patternInterface != PatternInterface.Selection || pattern is not ISelectionProvider selectionProvider)
            {
                return pattern;
            }

            return requiredSelectionProvider ??= new RequiredSelectionProvider(selectionProvider);
        }
    }

    private sealed class RequiredSelectionProvider(ISelectionProvider inner) : ISelectionProvider
    {
        public bool CanSelectMultiple => inner.CanSelectMultiple;
        public bool IsSelectionRequired => true;
        public IRawElementProviderSimple[] GetSelection() => inner.GetSelection();
    }
}
