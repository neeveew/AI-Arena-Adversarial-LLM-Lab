using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal enum TranscriptCardHeaderLayout
{
    Stacked,
    Inline
}

internal enum TranscriptCardFooterLayout
{
    Stacked,
    SideBySide
}

internal enum TranscriptActionKind
{
    Neutral,
    Primary,
    Danger
}

internal sealed class TranscriptCardRenderer
{
    private readonly Func<bool> compactTranscriptMode;
    private readonly TranscriptActionCoordinator transcriptActions;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> personaForSpeaker;
    private readonly Func<string> currentAvatarStyle;
    private readonly Func<bool> useChampionPortrait;
    private readonly Func<bool> useSystemGlyph;
    private readonly Func<bool> shouldShowStyleFit;
    private readonly Func<string, string, VoiceAdherenceDiagnostic> analyzeVoiceAdherence;
    private readonly Func<int, string> formatDuration;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Action<TranscriptMessage> copyTranscriptMessage;
    private readonly Func<TranscriptMessage, Task> togglePinTranscriptMessageAsync;
    private readonly Func<TranscriptMessage, Task> retryTranscriptMessageAsync;
    private readonly Func<TranscriptMessage, Task> deleteTranscriptMessageAsync;
    private readonly Action<TranscriptMessage> copyInternetUrl;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<bool> turnCompareMode;
    private readonly Func<TranscriptMessage, bool> isSelectedForCompare;
    private readonly Func<TranscriptMessage, bool> canCompareMessage;
    private readonly Action<TranscriptMessage> toggleTurnCompareMessage;
    private readonly Func<TranscriptMessage, bool> canSpeakTranscriptMessage;
    private readonly Action<TranscriptMessage> speakTranscriptMessage;
    private readonly Func<bool> showInternetDetails;

    public TranscriptCardRenderer(
        Func<bool> compactTranscriptMode,
        TranscriptActionCoordinator transcriptActions,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> personaForSpeaker,
        Func<string> currentAvatarStyle,
        Func<bool> useChampionPortrait,
        Func<bool> useSystemGlyph,
        Func<bool> shouldShowStyleFit,
        Func<string, string, VoiceAdherenceDiagnostic> analyzeVoiceAdherence,
        Func<int, string> formatDuration,
        Func<int, string> formatCompactNumber,
        Action<TranscriptMessage> copyTranscriptMessage,
        Func<TranscriptMessage, Task> togglePinTranscriptMessageAsync,
        Func<TranscriptMessage, Task> retryTranscriptMessageAsync,
        Func<TranscriptMessage, Task> deleteTranscriptMessageAsync,
        Action<TranscriptMessage> copyInternetUrl,
        Func<string, bool> isAgentSpeaker,
        Func<bool> turnCompareMode,
        Func<TranscriptMessage, bool> isSelectedForCompare,
        Func<TranscriptMessage, bool> canCompareMessage,
        Action<TranscriptMessage> toggleTurnCompareMessage,
        Func<TranscriptMessage, bool>? canSpeakTranscriptMessage = null,
        Action<TranscriptMessage>? speakTranscriptMessage = null,
        Func<bool>? showInternetDetails = null)
    {
        this.compactTranscriptMode = compactTranscriptMode;
        this.transcriptActions = transcriptActions;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.personaForSpeaker = personaForSpeaker;
        this.currentAvatarStyle = currentAvatarStyle;
        this.useChampionPortrait = useChampionPortrait;
        this.useSystemGlyph = useSystemGlyph;
        this.shouldShowStyleFit = shouldShowStyleFit;
        this.analyzeVoiceAdherence = analyzeVoiceAdherence;
        this.formatDuration = formatDuration;
        this.formatCompactNumber = formatCompactNumber;
        this.copyTranscriptMessage = copyTranscriptMessage;
        this.togglePinTranscriptMessageAsync = togglePinTranscriptMessageAsync;
        this.retryTranscriptMessageAsync = retryTranscriptMessageAsync;
        this.deleteTranscriptMessageAsync = deleteTranscriptMessageAsync;
        this.copyInternetUrl = copyInternetUrl;
        this.isAgentSpeaker = isAgentSpeaker;
        this.turnCompareMode = turnCompareMode;
        this.isSelectedForCompare = isSelectedForCompare;
        this.canCompareMessage = canCompareMessage;
        this.toggleTurnCompareMessage = toggleTurnCompareMessage;
        this.canSpeakTranscriptMessage = canSpeakTranscriptMessage ?? (_ => false);
        this.speakTranscriptMessage = speakTranscriptMessage ?? (_ => { });
        this.showInternetDetails = showInternetDetails ?? (() => true);
    }

