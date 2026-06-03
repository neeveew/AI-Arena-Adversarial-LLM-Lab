using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class CollaborateCoordinator
{
    private const double UserMessageMaxWidth = 820;
    private const double AssistantMessageMaxWidth = double.PositiveInfinity;
    private const int MaxStoredConversations = 24;

    private static readonly CollaborateRole[] DefaultRoles =
    [
        new("alpha", "Alpha", "Practical strategist. Produce concrete options, tradeoffs, and a clear path forward."),
        new("beta", "Beta", "Critical reviewer. Test assumptions, edge cases, and weak conclusions."),
        new("gamma", "Gamma", "Evidence mapper. Separate known facts from guesses and identify what would change the answer."),
        new("narrator", "Narrator", "Synthesis lead. Merge useful work into one direct answer.")
    ];

    private readonly IModelProviderClient modelClient;
    private readonly Dispatcher dispatcher;
    private readonly ScrollViewer chatScrollViewer;
    private readonly StackPanel messageItems;
    private readonly TextBox promptText;
    private readonly Button sendButton;
    private readonly Button clearButton;
    private readonly ComboBox modePicker;
    private readonly ComboBox roundsPicker;
    private readonly TextBlock statusText;
    private readonly TextBlock providerText;
    private readonly TextBlock topProviderText;
    private readonly TextBlock topModeText;
    private readonly TextBlock topTeamText;
    private readonly StackPanel participantItems;
    private readonly StackPanel recentItems;
    private readonly Func<ArenaViewSnapshot?> snapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<string> setShellStatus;
    private readonly CollaborateHistoryStore historyStore;
    private readonly List<CollaborateExchange> history = [];
    private readonly List<CollaborateConversation> conversations = [];
    private Guid? currentConversationId;
    private Popup? recentConversationPopup;

    private bool isRunning;

    public CollaborateCoordinator(
        IModelProviderClient? modelClient,
        Dispatcher dispatcher,
        ScrollViewer chatScrollViewer,
        StackPanel messageItems,
        TextBox promptText,
        Button sendButton,
        Button clearButton,
        ComboBox modePicker,
        ComboBox roundsPicker,
        TextBlock statusText,
        TextBlock providerText,
        TextBlock topProviderText,
        TextBlock topModeText,
        TextBlock topTeamText,
        StackPanel participantItems,
        StackPanel recentItems,
        Func<ArenaViewSnapshot?> snapshot,
        Func<string, Brush> resourceBrush,
        Action<string> setShellStatus,
        CollaborateHistoryStore? historyStore = null)
    {
        this.modelClient = modelClient ?? new ModelProviderClient();
        this.dispatcher = dispatcher;
        this.chatScrollViewer = chatScrollViewer;
        this.messageItems = messageItems;
        this.promptText = promptText;
        this.sendButton = sendButton;
        this.clearButton = clearButton;
        this.modePicker = modePicker;
        this.roundsPicker = roundsPicker;
        this.statusText = statusText;
        this.providerText = providerText;
        this.topProviderText = topProviderText;
        this.topModeText = topModeText;
        this.topTeamText = topTeamText;
        this.participantItems = participantItems;
        this.recentItems = recentItems;
        this.snapshot = snapshot;
        this.resourceBrush = resourceBrush;
        this.setShellStatus = setShellStatus;
        this.historyStore = historyStore ?? new CollaborateHistoryStore();
    }

    public void Initialize()
    {
        LoadPersistedConversations();
        RenderEmptyState();
        RefreshProviderState();
        RefreshRecentItems();
    }

    public void RefreshProviderState()
    {
        var current = snapshot();
        var providerModel = current is null ? "-" : DisplayModel(current.ProviderModel);
        providerText.Text = current is null
            ? "No active session."
            : $"{providerModel}\n{DisplayBaseUrl(current.ProviderBaseUrl)}";
        var mode = SelectedMode();
        var rounds = EffectiveRounds(mode, SelectedRounds());
        topProviderText.Text = providerModel;
        topModeText.Text = ModeLabel(mode);
        topTeamText.Text = $"{DefaultRoles.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} agents / {RoundLabel(rounds)}";
        roundsPicker.IsEnabled = !isRunning && !mode.Equals("fast", StringComparison.OrdinalIgnoreCase);

        participantItems.Children.Clear();
        foreach (var role in DefaultRoles)
        {
            var model = current is null ? "-" : DisplayModel(ModelForRole(current, role.Id));
            participantItems.Children.Add(CreateParticipantRow(role, model));
        }
    }

    public void RefreshTheme()
    {
        CloseRecentConversationMenu();
        RefreshProviderState();
        RefreshRecentItems();
        if (currentConversationId is Guid id)
        {
            var conversation = conversations.FirstOrDefault(item => item.Id == id);
            if (conversation is not null)
            {
                RenderConversation(conversation);
                return;
            }
        }

        if (history.Count == 0)
        {
            RenderEmptyState();
        }
    }

    public void Clear()
    {
        currentConversationId = null;
        history.Clear();
        promptText.Clear();
        statusText.Text = "Ready.";
        RefreshRecentItems();
        RenderEmptyState();
    }

    public async Task SendAsync()
    {
        if (isRunning)
        {
            return;
        }

        var prompt = promptText.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            promptText.Focus();
            return;
        }

        var current = snapshot();
        if (current is null)
        {
            statusText.Text = "Load a session before collaborating.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CleanModel(current.ProviderModel)))
        {
            statusText.Text = "No provider model is configured.";
            return;
        }

        isRunning = true;
        sendButton.IsEnabled = false;
        clearButton.IsEnabled = false;
        modePicker.IsEnabled = false;
        roundsPicker.IsEnabled = false;
        promptText.IsEnabled = false;
        promptText.Clear();

        if (history.Count == 0)
        {
            messageItems.Children.Clear();
        }

        AddUserMessage(prompt);
        var answerHost = AddAssistantMessage(out var traceItems);
        ScrollToEnd();

        try
        {
            var mode = SelectedMode();
            var rounds = EffectiveRounds(mode, SelectedRounds());
            statusText.Text = $"Running {ModeLabel(mode)} ({RoundLabel(rounds)})...";
            setShellStatus(statusText.Text);

            var result = mode switch
            {
                "fast" => await RunFastAsync(current, prompt, traceItems),
                "critique" => await RunCritiqueAsync(current, prompt, traceItems, rounds),
                _ => await RunTeamDraftAsync(current, prompt, traceItems, rounds)
            };

            var finalAnswer = string.IsNullOrWhiteSpace(result.FinalAnswer)
                ? "No answer was produced."
                : result.FinalAnswer;
            RenderMarkdown(answerHost, finalAnswer, 14);
            history.Add(new CollaborateExchange(prompt, finalAnswer, result.TraceSteps.ToArray()));
            TrimHistory();
            SaveCurrentConversation();
            statusText.Text = result.Ok ? "Ready." : "Answer completed with model errors.";
            setShellStatus(statusText.Text);
        }
        catch (Exception ex)
        {
            RenderMarkdown(answerHost, $"Collaboration failed: {ex.Message}", 14);
            statusText.Text = "Collaboration failed.";
            setShellStatus(statusText.Text);
        }
        finally
        {
            isRunning = false;
            sendButton.IsEnabled = true;
            clearButton.IsEnabled = true;
            modePicker.IsEnabled = true;
            promptText.IsEnabled = true;
            RefreshProviderState();
            promptText.Focus();
            ScrollToEnd();
        }
    }

    private async Task<CollaborateRunResult> RunFastAsync(ArenaViewSnapshot current, string prompt, StackPanel traceItems)
    {
        var final = await CompleteRoleAsync(
            current,
            "narrator",
            "Direct answer",
            PromptForFinal(current, prompt, []));
        AddTraceStep(traceItems, final);
        return ResultFromFinal(final, []);
    }

    private async Task<CollaborateRunResult> RunTeamDraftAsync(
        ArenaViewSnapshot current,
        string prompt,
        StackPanel traceItems,
        int rounds)
    {
        var steps = new List<CollaborateStep>();
        for (var round = 1; round <= rounds; round++)
        {
            foreach (var roleId in new[] { "alpha", "beta", "gamma" })
            {
                statusText.Text = $"{RoleName(roleId)} round {round}/{rounds}...";
                var step = await CompleteRoleAsync(
                    current,
                    roleId,
                    RoundLabel(round, round == 1 ? "Draft" : "Refinement"),
                    round == 1
                        ? PromptForDraft(current, prompt, roleId)
                        : PromptForRoundPass(
                            current,
                            prompt,
                            roleId,
                            round,
                            steps,
                            "Improve the team's answer. Add high-signal corrections, stronger options, sharper tradeoffs, or clearer next steps. Avoid repeating points that are already good."));
                steps.Add(step);
                AddTraceStep(traceItems, step);
            }
        }

        statusText.Text = "Narrator synthesizing...";
        var final = await CompleteRoleAsync(
            current,
            "narrator",
            "Synthesis",
            PromptForFinal(current, prompt, steps));
        AddTraceStep(traceItems, final);
        return ResultFromFinal(final, steps);
    }

    private async Task<CollaborateRunResult> RunCritiqueAsync(
        ArenaViewSnapshot current,
        string prompt,
        StackPanel traceItems,
        int rounds)
    {
        var steps = new List<CollaborateStep>();
        for (var round = 1; round <= rounds; round++)
        {
            if (round == 1)
            {
                statusText.Text = $"Alpha round {round}/{rounds}...";
                var draft = await CompleteRoleAsync(
                    current,
                    "alpha",
                    RoundLabel(round, "Draft"),
                    PromptForDraft(current, prompt, "alpha"));
                steps.Add(draft);
                AddTraceStep(traceItems, draft);

                statusText.Text = $"Beta round {round}/{rounds}...";
                var critique = await CompleteRoleAsync(
                    current,
                    "beta",
                    RoundLabel(round, "Critique"),
                    PromptForCritique(current, prompt, draft));
                steps.Add(critique);
                AddTraceStep(traceItems, critique);

                statusText.Text = $"Gamma round {round}/{rounds}...";
                var refinement = await CompleteRoleAsync(
                    current,
                    "gamma",
                    RoundLabel(round, "Refinement"),
                    PromptForRefinement(current, prompt, draft, critique));
                steps.Add(refinement);
                AddTraceStep(traceItems, refinement);
                continue;
            }

            foreach (var role in new[]
                     {
                         ("alpha", "Revision", "Revise the strongest answer based on the critiques and evidence so far. Keep it concise and action-oriented."),
                         ("beta", "Critique", "Find remaining weaknesses, hidden assumptions, and risks in the current team direction. Include concrete fixes."),
                         ("gamma", "Evidence refinement", "Tighten the answer around evidence, uncertainty, and decision criteria. Remove weak or unsupported claims.")
                     })
            {
                statusText.Text = $"{RoleName(role.Item1)} round {round}/{rounds}...";
                var step = await CompleteRoleAsync(
                    current,
                    role.Item1,
                    RoundLabel(round, role.Item2),
                    PromptForRoundPass(current, prompt, role.Item1, round, steps, role.Item3));
                steps.Add(step);
                AddTraceStep(traceItems, step);
            }
        }

        statusText.Text = "Narrator synthesizing...";
        var final = await CompleteRoleAsync(
            current,
            "narrator",
            "Synthesis",
            PromptForFinal(current, prompt, steps));
        AddTraceStep(traceItems, final);
        return ResultFromFinal(final, steps);
    }

    private async Task<CollaborateStep> CompleteRoleAsync(
        ArenaViewSnapshot current,
        string roleId,
        string label,
        IReadOnlyList<ModelChatMessage> messages)
    {
        var plan = ProviderPlanForRole(current, roleId);
        if (plan.Primary is null)
        {
            return CollaborateStep.Failed(roleId, RoleName(roleId), "-", label, "No model configured.");
        }

        var result = await modelClient.CompleteChatAsync(plan.Primary, messages);
        var model = plan.Primary.Model;
        if (!result.Ok && plan.Fallback is not null)
        {
            result = await modelClient.CompleteChatAsync(plan.Fallback, messages);
            model = plan.Fallback.Model;
        }

        return result.Ok
            ? CollaborateStep.Completed(roleId, RoleName(roleId), model, label, result.Text, result.LatencyMs, result.TotalTokens)
            : CollaborateStep.Failed(roleId, RoleName(roleId), model, label, result.Error);
    }

    private IReadOnlyList<ModelChatMessage> PromptForDraft(ArenaViewSnapshot current, string prompt, string roleId)
    {
        var role = Role(roleId);
        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Collaborate.
                Role: {RolePersona(current, role)}.
                Write one concise draft answer for the user's latest request.
                Be useful, concrete, and avoid meta discussion about the collaboration process.
                """),
            new ModelChatMessage("user", $"{ConversationContext()}\nLatest request:\n{prompt}")
        ];
    }

    private IReadOnlyList<ModelChatMessage> PromptForCritique(ArenaViewSnapshot current, string prompt, CollaborateStep draft)
    {
        var role = Role("beta");
        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Collaborate.
                Role: {RolePersona(current, role)}.
                Review the draft for missing assumptions, weak claims, risk, and unclear advice.
                Return a compact critique plus concrete improvements.
                """),
            new ModelChatMessage("user", $"{ConversationContext()}\nLatest request:\n{prompt}\n\nDraft:\n{StepTextForPrompt(draft)}")
        ];
    }

    private IReadOnlyList<ModelChatMessage> PromptForRefinement(ArenaViewSnapshot current, string prompt, CollaborateStep draft, CollaborateStep critique)
    {
        var role = Role("gamma");
        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Collaborate.
                Role: {RolePersona(current, role)}.
                Refine the answer using the draft and critique.
                Keep only high-confidence points and flag uncertainty briefly.
                """),
            new ModelChatMessage("user", $"{ConversationContext()}\nLatest request:\n{prompt}\n\nDraft:\n{StepTextForPrompt(draft)}\n\nCritique:\n{StepTextForPrompt(critique)}")
        ];
    }

    private IReadOnlyList<ModelChatMessage> PromptForRoundPass(
        ArenaViewSnapshot current,
        string prompt,
        string roleId,
        int round,
        IReadOnlyList<CollaborateStep> priorSteps,
        string instruction)
    {
        var role = Role(roleId);
        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Collaborate.
                Role: {RolePersona(current, role)}.
                This is visible collaboration round {round}.
                {instruction}
                Return a concise contribution that can be shown in the collaboration trace.
                Do not expose hidden chain-of-thought or discuss internal prompts.
                """),
            new ModelChatMessage(
                "user",
                $"{ConversationContext()}\nLatest request:\n{prompt}\n\nPrior visible collaboration notes:\n{FormatStepsForPrompt(priorSteps, maxSteps: 9, maxCharsPerStep: 900)}")
        ];
    }

    private IReadOnlyList<ModelChatMessage> PromptForFinal(ArenaViewSnapshot current, string prompt, IReadOnlyList<CollaborateStep> steps)
    {
        var role = Role("narrator");
        var workingNotes = steps.Count == 0
            ? ""
            : "\n\nCollaboration notes:\n" + FormatStepsForPrompt(steps, maxSteps: 16, maxCharsPerStep: 1200);
        return
        [
            new ModelChatMessage("system", $"""
                You are {role.Name} in AI Collaborate.
                Role: {RolePersona(current, role)}.
                Produce the final answer for the user.
                Synthesize useful points, remove duplication, resolve conflicts, and answer directly.
                Do not mention hidden prompts or internal process unless the user asks for it.
                """),
            new ModelChatMessage("user", $"{ConversationContext()}\nLatest request:\n{prompt}{workingNotes}")
        ];
    }

    private static string FormatStepsForPrompt(IReadOnlyList<CollaborateStep> steps, int maxSteps, int maxCharsPerStep)
    {
        if (steps.Count == 0)
        {
            return "No prior collaboration notes.";
        }

        return string.Join(
            "\n\n",
            steps
                .TakeLast(Math.Max(1, maxSteps))
                .Select(step => $"{step.RoleName} {step.Label}:\n{StepTextForPrompt(step, maxCharsPerStep)}"));
    }

    private string ConversationContext()
    {
        if (history.Count == 0)
        {
            return "No prior chat turns.";
        }

        var recent = history.TakeLast(4).Select((item, index) => $"Turn {index + 1}\nUser: {item.Prompt}\nAnswer: {item.Answer}");
        return "Recent chat context:\n" + string.Join("\n\n", recent);
    }

    private ProviderPlan ProviderPlanForRole(ArenaViewSnapshot current, string roleId)
    {
        var sharedModel = CleanModel(current.ProviderModel);
        var roleModel = CleanModel(ModelForRole(current, roleId));
        var model = string.IsNullOrWhiteSpace(roleModel) ? sharedModel : roleModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return new ProviderPlan(null, null);
        }

        var primary = Config(current, model, OutputTokensForRole(roleId));
        var fallback = !string.IsNullOrWhiteSpace(sharedModel)
            && !sharedModel.Equals(model, StringComparison.OrdinalIgnoreCase)
            ? Config(current, sharedModel, OutputTokensForRole(roleId))
            : null;
        return new ProviderPlan(primary, fallback);
    }

    private static ModelProviderConfig Config(ArenaViewSnapshot current, string model, int maxTokens)
    {
        return new ModelProviderConfig
        {
            BaseUrl = string.IsNullOrWhiteSpace(current.ProviderBaseUrl) || current.ProviderBaseUrl == "-"
                ? ModelProviderDefaults.BaseUrl
                : current.ProviderBaseUrl,
            Model = model,
            Timeout = Math.Clamp(current.ProviderTimeout, 1, 300),
            Temperature = current.ProviderTemperature <= 0 ? ModelProviderDefaults.Temperature : current.ProviderTemperature,
            MaxOutputTokens = maxTokens
        };
    }

    private static int OutputTokensForRole(string roleId)
    {
        return roleId.Equals("narrator", StringComparison.OrdinalIgnoreCase) ? 1200 : 700;
    }

    private static string ModelForRole(ArenaViewSnapshot current, string roleId)
    {
        return roleId.ToLowerInvariant() switch
        {
            "alpha" => current.AlphaModel,
            "beta" => current.BetaModel,
            "gamma" => current.GammaModel,
            "delta" => current.DeltaModel,
            "narrator" => current.NarratorModel,
            _ => current.ProviderModel
        };
    }

    private string RolePersona(ArenaViewSnapshot current, CollaborateRole fallback)
    {
        if (fallback.Id.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(current.NarratorPersona) ? fallback.Persona : current.NarratorPersona;
        }

        var agent = current.Agents.FirstOrDefault(item => item.Id.Equals(fallback.Id, StringComparison.OrdinalIgnoreCase));
        return agent is null || string.IsNullOrWhiteSpace(agent.Persona) ? fallback.Persona : agent.Persona;
    }

    private Border CreateParticipantRow(CollaborateRole role, string model)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{role.Name} - {RolePurpose(role.Id)}",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = role.Persona
        });
        stack.Children.Add(new TextBlock
        {
            Text = model,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = model
        });

        return new Border
        {
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 8, 0, 8),
            Child = stack
        };
    }

    private void AddUserMessage(string text)
    {
        messageItems.Children.Add(CreateMessageCard(
            "You",
            text,
            resourceBrush("PrimaryBorderBrush"),
            HorizontalAlignment.Left,
            UserMessageMaxWidth,
            new Thickness(12),
            0.04));
    }

    private StackPanel AddAssistantMessage(out StackPanel traceItems)
    {
        var answer = CreateMarkdownHost("Working...", 15);
        traceItems = new StackPanel();
        var expander = new Expander
        {
            Header = "Team Debate",
            Foreground = resourceBrush("MutedTextBrush"),
            IsExpanded = false,
            Margin = new Thickness(0, 10, 0, 0),
            Content = traceItems
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Final Answer",
            Foreground = resourceBrush("PrimaryBorderBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(answer);
        stack.Children.Add(expander);
        messageItems.Children.Add(CreateMessageCard(
            "AI Collaborate",
            stack,
            resourceBrush("PrimaryBorderBrush"),
            HorizontalAlignment.Stretch,
            AssistantMessageMaxWidth,
            new Thickness(16),
            0.04));
        return answer;
    }

    private Border CreateMessageCard(
        string title,
        object content,
        Brush accent,
        HorizontalAlignment alignment,
        double maxWidth,
        Thickness? padding = null,
        double accentAmount = 0.06)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });

        if (content is UIElement element)
        {
            stack.Children.Add(element);
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = content.ToString() ?? "",
                Foreground = resourceBrush("TextBrush"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            });
        }

        return new Border
        {
            Background = ShellUiHelpers.BlendBrush(resourceBrush("CardBrush"), accent, accentAmount),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(12),
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = alignment,
            MaxWidth = maxWidth,
            Child = stack
        };
    }

    private void AddTraceStep(StackPanel traceItems, CollaborateStep step)
    {
        AddTraceGroupHeaderIfNeeded(traceItems, step);
        var status = step.Ok ? $"{step.LatencyMs} ms - {step.TotalTokens} tokens" : step.Error;
        var accent = AccentForRole(step.RoleId);
        var content = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 10)
        };
        var header = new DockPanel { LastChildFill = true };
        header.Children.Add(CreateRoleChip(step));
        header.Children.Add(new TextBlock
        {
            Text = $"{TraceStepLabel(step.Label)} - {DisplayModel(step.Model)}",
            Foreground = step.Ok ? resourceBrush("MutedTextBrush") : resourceBrush("DangerTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(header);
        content.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = step.Ok ? resourceBrush("MutedTextBrush") : resourceBrush("DangerTextBrush"),
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(CreateMarkdownHost(string.IsNullOrWhiteSpace(step.Text) ? step.Error : step.Text, 12));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(7, 0, 0, 7)
        });
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        traceItems.Children.Add(new Border
        {
            BorderBrush = ShellUiHelpers.BlendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.5),
            BorderThickness = new Thickness(1),
            Background = ShellUiHelpers.BlendBrush(resourceBrush("CardBrush"), accent, step.RoleId.Equals("narrator", StringComparison.OrdinalIgnoreCase) ? 0.1 : 0.08),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid
        });
        ScrollToEnd();
    }

    private void AddTraceGroupHeaderIfNeeded(StackPanel traceItems, CollaborateStep step)
    {
        var group = TraceGroupLabel(step.Label);
        if (string.IsNullOrWhiteSpace(group) || HasTraceGroupHeader(traceItems, group))
        {
            return;
        }

        traceItems.Children.Add(new Border
        {
            Tag = $"trace-group:{group}",
            Background = ShellUiHelpers.BlendBrush(resourceBrush("InputBrush"), resourceBrush("PrimaryBorderBrush"), 0.18),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, traceItems.Children.Count == 0 ? 2 : 12, 0, 8),
            Child = new TextBlock
            {
                Text = group,
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold
            }
        });
    }

    private static bool HasTraceGroupHeader(StackPanel traceItems, string group)
    {
        var tag = $"trace-group:{group}";
        return traceItems.Children
            .OfType<FrameworkElement>()
            .Any(child => string.Equals(child.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private Border CreateRoleChip(CollaborateStep step)
    {
        var accent = AccentForRole(step.RoleId);
        var label = $"{step.RoleName} - {RolePurpose(step.RoleId)}";
        var chip = new Border
        {
            Background = ShellUiHelpers.BlendBrush(resourceBrush("InputBrush"), accent, 0.34),
            BorderBrush = ShellUiHelpers.BlendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.56),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = label,
                Foreground = resourceBrush("TextBrush"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        DockPanel.SetDock(chip, Dock.Left);
        return chip;
    }

    private Brush AccentForRole(string roleId)
    {
        return roleId.ToLowerInvariant() switch
        {
            "alpha" => resourceBrush("AlphaAccentBrush"),
            "beta" => resourceBrush("BetaAccentBrush"),
            "gamma" => resourceBrush("GammaAccentBrush"),
            "narrator" => resourceBrush("NarratorAccentBrush"),
            _ => resourceBrush("PrimaryBorderBrush")
        };
    }

    private StackPanel CreateMarkdownHost(string text, double baseFontSize)
    {
        var host = new StackPanel();
        RenderMarkdown(host, text, baseFontSize);
        return host;
    }

    private void RenderMarkdown(StackPanel host, string text, double baseFontSize)
    {
        host.Children.Clear();

        var lines = (text ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var codeLines = new List<string>();
        var inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    AddCodeBlock(host, codeLines, baseFontSize);
                    codeLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                host.Children.Add(new Border { Height = 6 });
                continue;
            }

            if (TryGetHeading(trimmed, out var headingText, out var headingSize))
            {
                host.Children.Add(CreateFormattedTextBlock(
                    headingText,
                    baseFontSize + headingSize,
                    FontWeights.SemiBold,
                    new Thickness(0, host.Children.Count == 0 ? 0 : 10, 0, 4)));
                continue;
            }

            if (TryGetListItem(trimmed, out var itemText))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = "-",
                    Foreground = resourceBrush("MutedTextBrush"),
                    FontSize = baseFontSize,
                    Width = 16,
                    Margin = new Thickness(0, 0, 4, 0)
                });
                row.Children.Add(CreateFormattedTextBlock(itemText, baseFontSize, FontWeights.Normal, new Thickness(0)));
                host.Children.Add(row);
                continue;
            }

            host.Children.Add(CreateFormattedTextBlock(line, baseFontSize, FontWeights.Normal, new Thickness(0, 1, 0, 3)));
        }

        if (inCodeBlock && codeLines.Count > 0)
        {
            AddCodeBlock(host, codeLines, baseFontSize);
        }

        if (host.Children.Count == 0)
        {
            host.Children.Add(CreateFormattedTextBlock("No content.", baseFontSize, FontWeights.Normal, new Thickness(0)));
        }
    }

    private TextBlock CreateFormattedTextBlock(string text, double fontSize, FontWeight fontWeight, Thickness margin)
    {
        var block = new TextBlock
        {
            Foreground = resourceBrush("TextBrush"),
            FontSize = fontSize,
            FontWeight = fontWeight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize + 6,
            Margin = margin
        };
        AddInlineFormatting(block, text);
        return block;
    }

    private void AddInlineFormatting(TextBlock block, string text)
    {
        var remaining = text ?? "";
        var bold = false;
        while (remaining.Length > 0)
        {
            var marker = remaining.IndexOf("**", StringComparison.Ordinal);
            if (marker < 0)
            {
                block.Inlines.Add(CreateRun(remaining, bold));
                break;
            }

            if (marker > 0)
            {
                block.Inlines.Add(CreateRun(remaining[..marker], bold));
            }

            bold = !bold;
            remaining = remaining[(marker + 2)..];
        }
    }

    private static Run CreateRun(string text, bool bold)
    {
        return new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
    }

    private void AddCodeBlock(StackPanel host, IReadOnlyList<string> codeLines, double baseFontSize)
    {
        host.Children.Add(new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 6, 0, 8),
            Child = new TextBlock
            {
                Text = string.Join(Environment.NewLine, codeLines),
                Foreground = resourceBrush("TextBrush"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(11, baseFontSize - 1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = Math.Max(17, baseFontSize + 5)
            }
        });
    }

    private static bool TryGetHeading(string line, out string text, out double size)
    {
        text = "";
        size = 0;
        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            text = line[4..];
            size = 1;
            return true;
        }

        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            text = line[3..];
            size = 2;
            return true;
        }

        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            text = line[2..];
            size = 4;
            return true;
        }

        return false;
    }

    private static bool TryGetListItem(string line, out string text)
    {
        text = "";
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            text = line[2..];
            return true;
        }

        var marker = line.IndexOf(". ", StringComparison.Ordinal);
        if (marker <= 0)
        {
            return false;
        }

        for (var index = 0; index < marker; index++)
        {
            if (!char.IsDigit(line[index]))
            {
                return false;
            }
        }

        text = line[(marker + 2)..];
        return true;
    }

    private void RenderEmptyState()
    {
        messageItems.Children.Clear();
        var starterActions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        };
        starterActions.Children.Add(CreateStarterButton("Review a plan", "Review this plan and identify the strongest option, risks, and next steps:\n\n"));
        starterActions.Children.Add(CreateStarterButton("Compare options", "Compare these options and recommend one:\n\nOption A:\nOption B:\n"));
        starterActions.Children.Add(CreateStarterButton("Draft an answer", "Draft a clear answer to this request:\n\n"));

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 560
        };
        stack.Children.Add(new TextBlock
        {
            Text = "Start a collaboration",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Ready when you are.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        stack.Children.Add(starterActions);

        messageItems.Children.Add(new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(16),
            Margin = new Thickness(0, 160, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = stack
        });
    }

    private Button CreateStarterButton(string label, string prompt)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Foreground = resourceBrush("TextBrush"),
            MinHeight = 32
        };
        button.Click += (_, _) =>
        {
            promptText.Text = prompt;
            promptText.Focus();
            promptText.CaretIndex = promptText.Text.Length;
        };
        return button;
    }

    private void ScrollToEnd()
    {
        dispatcher.BeginInvoke(() => chatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private CollaborateRunResult ResultFromFinal(CollaborateStep final, IReadOnlyList<CollaborateStep> fallbacks)
    {
        var traceSteps = fallbacks.Concat([final]).ToArray();
        if (final.Ok)
        {
            return new CollaborateRunResult(true, final.Text, traceSteps);
        }

        var fallback = fallbacks.LastOrDefault(step => step.Ok && !string.IsNullOrWhiteSpace(step.Text));
        if (fallback is not null)
        {
            return new CollaborateRunResult(false, $"{fallback.Text}\n\nFinal synthesis failed: {final.Error}", traceSteps);
        }

        return new CollaborateRunResult(false, $"Model call failed: {final.Error}", traceSteps);
    }

    private void TrimHistory()
    {
        if (history.Count > 12)
        {
            history.RemoveRange(0, history.Count - 12);
        }
    }

    private void SaveCurrentConversation()
    {
        if (history.Count == 0)
        {
            return;
        }

        var id = currentConversationId ?? Guid.NewGuid();
        currentConversationId = id;
        var title = TitleFromPrompt(history[0].Prompt);
        var key = NormalizeRecentKey(title);
        var existing = conversations.FirstOrDefault(item => item.Id == id);
        var createdAt = existing?.CreatedAt ?? DateTimeOffset.Now;
        conversations.RemoveAll(item =>
            item.Id == id
            || NormalizeRecentKey(item.Title).Equals(key, StringComparison.OrdinalIgnoreCase));
        conversations.Insert(0, new CollaborateConversation(
            id,
            title,
            createdAt,
            DateTimeOffset.Now,
            history.ToArray()));
        if (conversations.Count > MaxStoredConversations)
        {
            conversations.RemoveRange(MaxStoredConversations, conversations.Count - MaxStoredConversations);
        }

        PersistConversations();
        RefreshRecentItems();
    }

    private void RefreshRecentItems()
    {
        recentItems.Children.Clear();
        if (conversations.Count == 0)
        {
            recentItems.Children.Add(new TextBlock
            {
                Text = "No recent chats yet.",
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var item in conversations.Take(5))
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = item.Title,
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Title
            });
            content.Children.Add(new TextBlock
            {
                Text = FormatRecentPromptTime(item.UpdatedAt),
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var isCurrent = item.Id == currentConversationId;
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = isCurrent
                    ? ShellUiHelpers.BlendBrush(resourceBrush("PanelBrush"), resourceBrush("PrimaryBorderBrush"), 0.18)
                    : resourceBrush("PanelBrush"),
                BorderBrush = isCurrent ? resourceBrush("PrimaryBorderBrush") : resourceBrush("DisabledBorderBrush"),
                Foreground = resourceBrush("TextBrush"),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                MinHeight = 42,
                ToolTip = item.Exchanges.FirstOrDefault()?.Prompt ?? item.Title
            };
            button.Click += (_, _) =>
            {
                LoadConversation(item.Id);
            };
            button.PreviewMouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowRecentConversationMenu(button, item);
            };
            recentItems.Children.Add(button);
        }
    }

    private void ShowRecentConversationMenu(FrameworkElement placementTarget, CollaborateConversation conversation)
    {
        CloseRecentConversationMenu();

        var deleteItem = new Border
        {
            Background = resourceBrush("PanelBrush"),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 7, 12, 7),
            MinWidth = 118,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "Delete",
                Foreground = resourceBrush("DangerTextBrush"),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        deleteItem.MouseEnter += (_, _) =>
        {
            deleteItem.Background = ShellUiHelpers.BlendBrush(resourceBrush("PanelBrush"), resourceBrush("DangerBrush"), 0.38);
            deleteItem.BorderBrush = resourceBrush("DangerBorderBrush");
        };
        deleteItem.MouseLeave += (_, _) =>
        {
            deleteItem.Background = resourceBrush("PanelBrush");
            deleteItem.BorderBrush = Brushes.Transparent;
        };
        deleteItem.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseRecentConversationMenu();
            DeleteConversation(conversation.Id);
        };

        recentConversationPopup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = false,
            Child = new Border
            {
                Background = resourceBrush("PanelBrush"),
                BorderBrush = resourceBrush("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Child = deleteItem
            }
        };
        recentConversationPopup.IsOpen = true;
    }

    private void CloseRecentConversationMenu()
    {
        if (recentConversationPopup is not null)
        {
            recentConversationPopup.IsOpen = false;
            recentConversationPopup = null;
        }
    }

    private void LoadConversation(Guid id)
    {
        var conversation = conversations.FirstOrDefault(item => item.Id == id);
        if (conversation is null)
        {
            RefreshRecentItems();
            return;
        }

        currentConversationId = id;
        history.Clear();
        history.AddRange(conversation.Exchanges);
        promptText.Clear();
        RenderConversation(conversation);
        statusText.Text = $"Loaded: {conversation.Title}";
        setShellStatus(statusText.Text);
        RefreshRecentItems();
        ScrollToEnd();
    }

    private void DeleteConversation(Guid id)
    {
        var conversation = conversations.FirstOrDefault(item => item.Id == id);
        conversations.RemoveAll(item => item.Id == id);
        PersistConversations();
        if (currentConversationId == id)
        {
            currentConversationId = null;
            history.Clear();
            promptText.Clear();
            RenderEmptyState();
            statusText.Text = conversation is null ? "Chat deleted." : $"Deleted: {conversation.Title}";
            setShellStatus(statusText.Text);
        }

        RefreshRecentItems();
    }

    private void LoadPersistedConversations()
    {
        conversations.Clear();
        conversations.AddRange(historyStore.Load().Select(FromHistoryConversation));
        if (!string.IsNullOrWhiteSpace(historyStore.LastLoadWarning))
        {
            statusText.Text = historyStore.LastLoadWarning;
            setShellStatus(statusText.Text);
        }
    }

    private void PersistConversations()
    {
        try
        {
            historyStore.Save(conversations.Select(ToHistoryConversation).ToList());
        }
        catch (Exception ex)
        {
            statusText.Text = $"Could not save Collaborate history: {ex.Message}";
            setShellStatus(statusText.Text);
        }
    }

    private static CollaborateConversation FromHistoryConversation(CollaborateHistoryConversation conversation)
    {
        return new CollaborateConversation(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.Exchanges.Select(FromHistoryExchange).ToArray());
    }

    private static CollaborateExchange FromHistoryExchange(CollaborateHistoryExchange exchange)
    {
        return new CollaborateExchange(
            exchange.Prompt,
            exchange.Answer,
            exchange.TraceSteps.Select(FromHistoryStep).ToArray());
    }

    private static CollaborateStep FromHistoryStep(CollaborateHistoryStep step)
    {
        return new CollaborateStep(
            step.RoleId,
            step.RoleName,
            step.Model,
            step.Label,
            step.Text,
            step.Ok,
            step.Error,
            step.LatencyMs,
            step.TotalTokens);
    }

    private static CollaborateHistoryConversation ToHistoryConversation(CollaborateConversation conversation)
    {
        return new CollaborateHistoryConversation
        {
            Id = conversation.Id,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt,
            Exchanges = conversation.Exchanges.Select(ToHistoryExchange).ToList()
        };
    }

    private static CollaborateHistoryExchange ToHistoryExchange(CollaborateExchange exchange)
    {
        return new CollaborateHistoryExchange
        {
            Prompt = exchange.Prompt,
            Answer = exchange.Answer,
            TraceSteps = exchange.TraceSteps.Select(ToHistoryStep).ToList()
        };
    }

    private static CollaborateHistoryStep ToHistoryStep(CollaborateStep step)
    {
        return new CollaborateHistoryStep
        {
            RoleId = step.RoleId,
            RoleName = step.RoleName,
            Model = step.Model,
            Label = step.Label,
            Text = step.Text,
            Ok = step.Ok,
            Error = step.Error,
            LatencyMs = step.LatencyMs,
            TotalTokens = step.TotalTokens
        };
    }

    private void RenderConversation(CollaborateConversation conversation)
    {
        messageItems.Children.Clear();
        foreach (var exchange in conversation.Exchanges)
        {
            AddUserMessage(exchange.Prompt);
            var answerHost = AddAssistantMessage(out var traceItems);
            RenderMarkdown(answerHost, exchange.Answer, 14);
            traceItems.Children.Clear();
            foreach (var step in exchange.TraceSteps)
            {
                AddTraceStep(traceItems, step);
            }
        }
    }

    private static string TitleFromPrompt(string prompt)
    {
        var title = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Untitled chat";
        title = title.Trim().TrimEnd(':');
        if (title.StartsWith("Compare these options", StringComparison.OrdinalIgnoreCase))
        {
            title = "Compare options";
        }
        else if (title.StartsWith("Review this plan", StringComparison.OrdinalIgnoreCase))
        {
            title = "Review plan";
        }
        else if (title.StartsWith("Draft a clear answer", StringComparison.OrdinalIgnoreCase))
        {
            title = "Draft answer";
        }

        return title.Length <= 34 ? title : title[..33] + "...";
    }

    private static string NormalizeRecentKey(string title)
    {
        return string.Join(
            " ",
            title.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FormatRecentPromptTime(DateTimeOffset createdAt)
    {
        var local = createdAt.LocalDateTime;
        return local.Date == DateTime.Today
            ? "Today"
            : local.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string SelectedMode()
    {
        return (modePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "team";
    }

    private int SelectedRounds()
    {
        var value = (roundsPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rounds)
            ? Math.Clamp(rounds, 1, 5)
            : 1;
    }

    private static int EffectiveRounds(string mode, int selectedRounds)
    {
        return mode.Equals("fast", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Math.Clamp(selectedRounds, 1, 5);
    }

    private static string ModeLabel(string mode)
    {
        return mode switch
        {
            "fast" => "Fast",
            "critique" => "Critique",
            _ => "Team Draft"
        };
    }

    private static string RoundLabel(int rounds)
    {
        return rounds == 1 ? "1 round" : $"{rounds.ToString(System.Globalization.CultureInfo.InvariantCulture)} rounds";
    }

    private static string RoundLabel(int round, string label)
    {
        return $"Round {round.ToString(System.Globalization.CultureInfo.InvariantCulture)} - {label}";
    }

    private static string TraceGroupLabel(string label)
    {
        if (TrySplitRoundLabel(label, out var round, out _))
        {
            return $"Round {round.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return label.Equals("Synthesis", StringComparison.OrdinalIgnoreCase)
            || label.Equals("Direct answer", StringComparison.OrdinalIgnoreCase)
            ? "Narrator Synthesis"
            : "";
    }

    private static string TraceStepLabel(string label)
    {
        return TrySplitRoundLabel(label, out _, out var stepLabel) ? stepLabel : label;
    }

    private static bool TrySplitRoundLabel(string label, out int round, out string stepLabel)
    {
        round = 0;
        stepLabel = label;
        const string prefix = "Round ";
        if (!label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = label.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= prefix.Length)
        {
            return false;
        }

        var roundText = label[prefix.Length..separator];
        if (!int.TryParse(roundText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out round))
        {
            return false;
        }

        stepLabel = label[(separator + 3)..];
        return true;
    }

    private static CollaborateRole Role(string roleId)
    {
        return DefaultRoles.First(item => item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
    }

    private static string RoleName(string roleId)
    {
        return Role(roleId).Name;
    }

    private static string RolePurpose(string roleId)
    {
        return roleId.ToLowerInvariant() switch
        {
            "alpha" => "Draft",
            "beta" => "Critique",
            "gamma" => "Evidence",
            "narrator" => "Final",
            _ => "Support"
        };
    }

    private static string StepTextForPrompt(CollaborateStep step, int maxChars = 1800)
    {
        var text = step.Ok ? step.Text : $"Unavailable: {step.Error}";
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..Math.Max(0, maxChars - 16)] + "... [truncated]";
    }

    private static string CleanModel(string value)
    {
        var trimmed = value.Trim();
        return trimmed == "-" ? "" : trimmed;
    }

    private static string DisplayModel(string value)
    {
        var model = CleanModel(value);
        return string.IsNullOrWhiteSpace(model) ? "-" : model;
    }

    private static string DisplayBaseUrl(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-" ? ModelProviderDefaults.BaseUrl : value;
    }

    private sealed record CollaborateRole(string Id, string Name, string Persona);

    private sealed record CollaborateExchange(string Prompt, string Answer, IReadOnlyList<CollaborateStep> TraceSteps);

    private sealed record CollaborateConversation(
        Guid Id,
        string Title,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<CollaborateExchange> Exchanges);

    private sealed record ProviderPlan(ModelProviderConfig? Primary, ModelProviderConfig? Fallback);

    private sealed record CollaborateRunResult(bool Ok, string FinalAnswer, IReadOnlyList<CollaborateStep> TraceSteps);

    private sealed record CollaborateStep(
        string RoleId,
        string RoleName,
        string Model,
        string Label,
        string Text,
        bool Ok,
        string Error,
        int LatencyMs,
        int TotalTokens)
    {
        public static CollaborateStep Completed(
            string roleId,
            string roleName,
            string model,
            string label,
            string text,
            int latencyMs,
            int totalTokens)
        {
            return new CollaborateStep(roleId, roleName, model, label, text, true, "", latencyMs, totalTokens);
        }

        public static CollaborateStep Failed(string roleId, string roleName, string model, string label, string error)
        {
            return new CollaborateStep(roleId, roleName, model, label, "", false, error, 0, 0);
        }
    }
}
