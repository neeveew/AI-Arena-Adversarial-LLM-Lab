using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>Retains the first projection error without changing the authoritative outcome.</summary>
internal sealed class ConversationPresentationCompletion(AppErrorContext context)
{
    private Exception? failure;

    internal bool HasFailure => failure is not null;

    internal void Try(Action presentation)
    {
        try { presentation(); }
        catch (Exception exception) { failure ??= exception; }
    }

    internal string Warning(bool saved) => failure is null ? "" :
        AppPostCommitEvidence.CompletionWarning(failure,
            "the conversation view could not be fully updated", context, saved);
}
