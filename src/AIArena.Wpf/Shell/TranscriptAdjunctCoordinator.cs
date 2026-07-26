using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed record TranscriptBattleReviewSpeaker(
    string SpeakerId,
    string Speaker,
    int Turns,
    int TotalTokens,
    int PromptTokens,
    int CompletionTokens,
    int TotalLatencyMs,
    int AverageLatencyMs,
    string ModelSummary,
    int SharePercent);

internal sealed record TranscriptAfterActionSource(
    int Turn,
    string Speaker,
    string Title,
    string Domain,
    string Url,
    string Snippet,
    string CheckedAt);

internal sealed record TranscriptAfterActionReport(
    IReadOnlyList<string> MainClaims,
    IReadOnlyList<string> SourcedClaims,
    IReadOnlyList<TranscriptAfterActionSource> KeySources,
    IReadOnlyList<string> UnresolvedDisagreements,
    IReadOnlyList<string> SourceConflicts,
    string StrongestArgument,
    string BestAgentPerformance);

internal sealed record TranscriptBattleReview(
    string Verdict,
    string Severity,
    int Score,
    string LeadingVoice,
    string WatchTarget,
    string Summary,
    string NextAction,
    int MessageCount,
    int AgentTurnCount,
    int ModelCount,
    int TotalTokens,
    int TotalLatencyMs,
    string SlowestTurn,
    IReadOnlyList<string> Flags,
    IReadOnlyList<TranscriptBattleReviewSpeaker> Speakers,
    TranscriptAfterActionReport AfterActionReport);

internal sealed record TranscriptRunTraceSpan(
    int Turn,
    string SpeakerId,
    string Speaker,
    string Kind,
    string Status,
    string Model,
    int LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    IReadOnlyList<string> Flags);

internal sealed record TranscriptRunTraceTriage(
    string Severity,
    string Focus,
    string Summary,
    int IssueSpanCount,
    int PendingSpanCount,
    int ErrorSpanCount,
    int SlowSpanCount,
    int HighTokenSpanCount,
    int ToolSourceSpanCount,
    IReadOnlyList<string> ReviewTurns);

internal sealed record TranscriptRunTrace(
    int SpanCount,
    int ModelCallCount,
    int ToolCallCount,
    int IssueCount,
    int TotalTokens,
    int TotalLatencyMs,
    string SlowestSpan,
    string Summary,
    string NextAction,
    TranscriptRunTraceTriage Triage,
    IReadOnlyList<TranscriptRunTraceSpan> Spans);

internal sealed record TranscriptAutoModeratorAlert(
    string Label,
    string Body,
    string Severity);

internal sealed class TranscriptAdjunctCoordinator
{
    private readonly DiscourseDiagnosticsService discourseDiagnostics;
    private readonly VoiceStyleAdherenceService voiceStyleAdherenceService;
    private readonly TranscriptCardRenderer transcriptCards;
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<IReadOnlyDictionary<string, string>> agentPersonas;
    private readonly Func<IReadOnlyList<TranscriptMessage>> selectedTurnCompareMessages;
    private readonly Func<bool> hasTurnCompareSelection;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<bool> shouldShowStyleFit;
    private readonly Func<AIArena.Core.Models.VoiceAdherenceDiagnostic, Brush> voiceAdherenceAccent;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Func<int, string> formatDuration;
    private readonly Func<string, RoutedEventHandler?, bool, TranscriptActionKind, string?, Button> createActionButton;
    private readonly Action refreshTranscript;
    private readonly Action<IReadOnlyList<TranscriptMessage>> reselectLatestCompareTurns;
    private readonly Action clearTurnCompareSelection;
    private readonly Func<Task> generateDecisionCardAsync;
    private readonly ShellCardFactory cardFactory;

    private bool decisionCardExpanded;

    public TranscriptAdjunctCoordinator(
        DiscourseDiagnosticsService discourseDiagnostics,
        VoiceStyleAdherenceService voiceStyleAdherenceService,
        TranscriptCardRenderer transcriptCards,
        Func<bool> compactTranscriptMode,
        Func<IReadOnlyDictionary<string, string>> agentPersonas,
        Func<IReadOnlyList<TranscriptMessage>> selectedTurnCompareMessages,
        Func<bool> hasTurnCompareSelection,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, bool> isAgentSpeaker,
        Func<string, string> displayStatusValue,
        Func<bool> shouldShowStyleFit,
        Func<AIArena.Core.Models.VoiceAdherenceDiagnostic, Brush> voiceAdherenceAccent,
        Func<int, string> formatCompactNumber,
        Func<int, string> formatDuration,
        Func<string, RoutedEventHandler?, bool, TranscriptActionKind, string?, Button> createActionButton,
        Action refreshTranscript,
        Action<IReadOnlyList<TranscriptMessage>> reselectLatestCompareTurns,
        Action clearTurnCompareSelection,
        Func<Task> generateDecisionCardAsync)
    {
        this.discourseDiagnostics = discourseDiagnostics;
        this.voiceStyleAdherenceService = voiceStyleAdherenceService;
        this.transcriptCards = transcriptCards;
        this.compactTranscriptMode = compactTranscriptMode;
        this.agentPersonas = agentPersonas;
        this.selectedTurnCompareMessages = selectedTurnCompareMessages;
        this.hasTurnCompareSelection = hasTurnCompareSelection;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        cardFactory = new ShellCardFactory(resourceBrush, blendBrush);
        this.accentForSpeaker = accentForSpeaker;
        this.isAgentSpeaker = isAgentSpeaker;
        this.displayStatusValue = displayStatusValue;
        this.shouldShowStyleFit = shouldShowStyleFit;
        this.voiceAdherenceAccent = voiceAdherenceAccent;
        this.formatCompactNumber = formatCompactNumber;
        this.formatDuration = formatDuration;
        this.createActionButton = createActionButton;
        this.refreshTranscript = refreshTranscript;
        this.reselectLatestCompareTurns = reselectLatestCompareTurns;
        this.clearTurnCompareSelection = clearTurnCompareSelection;
        this.generateDecisionCardAsync = generateDecisionCardAsync;
    }

