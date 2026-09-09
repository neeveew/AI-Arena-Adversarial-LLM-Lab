using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;

internal static partial class Program
{
    static void TranscriptLiveCardKeepsPublicTextAndProgressTogether()
    {
        RunStaTest(() =>
        {
            var message = TranscriptForTest(7, "Alpha", "alpha", "dialogue", "generating") with
            {
                Text = "A saved placeholder must not appear before public deltas arrive.",
                Reasoning = "Private reasoning is not public progress."
            };
            var live = CreateTranscriptCardRendererForTest().CreateLiveCard(message);
            Require(live.Body.Text.Length == 0 && live.Status.Text.Contains("Generating", StringComparison.Ordinal),
                "a live card should begin with an empty public body and an explicit generating state");
            Require(live.Body.TextWrapping == TextWrapping.Wrap
                    && AutomationProperties.GetName(live.Status) == "Response progress"
                    && DescendantTextBlocks(live.Element).Contains(live.Body)
                    && DescendantTextBlocks(live.Element).Contains(live.Status),
                "the live response and accessible progress status should belong to the same wrapping transcript card");
            Require(!DescendantButtons(live.Element).Any(button =>
                    AutomationProperties.GetName(button) is "Copy" or "Pin" or "Retry" or "Delete"),
                "an uncommitted preview must not expose persisted-message mutation actions");
            live.Body.Text = "Literal <tag> and **markdown**\nsecond line";
            Require(live.Body.Text == "Literal <tag> and **markdown**\nsecond line"
                    && !DescendantTextBlocks(live.Element).Any(block => block.Text.Contains(message.Reasoning, StringComparison.Ordinal)),
                "public streaming text must remain exact plain text without displaying private reasoning");
        });
    }

