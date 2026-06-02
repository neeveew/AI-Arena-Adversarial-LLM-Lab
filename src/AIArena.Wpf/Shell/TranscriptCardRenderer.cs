using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal enum TranscriptActionKind
{
    Neutral,
    Primary,
    Danger
}

internal sealed class TranscriptCardRenderer
{
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<bool> arenaBusy;
    private readonly Action<Button> registerActionButton;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> personaForSpeaker;
    private readonly Func<string> currentAvatarStyle;
    private readonly Func<bool> useChampionPortrait;
    private readonly Func<bool> useSystemGlyph;
    private readonly Func<bool> shouldShowStyleFit;
    private readonly Func<string, string, VoiceAdherenceDiagnostic> analyzeVoiceAdherence;
    private readonly Func<VoiceAdherenceDiagnostic, Brush> voiceAdherenceAccent;
    private readonly Func<int, string> formatDuration;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Action<TranscriptMessage> copyTranscriptMessage;
    private readonly Func<TranscriptMessage, Task> togglePinTranscriptMessageAsync;
    private readonly Func<TranscriptMessage, Task> retryTranscriptMessageAsync;
    private readonly Func<TranscriptMessage, Task> deleteTranscriptMessageAsync;
    private readonly Func<TranscriptMessage, Task> approveInternetRequestAsync;
    private readonly Func<TranscriptMessage, Task> rejectInternetRequestAsync;
    private readonly Action<TranscriptMessage> copyInternetUrl;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<bool> turnCompareMode;
    private readonly Func<TranscriptMessage, bool> isSelectedForCompare;
    private readonly Func<TranscriptMessage, bool> canCompareMessage;
    private readonly Action<TranscriptMessage> toggleTurnCompareMessage;

    public TranscriptCardRenderer(
        Func<bool> compactTranscriptMode,
        Func<bool> arenaBusy,
        Action<Button> registerActionButton,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> personaForSpeaker,
        Func<string> currentAvatarStyle,
        Func<bool> useChampionPortrait,
        Func<bool> useSystemGlyph,
        Func<bool> shouldShowStyleFit,
        Func<string, string, VoiceAdherenceDiagnostic> analyzeVoiceAdherence,
        Func<VoiceAdherenceDiagnostic, Brush> voiceAdherenceAccent,
        Func<int, string> formatDuration,
        Func<int, string> formatCompactNumber,
        Action<TranscriptMessage> copyTranscriptMessage,
        Func<TranscriptMessage, Task> togglePinTranscriptMessageAsync,
        Func<TranscriptMessage, Task> retryTranscriptMessageAsync,
        Func<TranscriptMessage, Task> deleteTranscriptMessageAsync,
        Func<TranscriptMessage, Task> approveInternetRequestAsync,
        Func<TranscriptMessage, Task> rejectInternetRequestAsync,
        Action<TranscriptMessage> copyInternetUrl,
        Func<string, bool> isAgentSpeaker,
        Func<bool> turnCompareMode,
        Func<TranscriptMessage, bool> isSelectedForCompare,
        Func<TranscriptMessage, bool> canCompareMessage,
        Action<TranscriptMessage> toggleTurnCompareMessage)
    {
        this.compactTranscriptMode = compactTranscriptMode;
        this.arenaBusy = arenaBusy;
        this.registerActionButton = registerActionButton;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.personaForSpeaker = personaForSpeaker;
        this.currentAvatarStyle = currentAvatarStyle;
        this.useChampionPortrait = useChampionPortrait;
        this.useSystemGlyph = useSystemGlyph;
        this.shouldShowStyleFit = shouldShowStyleFit;
        this.analyzeVoiceAdherence = analyzeVoiceAdherence;
        this.voiceAdherenceAccent = voiceAdherenceAccent;
        this.formatDuration = formatDuration;
        this.formatCompactNumber = formatCompactNumber;
        this.copyTranscriptMessage = copyTranscriptMessage;
        this.togglePinTranscriptMessageAsync = togglePinTranscriptMessageAsync;
        this.retryTranscriptMessageAsync = retryTranscriptMessageAsync;
        this.deleteTranscriptMessageAsync = deleteTranscriptMessageAsync;
        this.approveInternetRequestAsync = approveInternetRequestAsync;
        this.rejectInternetRequestAsync = rejectInternetRequestAsync;
        this.copyInternetUrl = copyInternetUrl;
        this.isAgentSpeaker = isAgentSpeaker;
        this.turnCompareMode = turnCompareMode;
        this.isSelectedForCompare = isSelectedForCompare;
        this.canCompareMessage = canCompareMessage;
        this.toggleTurnCompareMessage = toggleTurnCompareMessage;
    }

