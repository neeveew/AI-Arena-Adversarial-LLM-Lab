using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ConversationStarterCommittedRefreshFailureDoesNotResend()
    {
        foreach (var staleProjection in new[] { false, true })
        foreach (var newerDraft in new[] { false, true })
        {
            WithConversationStarter(async fixture =>
            {
                var emptyView = fixture.Current;
                const string first = "  First public line\nSecond public line  ";
                const string next = "A newer draft typed while the first message is saving";
                const string privateFailure = "private refresh fixture detail sk-test-secret C:\\private\\session.json";
                var saves = 0;
                fixture.Text.Text = first;
                fixture.Operator.UpdateTurnMeter();
                fixture.BeforeSave = _ =>
                {
                    saves++;
                    if (newerDraft)
                    {
                        fixture.Text.Text = next;
                        fixture.Operator.UpdateTurnMeter();
                    }
                    return Task.CompletedTask;
                };
                fixture.AfterRefresh = () =>
                {
                    // A refresh can fail before or after publishing its new snapshot.
                    // Keep the old view for the former case to exercise durable guards.
                    if (staleProjection) fixture.Apply(emptyView);
                    throw new IOException(privateFailure);
                };

                await fixture.Starter.SendAndStartAsync();

                var saved = fixture.Load();
                var message = saved.Engine.Messages.Single();
                Require(message.SpeakerId == "operator" && message.Text == first && saves == 1
                        && new FactoryConversationService().Inspect(saved).HasUsableRoot,
                    "A failed opening refresh must leave one exact, durably anchored Public message.");
                Require(fixture.Starts == 0 && fixture.Text.Text == (newerDraft ? next : ""),
                    "A committed first send must not start Auto Chat after failed refresh; only its unchanged draft may clear.");
                Require(fixture.Status.Contains("Warning:", StringComparison.Ordinal)
                        && fixture.Status.Contains("saved", StringComparison.OrdinalIgnoreCase)
                        && !fixture.Status.Contains(privateFailure, StringComparison.Ordinal)
                        && !fixture.Status.Contains("sk-test-secret", StringComparison.Ordinal)
                        && !fixture.Status.Contains("retry", StringComparison.OrdinalIgnoreCase),
                    "A saved opening must report a safe secondary warning without inviting a duplicate send.");
                var hint = fixture.StartButton.ToolTip?.ToString() ?? "";
                Require(hint.Contains("saved", StringComparison.OrdinalIgnoreCase)
                        && hint.Contains("refresh", StringComparison.OrdinalIgnoreCase)
                        && !hint.Contains("retry", StringComparison.OrdinalIgnoreCase),
                    "Send and start must consume the saved-but-not-presented receipt instead of treating it as an unsent message.");
                Require(ReferenceEquals(staleProjection ? fixture.StarterHost.Content : fixture.Dock.Content, fixture.Composer),
                    "Failed refresh must preserve the actual single composer at the location owned by the projected state.");

                // Even an explicit repeated command against a stale empty view must
                // consult the saved root and leave both it and any newer draft alone.
                await fixture.Starter.SendAndStartAsync();
                var afterRepeat = fixture.Load();
                Require(saves == 1 && fixture.Starts == 0 && afterRepeat.Engine.Messages.Count == 1
                        && afterRepeat.Engine.Messages.Single().MessageId == message.MessageId
                        && afterRepeat.Engine.Messages.Single().Text == first
                        && fixture.Text.Text == (newerDraft ? next : ""),
                    "Repeating Send and start after a failed committed refresh must neither resend the opening nor consume a newer draft.");

                fixture.AfterRefresh = null;
                fixture.Apply(SnapshotViewMapper.FromCore(fixture.Session, afterRepeat) with { ProviderOnline = true });
                Require(!ConversationStartCoordinator.ShouldCenterComposer(fixture.Current)
                        && ReferenceEquals(fixture.Dock.Content, fixture.Composer)
                        && fixture.StarterHost.Content is null && fixture.Starts == 0,
                    "Refreshing the saved root must restore the ordinary composer without starting Auto Chat implicitly.");
            });
        }
    }
}