    static void ArenaTranscriptStreamKeepsOneHostThroughCommit()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Flush();
            var host = (ContentControl)fixture.Merge([]).Single();
            var live = fixture.LiveCards.Single();
            var initialContent = host.Content;
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "First " });
            operation.Flush();
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "draft" });
            operation.Flush();
            Require(ReferenceEquals(fixture.Merge([]).Single(), host)
                    && ReferenceEquals(host.Content, initialContent) && live.Body.Text == "First draft",
                "incremental deltas should update the existing card without replacing its row or rendered content");
            Require(fixture.RefreshCount == 1 && fixture.LiveCards.Count == 1,
                "delta updates must not rebuild the transcript list or allocate another live card");

            var completed = started with { Kind = ArenaTurnProgressKind.Completed, Text = "Committed public response.", Status = "ok", CreatedAt = 123.5 };
            operation.Report(completed);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "late text" });
            operation.Flush();
            Require(live.Body.Text == completed.Text && live.Status.Text == "Complete"
                    && ReferenceEquals(fixture.Merge([]).Single(), host),
                "completion should replace preview text with the authoritative result and retain its host while persistence arrives");
            var saved = ArenaStreamingMessageForTest(completed);
            var unrelated = saved with { CreatedAt = completed.CreatedAt + 1, Text = "Different turn incarnation." };
            var reconciled = fixture.Merge([unrelated, saved]);
            Require(reconciled.Count == 2 && ReferenceEquals(reconciled[0], unrelated)
                    && ReferenceEquals(reconciled[1], host) && !ReferenceEquals(host.Content, initialContent),
                "only the exact committed turn identity should replace its row with the existing stream host, without duplication");
            Require(fixture.FinalRenderCount == 1 && fixture.RenderedMessages.Single() == saved
                    && host.Content is Border finalCard
                    && DescendantButtons(finalCard).Any(button => AutomationProperties.GetName(button) == "Copy"),
                "after commit the stable host should contain the ordinary persisted transcript card and its actions");
            Require(ReferenceEquals(fixture.Merge([saved]).Single(), host),
                "later snapshot reconciliation must preserve the same committed row host");
        });
    }

    static void ArenaTranscriptStreamCoalescesWorkerDeltasBeforeFlush()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Flush();
            var live = fixture.LiveCards.Single();
            var textChanges = 0;
            var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler changed = (_, _) => textChanges++;
            descriptor.AddValueChanged(live.Body, changed);
            try
            {
                var chunks = Enumerable.Range(0, 250).Select(index => $"chunk-{index};").ToArray();
                Task.Run(() =>
                {
                    foreach (var chunk in chunks)
                        operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = chunk });
                }).GetAwaiter().GetResult();
                Require(live.Body.Text.Length == 0 && textChanges == 0,
                    "background provider callbacks must queue text without touching dispatcher-owned controls");
                operation.Flush();
                Require(live.Body.Text == string.Concat(chunks) && textChanges == 1,
                    "one dispatcher flush should append every ordered delta in one body update");
                operation.Flush();
                Require(textChanges == 1 && fixture.RefreshCount == 1 && fixture.LiveCards.Count == 1,
                    "empty flushes and batched deltas must not trigger extra body updates or transcript-list rebuilds");
            }
            finally
            {
                descriptor.RemoveValueChanged(live.Body, changed);
            }
        });
    }

    static void ArenaTranscriptStreamStopsPartialResponseAndIgnoresLateCallbacks()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            var started = ArenaStreamingStartedForTest();
            using var operation = fixture.Coordinator.Begin();
            operation.Report(started);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Partial public text" });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Interrupted, Status = "canceled" });
            operation.Flush();
            var live = fixture.LiveCards.Single();
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = " must not append" });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            operation.Dispose();
            operation.Report(started with { Kind = ArenaTurnProgressKind.Completed, Text = "Late completion", Status = "ok", CreatedAt = 9 });
            operation.Flush();
            Require(live.Body.Text == "Partial public text"
                    && live.Status.Text.Contains("Stopped", StringComparison.Ordinal)
                    && live.Status.Text.Contains("not saved", StringComparison.Ordinal)
                    && fixture.Merge([]).Count == 1 && fixture.FinalRenderCount == 0,
                "cancellation must preserve an honest unsaved partial response and ignore callbacks after disposal");

            using var unfinished = fixture.Coordinator.Begin();
            var next = started with { OperationId = "op-2", AttemptId = "attempt-2" };
            unfinished.Report(next);
            unfinished.Report(next with { Kind = ArenaTurnProgressKind.Delta, Text = "Queued partial" });
            unfinished.Dispose();
            var unfinishedCard = fixture.LiveCards.Last();
            Require(unfinishedCard.Body.Text == "Queued partial"
                    && unfinishedCard.Status.Text.Contains("Interrupted", StringComparison.Ordinal)
                    && unfinishedCard.Status.Text.Contains("not saved", StringComparison.Ordinal),
                "disposing an unfinished operation must flush queued public text and mark it as an unsaved interruption");
            unfinished.Report(next with { Kind = ArenaTurnProgressKind.Delta, Text = "late" });
            unfinished.Flush();
            Require(unfinishedCard.Body.Text == "Queued partial", "late provider data must not revive a disposed operation");
        });
    }

    static void ArenaTranscriptStreamIsolatesSessionInstancesAndLateOperations()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var previous = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            previous.Report(started);
            previous.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Old session partial" });
            previous.Flush();
            var previousCard = fixture.LiveCards.Single();
            previous.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = " queued before reset" });
            fixture.Identity = (started.SessionId, "instance-2");
            fixture.Coordinator.SynchronizeSession();
            previous.Report(started with { Kind = ArenaTurnProgressKind.Completed, Text = "stale saved text", CreatedAt = 7 });
            previous.Flush();
            Require(!fixture.Coordinator.HasCard && fixture.Merge([]).Count == 0
                    && previousCard.Body.Text == "Old session partial",
                "a new incarnation of the same named session must discard queued previews and detach older callbacks");

            using var current = fixture.Coordinator.Begin();
            current.Report(started);
            current.Flush();
            Require(!fixture.Coordinator.HasCard, "even the current operation must reject events carrying an older session instance");
            var newStarted = started with { SessionInstanceId = "instance-2", OperationId = "new-op" };
            current.Report(newStarted);
            current.Report(newStarted with { Kind = ArenaTurnProgressKind.Delta, Text = "New session text" });
            current.Flush();
            Require(fixture.LiveCards.Last().Body.Text == "New session text" && fixture.Merge([]).Count == 1,
                "a new session incarnation should accept only its own stream");
            fixture.Identity = ("another-session", "instance-2");
            fixture.Coordinator.SynchronizeSession();
            current.Report(newStarted with { Kind = ArenaTurnProgressKind.Delta, Text = "wrong session" });
            current.Flush();
            Require(!fixture.Coordinator.HasCard && fixture.Merge([]).Count == 0,
                "changing session names must isolate streams even if a caller supplies the same instance token");
            fixture.Identity = ("another-session", "");
            using var unidentified = fixture.Coordinator.Begin();
            unidentified.Report(started with { SessionId = fixture.Identity.SessionId, SessionInstanceId = "" });
            unidentified.Flush();
            Require(!fixture.Coordinator.HasCard, "missing durable session identity must fail closed for ephemeral response routing");
        });
    }

    static void ArenaTranscriptStreamReplacesSupersededAttemptWithoutMixingText()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Superseded answer" });
            operation.Flush();
            var host = (ContentControl)fixture.Merge([]).Single();
            operation.Report(started with { Kind = ArenaTurnProgressKind.Interrupted, Status = "superseded" });
            operation.Flush();
            Require(fixture.LiveCards.Last().Status.Text.Contains("Preparing response", StringComparison.Ordinal),
                "a superseded attempt should remain visibly provisional while its replacement is prepared");
            var replacement = started with { AttemptId = "replacement", Model = "fallback-model" };
            operation.Report(replacement);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "stale suffix" });
            operation.Report(replacement with { Kind = ArenaTurnProgressKind.Delta, Text = "Replacement public text" });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Completed, Text = "stale final", Status = "ok", CreatedAt = 42 });
            operation.Flush();
            Require(ReferenceEquals(fixture.Merge([]).Single(), host)
                    && fixture.LiveCards.Last().Body.Text == "Replacement public text"
                    && fixture.LiveCards.Last().Status.Text == "Writing…",
                "repair or fallback attempts must reuse the operation row while replacing its text and rejecting superseded events");
            Require(fixture.CreatedMessages.Last().Model == "fallback-model",
                "when a replacement uses another model, the visible live card must identify the model actually responding");
        });
    }

    static void ArenaTranscriptStreamHonorsCommittedFiltersAndRemoval()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            var completed = started with { Kind = ArenaTurnProgressKind.Completed, Text = "Committed answer", Status = "ok", CreatedAt = 77 };
            operation.Report(started);
            operation.Report(completed);
            operation.Flush();
            var saved = ArenaStreamingMessageForTest(completed);
            Require(fixture.Merge([]).Count == 1,
                "a just-completed response should remain visible while its committed snapshot has not yet arrived");
            Require(fixture.Merge([], [saved]).Count == 0 && fixture.FinalRenderCount == 0,
                "a saved response excluded by transcript filters must not leak back into the list as an ephemeral preview");
            var visible = fixture.Merge([saved], [saved]);
            Require(visible.Count == 1 && visible[0] is ContentControl && fixture.FinalRenderCount == 1,
                "clearing the filter should reconcile the saved response into one ordinary transcript card");
            Require(fixture.Merge([], []).Count == 0,
                "removing an already-reconciled committed row must not resurrect its old live response");
        });
    }

    static void ArenaTranscriptStreamCompletionReconcilesAlreadyArrivedSnapshot()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            IReadOnlyList<TranscriptMessage> persisted = [];
            List<object> displayedRows = [];
            fixture.AfterRefresh = () => displayedRows = fixture.Merge(persisted, persisted);
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Live text" });
            operation.Flush();
            var host = (ContentControl)displayedRows.Single();
            var completed = started with { Kind = ArenaTurnProgressKind.Completed, Text = "Saved final", Status = "ok", CreatedAt = 81 };
            persisted = [ArenaStreamingMessageForTest(completed)];
            displayedRows = fixture.Merge(persisted, persisted);
            var priorRefreshes = fixture.RefreshCount;
            operation.Report(completed);
            operation.Flush();
            Require(fixture.RefreshCount == priorRefreshes + 1 && displayedRows.Count == 1
                    && ReferenceEquals(displayedRows[0], host) && fixture.FinalRenderCount == 1,
                "completion must immediately reconcile an already-arrived saved snapshot into the stable host without waiting for another refresh");
            Require(fixture.RenderedMessages.Single().Text == completed.Text,
                "the reconciled final card must use committed text rather than the older streamed preview");
        });
    }

    static void ArenaTranscriptStreamKeepsUnsavedPartialSeparateFromSavedError()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Incomplete public response" });
            operation.Flush();
            var host = (ContentControl)fixture.Merge([]).Single();
            var failed = started with { Kind = ArenaTurnProgressKind.Completed, Text = "Model call failed: connection interrupted.", Status = "error", CreatedAt = 82 };
            operation.Report(failed);
            operation.Flush();
            var savedError = ArenaStreamingMessageForTest(failed) with { Kind = "error" };
            var rows = fixture.Merge([savedError], [savedError]);
            Require(rows.Count == 2 && ReferenceEquals(rows[0], host) && ReferenceEquals(rows[1], savedError)
                    && fixture.LiveCards.Single().Body.Text == "Incomplete public response"
                    && fixture.LiveCards.Single().Status.Text.Contains("not saved", StringComparison.Ordinal),
                "a failed stream must retain its visibly unsaved partial separately from the ordinary committed error row");
            Require(savedError.Status == "error" && savedError.Text == failed.Text
                    && !savedError.Text.Contains("Incomplete public response", StringComparison.Ordinal),
                "the preview must never overwrite the saved error or manufacture partial causal transcript content");
            Require(fixture.Merge([], [savedError]).Count == 0,
                "filtering the committed failure must also hide its associated unsaved preview");
            Require(fixture.Merge([], []).Count == 0,
                "removing an observed committed failure must not resurrect its associated partial preview");

            using var next = fixture.Coordinator.Begin();
            var nextStart = started with { OperationId = "empty-failure", AttemptId = "empty-attempt" };
            next.Report(nextStart);
            var emptyFailure = nextStart with { Kind = ArenaTurnProgressKind.Completed, Text = "Model call failed before any public text.", Status = "error", CreatedAt = 83 };
            next.Report(emptyFailure);
            next.Flush();
            var nextSavedError = ArenaStreamingMessageForTest(emptyFailure) with { Kind = "error" };
            Require(fixture.Merge([nextSavedError], [nextSavedError]).Count == 1 && fixture.FinalRenderCount == 1,
                "a failure with no public deltas should produce only the ordinary saved error card, not an empty partial companion");
        });
    }

    static void ArenaTranscriptStreamRefreshesCommittedActionStateWithoutReplacingHost()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            var completed = started with { Kind = ArenaTurnProgressKind.Completed, Text = "Saved reply", Status = "ok", CreatedAt = 84 };
            operation.Report(started);
            operation.Report(completed);
            operation.Flush();
            var message = ArenaStreamingMessageForTest(completed);
            var renderer = CreateTranscriptCardRendererForTest();
            List<object> MergeDescriptor(ArenaStreamingRowForTest descriptor)
            {
                var rows = new List<object> { descriptor };
                fixture.Coordinator.MergeRows(rows,
                    row => (row as ArenaStreamingRowForTest)?.Message,
                    row =>
                    {
                        var item = (ArenaStreamingRowForTest)row;
                        return renderer.CreateCard(item.Message, item.Retryable, item.SearchMatch, item.IsLatest);
                    }, [message]);
                return rows;
            }
            var first = new ArenaStreamingRowForTest(message, Retryable: true, SearchMatch: false, IsLatest: true);
            var host = (ContentControl)MergeDescriptor(first).Single();
            var firstCard = (Border)host.Content;
            Require(DescendantButtons(firstCard).Single(button => AutomationProperties.GetName(button) == "Retry").IsEnabled,
                "the initial committed card should expose its currently allowed Retry action");
            var changed = first with { Retryable = false, SearchMatch = true, IsLatest = false };
            Require(ReferenceEquals(MergeDescriptor(changed).Single(), host) && !ReferenceEquals(host.Content, firstCard)
                    && !DescendantButtons((Border)host.Content).Single(button => AutomationProperties.GetName(button) == "Retry").IsEnabled,
                "row-state changes must refresh committed actions while preserving the transcript row identity");
        });
    }

    private sealed record ArenaStreamingRowForTest(TranscriptMessage Message, bool Retryable, bool SearchMatch, bool IsLatest);

    static void ArenaTranscriptStreamExplicitResetDiscardsSameInstanceCallbacks()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            var originalIdentity = fixture.Identity;
            List<object> displayedRows = [];
            fixture.AfterRefresh = () => displayedRows = fixture.Merge([], []);
            using var beforeReset = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            beforeReset.Report(started);
            beforeReset.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Before reset" });
            beforeReset.Flush();
            var oldCard = fixture.LiveCards.Single();
            var oldHost = displayedRows.Single();
            beforeReset.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = " queued before reset" });
            var previousRefreshes = fixture.RefreshCount;
            fixture.Coordinator.Clear();
            Require(fixture.Identity == originalIdentity && !fixture.Coordinator.HasCard
                    && displayedRows.Count == 0 && fixture.RefreshCount == previousRefreshes + 1,
                "a committed transcript reset must immediately remove the live row without requiring a new session instance");
            beforeReset.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = " late after reset" });
            beforeReset.Report(started with { Kind = ArenaTurnProgressKind.Completed, Text = "stale completion", Status = "ok", CreatedAt = 85 });
            beforeReset.Flush();
            Require(displayedRows.Count == 0 && fixture.LiveCards.Count == 1 && oldCard.Body.Text == "Before reset",
                "queued and late callbacks from the cleared operation must not repopulate the reset transcript");

            using var afterReset = fixture.Coordinator.Begin();
            var newStarted = started with { OperationId = "after-reset", AttemptId = "new-attempt" };
            afterReset.Report(newStarted);
            afterReset.Report(newStarted with { Kind = ArenaTurnProgressKind.Delta, Text = "Fresh response" });
            beforeReset.Report(started);
            beforeReset.Flush();
            afterReset.Flush();
            Require(fixture.Identity == originalIdentity && displayedRows.Count == 1
                    && !ReferenceEquals(displayedRows[0], oldHost)
                    && fixture.LiveCards.Count == 2 && fixture.LiveCards.Last().Body.Text == "Fresh response",
                "reset must permit a new operation in the same session while old operation callbacks remain detached");
        });
    }

    static void TranscriptListStreamingStartDetectionRespectsReaderScroll()
    {
        RunStaTest(() =>
        {
            var list = new TranscriptListBox();
            Require(list.IsAtStart, "an unrealized empty transcript should be eligible to follow its first response");
            ScrollViewer.SetCanContentScroll(list, false);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            for (var index = 0; index < 24; index++)
                list.Items.Add(new TextBlock { Text = $"Transcript row {index}", Height = 48, TextWrapping = TextWrapping.Wrap });
            var window = new Window
            {
                Width = 380,
                Height = 240,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -10000,
                Top = -10000,
                Content = list
            };
            try
            {
                window.Show();
                PumpFollowLiveLayout(window);
                var viewport = NativeWindowDescendants<ScrollViewer>(list).First();
                Require(viewport.ScrollableHeight > viewport.ViewportHeight && list.IsAtStart,
                    "the realized newest-first transcript must begin at its latest row with enough content to scroll");
                viewport.ScrollToVerticalOffset(144);
                PumpFollowLiveLayout(window);
                Require(viewport.VerticalOffset > 100 && !list.IsAtStart,
                    "reading older transcript rows must disable the at-start signal used by follow-chat");
                ((TextBlock)list.Items[0]).Height = 180;
                PumpFollowLiveLayout(window);
                Require(viewport.VerticalOffset > 0 && !list.IsAtStart,
                    "growth of the latest response must not misclassify a historical reading position as following the latest turn");
                list.ScrollToTop();
                PumpFollowLiveLayout(window);
                Require(list.IsAtStart && viewport.VerticalOffset < 1,
                    "an explicit return to the newest row should restore the at-start follow signal");
            }
            finally
            {
                window.Close();
            }
        });
    }

    static void ArenaTranscriptStreamShowsObservedActivityBeforeVisibleWriting()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Flush();
            var live = fixture.LiveCards.Single();
            var host = fixture.Merge([]).Single();
            Require(live.Status.Text == "Waiting for response…" && live.Body.Text.Length == 0,
                "a quiet provider must remain in an honest waiting state instead of claiming unobserved work");
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Writing });
            operation.Flush();
            Require(live.Status.Text == "Waiting for response…",
                "provider writing activity without visible public text must not claim a response is being written");
            var stages = new[]
            {
                (ArenaTurnActivity.Loading, "Loading model…"),
                (ArenaTurnActivity.ReadingContext, "Reading context…"),
                (ArenaTurnActivity.Thinking, "Thinking…")
            };
            foreach (var (stage, expected) in stages)
            {
                operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = stage });
                operation.Flush();
                Require(live.Status.Text == expected && live.Body.Text.Length == 0,
                    $"observed {stage} activity must be visible without adding contents to the public response");
            }
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = (ArenaTurnActivity)999 });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "" });
            operation.Flush();
            Require(live.Status.Text == "Thinking…", "unknown activities and empty deltas must preserve the last supported activity");
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "Actual public text" });
            operation.Flush();
            Require(live.Status.Text == "Writing…" && live.Body.Text == "Actual public text"
                    && ReferenceEquals(host, fixture.Merge([]).Single()) && fixture.RefreshCount == 1 && fixture.LiveCards.Count == 1,
                "the first public delta should switch the existing card to writing without rebuilding transcript rows");
            operation.Report(started with { Kind = ArenaTurnProgressKind.Completed, Text = "Final", Status = "ok", CreatedAt = 90 });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            operation.Flush();
            Require(live.Status.Text == "Complete" && live.Body.Text == "Final",
                "late provider activity must not revive or overwrite a completed response");
        });
    }

    static void ArenaTranscriptStreamRecoveryLabelYieldsToNewAttemptActivity()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var operation = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            operation.Report(started);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Interrupted, Status = "failed" });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.RetryingWithReducedReasoning });
            operation.Flush();
            var host = fixture.Merge([]).Single();
            var live = fixture.LiveCards.Single();
            Require(live.Status.Text == "Retrying with reduced reasoning…",
                "a supported reasoning recovery should explain its retry after a failed attempt");
            var retry = started with { AttemptId = "reduced-reasoning-attempt", Activity = ArenaTurnActivity.RetryingWithReducedReasoning };
            operation.Report(retry);
            operation.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Loading });
            operation.Report(started with { Kind = ArenaTurnProgressKind.Delta, Text = "old answer" });
            operation.Flush();
            Require(live.Status.Text == "Retrying with reduced reasoning…" && live.Body.Text.Length == 0,
                "a new attempt should retain its recovery label and reject activity or text from the previous attempt");
            operation.Report(retry with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            operation.Flush();
            Require(live.Status.Text == "Thinking…", "actual retry activity should replace the provisional recovery explanation");
            operation.Report(retry with { Kind = ArenaTurnProgressKind.Delta, Text = "Recovered public answer" });
            operation.Flush();
            Require(live.Status.Text == "Writing…" && live.Body.Text == "Recovered public answer"
                    && ReferenceEquals(host, fixture.Merge([]).Single()) && fixture.LiveCards.Count == 1,
                "a recovered answer must use the stable operation host without mixing old text or creating another card");
        });
    }

    static void ArenaTranscriptStreamResetRejectsQueuedAndLateActivity()
    {
        RunStaTest(() =>
        {
            var fixture = new ArenaTranscriptStreamingFixture();
            using var old = fixture.Coordinator.Begin();
            var started = ArenaStreamingStartedForTest();
            old.Report(started);
            old.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            old.Flush();
            var oldCard = fixture.LiveCards.Single();
            old.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Loading });
            fixture.Coordinator.Clear();
            old.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.ReadingContext });
            old.Flush();
            Require(!fixture.Coordinator.HasCard && oldCard.Status.Text == "Thinking…",
                "same-instance reset must discard queued activity and detach later callbacks from the cleared card");
            using var current = fixture.Coordinator.Begin();
            var next = started with { OperationId = "after-reset", AttemptId = "new-attempt" };
            current.Report(next);
            current.Report(next with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Loading });
            old.Report(started with { Kind = ArenaTurnProgressKind.Activity, Activity = ArenaTurnActivity.Thinking });
            old.Flush();
            current.Flush();
            Require(fixture.LiveCards.Last().Status.Text == "Loading model…" && fixture.Merge([]).Count == 1,
                "activity from a new operation must remain visible after reset without old callbacks replacing it");
        });
    }

    private static ArenaTurnProgress ArenaStreamingStartedForTest() => new(
        "stream-session", "instance-1", "op-1", "attempt-1", "alpha", "Alpha", "model-A", 7,
        ArenaTurnProgressKind.Started, Status: "generating");

    private static TranscriptMessage ArenaStreamingMessageForTest(ArenaTurnProgress progress) =>
        TranscriptForTest(progress.Turn, progress.SpeakerName, progress.SpeakerId, "dialogue", progress.Status) with
        {
            CreatedAt = progress.CreatedAt,
            Text = progress.Text,
            Model = progress.Model
        };

    private sealed class ArenaTranscriptStreamingFixture
    {
        private readonly TranscriptCardRenderer renderer = CreateTranscriptCardRendererForTest();
        public (string SessionId, string InstanceId) Identity { get; set; } = ("stream-session", "instance-1");
        public List<TranscriptCardRenderer.LiveCard> LiveCards { get; } = [];
        public List<TranscriptMessage> CreatedMessages { get; } = [];
        public List<TranscriptMessage> RenderedMessages { get; } = [];
        public int RefreshCount { get; private set; }
        public Action? AfterRefresh { get; set; }
        public int FinalRenderCount => RenderedMessages.Count;
        public ArenaTranscriptStreamCoordinator Coordinator { get; }

        public ArenaTranscriptStreamingFixture()
        {
            Coordinator = new ArenaTranscriptStreamCoordinator(
                Dispatcher.CurrentDispatcher, () => Identity,
                message =>
                {
                    CreatedMessages.Add(message);
                    var live = renderer.CreateLiveCard(message);
                    LiveCards.Add(live);
                    return live;
                },
                () =>
                {
                    RefreshCount++;
                    AfterRefresh?.Invoke();
                });
        }

        public List<object> Merge(IReadOnlyList<TranscriptMessage> visible, IReadOnlyList<TranscriptMessage>? persisted = null)
        {
            var rows = visible.Cast<object>().ToList();
            Coordinator.MergeRows(rows, row => row as TranscriptMessage,
                row =>
                {
                    var message = (TranscriptMessage)row;
                    RenderedMessages.Add(message);
                    return renderer.CreateCard(message, retryable: true, searchMatch: false, isLatest: true);
                }, persisted);
            return rows;
        }
    }
}
