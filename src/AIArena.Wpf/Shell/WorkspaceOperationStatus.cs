using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Keeps workspace operation receipts separate from incidental UI notices.
/// Only the operation's owner can advance its typed lifecycle; display text
/// (including a copied conversation title) never determines its state.
/// </summary>
internal sealed class WorkspaceOperationStatus(
    ApplicationStatusCenter center,
    string keyPrefix,
    string source,
    string navigationTarget,
    Func<ApplicationStatusIdentity> identity)
{
    public ApplicationStatusReceipt Begin(string summary, string operation = "run") =>
        center.Begin($"{keyPrefix}.{operation}", source, summary,
            navigationTarget: navigationTarget, identity: identity());

    public bool Update(ApplicationStatusReceipt receipt, string summary, string? detail = null) =>
        center.Update(receipt, summary, detail);

    public bool Complete(ApplicationStatusReceipt receipt, string summary, string? detail = null) =>
        center.Complete(receipt, summary, detail);

    public bool Fail(ApplicationStatusReceipt receipt, string summary, string? detail = null) =>
        center.Fail(receipt, summary, detail);

    public bool Cancel(ApplicationStatusReceipt receipt, string summary, string? detail = null) =>
        center.Cancel(receipt, summary, detail);

    public bool MarkUnconfirmed(ApplicationStatusReceipt receipt, string summary, string? detail = null) =>
        center.MarkUnconfirmed(receipt, summary, detail);

    public void PublishNotice(string summary, ApplicationStatusState state = ApplicationStatusState.Info,
        string? detail = null)
    {
        // A notice is deliberately unable to begin or finish a run. Persistent
        // failures still use the center's existing priority/redaction policy.
        if (state == ApplicationStatusState.Running)
        {
            throw new ArgumentException("Running work requires an owned operation receipt.", nameof(state));
        }

        center.PublishNotice($"{keyPrefix}.notice", source, state, summary, detail,
            navigationTarget: navigationTarget, identity: identity());
    }
}