    public Border CreateTurnComparePanel(IReadOnlyList<TranscriptMessage> visibleMessages)
    {
        var selected = selectedTurnCompareMessages().ToArray();
        var accent = resourceBrush("BetaAccentBrush");
        var panel = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Turn Compare",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = selected.Length >= 2
                ? CompareSummary(selected[0], selected[1])
                : "Select two transcript cards to compare wording, model, tokens, context, and latency.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(PanelActionButton(
            "Auto latest",
            (_, _) => reselectLatestCompareTurns(visibleMessages),
            visibleMessages.Any(TranscriptInsightCoordinator.CanCompareMessage),
            TranscriptActionKind.Primary,
            "\uE72C"));
        actions.Children.Add(PanelActionButton(
            "Clear",
            (_, _) => clearTurnCompareSelection(),
            hasTurnCompareSelection(),
            TranscriptActionKind.Danger,
            "\uE711"));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        panel.Children.Add(header);

        if (selected.Length >= 2)
        {
            var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            metrics.Children.Add(CreateCompareMetric("Token delta", CompareDelta(selected[0].CompletionTokens, selected[1].CompletionTokens), resourceBrush("PrimaryBorderBrush")));
            metrics.Children.Add(CreateCompareMetric("Context delta", CompareDelta(selected[0].PromptTokens, selected[1].PromptTokens), resourceBrush("GammaAccentBrush")));
            metrics.Children.Add(CreateCompareMetric("Latency delta", CompareDurationDelta(selected[0].LatencyMs, selected[1].LatencyMs), resourceBrush("AlphaAccentBrush")));
            panel.Children.Add(metrics);
        }

        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(selected.Length > 0 ? CreateTurnCompareColumn(selected[0], "A") : CreateTurnComparePlaceholder("A"));
        grid.Children.Add(selected.Length > 1 ? CreateTurnCompareColumn(selected[1], "B") : CreateTurnComparePlaceholder("B"));
        panel.Children.Add(grid);

        return new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.09),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.56),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    public Border CreateDecisionCardPanel(ArenaViewSnapshot snapshot)
    {
        var accent = resourceBrush("NarratorAccentBrush");
        var hasCard = !string.IsNullOrWhiteSpace(snapshot.DecisionCard);
        var card = new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.58),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var root = new DockPanel { LastChildFill = true };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var expandButton = PanelActionButton(
            decisionCardExpanded ? "Collapse" : "Expand",
            (_, _) =>
            {
                decisionCardExpanded = !decisionCardExpanded;
                refreshTranscript();
            },
            hasCard,
            TranscriptActionKind.Neutral,
            decisionCardExpanded ? "\uE70E" : "\uE70D");
        var generateButton = PanelActionButton("Generate", async (_, _) => await generateDecisionCardAsync(), true, TranscriptActionKind.Primary, "\uE9D2");
        SetDecisionCardActionSize(expandButton);
        SetDecisionCardActionSize(generateButton);
        actions.Children.Add(expandButton);
        actions.Children.Add(generateButton);
        DockPanel.SetDock(actions, Dock.Right);
        root.Children.Add(actions);

        var content = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Decision Card",
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        });
        if (snapshot.DecisionCardUpdatedAt > 0)
        {
            titleRow.Children.Add(new TextBlock
            {
                Text = $"  updated {DateTimeOffset.FromUnixTimeSeconds((long)snapshot.DecisionCardUpdatedAt).ToLocalTime():h:mm tt}",
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        content.Children.Add(titleRow);
        var summary = hasCard
            ? snapshot.DecisionCard.Trim().Replace("\r", " ").Replace("\n", " ")
            : "No decision card yet. Generate one to capture agreed points, conflict, risk, and the next operator move.";
        content.Children.Add(new TextBlock
        {
            Text = decisionCardExpanded && hasCard ? snapshot.DecisionCard.Trim() : summary,
            Foreground = hasCard ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = decisionCardExpanded && hasCard ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = decisionCardExpanded && hasCard ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            LineHeight = decisionCardExpanded && hasCard ? 18 : double.NaN,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = summary
        });
        root.Children.Add(content);
        card.Child = root;
        return card;
    }

    public Border CreateBattleReviewPanel(IReadOnlyList<TranscriptMessage> messages)
    {
        var diagnostics = discourseDiagnostics.Analyze(messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn), agentPersonas());
        var review = BuildBattleReview(messages, diagnostics);
        var accent = review.Severity switch
        {
            "danger" => resourceBrush("DangerBorderBrush"),
            "watch" => resourceBrush("BetaAccentBrush"),
            _ => resourceBrush("PrimaryBorderBrush")
        };

        var panel = new StackPanel();
        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8)
        };
        actions.Children.Add(PanelActionButton(
            "Copy Markdown",
            (_, _) => CopyBattleReviewPacket(review),
            messages.Count > 0,
            TranscriptActionKind.Primary,
            "\uE8C8"));
        actions.Children.Add(PanelActionButton(
            "Copy JSON",
            (_, _) => CopyBattleReviewJson(review),
            messages.Count > 0,
            TranscriptActionKind.Neutral,
            "\uE8D2"));
        actions.Children.Add(PanelActionButton(
            "Copy nudge",
            (_, _) => CopyBattleReviewNudge(review),
            messages.Count > 0,
            TranscriptActionKind.Neutral,
            "\uE8AD"));
        actions.Children.Add(PanelActionButton(
            "Copy trace",
            (_, _) => CopyRunTracePacket(messages),
            messages.Count > 0,
            TranscriptActionKind.Neutral,
            "\uE9D9"));
        panel.Children.Add(actions);

        var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        metrics.Children.Add(CreateCompareMetric("Score", review.Score.ToString(CultureInfo.InvariantCulture), accent));
        metrics.Children.Add(CreateCompareMetric("Leading voice", review.LeadingVoice, resourceBrush("AlphaAccentBrush")));
        metrics.Children.Add(CreateCompareMetric("Watch", review.WatchTarget, resourceBrush("BetaAccentBrush")));
        metrics.Children.Add(CreateCompareMetric("Models", review.ModelCount.ToString(CultureInfo.InvariantCulture), resourceBrush("GammaAccentBrush")));
        metrics.Children.Add(CreateCompareMetric("Tokens", CompactNumber(review.TotalTokens), resourceBrush("PrimaryBorderBrush")));
        metrics.Children.Add(CreateCompareMetric("Latency", FormatReviewDuration(review.TotalLatencyMs), resourceBrush("AssistBorderBrush")));
        panel.Children.Add(metrics);

        panel.Children.Add(new TextBlock
        {
            Text = review.NextAction,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (review.Flags.Count > 0)
        {
            var flags = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var flag in review.Flags.Take(6))
            {
                flags.Children.Add(transcriptCards.CreateStatPill(flag, isInternet: false, accentOverride: accent));
            }

            panel.Children.Add(flags);
        }

        panel.Children.Add(CreateAfterActionReportRows(review.AfterActionReport));

        if (review.Speakers.Count > 0)
        {
            panel.Children.Add(CreateSpeakerReviewRows(review));
        }

        panel.Children.Add(CreateRunTraceRows(BuildRunTrace(messages)));

        return CreateCard(
            "Battle Review",
            review.Summary,
            blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            accent,
            panel);
    }

    public Border? CreateAutoModeratorPanel(IReadOnlyList<TranscriptMessage> messages)
    {
        var alerts = BuildAutoModeratorAlerts(messages);
        if (alerts.Count == 0)
        {
            return null;
        }

        var danger = alerts.Any(alert => alert.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase));
        var accent = danger ? resourceBrush("DangerBorderBrush") : resourceBrush("BetaAccentBrush");
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var alert in alerts.Take(5))
        {
            var alertAccent = alert.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase)
                ? resourceBrush("DangerBorderBrush")
                : resourceBrush("BetaAccentBrush");
            panel.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("InputBrush"), alertAccent, 0.1),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), alertAccent, 0.44),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 8, 10, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = alert.Label,
                            Foreground = alertAccent,
                            FontSize = 11.5,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = alert.Body,
                            Foreground = resourceBrush("TextBrush"),
                            FontSize = 12.5,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 18,
                            Margin = new Thickness(0, 2, 0, 0)
                        }
                    }
                }
            });
        }

        return CreateCard(
            "Auto Moderator",
            "Suggested watch items from the current transcript window.",
            blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            accent,
            panel);
    }

    public IReadOnlyList<TranscriptAutoModeratorAlert> BuildAutoModeratorAlerts(IReadOnlyList<TranscriptMessage> messages)
    {
        var conversation = messages
            .Where(message => message.Kind is "message" or "" or "internet")
            .Where(message => !message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (conversation.Length < 2)
        {
            return [];
        }

        var diagnostics = discourseDiagnostics.Analyze(messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn), agentPersonas());
        var alerts = new List<TranscriptAutoModeratorAlert>();
        if (diagnostics.StateSeverity.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new TranscriptAutoModeratorAlert(diagnostics.StateLabel, "The discourse state is entering a risky pattern. Use an operator turn to demand evidence, a concrete next step, or a dissenting frame.", "danger"));
        }

        if (diagnostics.UnsupportedClaimCount > 0 && diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new TranscriptAutoModeratorAlert("Evidence-starved claims", $"{diagnostics.UnsupportedClaimCount} unsupported claim marker(s) with weak evidence pressure. Ask the next agent to separate evidence, inference, and assumption.", "danger"));
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            alerts.Add(new TranscriptAutoModeratorAlert("Consensus lock-in", $"Consensus is {diagnostics.ConsensusPercent}%. Inject a challenge or boundary-test turn before the agents converge too early.", "watch"));
        }

        if (diagnostics.RoleDriftPercent >= 38)
        {
            alerts.Add(new TranscriptAutoModeratorAlert("Role drift", $"Role drift is {diagnostics.RoleDriftPercent}%. Remind agents to preserve their assigned persona and pressure profile.", "watch"));
        }

        if (diagnostics.NarrativeHeatScore >= 82)
        {
            alerts.Add(new TranscriptAutoModeratorAlert("Narrative heat", $"Narrative heat is {diagnostics.NarrativeHeatLabel}. Ask for a testable claim or operational checkpoint to cool the rhetoric.", "watch"));
        }

        var voiceAlert = VoiceDriftAutoModeratorAlert(conversation);
        if (voiceAlert is not null)
        {
            alerts.Add(voiceAlert);
        }

        return alerts
            .GroupBy(alert => alert.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static TranscriptBattleReview BuildBattleReview(IReadOnlyList<TranscriptMessage> messages, FrictionDiagnostics diagnostics)
    {
        var reviewable = ReviewableMessages(messages).ToArray();
        var agentTurns = reviewable
            .Where(message => !message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var models = reviewable
            .Select(message => message.Model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalTokens = reviewable.Sum(message => Math.Max(0, message.TotalTokens));
        var totalLatency = reviewable.Sum(message => Math.Max(0, message.LatencyMs));
        var slowest = reviewable
            .Where(message => message.LatencyMs > 0)
            .OrderByDescending(message => message.LatencyMs)
            .FirstOrDefault();
        var speakers = SpeakerBreakdown(reviewable, totalTokens);
        var errorCount = messages.Count(message =>
            message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || message.Status.Equals("failed", StringComparison.OrdinalIgnoreCase));
        var hasNarrator = reviewable.Any(message => message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase));

        var score = 100;
        score -= Math.Min(28, diagnostics.UnsupportedClaimCount * 7);
        if (diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            score -= 16;
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            score -= 8;
        }

        if (diagnostics.RoleDriftPercent >= 35)
        {
            score -= 12;
        }

        if (diagnostics.NarrativeHeatScore >= 82)
        {
            score -= 10;
        }

        if (diagnostics.StateSeverity.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            score -= 18;
        }

        score -= Math.Min(24, errorCount * 12);
        if (!hasNarrator && reviewable.Length >= 3)
        {
            score -= 5;
        }

        score = Math.Clamp(score, 0, 100);
        var severity = score < 64 || errorCount > 0 || diagnostics.StateSeverity.Equals("danger", StringComparison.OrdinalIgnoreCase)
            ? "danger"
            : score < 80 || diagnostics.UnsupportedClaimCount > 0 || diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase)
                ? "watch"
                : "healthy";
        var verdict = severity switch
        {
            "danger" => "Needs intervention",
            "watch" => "Watch before deciding",
            _ => "Ready to compare"
        };
        var leading = speakers.Count == 0
            ? "none"
            : $"{speakers[0].Speaker} {speakers[0].SharePercent}%";
        var watch = WatchTarget(speakers, diagnostics, slowest, errorCount);
        var flags = BattleReviewFlags(messages, diagnostics, models.Length, hasNarrator, slowest, errorCount);
        var summary = $"{verdict}: {reviewable.Length} reviewable turn(s), {models.Length} model(s), {CompactNumber(totalTokens)} token(s), {flags.Count} flag(s).";
        var nextAction = BattleReviewNextAction(messages, diagnostics, errorCount, score);
        var afterAction = BuildAfterActionReport(reviewable, diagnostics, speakers);

        return new TranscriptBattleReview(
            verdict,
            severity,
            score,
            leading,
            watch,
            summary,
            nextAction,
            reviewable.Length,
            agentTurns.Length,
            models.Length,
            totalTokens,
            totalLatency,
            slowest is null ? "none" : $"turn {slowest.Turn} {slowest.Speaker} {FormatReviewDuration(slowest.LatencyMs)}",
            flags,
            speakers,
            afterAction);
    }

    public static IReadOnlyList<string> BattleReviewLines(TranscriptBattleReview review)
    {
        var lines = new List<string>
        {
            $"Verdict: {review.Verdict}",
            $"Score: {review.Score}/100",
            $"Turns: {review.MessageCount} reviewable / {review.AgentTurnCount} participant",
            $"Models: {review.ModelCount}",
            $"Tokens: {CompactNumber(review.TotalTokens)}",
            $"Latency: {FormatReviewDuration(review.TotalLatencyMs)}",
            $"Leading voice: {review.LeadingVoice}",
            $"Watch: {review.WatchTarget}",
            $"Slowest: {review.SlowestTurn}",
            $"Next: {review.NextAction}"
        };

        if (review.Flags.Count > 0)
        {
            lines.Add($"Flags: {string.Join("; ", review.Flags)}");
        }

        lines.Add($"Strongest argument: {review.AfterActionReport.StrongestArgument}");
        lines.Add($"Best agent performance: {review.AfterActionReport.BestAgentPerformance}");

        if (review.AfterActionReport.MainClaims.Count > 0)
        {
            lines.Add("Main claims:");
            lines.AddRange(review.AfterActionReport.MainClaims.Select(claim => $"- {claim}"));
        }

        if (review.AfterActionReport.SourcedClaims.Count > 0)
        {
            lines.Add("Sourced claims:");
            lines.AddRange(review.AfterActionReport.SourcedClaims.Select(claim => $"- {claim}"));
        }

        if (review.AfterActionReport.KeySources.Count > 0)
        {
            lines.Add("Key sources:");
            lines.AddRange(review.AfterActionReport.KeySources.Select(source => $"- {FormatAfterActionSource(source)}"));
        }

        if (review.AfterActionReport.UnresolvedDisagreements.Count > 0)
        {
            lines.Add("Unresolved disagreements:");
            lines.AddRange(review.AfterActionReport.UnresolvedDisagreements.Select(item => $"- {item}"));
        }

        if (review.AfterActionReport.SourceConflicts.Count > 0)
        {
            lines.Add("Source conflicts:");
            lines.AddRange(review.AfterActionReport.SourceConflicts.Select(item => $"- {item}"));
        }

        foreach (var speaker in review.Speakers.Take(6))
        {
            lines.Add($"Speaker: {speaker.Speaker} - {speaker.Turns} turn(s), {CompactNumber(speaker.TotalTokens)} tok, avg {FormatReviewDuration(speaker.AverageLatencyMs)}, {speaker.ModelSummary}");
        }

        return lines;
    }

    public static string BattleReviewText(TranscriptBattleReview review)
    {
        return BattleReviewMarkdown(review);
    }

    public static string BattleReviewMarkdown(TranscriptBattleReview review)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AI Arena Battle Review");
        builder.AppendLine();
        foreach (var line in BattleReviewLines(review))
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    public static string BattleReviewJson(TranscriptBattleReview review)
    {
        return JsonSerializer.Serialize(
            new
            {
                title = "AI Arena Battle Review",
                review.Verdict,
                review.Severity,
                review.Score,
                review.Summary,
                review.NextAction,
                review.LeadingVoice,
                review.WatchTarget,
                review.MessageCount,
                review.AgentTurnCount,
                review.ModelCount,
                review.TotalTokens,
                review.TotalLatencyMs,
                review.SlowestTurn,
                review.Flags,
                review.Speakers,
                afterAction = review.AfterActionReport
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BattleReviewNudgeText(TranscriptBattleReview review)
    {
        var focus = review.WatchTarget.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? "the strongest unresolved claim"
            : review.WatchTarget;
        var flags = review.Flags.Count == 0
            ? "no major flags"
            : string.Join(", ", review.Flags.Take(3));
        return $"Operator intervention: focus on {focus}. {review.NextAction} Current review: {review.Verdict}, score {review.Score}/100, flags: {flags}.";
    }

    public static TranscriptAfterActionReport BuildAfterActionReport(
        IReadOnlyList<TranscriptMessage> messages,
        FrictionDiagnostics diagnostics,
        IReadOnlyList<TranscriptBattleReviewSpeaker>? speakers = null)
    {
        var reviewable = ReviewableMessages(messages).ToArray();
        var mainClaims = reviewable
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => ClaimLine(message))
            .Where(claim => !string.IsNullOrWhiteSpace(claim))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var sourcedClaims = reviewable
            .Where(message => message.InternetSources.Count > 0)
            .Select(message => $"{ClaimLine(message)} ({message.InternetSources.Count} source(s))")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        var keySources = KeyAfterActionSources(reviewable);
        var unresolved = UnresolvedDisagreementLines(diagnostics, reviewable);
        var conflicts = SourceConflictLines(diagnostics, reviewable);
        var speakerRows = speakers ?? SpeakerBreakdown(reviewable, reviewable.Sum(message => Math.Max(0, message.TotalTokens)));

        return new TranscriptAfterActionReport(
            mainClaims,
            sourcedClaims,
            keySources,
            unresolved,
            conflicts,
            StrongestArgumentLine(reviewable),
            BestAgentPerformanceLine(reviewable, speakerRows));
    }

    public static TranscriptRunTrace BuildRunTrace(IReadOnlyList<TranscriptMessage> messages)
    {
        var spans = messages
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .Select(ToRunTraceSpan)
            .ToArray();
        var modelCalls = spans.Count(span => IsModelTraceKind(span.Kind));
        var toolCalls = spans.Count(span => span.Kind.Contains("internet", StringComparison.OrdinalIgnoreCase)
            || span.Flags.Any(flag => flag.Contains("tool", StringComparison.OrdinalIgnoreCase)));
        var issues = spans.Count(span => span.Flags.Any(IsIssueTraceFlag));
        var totalTokens = spans.Sum(span => Math.Max(0, span.TotalTokens));
        var totalLatency = spans.Sum(span => Math.Max(0, span.LatencyMs));
        var slowest = spans
            .Where(span => span.LatencyMs > 0)
            .OrderByDescending(span => span.LatencyMs)
            .FirstOrDefault();
        var summary = spans.Length == 0
            ? "No trace spans yet."
            : $"{spans.Length} spans / {modelCalls} model calls / {toolCalls} tool events / {issues} issue markers.";
        var triage = BuildRunTraceTriage(spans);

        return new TranscriptRunTrace(
            spans.Length,
            modelCalls,
            toolCalls,
            issues,
            totalTokens,
            totalLatency,
            slowest is null ? "-" : $"Turn {slowest.Turn} {slowest.Speaker} ({FormatReviewDuration(slowest.LatencyMs)})",
            summary,
            RunTraceNextAction(spans),
            triage,
            spans);
    }

    public static IReadOnlyList<string> RunTraceLines(TranscriptRunTrace trace)
    {
        var lines = new List<string>
        {
            $"Summary: {trace.Summary}",
            $"Spans: {trace.SpanCount.ToString(CultureInfo.InvariantCulture)}",
            $"Model calls: {trace.ModelCallCount.ToString(CultureInfo.InvariantCulture)}",
            $"Tool events: {trace.ToolCallCount.ToString(CultureInfo.InvariantCulture)}",
            $"Issues: {trace.IssueCount.ToString(CultureInfo.InvariantCulture)}",
            $"Tokens: {CompactNumber(trace.TotalTokens)}",
            $"Latency: {FormatReviewDuration(trace.TotalLatencyMs)}",
            $"Slowest: {trace.SlowestSpan}",
            $"Triage: {trace.Triage.Summary}",
            $"Focus: {trace.Triage.Focus}",
            $"Next: {trace.NextAction}",
            "Recent spans:"
        };

        if (trace.Triage.ReviewTurns.Count > 0)
        {
            lines.Add("Review queue:");
            foreach (var item in trace.Triage.ReviewTurns)
            {
                lines.Add($"- {item}");
            }
        }

        foreach (var span in trace.Spans.TakeLast(12))
        {
            lines.Add($"- Turn {span.Turn}: {span.Speaker} / {span.Kind} / {span.Status} / {span.Model} / {CompactNumber(span.TotalTokens)} tok / {FormatReviewDuration(span.LatencyMs)}{(span.Flags.Count == 0 ? "" : $" / {string.Join(", ", span.Flags)}")}");
        }

        return lines;
    }

    public static string RunTraceText(TranscriptRunTrace trace)
    {
        var builder = new StringBuilder();
        builder.AppendLine("AI Arena Run Trace");
        foreach (var line in RunTraceLines(trace))
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    internal static TranscriptRunTraceTriage BuildRunTraceTriage(IReadOnlyList<TranscriptRunTraceSpan> spans)
    {
        var issueSpans = spans.Where(span => span.Flags.Any(IsIssueTraceFlag)).ToArray();
        var pending = spans.Where(span => span.Flags.Contains("pending", StringComparer.OrdinalIgnoreCase)).ToArray();
        var errors = spans.Where(span => span.Flags.Contains("error", StringComparer.OrdinalIgnoreCase)
            || span.Flags.Contains("rejected", StringComparer.OrdinalIgnoreCase)
            || span.Flags.Contains("empty text", StringComparer.OrdinalIgnoreCase)).ToArray();
        var slow = spans.Where(span => span.Flags.Contains("slow", StringComparer.OrdinalIgnoreCase)).ToArray();
        var highToken = spans.Where(span => span.Flags.Contains("high tokens", StringComparer.OrdinalIgnoreCase)).ToArray();
        var toolSource = spans.Where(span => span.Flags.Contains("tool", StringComparer.OrdinalIgnoreCase)
            || span.Flags.Any(flag => flag.Contains("source", StringComparison.OrdinalIgnoreCase))
            || span.Flags.Contains("cached", StringComparer.OrdinalIgnoreCase)).ToArray();
        var severity = errors.Length > 0
            ? "Repair"
            : slow.Length > 0 || highToken.Length > 0
                ? "Watch"
                : issueSpans.Length > 0
                    ? "Review"
                    : "Clean";
        var focusSpan = errors.FirstOrDefault()
            ?? highToken.FirstOrDefault()
            ?? slow.FirstOrDefault()
            ?? pending.FirstOrDefault()
            ?? toolSource.FirstOrDefault()
            ?? spans.LastOrDefault();
        var focus = focusSpan is null
            ? "Run a match to create trace spans."
            : $"Turn {focusSpan.Turn} {focusSpan.Speaker}: {focusSpan.Kind}{(focusSpan.Flags.Count == 0 ? "" : $" ({string.Join(", ", focusSpan.Flags.Take(3))})")}";
        var summary = $"{severity}: {issueSpans.Length} issue span(s), {pending.Length} pending span(s), {errors.Length} repair span(s), {slow.Length} slow span(s), {highToken.Length} high-token span(s), {toolSource.Length} tool/source span(s).";
        var reviewTurns = issueSpans
            .Concat(toolSource)
            .GroupBy(span => (span.Turn, span.SpeakerId))
            .Select(group => group.First())
            .OrderBy(span => span.Flags.Any(IsIssueTraceFlag) ? 0 : 1)
            .ThenBy(span => span.Turn)
            .Take(6)
            .Select(span => $"Turn {span.Turn}: {span.Speaker} / {span.Kind} / {string.Join(", ", span.Flags)}")
            .ToArray();

        return new TranscriptRunTraceTriage(
            severity,
            focus,
            summary,
            issueSpans.Length,
            pending.Length,
            errors.Length,
            slow.Length,
            highToken.Length,
            toolSource.Length,
            reviewTurns);
    }

    private static TranscriptRunTraceSpan ToRunTraceSpan(TranscriptMessage message)
    {
        var flags = RunTraceFlags(message);
        return new TranscriptRunTraceSpan(
            message.Turn,
            message.SpeakerId,
            string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker,
            RunTraceKind(message),
            string.IsNullOrWhiteSpace(message.Status) ? "ok" : message.Status,
            string.IsNullOrWhiteSpace(message.Model) ? "-" : message.Model,
            Math.Max(0, message.LatencyMs),
            Math.Max(0, message.PromptTokens),
            Math.Max(0, message.CompletionTokens),
            Math.Max(0, message.TotalTokens),
            flags);
    }

    private static IReadOnlyList<string> RunTraceFlags(TranscriptMessage message)
    {
        var flags = new List<string>();
        if (message.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("error");
        }

        if (message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("pending");
        }

        if (message.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("rejected");
        }

        if (message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(message.InternetTool))
        {
            flags.Add("tool");
        }

        if (message.InternetCached)
        {
            flags.Add("cached");
        }

        if (message.InternetSources.Count > 0)
        {
            flags.Add($"{message.InternetSources.Count} source(s)");
        }

        if (message.LatencyMs >= 30000)
        {
            flags.Add("slow");
        }

        if (message.TotalTokens >= 4000)
        {
            flags.Add("high tokens");
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            flags.Add("empty text");
        }

        return flags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(7)
            .ToArray();
    }

    private static string RunTraceKind(TranscriptMessage message)
    {
        if (message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase))
        {
            return "internet tool";
        }

        if (message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            return "operator";
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return "narrator model";
        }

        return string.IsNullOrWhiteSpace(message.Model) || message.Model.Equals("operator", StringComparison.OrdinalIgnoreCase)
            ? message.Kind
            : "agent model";
    }

    private static bool IsModelTraceKind(string kind)
    {
        return kind.Equals("agent model", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("narrator model", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIssueTraceFlag(string flag)
    {
        return flag.Equals("error", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("rejected", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("slow", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("high tokens", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("empty text", StringComparison.OrdinalIgnoreCase);
    }

    private static string RunTraceNextAction(IReadOnlyList<TranscriptRunTraceSpan> spans)
    {
        if (spans.Count == 0)
        {
            return "Run a match, then use Review mode to inspect trace spans.";
        }

        if (spans.Any(span => span.Flags.Contains("error") || span.Flags.Contains("empty text")))
        {
            return "Repair failed or empty spans before treating the transcript as comparable evidence.";
        }

        if (spans.Any(span => span.Flags.Contains("high tokens")))
        {
            return "Inspect context growth and consider reducing transcript or private-memory windows.";
        }

        if (spans.Any(span => span.Flags.Contains("slow")))
        {
            return "Inspect the slowest model/provider span before repeating this setup.";
        }

        if (spans.Any(span => span.Flags.Contains("tool") || span.Flags.Any(flag => flag.Contains("source", StringComparison.OrdinalIgnoreCase))))
        {
            return "Verify tool/source spans before copying the run as evidence.";
        }

        return "Use Turn Compare or copy the trace packet as a baseline for a replay or fork.";
    }

    public static string CompareSummary(TranscriptMessage left, TranscriptMessage right)
    {
        return $"Comparing turn {left.Turn} ({left.Speaker}) with turn {right.Turn} ({right.Speaker}).";
    }

    public static string CompareDelta(int left, int right)
    {
        var delta = left - right;
        return delta == 0 ? "0" : delta > 0 ? $"+{delta}" : delta.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string CompareDurationDelta(int leftMs, int rightMs)
    {
        var delta = leftMs - rightMs;
        return delta == 0 ? "0s" : delta > 0 ? $"+{formatDuration(delta)}" : $"-{formatDuration(Math.Abs(delta))}";
    }

    private TranscriptAutoModeratorAlert? VoiceDriftAutoModeratorAlert(IReadOnlyList<TranscriptMessage> messages)
    {
        var drift = messages
            .Where(message => isAgentSpeaker(message.SpeakerId) || message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
            .Where(message => !string.IsNullOrWhiteSpace(message.VoiceStyle))
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Take(5)
            .Select(message => (Message: message, Diagnostic: voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text)))
            .Where(item => item.Diagnostic.State is "broken" or "drifting")
            .Take(2)
            .ToArray();
        if (drift.Length == 0)
        {
            return null;
        }

        var summary = string.Join("; ", drift.Select(item => $"{displayStatusValue(item.Message.SpeakerId)} {item.Diagnostic.Label}: {item.Diagnostic.State}"));
        return new TranscriptAutoModeratorAlert("Voice drift", $"{summary}. Turn on debug voice enforcement or use an operator nudge if voice style matters for this run.", "watch");
    }

    private static IReadOnlyList<TranscriptMessage> ReviewableMessages(IReadOnlyList<TranscriptMessage> messages)
    {
        return messages
            .Where(message => message.Kind is "" or "message" or "narration")
            .Where(message => !message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .Where(message => !message.SpeakerId.Equals("system", StringComparison.OrdinalIgnoreCase))
            .Where(message => !message.SpeakerId.Equals("internet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
    }

    private static IReadOnlyList<TranscriptAfterActionSource> KeyAfterActionSources(IReadOnlyList<TranscriptMessage> messages)
    {
        return messages
            .Where(message => message.InternetSources.Count > 0)
            .SelectMany(message => message.InternetSources.Select(source => (message, source)))
            .Select(pair =>
            {
                var item = AgentInternetSourceItem.FromDisplayText(pair.source);
                return new TranscriptAfterActionSource(
                    pair.message.Turn,
                    pair.message.Speaker,
                    SourceValue(item.Title, item.Domain, item.Url, item.DisplayText, "Source"),
                    item.Domain,
                    item.Url,
                    TrimForReport(SourceValue(item.Snippet, "", "", item.DisplayText, ""), 180),
                    pair.message.InternetCheckedAt);
            })
            .GroupBy(source => string.IsNullOrWhiteSpace(source.Url) ? $"{source.Title}|{source.Domain}" : source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyList<string> UnresolvedDisagreementLines(FrictionDiagnostics diagnostics, IReadOnlyList<TranscriptMessage> messages)
    {
        var lines = new List<string>();
        if (diagnostics.Details.TryGetValue("consensus", out var consensus)
            && consensus.Label is "High" or "Collapse Risk")
        {
            lines.AddRange(consensus.Details.Where(IsActionableDiagnosticDetail));
        }

        if (diagnostics.Details.TryGetValue("unsupportedClaims", out var unsupported)
            && !unsupported.Label.Equals("Low", StringComparison.OrdinalIgnoreCase))
        {
            lines.AddRange(unsupported.Details.Where(IsActionableDiagnosticDetail));
        }

        lines.AddRange(messages
            .Where(message => ContainsDisagreementCue(message.Text))
            .Select(ClaimLine)
            .Where(line => !string.IsNullOrWhiteSpace(line)));

        return lines
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static IReadOnlyList<string> SourceConflictLines(FrictionDiagnostics diagnostics, IReadOnlyList<TranscriptMessage> messages)
    {
        var lines = new List<string>();
        if (diagnostics.Details.TryGetValue("sourceConflicts", out var sourceConflicts))
        {
            lines.AddRange(sourceConflicts.Details.Where(IsActionableDiagnosticDetail));
        }

        lines.AddRange(messages
            .Where(message => message.InternetSources.Count > 0 && ContainsSourceConflictCue(message.Text))
            .Select(message => $"{ClaimLine(message)} ({message.InternetSources.Count} source(s))"));

        return lines
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static string StrongestArgumentLine(IReadOnlyList<TranscriptMessage> messages)
    {
        var strongest = messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .OrderByDescending(message => message.InternetSources.Count > 0)
            .ThenByDescending(message => Math.Max(0, message.TotalTokens))
            .ThenByDescending(message => message.Text.Length)
            .FirstOrDefault();
        return strongest is null ? "none" : ClaimLine(strongest);
    }

    private static string BestAgentPerformanceLine(
        IReadOnlyList<TranscriptMessage> messages,
        IReadOnlyList<TranscriptBattleReviewSpeaker> speakers)
    {
        if (speakers.Count == 0)
        {
            return "none";
        }

        var sourceCounts = messages
            .GroupBy(message => message.SpeakerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(message => message.InternetSources.Count), StringComparer.OrdinalIgnoreCase);
        var errors = messages
            .Where(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
                || message.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            .GroupBy(message => message.SpeakerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var best = speakers
            .OrderByDescending(speaker => sourceCounts.GetValueOrDefault(speaker.SpeakerId) > 0)
            .ThenBy(speaker => errors.GetValueOrDefault(speaker.SpeakerId))
            .ThenByDescending(speaker => speaker.Turns)
            .ThenByDescending(speaker => speaker.TotalTokens)
            .First();
        var sourceText = sourceCounts.GetValueOrDefault(best.SpeakerId) > 0
            ? $", {sourceCounts[best.SpeakerId]} source(s)"
            : "";
        return $"{best.Speaker}: {best.Turns} turn(s), {CompactNumber(best.TotalTokens)} tok{sourceText}, {best.ModelSummary}";
    }

    private static string ClaimLine(TranscriptMessage message)
    {
        var preview = TrimForReport(FirstSentenceOrLine(message.Text), 180);
        return string.IsNullOrWhiteSpace(preview)
            ? ""
            : $"Turn {message.Turn} {message.Speaker}: {preview}";
    }

    private static string FormatAfterActionSource(TranscriptAfterActionSource source)
    {
        var title = SourceValue(source.Title, source.Domain, source.Url, "", "Source");
        var location = string.Join(" ", new[] { source.Domain, source.Url }.Where(item => !string.IsNullOrWhiteSpace(item)));
        var checkedAt = string.IsNullOrWhiteSpace(source.CheckedAt) ? "" : $" checked {source.CheckedAt}";
        return $"Turn {source.Turn} {source.Speaker}: {title}{(string.IsNullOrWhiteSpace(location) ? "" : $" - {location}")}{checkedAt}";
    }

    private static string FirstSentenceOrLine(string text)
    {
        var normalized = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        var sentenceEnd = normalized.IndexOfAny(['.', '!', '?']);
        return sentenceEnd >= 40 ? normalized[..(sentenceEnd + 1)] : normalized;
    }

    private static string TrimForReport(string value, int max)
    {
        var clean = string.Join(" ", (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= max ? clean : clean[..Math.Max(0, max - 1)].TrimEnd() + "...";
    }

    private static bool IsActionableDiagnosticDetail(string detail)
    {
        return !string.IsNullOrWhiteSpace(detail)
            && !detail.StartsWith("No ", StringComparison.OrdinalIgnoreCase)
            && !detail.Contains("not factual correctness", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDisagreementCue(string text)
    {
        return new[] { "however", "but ", "disagree", "reject", "not proven", "unsupported", "uncertain", "assumption" }
            .Any(cue => (text ?? "").Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSourceConflictCue(string text)
    {
        return new[] { "source", "sources", "conflict", "contradict", "disagree", "different reports", "not aligned" }
            .Any(cue => (text ?? "").Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static string SourceValue(string first, string second, string third, string fourth, string fallback)
    {
        foreach (var value in new[] { first, second, third, fourth })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback;
    }

    private static IReadOnlyList<TranscriptBattleReviewSpeaker> SpeakerBreakdown(IReadOnlyList<TranscriptMessage> messages, int totalTokens)
    {
        return messages
            .GroupBy(message => message.SpeakerId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                var tokens = items.Sum(message => Math.Max(0, message.TotalTokens));
                var latency = items.Sum(message => Math.Max(0, message.LatencyMs));
                var turns = Math.Max(1, items.Length);
                var models = items
                    .Select(message => message.Model.Trim())
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();
                var modelSummary = models.Length == 0
                    ? "model unknown"
                    : string.Join(", ", models) + (items.Select(message => message.Model.Trim()).Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 2 ? ", +" : "");
                return new TranscriptBattleReviewSpeaker(
                    group.Key,
                    items.First().Speaker,
                    items.Length,
                    tokens,
                    items.Sum(message => Math.Max(0, message.PromptTokens)),
                    items.Sum(message => Math.Max(0, message.CompletionTokens)),
                    latency,
                    latency / turns,
                    modelSummary,
                    totalTokens <= 0 ? 0 : (int)Math.Round(tokens / (double)totalTokens * 100));
            })
            .OrderByDescending(speaker => speaker.TotalTokens)
            .ThenByDescending(speaker => speaker.Turns)
            .ThenBy(speaker => speaker.Speaker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string WatchTarget(
        IReadOnlyList<TranscriptBattleReviewSpeaker> speakers,
        FrictionDiagnostics diagnostics,
        TranscriptMessage? slowest,
        int errorCount)
    {
        if (errorCount > 0)
        {
            return "failed turn";
        }

        if (diagnostics.UnsupportedClaimCount > 0 || diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            return "evidence";
        }

        if (diagnostics.RoleDriftPercent >= 38)
        {
            return "role drift";
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            return "lock-in";
        }

        if (slowest is not null && slowest.LatencyMs >= 30000)
        {
            return $"turn {slowest.Turn}";
        }

        var dominant = speakers.FirstOrDefault(speaker => speaker.SharePercent >= 60);
        return dominant is null ? "none" : dominant.Speaker;
    }

    private static IReadOnlyList<string> BattleReviewFlags(
        IReadOnlyList<TranscriptMessage> messages,
        FrictionDiagnostics diagnostics,
        int modelCount,
        bool hasNarrator,
        TranscriptMessage? slowest,
        int errorCount)
    {
        var flags = new List<string>();
        if (messages.Count == 0)
        {
            flags.Add("no transcript");
        }

        if (errorCount > 0)
        {
            flags.Add($"{errorCount} error turn(s)");
        }

        if (modelCount <= 1)
        {
            flags.Add("single model");
        }

        if (!hasNarrator && messages.Count >= 3)
        {
            flags.Add("no narrator synthesis");
        }

        if (diagnostics.StateSeverity.Equals("danger", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add(diagnostics.StateLabel);
        }

        if (diagnostics.UnsupportedClaimCount > 0)
        {
            flags.Add($"{diagnostics.UnsupportedClaimCount} unsupported claim(s)");
        }

        if (diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("weak evidence");
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            flags.Add($"consensus {diagnostics.ConsensusPercent}%");
        }

        if (diagnostics.RoleDriftPercent >= 38)
        {
            flags.Add($"role drift {diagnostics.RoleDriftPercent}%");
        }

        if (diagnostics.NarrativeHeatScore >= 82)
        {
            flags.Add($"heat {diagnostics.NarrativeHeatLabel}");
        }

        if (slowest is not null && slowest.LatencyMs >= 30000)
        {
            flags.Add($"slow turn {slowest.Turn}");
        }

        return flags
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static string BattleReviewNextAction(
        IReadOnlyList<TranscriptMessage> messages,
        FrictionDiagnostics diagnostics,
        int errorCount,
        int score)
    {
        if (messages.Count == 0)
        {
            return "Run a match, then use Review mode to compare agents, telemetry, and risk flags.";
        }

        if (errorCount > 0)
        {
            return "Repair or retry failed turns before treating this run as comparable evidence.";
        }

        if (diagnostics.UnsupportedClaimCount > 0 || diagnostics.EvidencePressureLabel.Equals("Weak", StringComparison.OrdinalIgnoreCase))
        {
            return "Ask the next agent to separate evidence, inference, and assumption before deciding.";
        }

        if (diagnostics.ConsensusPercent >= 78)
        {
            return "Inject one dissenting operator turn or compare against a fork before accepting the consensus.";
        }

        if (score < 80)
        {
            return "Generate a Decision Card and compare the last two substantive turns before choosing a winner.";
        }

        return "Use Turn Compare or copy this packet as a local judge note for the run.";
    }

    private UIElement CreateAfterActionReportRows(TranscriptAfterActionReport report)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        rows.Children.Add(CreateAfterActionRow("Strongest", report.StrongestArgument, resourceBrush("PrimaryBorderBrush")));
        rows.Children.Add(CreateAfterActionRow("Best agent", report.BestAgentPerformance, resourceBrush("AlphaAccentBrush")));
        if (report.KeySources.Count > 0)
        {
            rows.Children.Add(CreateAfterActionRow("Sources", string.Join("; ", report.KeySources.Take(3).Select(FormatAfterActionSource)), resourceBrush("AssistBorderBrush")));
        }

        if (report.SourceConflicts.Count > 0)
        {
            rows.Children.Add(CreateAfterActionRow("Conflicts", string.Join("; ", report.SourceConflicts.Take(2)), resourceBrush("BetaAccentBrush")));
        }
        else if (report.UnresolvedDisagreements.Count > 0)
        {
            rows.Children.Add(CreateAfterActionRow("Unresolved", string.Join("; ", report.UnresolvedDisagreements.Take(2)), resourceBrush("BetaAccentBrush")));
        }

        return rows;
    }

    private Border CreateAfterActionRow(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Foreground = accent,
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(value) ? "none" : value,
                        Foreground = resourceBrush("TextBrush"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 17,
                        Margin = new Thickness(0, 2, 0, 0)
                    }
                }
            }
        };
    }

    private UIElement CreateSpeakerReviewRows(TranscriptBattleReview review)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var speaker in review.Speakers.Take(5))
        {
            var accent = accentForSpeaker(speaker.SpeakerId);
            rows.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("InputBrush"), accent, 0.09),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.36),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children =
                    {
                        new StackPanel
                        {
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = speaker.Speaker,
                                    Foreground = resourceBrush("TextBrush"),
                                    FontSize = 12,
                                    FontWeight = FontWeights.SemiBold,
                                    TextTrimming = TextTrimming.CharacterEllipsis
                                },
                                new TextBlock
                                {
                                    Text = speaker.ModelSummary,
                                    Foreground = resourceBrush("MutedTextBrush"),
                                    FontSize = 10.5,
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                    Margin = new Thickness(0, 2, 0, 0)
                                }
                            }
                        },
                        SpeakerMetricStack(speaker)
                    }
                }
            });
        }

        return rows;
    }

    private UIElement CreateRunTraceRows(TranscriptRunTrace trace)
    {
        var accent = trace.IssueCount > 0 ? resourceBrush("BetaAccentBrush") : resourceBrush("PrimaryBorderBrush");
        var root = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(new TextBlock
        {
            Text = "Run Trace",
            Foreground = accent,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        root.Children.Add(new TextBlock
        {
            Text = trace.Summary,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new TextBlock
        {
            Text = trace.Triage.Summary,
            Foreground = accent,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"Focus: {trace.Triage.Focus}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new TextBlock
        {
            Text = trace.NextAction,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        metrics.Children.Add(CreateCompareMetric("Triage", trace.Triage.Severity, accent));
        metrics.Children.Add(CreateCompareMetric("Spans", trace.SpanCount.ToString(CultureInfo.InvariantCulture), accent));
        metrics.Children.Add(CreateCompareMetric("Tools", trace.ToolCallCount.ToString(CultureInfo.InvariantCulture), resourceBrush("AssistBorderBrush")));
        metrics.Children.Add(CreateCompareMetric("Issues", trace.IssueCount.ToString(CultureInfo.InvariantCulture), trace.IssueCount > 0 ? resourceBrush("BetaAccentBrush") : resourceBrush("PrimaryBorderBrush")));
        metrics.Children.Add(CreateCompareMetric("Slowest", trace.SlowestSpan, resourceBrush("AlphaAccentBrush")));
        root.Children.Add(metrics);

        if (trace.Triage.ReviewTurns.Count > 0)
        {
            var queue = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            queue.Children.Add(new TextBlock
            {
                Text = "Review Queue",
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            foreach (var item in trace.Triage.ReviewTurns.Take(compactTranscriptMode() ? 3 : 5))
            {
                queue.Children.Add(new TextBlock
                {
                    Text = item,
                    Foreground = resourceBrush("MutedTextBrush"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            root.Children.Add(queue);
        }

        foreach (var span in trace.Spans.TakeLast(compactTranscriptMode() ? 6 : 9))
        {
            var rowAccent = RunTraceAccent(span);
            var row = new StackPanel();
            row.Children.Add(new TextBlock
            {
                Text = $"Turn {span.Turn}: {span.Speaker} - {span.Kind}",
                Foreground = resourceBrush("TextBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{span.Status} / {span.Model} / {CompactNumber(span.TotalTokens)} tok / {FormatReviewDuration(span.LatencyMs)}",
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });

            if (span.Flags.Count > 0)
            {
                var flags = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                foreach (var flag in span.Flags.Take(5))
                {
                    flags.Children.Add(transcriptCards.CreateStatPill(flag, isInternet: false, accentOverride: rowAccent));
                }

                row.Children.Add(flags);
            }

            root.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("InputBrush"), rowAccent, 0.08),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), rowAccent, 0.34),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 9, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            });
        }

        return root;
    }

    private StackPanel SpeakerMetricStack(TranscriptBattleReviewSpeaker speaker)
    {
        var metrics = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        metrics.Children.Add(new TextBlock
        {
            Text = $"{speaker.SharePercent}%",
            Foreground = resourceBrush("PrimaryBorderBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        metrics.Children.Add(new TextBlock
        {
            Text = $"{speaker.Turns} turn(s) / {CompactNumber(speaker.TotalTokens)} tok / avg {FormatReviewDuration(speaker.AverageLatencyMs)}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(metrics, 1);
        return metrics;
    }

    private Brush RunTraceAccent(TranscriptRunTraceSpan span)
    {
        if (span.Flags.Any(flag => flag.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            return resourceBrush("DangerBorderBrush");
        }

        if (span.Flags.Any(IsIssueTraceFlag))
        {
            return resourceBrush("BetaAccentBrush");
        }

        if (span.Flags.Any(flag => flag.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || flag.Contains("source", StringComparison.OrdinalIgnoreCase)
            || flag.Contains("approval", StringComparison.OrdinalIgnoreCase)))
        {
            return resourceBrush("AssistBorderBrush");
        }

        return span.Kind.Contains("model", StringComparison.OrdinalIgnoreCase)
            ? accentForSpeaker(span.SpeakerId)
            : resourceBrush("MutedTextBrush");
    }

    private static string CompactNumber(int value)
    {
        if (value < 0)
        {
            value = 0;
        }

        return value >= 1000
            ? $"{value / 1000d:0.#}k"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatReviewDuration(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0s";
        }

        if (milliseconds < 1000)
        {
            return $"{milliseconds} ms";
        }

        if (milliseconds < 60000)
        {
            return $"{milliseconds / 1000d:0.#}s";
        }

        return $"{milliseconds / 60000d:0.#}m";
    }

    private static void CopyBattleReviewPacket(TranscriptBattleReview review)
    {
        ShellClipboard.TrySetText(BattleReviewText(review));
    }

    private static void CopyBattleReviewJson(TranscriptBattleReview review)
    {
        ShellClipboard.TrySetText(BattleReviewJson(review));
    }

    private static void CopyBattleReviewNudge(TranscriptBattleReview review)
    {
        ShellClipboard.TrySetText(BattleReviewNudgeText(review));
    }

    private static void CopyRunTracePacket(IReadOnlyList<TranscriptMessage> messages)
    {
        ShellClipboard.TrySetText(RunTraceText(BuildRunTrace(messages)));
    }

    private Border CreateTurnCompareColumn(TranscriptMessage message, string slot)
    {
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase);
        var isSystemEvent = TranscriptCardRenderer.IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? resourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet ? resourceBrush("AssistBorderBrush") : accentForSpeaker(message.SpeakerId));

        var stack = new StackPanel();
        var title = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        title.Children.Add(transcriptCards.CreateStatPill(slot, isInternet));
        title.Children.Add(new TextBlock
        {
            Text = $"Turn {message.Turn}: {TranscriptCardRenderer.TranscriptSpeakerTitle(message, isInternet, isSystemEvent)}",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(title);

        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (!string.IsNullOrWhiteSpace(message.Model))
        {
            meta.Children.Add(transcriptCards.CreateStatPill(message.Model, isInternet));
        }
        var compareVoiceChip = RoleStyleCatalog.VoiceStyleChipText(message.VoiceStyle);
        if (!isInternet && !isSystemEvent && !string.IsNullOrWhiteSpace(compareVoiceChip))
        {
            meta.Children.Add(transcriptCards.CreateStatPill(compareVoiceChip, isInternet));
            if (shouldShowStyleFit())
            {
                var compareAdherence = voiceStyleAdherenceService.Analyze(message.VoiceStyle, message.Text);
                meta.Children.Add(transcriptCards.CreateStatPill(
                    RoleStyleCatalog.VoiceAdherenceChipText(compareAdherence),
                    isInternet,
                    accentOverride: voiceAdherenceAccent(compareAdherence),
                    toolTip: RoleStyleCatalog.VoiceAdherenceTooltip(compareAdherence)));
            }
        }
        meta.Children.Add(transcriptCards.CreateStatPill(FormatGeneratedTokens(message), isInternet));
        if (message.PromptTokens > 0)
        {
            meta.Children.Add(transcriptCards.CreateStatPill($"ctx: {formatCompactNumber(message.PromptTokens)}", isInternet));
        }
        if (message.LatencyMs > 0)
        {
            meta.Children.Add(transcriptCards.CreateStatPill(formatDuration(message.LatencyMs), isInternet));
        }
        meta.Children.Add(transcriptCards.CreateStatPill(TranscriptCardRenderer.DisplayTime(message.CreatedAt), isInternet));
        stack.Children.Add(meta);

        stack.Children.Add(new Border
        {
            Background = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, 0.12),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.44),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11),
            Child = new ScrollViewer
            {
                MaxHeight = compactTranscriptMode() ? 170 : 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text,
                    Foreground = resourceBrush("TextBrush"),
                    FontSize = compactTranscriptMode() ? 12 : 13,
                    LineHeight = compactTranscriptMode() ? 17 : 19,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11),
            Margin = slot == "A" ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0),
            Child = stack
        };
    }

    private Border CreateTurnComparePlaceholder(string slot)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"Slot {slot}",
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Use Compare on a transcript card to fill this side.",
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        });

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), resourceBrush("MutedTextBrush"), 0.04),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11),
            Margin = slot == "A" ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0),
            Child = stack
        };
    }

    private Border CreateCompareMetric(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private void SetDecisionCardActionSize(Button button)
    {
        button.Width = double.NaN;
        button.MinWidth = 86;
        button.Height = compactTranscriptMode() ? 26 : 32;
        button.MinHeight = button.Height;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = compactTranscriptMode()
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(10, 5, 10, 5);
    }

    private Button PanelActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind, string glyph)
    {
        var button = ActionButton(text, handler, enabled, kind);
        button.Content = CreateInlineCommandContent(glyph, text, compactTranscriptMode() ? 11 : 12);
        button.MinWidth = compactTranscriptMode() ? 78 : 92;
        button.MinHeight = compactTranscriptMode() ? 28 : 32;
        button.Padding = compactTranscriptMode()
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(10, 5, 10, 5);
        return button;
    }

    private static StackPanel CreateInlineCommandContent(string glyph, string label, double iconSize)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = iconSize,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        return cardFactory.CreateCard(title, body, background, accent, extraContent);
    }

    private Button ActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        return createActionButton(text, handler, enabled, kind, iconGlyph);
    }

    private string FormatGeneratedTokens(TranscriptMessage message)
    {
        return message.CompletionTokens > 0
            ? $"{formatCompactNumber(message.CompletionTokens)} Tok"
            : "Tok unknown";
    }
}
