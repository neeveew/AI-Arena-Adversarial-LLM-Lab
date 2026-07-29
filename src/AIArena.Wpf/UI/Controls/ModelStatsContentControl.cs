using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Gives the focusable transcript model-stat capsule a native UI Automation peer.
/// Plain ContentControl does not reliably enter the desktop automation tree.
/// </summary>
internal sealed class ModelStatsContentControl : ContentControl
{
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }
}
