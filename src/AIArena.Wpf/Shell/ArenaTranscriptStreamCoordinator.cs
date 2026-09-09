using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

/// <summary>One ephemeral Arena response, batched onto the dispatcher and reconciled with its saved card.</summary>
internal sealed class ArenaTranscriptStreamCoordinator(
    Dispatcher dispatcher,
    Func<(string SessionId, string InstanceId)> sessionIdentity,
    Func<TranscriptMessage, TranscriptCardRenderer.LiveCard> createCard,
    Action refreshRows)
{
    private StreamOperation? active;
    private (string SessionId, string InstanceId) identity;
    private ContentControl? host;
    private TranscriptCardRenderer.LiveCard? card;
    private ArenaTurnProgress? current;
    private TranscriptMessage? renderedFinal;
    private object? renderedRow;
    private bool failedPreview;
    private readonly StringBuilder text = new();
    private bool completed;
    private bool interrupted;

    internal bool HasCard => host is not null;
    internal bool HasUncommittedCard => host is not null && (!completed || renderedFinal is null);

    internal StreamOperation Begin()
    {
        SynchronizeSession();
        active?.Dispose();
        return active = new StreamOperation(this, dispatcher);
    }

    internal void SynchronizeSession()
    {
        var next = sessionIdentity();
        if (identity == next) return;
        identity = next;
        active?.Detach();
        active = null;
        ClearCard();
    }

    internal void Clear()
    {
        active?.Detach();
        active = null;
        ClearCard();
        refreshRows();
    }

    private void ClearCard()
    {
        host = null;
        card = null;
        current = null;
        renderedFinal = null;
        renderedRow = null;
        failedPreview = false;
        text.Clear();
        completed = interrupted = false;
    }

    internal void MergeRows(List<object> rows, Func<object, TranscriptMessage?> messageForRow,
        Func<object, UIElement> renderRow, IReadOnlyList<TranscriptMessage>? persistedMessages = null)
    {
        SynchronizeSession();
        if (host is null || current is null) return;
        if (completed)
        {
            var index = rows.FindIndex(row => Matches(messageForRow(row), current));
            if (index >= 0)
            {
                var row = rows[index];
                var message = messageForRow(row)!;
                if (failedPreview)
                {
                    renderedFinal = message;
                    rows.Insert(index, host);
                    return;
                }
                if (!Equals(renderedRow, row))
                {
                    host.Content = renderRow(row);
                    renderedFinal = message;
                    renderedRow = row;
                }
                rows[index] = host;
                return;
            }
            // A final row hidden by filters or removed from an already refreshed snapshot
            // must stay hidden. Before refresh, retain the live card until persistence arrives.
            var hiddenFinal = persistedMessages?.FirstOrDefault(message => Matches(message, current));
            if (hiddenFinal is not null) renderedFinal = hiddenFinal;
            if (renderedFinal is not null) return;
        }
        rows.Insert(0, host);
    }

    private static bool Matches(TranscriptMessage? message, ArenaTurnProgress progress) =>
        message is not null && message.Turn == progress.Turn
        && message.CreatedAt == progress.CreatedAt
        && string.Equals(message.SpeakerId, progress.SpeakerId, StringComparison.OrdinalIgnoreCase);

    private void Apply(StreamOperation source, IReadOnlyList<ArenaTurnProgress> events)
    {
        SynchronizeSession();
        if (!ReferenceEquals(active, source)) return;
        var changedRows = false;
        foreach (var progress in events)
        {
            if (string.IsNullOrEmpty(identity.InstanceId)
                || progress.SessionId != identity.SessionId || progress.SessionInstanceId != identity.InstanceId)
                continue;
            if (progress.Kind == ArenaTurnProgressKind.Started)
            {
                // A fallback, tool answer, or repair replaces its superseded preview.
                var sameOperation = current?.OperationId == progress.OperationId;
                if (!sameOperation)
                {
                    ClearCard();
                    card = createCard(ToMessage(progress));
                    host = new ContentControl { Content = card.Element,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    changedRows = true;
                }
                else if (current!.Model != progress.Model)
                {
                    card = createCard(ToMessage(progress));
                    host!.Content = card.Element;
                }
                current = progress;
                renderedFinal = null;
                renderedRow = null;
                failedPreview = false;
                text.Clear();
                completed = interrupted = false;
                card!.Status.Text = progress.Activity == ArenaTurnActivity.RetryingWithReducedReasoning
                    ? "Retrying with reduced reasoning…" : "Waiting for response…";
            }
            else if (current is null || current.OperationId != progress.OperationId
                || current.AttemptId != progress.AttemptId || completed)
            {
                continue;
            }
            else if (progress.Kind == ArenaTurnProgressKind.Delta && !interrupted)
            {
                if (!string.IsNullOrEmpty(progress.Text))
                {
                    text.Append(progress.Text);
                    card!.Status.Text = "Writing…";
                }
            }
            else if (progress.Kind == ArenaTurnProgressKind.Activity
                && (!interrupted || progress.Activity == ArenaTurnActivity.RetryingWithReducedReasoning))
            {
                if (progress.Activity == ArenaTurnActivity.Writing && text.Length == 0) continue;
                var label = ActivityLabel(progress.Activity);
                if (label is not null) card!.Status.Text = label;
            }
            else if (progress.Kind == ArenaTurnProgressKind.Completed)
            {
                current = progress;
                failedPreview = progress.Status.Equals("error", StringComparison.OrdinalIgnoreCase) && text.Length > 0;
                if (!failedPreview) text.Clear().Append(progress.Text);
                completed = true;
                changedRows = true;
                card!.Status.Text = failedPreview ? "Interrupted · partial response, not saved"
                    : progress.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "Response interrupted" : "Complete";
            }
            else if (progress.Kind == ArenaTurnProgressKind.Interrupted)
            {
                interrupted = true;
                card!.Status.Text = progress.Status == "superseded" ? "Preparing response…"
                    : progress.Status == "canceled" ? "Stopped · partial response, not saved"
                    : "Interrupted · partial response, not saved";
            }
        }
        if (card is not null && renderedFinal is null) card.Body.Text = text.ToString();
        if (changedRows) refreshRows();
    }

    private static string? ActivityLabel(ArenaTurnActivity? activity) => activity switch
    {
        ArenaTurnActivity.Loading => "Loading model…",
        ArenaTurnActivity.ReadingContext => "Reading context…",
        ArenaTurnActivity.Thinking => "Thinking…",
        ArenaTurnActivity.Writing => "Writing…",
        ArenaTurnActivity.RetryingWithReducedReasoning => "Retrying with reduced reasoning…",
        _ => null
    };

    private void Finish(StreamOperation source)
    {
        if (!ReferenceEquals(active, source)) return;
        if (card is not null && !completed && !interrupted)
            card.Status.Text = "Interrupted · partial response, not saved";
        active = null;
    }

    private static TranscriptMessage ToMessage(ArenaTurnProgress value) => new(
        value.Turn, value.SpeakerName, value.SpeakerId, 0, value.Model,
        0, 0, 0, 0, "generating", "", false, "dialogue", "", "",
        "", "", "", "", "", "", "", false, []);

    internal sealed class StreamOperation : IProgress<ArenaTurnProgress>, IDisposable
    {
        private readonly ArenaTranscriptStreamCoordinator owner;
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer timer;
        private readonly object gate = new();
        private readonly List<ArenaTurnProgress> pending = [];
        private bool disposed;

        internal StreamOperation(ArenaTranscriptStreamCoordinator owner, Dispatcher dispatcher)
        {
            this.owner = owner;
            this.dispatcher = dispatcher;
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(80)
            };
            timer.Tick += OnTick;
            timer.Start();
        }

        public void Report(ArenaTurnProgress value)
        {
            lock (gate)
            {
                if (disposed) return;
                pending.Add(value);
            }
            if (value.Kind != ArenaTurnProgressKind.Delta && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
        }

        private void OnTick(object? sender, EventArgs e) => Flush();

        internal void Flush()
        {
            dispatcher.VerifyAccess();
            ArenaTurnProgress[] events;
            lock (gate)
            {
                events = pending.ToArray();
                pending.Clear();
            }
            if (events.Length > 0) owner.Apply(this, events);
        }

        internal void Detach()
        {
            lock (gate)
            {
                disposed = true;
                pending.Clear();
            }
            timer.Stop();
            timer.Tick -= OnTick;
        }

        public void Dispose()
        {
            dispatcher.VerifyAccess();
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            timer.Stop();
            timer.Tick -= OnTick;
            Flush();
            owner.Finish(this);
        }
    }
}