    public Border CreateCard(TranscriptMessage message, bool retryable, bool searchMatch, bool isLatest)
    {
        var hasInternetDetails = HasInternetDetails(message);
        var isInternet = IsInternetMessage(message);
        var body = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text;
        var isSystemEvent = IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? resourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet || hasInternetDetails) ? resourceBrush("AssistBorderBrush") : accentForSpeaker(message.SpeakerId);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var canMutate = message.Turn > 0;
        actions.Children.Add(CreateActionButton("Copy", (_, _) => copyTranscriptMessage(message), canMutate, iconGlyph: "\uE8C8"));
        actions.Children.Add(CreateActionButton(message.Pinned ? "Unpin" : "Pin", async (_, _) => await togglePinTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Primary, "\uE718"));
        actions.Children.Add(CreateActionButton("Retry", async (_, _) => await retryTranscriptMessageAsync(message), canMutate && retryable && isAgentSpeaker(message.SpeakerId) && !isInternet, iconGlyph: "\uE72C"));
        actions.Children.Add(CreateActionButton("Delete", async (_, _) => await deleteTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Danger, "\uE74D"));
        if (turnCompareMode())
        {
            var selectedForCompare = isSelectedForCompare(message);
            actions.Children.Add(CreateActionButton(
                selectedForCompare ? "Drop compare" : "Compare",
                (_, _) => toggleTurnCompareMessage(message),
                canMutate && canCompareMessage(message),
                selectedForCompare ? TranscriptActionKind.Primary : TranscriptActionKind.Neutral));
        }
        if (message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase) && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            actions.Children.Add(CreateActionButton("Approve Once", async (_, _) => await approveInternetRequestAsync(message), canMutate, TranscriptActionKind.Primary));
            actions.Children.Add(CreateActionButton("Reject", async (_, _) => await rejectInternetRequestAsync(message), canMutate, TranscriptActionKind.Danger));
            actions.Children.Add(CreateActionButton("Copy URL", (_, _) => copyInternetUrl(message), canMutate && !string.IsNullOrWhiteSpace(message.InternetUrl)));
        }

        var extras = new StackPanel();
        if (hasInternetDetails)
        {
            extras.Children.Add(CreateExpander(
                message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase) ? "Source / selection" : "Internet details",
                accent: resourceBrush("AssistBorderBrush"),
                content: CreateInternetDetails(message)));
        }
        if (!string.IsNullOrWhiteSpace(message.Reasoning))
        {
            extras.Children.Add(CreateExpander(
                "Model reasoning",
                accentForSpeaker(message.SpeakerId),
                new TextBlock
                {
                    Text = message.Reasoning,
                    Foreground = resourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                }));
        }
        extras.Children.Add(actions);
        return CreateCardLayout(message, body, accent, isInternet, searchMatch, isLatest, isSystemEvent, extras);
    }

    public Button CreateActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        var iconMode = !string.IsNullOrWhiteSpace(iconGlyph);
        var compact = compactTranscriptMode();
        var iconSize = compact ? 24 : 30;
        var background = resourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("ControlBorderBrush")
        };
        var foreground = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("TextBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerTextBrush"),
            _ => resourceBrush("TextBrush")
        };
        var button = new Button
        {
            Content = iconMode
                ? new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = compact ? 13 : 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : text,
            IsEnabled = enabled && !arenaBusy(),
            Tag = enabled,
            Background = iconMode ? Brushes.Transparent : background,
            BorderBrush = iconMode ? Brushes.Transparent : border,
            Foreground = foreground,
            FontSize = iconMode ? (compact ? 13 : 15) : (compact ? 12 : 13),
            FontWeight = FontWeights.SemiBold,
            MinWidth = iconMode ? iconSize : 0,
            MinHeight = iconMode ? iconSize : (compact ? 26 : 32),
            Width = iconMode ? iconSize : double.NaN,
            Height = iconMode ? iconSize : double.NaN,
            Padding = iconMode ? new Thickness(0) : compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5),
            Margin = iconMode ? new Thickness(0, 0, compact ? 3 : 5, compact ? 2 : 4) : new Thickness(0, 0, compact ? 5 : 8, compact ? 5 : 8),
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        if (handler is not null)
        {
            button.Click += handler;
        }
        registerActionButton(button);
        return button;
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
            CornerRadius = new CornerRadius(4),
            Padding = compact ? new Thickness(5, 0, 5, 1) : new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, compact ? 4 : 6, 0),
            ToolTip = toolTip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = isDanger ? resourceBrush("DangerTextBrush") : accentOverride ?? resourceBrush("MutedTextBrush"),
                FontSize = compact ? 10 : 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    public Expander CreateExpander(string header, Brush accent, UIElement content)
    {
        return new Expander
        {
            Header = header,
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
            ToolTip = $"Show {header.ToLowerInvariant()}",
            Content = new Border
            {
                Background = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, 0.14),
                BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = content
            }
        };
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
                CornerRadius = new CornerRadius(4),
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
        AddRow("Mode", message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase) ? "pending approval" : "executed");
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
                    CornerRadius = new CornerRadius(4),
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
        if (isSystemEvent)
        {
            return "SYSTEM EVENT";
        }

        if (isInternet)
        {
            return message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                ? "Internet Approval"
                : "Internet Tool";
        }

        return string.IsNullOrWhiteSpace(message.Speaker) ? "Unknown" : message.Speaker;
    }

    public static bool IsSystemEvent(TranscriptMessage message, bool isInternet)
    {
        if (isInternet || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
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

    private IReadOnlyList<Border> CreateStatPills(TranscriptMessage message, bool isInternet)
    {
        var pills = new List<Border>
        {
            CreateStatPill(formatDuration(message.LatencyMs), isInternet)
        };

        pills.Add(CreateStatPill(FormatGeneratedTokens(message), isInternet));

        if (message.PromptTokens > 0)
        {
            pills.Add(CreateStatPill($"ctx: {formatCompactNumber(message.PromptTokens)}", isInternet));
        }
        else if (message.TotalTokens > 0 && message.CompletionTokens <= 0)
        {
            pills.Add(CreateStatPill($"total: {formatCompactNumber(message.TotalTokens)}", isInternet));
        }

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

    private Border CreateCardLayout(TranscriptMessage message, string body, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent, UIElement? extraContent)
    {
        var compact = compactTranscriptMode();
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accentWeight = isLatest ? (compact ? 0.28 : 0.32) : (compact ? 0.14 : 0.18);
        var normalBackground = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, isError ? 0.16 : accentWeight);
        var hoverBackground = blendBrush(resourceBrush("TranscriptBodyBrush"), accent, isError ? 0.22 : accentWeight + 0.06);
        var normalBorder = isError
            ? resourceBrush("DangerBorderBrush")
            : blendBrush(resourceBrush("ControlBorderBrush"), accent, searchMatch || isLatest ? 0.82 : 0.38);
        var hoverBorder = isError ? resourceBrush("DangerTextBrush") : blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.82);
        var border = new Border
        {
            Style = null,
            Background = normalBackground,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(searchMatch || isLatest || isError ? 2 : 1),
            CornerRadius = new CornerRadius(compact ? 6 : 8),
            Margin = new Thickness(0, 0, 0, compact ? 6 : 12),
            Opacity = isLatest || isError || isSystemEvent ? 1.0 : 0.88
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 46 : 84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rail = new Grid
        {
            Background = blendBrush(resourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.22 : 0.11)
        };
        rail.Children.Add(new Border
        {
            Width = isLatest ? (compact ? 4 : 5) : (compact ? 2 : 3),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(compact ? 6 : 8, 0, 0, compact ? 6 : 8)
        });
        var railStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = compact ? new Thickness(6, 8, 4, 6) : new Thickness(8, 13, 6, 12)
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
            Foreground = Brushes.White,
            FontSize = compact ? 16 : 20,
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
            FontSize = compact ? 9 : 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
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
            Background = blendBrush(resourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.28 : isError ? 0.18 : 0.16),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, isLatest ? 0.56 : 0.28),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = compact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(0, compact ? 6 : 8, 0, 0)
        };
        header.Child = CreateHeader(message, accent, isInternet, searchMatch, isLatest, isSystemEvent);
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var bodyStack = new StackPanel
        {
            Margin = compact ? new Thickness(10, 8, 10, 8) : new Thickness(14, 13, 14, 13)
        };
        var bodyBlock = new TextBlock
        {
            Text = body,
            Foreground = isError ? resourceBrush("DangerTextBrush") : resourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = compact ? 13 : 15,
            LineHeight = compact ? 18 : 22
        };
        if (isError)
        {
            bodyStack.Children.Add(new Border
            {
                Background = blendBrush(resourceBrush("DangerBrush"), accent, 0.08),
                BorderBrush = resourceBrush("DangerBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
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
            Width = 44,
            Height = 44,
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
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = TranscriptSpeakerTitle(message, isInternet, isSystemEvent),
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        if (isLatest)
        {
            identity.Children.Add(CreateStatPill("Latest", isInternet));
        }
        if (!isInternet && !string.IsNullOrWhiteSpace(message.Model))
        {
            identity.Children.Add(CreateStatPill(message.Model, isInternet));
        }
        var voiceChip = RoleStyleCatalog.VoiceStyleChipText(message.VoiceStyle);
        if (!isInternet && !isSystemEvent && !string.IsNullOrWhiteSpace(voiceChip))
        {
            identity.Children.Add(CreateStatPill(voiceChip, isInternet));
            if (shouldShowStyleFit())
            {
                var voiceAdherence = analyzeVoiceAdherence(message.VoiceStyle, message.Text);
                identity.Children.Add(CreateStatPill(
                    RoleStyleCatalog.VoiceAdherenceChipText(voiceAdherence),
                    isInternet,
                    accentOverride: voiceAdherenceAccent(voiceAdherence),
                    toolTip: RoleStyleCatalog.VoiceAdherenceTooltip(voiceAdherence)));
            }
        }
        foreach (var pill in CreateStatPills(message, isInternet))
        {
            identity.Children.Add(pill);
        }
        if (searchMatch)
        {
            identity.Children.Add(CreateStatPill("search match", isInternet));
        }
        Grid.SetColumn(identity, 0);
        header.Children.Add(identity);

        var time = new TextBlock
        {
            Text = DisplayTime(message.CreatedAt),
            Foreground = resourceBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(time, 1);
        header.Children.Add(time);

        return header;
    }

    private static bool HasInternetDetails(TranscriptMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.InternetTool)
            || !string.IsNullOrWhiteSpace(message.InternetQuery)
            || !string.IsNullOrWhiteSpace(message.InternetUrl)
            || message.InternetSources.Count > 0;
    }

    private static bool IsInternetMessage(TranscriptMessage message)
    {
        return message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase);
    }

    private static string TranscriptRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet) || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSTEM";
        }

        return message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase)
            ? "OPERATOR"
            : "AGENT";
    }

    private static string CompactRailLabel(TranscriptMessage message, bool isInternet)
    {
        if (IsSystemEvent(message, isInternet) || message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase))
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

    private string FormatGeneratedTokens(TranscriptMessage message)
    {
        return message.CompletionTokens > 0
            ? $"{formatCompactNumber(message.CompletionTokens)} Tok"
            : "Tok unknown";
    }
}