    public Border CreateCard(TranscriptMessage message, bool retryable, bool searchMatch, bool isLatest)
    {
        var hasInternetDetails = HasInternetDetails(message);
        var visibleInternetDetails = ShouldRenderInternetDetails(message, showInternetDetails());
        var isInternet = IsInternetMessage(message);
        var body = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text;
        var isSystemEvent = IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? resourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet || visibleInternetDetails) ? resourceBrush("AssistBorderBrush") : accentForSpeaker(message.SpeakerId);

        var actions = new RightAlignedWrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0)
        };
        var canMutate = message.Turn > 0;
        actions.Children.Add(CreateActionButton("Copy", (_, _) => copyTranscriptMessage(message), canMutate, iconGlyph: "\uE8C8"));
        actions.Children.Add(CreateActionButton("Speak", (_, _) => speakTranscriptMessage(message), canMutate && canSpeakTranscriptMessage(message), iconGlyph: "\uE189"));
        var pinButton = CreateActionButton(
            message.Pinned ? "Unpin" : "Pin",
            async (_, _) => await togglePinTranscriptMessageAsync(message),
            canMutate,
            message.Pinned ? TranscriptActionKind.Primary : TranscriptActionKind.Neutral,
            "\uE718");
        SetToggleActionState(
            pinButton,
            message.Pinned,
            message.Pinned
                ? "This message is pinned. Activate to unpin it."
                : "This message is not pinned. Activate to pin it.");
        actions.Children.Add(pinButton);
        actions.Children.Add(CreateActionButton("Retry", async (_, _) => await retryTranscriptMessageAsync(message), canMutate && retryable && isAgentSpeaker(message.SpeakerId) && !isInternet, iconGlyph: "\uE72C"));
        actions.Children.Add(CreateActionButton("Delete", async (_, _) => await deleteTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Danger, "\uE74D"));
        if (turnCompareMode())
        {
            var selectedForCompare = isSelectedForCompare(message);
            var compareButton = CreateActionButton(
                selectedForCompare ? "Drop compare" : "Compare",
                (_, _) => toggleTurnCompareMessage(message),
                canMutate && canCompareMessage(message),
                selectedForCompare ? TranscriptActionKind.Primary : TranscriptActionKind.Neutral,
                "\uE8AB");
            SetToggleActionState(
                compareButton,
                selectedForCompare,
                selectedForCompare
                    ? "This message is selected for comparison. Activate to remove it."
                    : "This message is not selected for comparison. Activate to add it.");
            actions.Children.Add(compareButton);
        }
        AutomationProperties.SetName(actions, $"Message actions for turn {message.Turn}");
        AutomationProperties.SetHelpText(
            actions,
            "Copy, speak, pin, retry, delete, or compare this transcript message when each action is enabled.");

        var extras = new StackPanel();
        if (visibleInternetDetails)
        {
            extras.Children.Add(CreateExpander(
                "Internet details",
                accent: resourceBrush("AssistBorderBrush"),
                content: CreateInternetDetails(message)));
        }

        Expander? reasoning = null;
        if (!string.IsNullOrWhiteSpace(message.Reasoning))
        {
            reasoning = CreateExpander(
                "Model reasoning",
                accentForSpeaker(message.SpeakerId),
                new TextBlock
                {
                    Text = message.Reasoning,
                    Foreground = resourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 8, 0, 0)
                });
        }
        extras.Children.Add(CreateMessageFooter(message, actions, reasoning));
        return CreateCardLayout(message, body, accent, isInternet, searchMatch, isLatest, isSystemEvent, extras);
    }

    public Button CreateActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        return transcriptActions.CreateCardButton(text, handler, enabled, kind, iconGlyph);
    }

    private static void SetToggleActionState(Button button, bool active, string helpText)
    {
        AutomationProperties.SetItemStatus(button, active ? "active" : "inactive");
        AutomationProperties.SetHelpText(button, helpText);
    }

    internal static bool CanSpeakMessage(TranscriptMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Text);
    }

    internal static bool ShouldRenderInternetDetails(TranscriptMessage message, bool showDebugDetails)
    {
        return showDebugDetails && HasInternetDetails(message);
    }

    public Border CreateStatPill(string text, bool isInternet, bool isDanger = false, Brush? accentOverride = null, string? toolTip = null)
    {
        var compact = compactTranscriptMode();
        var accent = accentOverride ?? resourceBrush(isInternet ? "AssistBorderBrush" : "PrimaryBorderBrush");
        return new Border
        {
            Background = isDanger ? resourceBrush("DangerBrush") : blendBrush(resourceBrush("TranscriptBodyBrush"), accent, 0.1),
            BorderBrush = isDanger ? resourceBrush("DangerBorderBrush") : blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.38),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.SmallRadius,
            Padding = compact ? new Thickness(5, 0, 5, 1) : new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, compact ? 4 : 6, 0),
            ToolTip = toolTip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = isDanger ? resourceBrush("DangerTextBrush") : accentOverride ?? resourceBrush("MutedTextBrush"),
                FontSize = compact ? ArenaTokens.CaptionFontSize : ArenaTokens.BodyFontSize,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = compact ? 150 : 230,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
    }

    public Expander CreateExpander(string header, Brush accent, UIElement content)
    {
        if (content is FrameworkElement contentElement)
        {
            contentElement.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        return new Expander
        {
            Header = header,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = $"Show {header.ToLowerInvariant()}",
            Content = new Border
            {
                Background = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, 0.14),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
                BorderThickness = new Thickness(1),
                CornerRadius = ArenaTokens.MediumRadius,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = content
            }
        };
    }

    private Grid CreateMessageFooter(TranscriptMessage message, WrapPanel actions, Expander? reasoning)
    {
        var compact = compactTranscriptMode();
        var footer = new Grid
        {
            Margin = new Thickness(0, compact ? 3 : 5, 0, 0)
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AutomationProperties.SetName(footer, $"Transcript message footer for turn {message.Turn}");

        if (reasoning is not null)
        {
            Grid.SetRow(reasoning, 0);
            Grid.SetColumn(reasoning, 0);
            Grid.SetColumnSpan(reasoning, 2);
            Panel.SetZIndex(reasoning, 0);
            footer.Children.Add(reasoning);
        }

        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(actions, 1);
        footer.Children.Add(actions);

        void ApplyLayout(double width)
        {
            if (reasoning is null)
            {
                Grid.SetRow(actions, 0);
                Grid.SetColumn(actions, 0);
                Grid.SetColumnSpan(actions, 2);
                actions.Margin = new Thickness(0, compact ? 3 : 5, 0, 0);
                return;
            }

            var layout = ResolveCardFooterLayout(width);
            var sideBySide = layout == TranscriptCardFooterLayout.SideBySide;
            Grid.SetRow(reasoning, 0);
            Grid.SetColumn(reasoning, 0);
            Grid.SetColumnSpan(reasoning, 2);
            Grid.SetRow(actions, sideBySide ? 0 : 1);
            Grid.SetColumn(actions, sideBySide ? 1 : 0);
            Grid.SetColumnSpan(actions, sideBySide ? 1 : 2);
            actions.Margin = sideBySide
                ? new Thickness(compact ? 8 : 12, 12, 0, 0)
                : new Thickness(0, compact ? 5 : 7, 0, 0);
        }

        ApplyLayout(0);
        footer.Loaded += (_, _) => ApplyLayout(footer.ActualWidth);
        footer.SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
        return footer;
    }

    public UIElement CreateInternetDetails(TranscriptMessage message)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 2, 0, 0)
        };

        void AddRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var row = new Border
            {
                Background = resourceBrush("InputBrush"),
                BorderBrush = resourceBrush("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = ArenaTokens.SmallRadius,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = $"{label}: {value}",
                    Foreground = resourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            panel.Children.Add(row);
        }

        AddRow("Requester", message.InternetRequester);
        AddRow("Mode", "executed");
        AddRow("Tool", message.InternetTool);
        AddRow("Query", message.InternetQuery);
        AddRow("URL", message.InternetUrl);
        AddRow("Reason", message.InternetReason);
        AddRow("Fetch", string.IsNullOrWhiteSpace(message.InternetCheckedAt)
            ? ""
            : $"{(message.InternetCached ? "cached" : "fetched")} at {message.InternetCheckedAt}");
        AddRow("Summary", message.InternetSummary);
        if (message.InternetSources.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Sources",
                Foreground = resourceBrush("MutedTextBrush"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4)
            });
            foreach (var source in message.InternetSources)
            {
                panel.Children.Add(new Border
                {
                    Background = blendBrush(resourceBrush("TranscriptBodyBrush"), resourceBrush("AssistBorderBrush"), 0.12),
                    BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), resourceBrush("AssistBorderBrush"), 0.45),
                    BorderThickness = new Thickness(1),
                    CornerRadius = ArenaTokens.SmallRadius,
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = source,
                        Foreground = resourceBrush("TextBrush"),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        return panel;
    }

    public static string TranscriptSpeakerTitle(TranscriptMessage message, bool isInternet, bool isSystemEvent)
    {
        if (isInternet)
        {
            return "Internet Tool";
        }

        if (isSystemEvent)
        {
            return "System Event";
        }

        return string.IsNullOrWhiteSpace(message.Speaker) ? "Unknown" : message.Speaker;
    }

    public static bool IsSystemEvent(TranscriptMessage message, bool isInternet)
    {
        if (isInternet)
        {
            return true;
        }

        if (!message.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Text.Contains("Model call failed", StringComparison.OrdinalIgnoreCase)
            || message.Text.Contains("Provider unreachable", StringComparison.OrdinalIgnoreCase)
            || message.Text.Contains("provider", StringComparison.OrdinalIgnoreCase);
    }

    public static string DisplayTime(double createdAt)
    {
        if (createdAt <= 0)
        {
            return "";
        }

        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds((long)createdAt)
                .ToLocalTime()
                .ToString("h:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    internal static TranscriptCardHeaderLayout ResolveCardHeaderLayout(double width)
    {
        return double.IsFinite(width) && width >= 720
            ? TranscriptCardHeaderLayout.Inline
            : TranscriptCardHeaderLayout.Stacked;
    }

    internal static TranscriptCardFooterLayout ResolveCardFooterLayout(double width)
    {
        return double.IsFinite(width) && width >= 620
            ? TranscriptCardFooterLayout.SideBySide
            : TranscriptCardFooterLayout.Stacked;
    }

    internal static string BuildModelStatsSummary(
        TranscriptMessage message,
        Func<int, string> formatDuration,
        Func<int, string> formatCompactNumber)
    {
        if (!ShouldRenderModelStats(message))
        {
            return "";
        }

        var summary = new List<string> { message.Model.Trim() };
        if (message.LatencyMs > 0)
        {
            summary.Add(formatDuration(message.LatencyMs));
        }

        if (message.CompletionTokens > 0)
        {
            summary.Add($"{formatCompactNumber(message.CompletionTokens)} Tok");
        }

        if (message.TokensPerSecond > 0)
        {
            summary.Add($"{message.TokensPerSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} tok/s");
        }

        return string.Join("  \u00B7  ", summary);
    }

    internal string BuildModelStatsHelpText(TranscriptMessage message)
    {
        if (!ShouldRenderModelStats(message))
        {
            return "";
        }

        var details = new List<string>
        {
            $"Model: {message.Model.Trim()}"
        };
        if (!string.IsNullOrWhiteSpace(message.VoiceStyle))
        {
            details.Add($"Voice: {RoleStyleCatalog.VoiceStyleLabel(message.VoiceStyle)}");
        }

        if (message.LatencyMs > 0)
        {
            details.Add($"Response time: {formatDuration(message.LatencyMs)}");
        }

        if (message.TimeToFirstTokenMs > 0)
        {
            details.Add($"Time to first token: {formatDuration(message.TimeToFirstTokenMs)}");
        }

        if (message.ModelLoadTimeMs > 0)
        {
            details.Add($"Model load: {formatDuration(message.ModelLoadTimeMs)}");
        }

        if (message.TokensPerSecond > 0)
        {
            details.Add($"Throughput: {message.TokensPerSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} tok/s");
        }

        if (message.PromptTokens > 0)
        {
            details.Add($"Prompt/context tokens: {formatCompactNumber(message.PromptTokens)}");
        }

        if (message.CompletionTokens > 0)
        {
            details.Add($"Generated tokens: {formatCompactNumber(message.CompletionTokens)}");
        }

        if (message.TotalTokens > 0)
        {
            details.Add($"Total tokens: {formatCompactNumber(message.TotalTokens)}");
        }

        if (shouldShowStyleFit() && !string.IsNullOrWhiteSpace(message.VoiceStyle))
        {
            var diagnostic = analyzeVoiceAdherence(message.VoiceStyle, message.Text);
            var summary = string.IsNullOrWhiteSpace(diagnostic.Summary)
                ? RoleStyleCatalog.VoiceAdherenceChipText(diagnostic)
                : $"{RoleStyleCatalog.VoiceAdherenceChipText(diagnostic)} - {diagnostic.Summary}";
            details.Add($"Style fit: {summary}");
        }

        if (!string.IsNullOrWhiteSpace(message.Status))
        {
            details.Add($"Status: {message.Status.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(message.ProviderResponseId))
        {
            details.Add($"Provider response ID: {message.ProviderResponseId.Trim()}");
        }

        return string.Join(Environment.NewLine, details);
    }

    private ContentControl? CreateModelStatsHost(TranscriptMessage message)
    {
        var summary = BuildModelStatsSummary(message, formatDuration, formatCompactNumber);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var detail = BuildModelStatsHelpText(message);
        var capsule = CreateStatPill(summary, isInternet: false);
        capsule.Margin = new Thickness(0);
        if (capsule.Child is TextBlock summaryText)
        {
            summaryText.MaxWidth = compactTranscriptMode() ? 280 : 360;
        }

        var host = new ModelStatsContentControl
        {
            Content = capsule,
            Focusable = true,
            Cursor = Cursors.Help,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        KeyboardNavigation.SetIsTabStop(host, true);
        host.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Arena.FocusVisual");
        AutomationProperties.SetName(host, $"Model statistics for turn {message.Turn}: {summary}");
        AutomationProperties.SetHelpText(host, detail);

        var toolTip = new ToolTip
        {
            Background = resourceBrush("TranscriptBodyBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Foreground = resourceBrush("TextBrush"),
            Padding = new Thickness(10, 8, 10, 8),
            Placement = PlacementMode.Bottom,
            PlacementTarget = host,
            MaxWidth = 440,
            Content = new TextBlock
            {
                Text = detail,
                Foreground = resourceBrush("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 410
            }
        };
        host.ToolTip = toolTip;
        ToolTipService.SetInitialShowDelay(host, 300);
        ToolTipService.SetShowDuration(host, 60000);
        host.GotKeyboardFocus += (_, _) => toolTip.IsOpen = true;
        host.LostKeyboardFocus += (_, _) => toolTip.IsOpen = false;
        host.Unloaded += (_, _) => toolTip.IsOpen = false;
        return host;
    }

    private IReadOnlyList<Border> CreateStatePills(TranscriptMessage message, bool isInternet)
    {
        var pills = new List<Border>();
        if (!message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            pills.Add(CreateStatPill(message.Status, isInternet, isDanger: message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)));
        }

        if (message.Pinned)
        {
            pills.Add(CreateStatPill("pinned", isInternet));
        }

        return pills;
    }

    private static bool ShouldRenderModelStats(TranscriptMessage message)
    {
        var isInternet = IsInternetMessage(message);
        var isSystem = IsSystemEvent(message, isInternet)
            || message.Kind.Equals("system", StringComparison.OrdinalIgnoreCase)
            || message.SpeakerId.Equals("system", StringComparison.OrdinalIgnoreCase);
        var isOperator = message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase);
        var model = message.Model?.Trim();
        return message.Turn > 0
            && !isInternet
            && !isSystem
            && !isOperator
            && !string.IsNullOrWhiteSpace(model)
            && !model.Equals("-", StringComparison.Ordinal);
    }

    private Border CreateCardLayout(TranscriptMessage message, string body, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent, UIElement? extraContent)
    {
        var compact = compactTranscriptMode();
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        // Speaker colour belongs in the peripheral rail; a stronger field tint is reserved
        // for the current/error card so long-form reading does not require repeated colour
        // adaptation across large saturated surfaces.
        var accentWeight = isLatest ? (compact ? 0.12 : 0.15) : (compact ? 0.025 : 0.035);
        var normalBackground = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, isError ? 0.14 : accentWeight);
        var hoverBackground = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, isError ? 0.20 : accentWeight + 0.055);
        var normalBorder = isError
            ? resourceBrush("DangerBorderBrush")
            : blendBrush(resourceBrush("ControlBorderBrush"), accent, searchMatch || isLatest ? 0.74 : 0.18);
        var hoverBorder = isError ? resourceBrush("DangerTextBrush") : blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.82);
        var border = new Border
        {
            Style = null,
            Background = normalBackground,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(searchMatch || isLatest || isError ? 2 : 1),
            CornerRadius = new CornerRadius(compact ? ArenaTokens.SmallRadiusValue : ArenaTokens.MediumRadiusValue),
            Margin = new Thickness(0, 0, 0, compact ? 4 : 8),
            Opacity = 1.0
        };
        border.MouseEnter += (_, _) =>
        {
            border.Background = hoverBackground;
            border.BorderBrush = hoverBorder;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = normalBackground;
            border.BorderBrush = normalBorder;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 36 : 58) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rail = new Grid
        {
            Background = blendBrush(resourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.14 : 0.045)
        };
        rail.Children.Add(new Border
        {
            Width = isLatest ? (compact ? 4 : 5) : (compact ? 2 : 3),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(compact ? ArenaTokens.SmallRadiusValue : ArenaTokens.MediumRadiusValue, 0, 0, compact ? ArenaTokens.SmallRadiusValue : ArenaTokens.MediumRadiusValue)
        });
        var railStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = compact ? new Thickness(5, 6, 3, 5) : new Thickness(6, 9, 5, 8)
        };
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!compact)
        {
            railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var turnNumber = new TextBlock
        {
            Text = message.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = resourceBrush("TextBrush"),
            FontSize = compact ? 13 : 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(turnNumber, 0);
        railStack.Children.Add(turnNumber);

        if (!compact)
        {
            var avatar = CreateAvatar(message, accent, isInternet, isSystemEvent);
            avatar.Margin = new Thickness(0, 7, 0, 5);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(avatar, 1);
            railStack.Children.Add(avatar);
        }

        var railLabel = new TextBlock
        {
            Text = compact ? CompactRailLabel(message, isInternet) : TranscriptRailLabel(message, isInternet),
            Foreground = accent,
            FontSize = compact ? 8.5 : 9.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        Grid.SetRow(railLabel, compact ? 1 : 2);
        railStack.Children.Add(railLabel);

        rail.Children.Add(railStack);
        Grid.SetColumn(rail, 0);
        grid.Children.Add(rail);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var header = new Border
        {
            Background = blendBrush(resourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.17 : isError ? 0.14 : 0.055),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, isLatest ? 0.48 : 0.16),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = compact ? new Thickness(8, 5, 8, 5) : new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(0, compact ? ArenaTokens.SmallRadiusValue : ArenaTokens.MediumRadiusValue, 0, 0)
        };
        header.Child = CreateHeader(message, accent, isInternet, searchMatch, isLatest, isSystemEvent);
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var bodyStack = new StackPanel
        {
            Margin = compact ? new Thickness(8, 7, 8, 8) : new Thickness(12, 10, 12, 11)
        };
        var bodyBlock = new TextBlock
        {
            Text = body,
            Foreground = isError ? resourceBrush("DangerTextBrush") : resourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = compact ? 12 : 14,
            LineHeight = compact ? 17 : 20,
            MaxWidth = 940,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        if (isError)
        {
            bodyStack.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("DangerBrush"), accent, 0.08),
                BorderBrush = resourceBrush("DangerBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = ArenaTokens.MediumRadius,
                Padding = new Thickness(12),
                Child = bodyBlock
            });
        }
        else
        {
            bodyStack.Children.Add(bodyBlock);
        }

        if (extraContent is not null)
        {
            bodyStack.Children.Add(extraContent);
        }

        Grid.SetRow(bodyStack, 1);
        content.Children.Add(bodyStack);

        border.Child = grid;
        return border;
    }

    private AgentAvatarControl CreateAvatar(TranscriptMessage message, Brush accent, bool isInternet, bool isSystemEvent)
    {
        var avatar = new AgentAvatarControl
        {
            Width = 34,
            Height = 34,
            AgentId = message.SpeakerId,
            DisplayName = message.Speaker,
            Model = message.Model,
            Persona = personaForSpeaker(message.SpeakerId),
            AccentBrush = accent,
            BaseBrush = resourceBrush("TranscriptBodyBrush"),
            IsSystem = isSystemEvent || isInternet,
            AvatarStyle = currentAvatarStyle(),
            UseChampionPortrait = useChampionPortrait(),
            UseSystemGlyph = useSystemGlyph(),
            FallbackText = SpeakerGlyph(message, isInternet),
            ToolTip = AvatarToolTip(message, isSystemEvent)
        };
        ToolTipService.SetShowDuration(avatar, 60000);
        return avatar;
    }

    private Grid CreateHeader(TranscriptMessage message, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent)
    {
        var compact = compactTranscriptMode();
        var header = new Grid();
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = TranscriptSpeakerTitle(message, isInternet, isSystemEvent),
            Foreground = resourceBrush("TextBrush"),
            FontSize = compact ? 12.5 : 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = compact ? 360 : 520,
            Margin = new Thickness(0, 0, 8, 0)
        });
        if (isLatest)
        {
            titleRow.Children.Add(CreateStatPill("Latest", isInternet));
        }
        if (searchMatch)
        {
            titleRow.Children.Add(CreateStatPill("Search match", isInternet));
        }
        if (!isInternet && message.InternetSources.Count > 0)
        {
            var sourceButton = AgentInternetSourcesPresenter.CreateButton(
                InternetSourceSummaryForTurn(message),
                resourceBrush,
                blendBrush,
                $"Show internet sources for turn {message.Turn}",
                compact ? 22 : 23);
            sourceButton.Margin = new Thickness(0, 0, 6, 0);
            sourceButton.ToolTip = $"Searched web: {message.InternetSources.Count} source(s)";
            titleRow.Children.Add(sourceButton);
        }
        Grid.SetRow(titleRow, 0);
        Grid.SetColumn(titleRow, 0);
        header.Children.Add(titleRow);

        var modelStats = CreateModelStatsHost(message);
        if (modelStats is not null)
        {
            header.Children.Add(modelStats);
        }

        var state = new WrapPanel
        {
            Margin = compact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 6, 0, 0)
        };
        foreach (var pill in CreateStatePills(message, isInternet))
        {
            state.Children.Add(pill);
        }
        Grid.SetColumn(state, 0);
        Grid.SetColumnSpan(state, 3);
        if (state.Children.Count > 0)
        {
            header.Children.Add(state);
        }

        var time = new TextBlock
        {
            Text = DisplayTime(message.CreatedAt),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = compact ? ArenaTokens.CaptionFontSize : ArenaTokens.LabelFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetRow(time, 0);
        Grid.SetColumn(time, 2);
        header.Children.Add(time);

        void ApplyLayout(double width)
        {
            if (modelStats is null)
            {
                Grid.SetRow(state, 1);
                return;
            }

            var inline = ResolveCardHeaderLayout(width) == TranscriptCardHeaderLayout.Inline;
            Grid.SetRow(modelStats, inline ? 0 : 1);
            Grid.SetColumn(modelStats, inline ? 1 : 0);
            Grid.SetColumnSpan(modelStats, inline ? 1 : 3);
            modelStats.Margin = inline
                ? new Thickness(compact ? 6 : 10, 0, 0, 0)
                : new Thickness(0, compact ? 4 : 6, 0, 0);
            Grid.SetRow(state, inline ? 1 : 2);
        }

        ApplyLayout(0);
        header.Loaded += (_, _) => ApplyLayout(header.ActualWidth);
        header.SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
        return header;
    }

    private static bool HasInternetDetails(TranscriptMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.InternetTool)
            || !string.IsNullOrWhiteSpace(message.InternetQuery)
            || !string.IsNullOrWhiteSpace(message.InternetUrl)
            || message.InternetSources.Count > 0;
    }

    private static AgentInternetSourceSummary InternetSourceSummaryForTurn(TranscriptMessage message)
    {
        return new AgentInternetSourceSummary(
            message.InternetQuery,
            message.InternetCheckedAt,
            message.InternetSources);
    }

    private static bool IsInternetMessage(TranscriptMessage message)
    {
        return message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase);
    }

    internal static string TranscriptRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (isInternet)
        {
            return "Internet";
        }

        if (IsSystemEvent(message, isInternet))
        {
            return "System";
        }

        return message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase)
            ? "Operator"
            : "Agent";
    }

    internal static string CompactRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (isInternet)
        {
            return "WEB";
        }

        if (IsSystemEvent(message, isInternet))
        {
            return "SYS";
        }

        if (message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            return "OP";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerId)
            ? "AI"
            : message.SpeakerId[..Math.Min(3, message.SpeakerId.Length)].ToUpperInvariant();
    }

    private static string SpeakerGlyph(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet))
        {
            return "!";
        }

        if (isInternet)
        {
            return "i";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerId)
            ? "?"
            : message.SpeakerId[..1].ToUpperInvariant();
    }

    private static string AvatarToolTip(TranscriptMessage message, bool isSystemEvent)
    {
        var speaker = string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker;
        var model = string.IsNullOrWhiteSpace(message.Model) ? "-" : message.Model;
        var kind = isSystemEvent ? "System event" : "Deterministic procedural avatar";
        return $"{speaker}{Environment.NewLine}Model: {model}{Environment.NewLine}{kind}";
    }

}
