using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Visual card surface that also exposes one stable UI Automation grouping peer.
/// Standard Border is deliberately lightweight and has no peer of its own, so
/// aggregate card names and evidence summaries attached to it are otherwise lost.
/// </summary>
public sealed class AccessibleCardBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new AccessibleCardBorderAutomationPeer(this);
}

internal sealed class AccessibleCardBorderAutomationPeer(AccessibleCardBorder owner)
    : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(AccessibleCardBorder);

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
