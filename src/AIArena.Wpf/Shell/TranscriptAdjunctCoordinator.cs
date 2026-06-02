using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

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
        actions.Children.Add(ActionButton(
            "Auto latest",
            (_, _) => reselectLatestCompareTurns(visibleMessages),
            visibleMessages.Any(TranscriptInsightCoordinator.CanCompareMessage)));
        actions.Children.Add(ActionButton(
            "Clear",
            (_, _) => clearTurnCompareSelection(),
            hasTurnCompareSelection()));
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
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
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
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var root = new DockPanel { LastChildFill = true };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var expandButton = ActionButton(
            decisionCardExpanded ? "Collapse" : "Expand",
            (_, _) =>
            {
                decisionCardExpanded = !decisionCardExpanded;
                refreshTranscript();
            },
            hasCard);
        var generateButton = ActionButton("Generate", async (_, _) => await generateDecisionCardAsync(), true, TranscriptActionKind.Primary);
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
            FontSize = 12
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
            FontSize = 11.5,
            TextWrapping = decisionCardExpanded && hasCard ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = decisionCardExpanded && hasCard ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            LineHeight = decisionCardExpanded && hasCard ? 17 : double.NaN,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = summary
        });
        root.Children.Add(content);
        card.Child = root;
        return card;
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
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var alert in alerts.Take(5))
        {
            var alertAccent = alert.Severity.Equals("danger", StringComparison.OrdinalIgnoreCase)
                ? resourceBrush("DangerBorderBrush")
                : resourceBrush("BetaAccentBrush");
            panel.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("InputBrush"), alertAccent, 0.1),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), alertAccent, 0.35),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = alert.Label,
                            Foreground = alertAccent,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = alert.Body,
                            Foreground = resourceBrush("TextBrush"),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
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

    public Border CreateNewsInspectorCard(TranscriptMessage message)
    {
        var isPending = message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accent = isError
            ? resourceBrush("DangerBorderBrush")
            : isPending
                ? resourceBrush("NarratorAccentBrush")
                : resourceBrush("AssistBorderBrush");
        var title = $"Turn {message.Turn} - {InternetInspectorTitle(message)}";
        var body = string.Join(
            Environment.NewLine,
            string.IsNullOrWhiteSpace(message.InternetTool) ? "" : $"Tool: {message.InternetTool}",
            string.IsNullOrWhiteSpace(message.InternetQuery) ? "" : $"Query: {message.InternetQuery}",
            string.IsNullOrWhiteSpace(message.InternetUrl) ? "" : $"URL: {message.InternetUrl}",
            string.IsNullOrWhiteSpace(message.InternetReason) ? "" : $"Why selected: {message.InternetReason}",
            string.IsNullOrWhiteSpace(message.InternetCheckedAt) ? "" : $"Fetched: {message.InternetCheckedAt}",
            string.IsNullOrWhiteSpace(message.Text) ? "" : $"Drop: {message.Text}")
            .Trim();
        var extras = transcriptCards.CreateExpander("Internet details", accent, transcriptCards.CreateInternetDetails(message));
        return CreateCard(title, string.IsNullOrWhiteSpace(body) ? "(no internet details)" : body, resourceBrush("CardBrush"), accent, extras);
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

    public static string InternetInspectorTitle(TranscriptMessage message)
    {
        if (message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase))
        {
            return $"Approval - {message.Status}";
        }

        if (message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return $"Curated news - {message.Status}";
        }

        return $"Internet tool - {message.Status}";
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

    private Border CreateTurnCompareColumn(TranscriptMessage message, string slot)
    {
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase);
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
            FontSize = 14,
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
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.36),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
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
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
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
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Use Compare on any transcript card to fill this side.",
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 0),
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
        button.MinWidth = 74;
        button.Height = compactTranscriptMode() ? 26 : 32;
        button.MinHeight = button.Height;
        button.VerticalAlignment = VerticalAlignment.Top;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = compactTranscriptMode()
            ? new Thickness(8, 3, 8, 3)
            : new Thickness(10, 5, 10, 5);
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        return CreateCard(CreateCardTitle(title), body, background, accent, extraContent);
    }

    private Border CreateCard(UIElement title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        var border = new Border
        {
            Style = null,
            Background = background,
            BorderBrush = resourceBrush("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Margin = new Thickness(-16, -16, 10, -16)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(stack, 1);
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = resourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        });
        if (extraContent is not null)
        {
            stack.Children.Add(extraContent);
        }
        grid.Children.Add(stack);

        border.Child = grid;
        return border;
    }

    private TextBlock CreateCardTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        };
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
