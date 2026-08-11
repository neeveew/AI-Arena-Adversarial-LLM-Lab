using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Shared, responsive page-header contract: one title, one purpose line, an
/// optional truthful status, and one contextual primary action.
/// </summary>
public partial class WorkspacePageHeaderControl : UserControl
{
    internal const double CompactLayoutThreshold = 780;
    private string presentedStatus = "";

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty StatusKindProperty = DependencyProperty.Register(
        nameof(StatusKind), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("Status", OnPresentationPropertyChanged));

    public static readonly DependencyProperty PrimaryActionTextProperty = DependencyProperty.Register(
        nameof(PrimaryActionText), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty PrimaryActionAutomationNameProperty = DependencyProperty.Register(
        nameof(PrimaryActionAutomationName), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty PrimaryActionHelpTextProperty = DependencyProperty.Register(
        nameof(PrimaryActionHelpText), typeof(string), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata("", OnPresentationPropertyChanged));

    public static readonly DependencyProperty IsPrimaryActionEnabledProperty = DependencyProperty.Register(
        nameof(IsPrimaryActionEnabled), typeof(bool), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompactPresentationProperty = DependencyProperty.Register(
        nameof(IsCompactPresentation), typeof(bool), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata(false, OnPresentationPropertyChanged));

    public static readonly DependencyProperty AnnounceStatusChangesProperty = DependencyProperty.Register(
        nameof(AnnounceStatusChanges), typeof(bool), typeof(WorkspacePageHeaderControl),
        new PropertyMetadata(true, OnPresentationPropertyChanged));

    public static readonly RoutedEvent PrimaryActionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(PrimaryActionRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(WorkspacePageHeaderControl));

    public WorkspacePageHeaderControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyPresentation();
            ApplyResponsiveLayout(ActualWidth > 0 && ActualWidth < CompactLayoutThreshold);
        };
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string StatusKind { get => (string)GetValue(StatusKindProperty); set => SetValue(StatusKindProperty, value); }
    public string PrimaryActionText { get => (string)GetValue(PrimaryActionTextProperty); set => SetValue(PrimaryActionTextProperty, value); }
    public string PrimaryActionAutomationName { get => (string)GetValue(PrimaryActionAutomationNameProperty); set => SetValue(PrimaryActionAutomationNameProperty, value); }
    public string PrimaryActionHelpText { get => (string)GetValue(PrimaryActionHelpTextProperty); set => SetValue(PrimaryActionHelpTextProperty, value); }
    public bool IsPrimaryActionEnabled { get => (bool)GetValue(IsPrimaryActionEnabledProperty); set => SetValue(IsPrimaryActionEnabledProperty, value); }
    public bool IsCompactPresentation { get => (bool)GetValue(IsCompactPresentationProperty); set => SetValue(IsCompactPresentationProperty, value); }
    public bool AnnounceStatusChanges { get => (bool)GetValue(AnnounceStatusChangesProperty); set => SetValue(AnnounceStatusChangesProperty, value); }

    public event RoutedEventHandler PrimaryActionRequested
    {
        add => AddHandler(PrimaryActionRequestedEvent, value);
        remove => RemoveHandler(PrimaryActionRequestedEvent, value);
    }

    internal bool UsesCompactLayout => Grid.GetRow(PrimaryActionButton) == 1;
    internal Button PrimaryAction => PrimaryActionButton;

    internal void ApplyResponsiveLayout(bool compact)
    {
        var wrapAction = compact && !IsCompactPresentation;
        Grid.SetRow(PrimaryActionButton, wrapAction ? 1 : 0);
        Grid.SetColumn(PrimaryActionButton, wrapAction ? 0 : 1);
        ActionColumn.Width = wrapAction ? new GridLength(0) : GridLength.Auto;
        CompactActionRow.Height = wrapAction && PrimaryActionButton.Visibility == Visibility.Visible
            ? GridLength.Auto
            : new GridLength(0);
        PrimaryActionButton.HorizontalAlignment = wrapAction ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        PrimaryActionButton.Margin = wrapAction
            ? new Thickness(0, 8, 0, 0)
            : IsCompactPresentation
                ? new Thickness(8, 0, 0, 0)
                : new Thickness(12, 0, 0, 0);
    }

    private static void OnPresentationPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WorkspacePageHeaderControl header && header.IsInitialized)
        {
            header.ApplyPresentation();
        }
    }

