using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>Reports conversation storage failures without throwing while reporting another failure.</summary>
internal readonly record struct ConversationPersistenceResult(bool Ok, string Message)
{
    internal static ConversationPersistenceResult Success { get; } = new(true, "");

    internal static ConversationPersistenceResult TrySave(Action save, AppErrorContext context)
    {
        try
        {
            save();
            return Success;
        }
        catch (Exception exception)
        {
            return new(false, AppErrorPresenter.Present(exception, context).DisplayText);
        }
    }

    internal string WithOutcome(string outcome) => Ok ? outcome : $"{outcome} {Message}";
}