    private void ApplyPresentation()
    {
        var title = (Title ?? "").Trim();
        var purpose = (Description ?? "").Trim();
        var status = (Status ?? "").Trim();
        var action = (PrimaryActionText ?? "").Trim();
        var statusChanged = !string.Equals(presentedStatus, status, StringComparison.Ordinal);
        presentedStatus = status;

        StatusChip.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PrimaryActionButton.Visibility = action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CompactActionRow.Height = UsesCompactLayout && action.Length > 0 ? GridLength.Auto : new GridLength(0);
        ApplyPresentationDensity();
        ApplyResponsiveLayout(ActualWidth > 0 && ActualWidth < CompactLayoutThreshold);

        AutomationProperties.SetName(this, title.Length == 0 ? "Workspace page header" : $"{title} page header");
        AutomationProperties.SetHelpText(this, purpose);
        AutomationProperties.SetName(StatusText, status.Length == 0 ? "Workspace status" : $"{title} status: {status}");
        AutomationProperties.SetHelpText(StatusText, status);
        AutomationProperties.SetItemStatus(StatusText, string.IsNullOrWhiteSpace(StatusKind) ? "status" : StatusKind.Trim());
        AutomationProperties.SetLiveSetting(
            StatusText,
            AnnounceStatusChanges ? AutomationLiveSetting.Polite : AutomationLiveSetting.Off);
        ToolTipService.SetToolTip(StatusChip, status.Length == 0 ? null : status);
        AutomationProperties.SetName(PrimaryActionButton,
            string.IsNullOrWhiteSpace(PrimaryActionAutomationName) ? action : PrimaryActionAutomationName.Trim());
        AutomationProperties.SetHelpText(PrimaryActionButton,
            string.IsNullOrWhiteSpace(PrimaryActionHelpText) ? action : PrimaryActionHelpText.Trim());

        if (statusChanged && status.Length > 0 && IsLoaded)
        {
            ArenaMotion.StatusChanged(StatusChip);
            if (AnnounceStatusChanges)
            {
                UIElementAutomationPeer.CreatePeerForElement(StatusText)?
                    .RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private void ApplyPresentationDensity()
    {
        var compact = IsCompactPresentation;
        Grid.SetColumnSpan(TitleStatusPanel, compact ? 1 : 2);
        Grid.SetRow(DescriptionText, compact ? 0 : 1);
        Grid.SetColumn(DescriptionText, compact ? 1 : 0);
        Grid.SetColumnSpan(DescriptionText, compact ? 1 : 2);
        DescriptionText.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        DescriptionText.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        DescriptionText.TextWrapping = TextWrapping.NoWrap;
        DescriptionText.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;

        if (compact)
        {
            HeaderChrome.SetResourceReference(Border.PaddingProperty, "Arena.Inset.PageHeader.Compact");
            HeaderChrome.SetResourceReference(Border.MarginProperty, "Arena.Layout.PageHeaderCompactMargin");
            TitleText.SetResourceReference(TextBlock.FontSizeProperty, "Arena.Type.TitleSize");
            DescriptionText.SetResourceReference(TextBlock.FontSizeProperty, "Arena.Type.CompactBodySize");
            DescriptionText.SetResourceReference(TextBlock.MarginProperty, "Arena.Gap.Inline.Before.Spacious");
            PrimaryActionButton.SetResourceReference(Button.MinHeightProperty, "Arena.Target.Compact");
            PrimaryActionButton.SetResourceReference(Button.PaddingProperty, "Arena.Inset.CompactAction");
            PrimaryActionButton.MinWidth = 88;
            PrimaryActionButton.VerticalAlignment = VerticalAlignment.Center;
            return;
        }

        HeaderChrome.ClearValue(Border.PaddingProperty);
        HeaderChrome.ClearValue(Border.MarginProperty);
        TitleText.ClearValue(TextBlock.FontSizeProperty);
        DescriptionText.ClearValue(TextBlock.FontSizeProperty);
        DescriptionText.SetResourceReference(TextBlock.MarginProperty, "Arena.Gap.Stack.Before.Micro");
        PrimaryActionButton.SetResourceReference(Button.MinHeightProperty, "Arena.Target.Primary");
        PrimaryActionButton.ClearValue(Button.PaddingProperty);
        PrimaryActionButton.MinWidth = 104;
        PrimaryActionButton.VerticalAlignment = VerticalAlignment.Top;
    }

    private void WorkspacePageHeaderControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width > 0 && e.NewSize.Width < CompactLayoutThreshold);

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e) =>
        RaiseEvent(new RoutedEventArgs(PrimaryActionRequestedEvent, this));
}
