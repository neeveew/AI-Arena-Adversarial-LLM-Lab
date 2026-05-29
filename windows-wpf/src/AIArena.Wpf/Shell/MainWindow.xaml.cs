using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CoreDialogueMessage = AIArena.Core.Models.DialogueMessage;
using CoreDiscourseTurn = AIArena.Core.Models.DiscourseTurn;
using CoreFrictionDiagnostics = AIArena.Core.Models.FrictionDiagnostics;
using CoreMetricDiagnostic = AIArena.Core.Models.MetricDiagnostic;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;
using CoreModelProviderConfig = AIArena.Core.Models.ModelProviderConfig;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow : Window
{
    private readonly SessionStore _coreSessionStore = new();
    private readonly EventLogStore _eventLogStore = new();
    private readonly ModelProviderHealthService _providerHealth = new();
    private readonly ModelPreloadService _modelPreloadService = new();
    private readonly TranscriptService _transcriptService = new();
    private readonly TurnRunnerService _turnRunner = new();
    private readonly MatchGenerationService _matchGeneration = new();
    private readonly NarratorService _narratorService = new();
    private readonly DiscourseDiagnosticsService _discourseDiagnostics = new();
    private readonly InternetToolService _internetToolService;
    private readonly CuratedNewsService _curatedNewsService = new();
    private readonly WpfSettingsStore _wpfSettingsStore = new();
    private readonly ScenarioTemplateStore _scenarioTemplateStore = new();
    private readonly SystemTelemetryService _systemTelemetryService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _modelRefreshTimer;
    private readonly DispatcherTimer _providerHealthTimer;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly List<Button> _agentTurnButtons = [];
    private readonly List<Button> _narratorActionButtons = [];
    private readonly List<Button> _transcriptActionButtons = [];
    private readonly List<CheckBox> _lockControls = [];
    private readonly Dictionary<string, string> _roleModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _arenaOperationLock = new(1, 1);
    private IReadOnlyList<string> _advertisedModels = [];
    private IReadOnlyList<TranscriptMessage> _lastRenderedMessages = [];
    private IReadOnlyDictionary<string, string> _lastAgentPersonas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool _isRefreshingModels;
    private bool _isCheckingProviderHealth;
    private CoreSessionSummary? _activeSession;
    private DateTimeOffset _activeSnapshotWriteUtc;
    private bool _isSelectingSession;
    private bool _isSelectingTheme;
    private bool _isRenderingSnapshot;
    private bool _isUpdatingRoleModelEditor;
    private bool _isPersistingModelRouting;
    private bool _arenaBusy;
    private bool _telemetrySampleInFlight;
    private string _transcriptDashboardLayout = "";
    private Button? _breathingOperationButton;
    private WpfSettings _wpfSettings = new();
    private ThemePalette _theme = ThemePalette.Resolve("system");
    private CancellationTokenSource? _autoChatCancellation;
    private const int DiagnosticHistoryLimit = 36;
    private const int DiagnosticWindowSize = 8;
    private const int TelemetryHistoryLimit = 36;
    private readonly List<double> _cpuTelemetryHistory = [];
    private readonly List<double> _gpuTelemetryHistory = [];
    private readonly List<double> _vramTelemetryHistory = [];
    private readonly List<double> _ramTelemetryHistory = [];
    private Style? _lockToggleStyle;

    private sealed record DiagnosticHistoryPoint(
        int Friction,
        int Consensus,
        int RoleDrift,
        int UnsupportedClaims,
        int EvidencePressure,
        int NarrativeHeat);

    private sealed record DiagnosticSeriesSet(
        IReadOnlyList<DiagnosticHistoryPoint> Points,
        int UnsupportedClaimsMax);

    public MainWindow()
    {
        InitializeComponent();
        _internetToolService = new InternetToolService(eventLogStore: _eventLogStore);
        _wpfSettings = _wpfSettingsStore.Load();
        ApplyTheme(_wpfSettings.ThemeId, persist: false, rerender: false);
        InitializeThemePicker();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _refreshTimer.Tick += (_, _) => RefreshIfSnapshotChanged();
        _modelRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _modelRefreshTimer.Tick += async (_, _) => await RefreshAdvertisedModelsAsync();
        _providerHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _providerHealthTimer.Tick += async (_, _) => await RefreshProviderReachabilityAsync();
        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += async (_, _) => await UpdateSystemTelemetryAsync();
        InitializeVisualSettings();
        LoadScenarioTemplates();
        Loaded += (_, _) =>
        {
            LoadSessions();
            _refreshTimer.Start();
            _providerHealthTimer.Start();
            UpdateTelemetryTimerState();
            _ = RefreshProviderReachabilityAsync(force: true);
        };
        SourceInitialized += (_, _) => ApplyNativeChromeColor();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _modelRefreshTimer.Stop();
            _providerHealthTimer.Stop();
            _telemetryTimer.Stop();
        };
    }

    private void InitializeThemePicker()
    {
        _isSelectingTheme = true;
        ThemePicker.ItemsSource = ThemePalette.BuiltIn;
        ThemePicker.SelectedValue = ThemePalette.BuiltIn.Any(item => item.Id == _wpfSettings.ThemeId)
            ? _wpfSettings.ThemeId
            : "system";
        _isSelectingTheme = false;
    }

    private void InitializeVisualSettings()
    {
        _isRenderingSnapshot = true;
        SelectComboTag(AvatarStylePicker, CurrentAvatarStyle());
        SelectComboTag(SystemGlyphStylePicker, _wpfSettings.SystemEventGlyphs ? "glyph" : "fallback");
        SelectComboTag(TopStripModePicker, CurrentTopStripMode());
        _isRenderingSnapshot = false;
        UpdateTranscriptDashboardLayout(TranscriptDashboardGrid.ActualWidth, force: true);
        UpdateTelemetryTimerState();
    }

    private async void LoadSessions(string? preferredSessionId = null)
    {
        await LoadSessionsAsync(preferredSessionId);
    }

    private async Task LoadSessionsAsync(string? preferredSessionId = null)
    {
        var sessions = await _coreSessionStore.ListSessionsAsync();
        _isSelectingSession = true;
        SessionPicker.ItemsSource = sessions;

        var defaultSession = sessions.FirstOrDefault(session => session.Id.Equals(preferredSessionId, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals(_activeSession?.Id, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(session => session.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault();
        SessionPicker.SelectedItem = defaultSession;
        _isSelectingSession = false;

        if (defaultSession is null)
        {
            LoadStatus.Text = $"No sessions found in {Path.Combine(_coreSessionStore.DataRoot, "sessions")}";
            PopulateFallbackState("No AI Arena sessions found.");
            return;
        }

        await LoadSessionAsync(defaultSession, force: true);
    }

    private void LoadScenarioTemplates(string? preferredTemplateId = null)
    {
        var templates = _scenarioTemplateStore.Load();
        ScenarioTemplatePicker.ItemsSource = templates;
        ScenarioTemplatePicker.DisplayMemberPath = "Name";
        var selected = templates.FirstOrDefault(template => template.Id.Equals(preferredTemplateId ?? "", StringComparison.OrdinalIgnoreCase))
            ?? templates.FirstOrDefault();
        ScenarioTemplatePicker.SelectedItem = selected;
        ScenarioTemplateStatus.Text = selected is null
            ? "No saved scenario templates yet."
            : $"Selected: {selected.Name} ({selected.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm})";
    }

    private void SessionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingSession || SessionPicker.SelectedItem is not CoreSessionSummary session)
        {
            return;
        }

        _ = LoadSessionAsync(session, force: true);
    }

    private async void RefreshIfSnapshotChanged()
    {
        if (_activeSession is null)
        {
            await LoadSessionsAsync();
            return;
        }

        var sessions = await _coreSessionStore.ListSessionsAsync();
        var latestSession = sessions.FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latestSession is null)
        {
            await LoadSessionsAsync();
            return;
        }

        if (latestSession.LastModified != _activeSnapshotWriteUtc)
        {
            await LoadSessionAsync(latestSession, force: true);
        }
    }

    private async Task LoadSessionAsync(CoreSessionSummary session, bool force)
    {
        if (!force && session.LastModified == _activeSnapshotWriteUtc)
        {
            return;
        }

        try
        {
            var coreSnapshot = await _coreSessionStore.LoadSnapshotAsync(session.Id);
            var snapshot = coreSnapshot is null
                ? SnapshotViewMapper.Empty(session, "No snapshot file.")
                : SnapshotViewMapper.FromCore(session, coreSnapshot);
            _activeSession = session;
            _activeSnapshotWriteUtc = session.LastModified;
            RenderSnapshot(snapshot);
            RefreshCheckpoints();
            LoadStatus.Text = $"Loaded read-only snapshot: {snapshot.SnapshotPath}\nAuto-refresh: 1.2s";
        }
        catch (Exception ex)
        {
            _activeSession = session;
            _activeSnapshotWriteUtc = session.LastModified;
            PopulateFallbackState($"Could not load snapshot: {ex.Message}");
            ClearCheckpoints("No checkpoint data.");
            LoadStatus.Text = $"Could not load session '{session.Id}': {ex.Message}";
        }
    }

    private void RenderSnapshot(ArenaSnapshot snapshot)
    {
        UpdateTopBarStatus(snapshot);
        if (!_arenaBusy)
        {
            ArenaRunStatus.Text = snapshot.ProviderOnline ? "Ready." : "Provider offline. Check LM Studio or the provider base URL.";
        }
        _isRenderingSnapshot = true;
        var activeCount = snapshot.Agents.Count(agent => agent.Active);
        SelectComboTag(ActiveParticipantsPicker, Math.Clamp(activeCount, 1, 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
        ProviderBaseUrlText.Text = snapshot.ProviderBaseUrl;
        ProviderModelText.Text = snapshot.ProviderModel == "-" ? "" : snapshot.ProviderModel;
        _roleModels["alpha"] = snapshot.AlphaModel;
        _roleModels["beta"] = snapshot.BetaModel;
        _roleModels["gamma"] = snapshot.GammaModel;
        _roleModels["delta"] = snapshot.DeltaModel;
        _roleModels["narrator"] = snapshot.NarratorModel;
        UpdateRoleModelEditors();
        UpdateRoleModelSummary();
        ProviderTimeoutText.Text = snapshot.ProviderTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ProviderTemperatureText.Text = snapshot.ProviderTemperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        ProviderMaxOutputText.Text = snapshot.ProviderMaxOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextTranscriptWindowText.Text = snapshot.TranscriptWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextPrivateWindowText.Text = snapshot.PrivateWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextNotesWindowText.Text = snapshot.NotesWindow.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ContextSummaryText.Text = string.IsNullOrWhiteSpace(snapshot.Summary) ? "No summary has been generated for this session." : snapshot.Summary;
        UseInternetCheckBox.IsChecked = snapshot.InternetEnabled;
        SelectComboTag(InternetModePicker, snapshot.InternetMode);
        SelectComboTag(InternetSourceScopePicker, snapshot.InternetSourceScope);
        InternetMaxResultsText.Text = snapshot.InternetMaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AllowParticipantInternetCheckBox.IsChecked = snapshot.AllowParticipantInternetRequests;
        AllowNarratorInternetCheckBox.IsChecked = snapshot.AllowNarratorInternetRequests;
        RequireInternetApprovalCheckBox.IsChecked = snapshot.RequireInternetApproval;
        _isRenderingSnapshot = false;
        UpdateSessionOverview(snapshot);
        _lastAgentPersonas = snapshot.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .ToDictionary(agent => agent.Id, agent => agent.Persona, StringComparer.OrdinalIgnoreCase);
        PopulateTranscript(snapshot.Messages);
        PopulateAgents(snapshot);
        PopulateCustomMatch(snapshot);
        PopulateNews(snapshot.Messages);
    }

    private void UpdateSessionOverview(ArenaSnapshot snapshot)
    {
        SessionOverviewMatchText.Text = DisplayStatusValue(snapshot.MatchType);
        SessionOverviewTurnsText.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SessionOverviewParticipantsText.Text = $"{snapshot.Agents.Count(agent => agent.Active)} agents + operator";
        SessionOverviewTokensText.Text = FormatCompactNumber(snapshot.Messages.Sum(message => Math.Max(message.CompletionTokens, 0)));
        SessionOverviewProviderText.Text = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        SessionOverviewProviderText.Foreground = snapshot.ProviderOnline ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DangerTextBrush");
        var context = snapshot.Messages.Select(message => message.PromptTokens).DefaultIfEmpty(0).Max();
        SessionOverviewContextText.Text = context > 0 ? FormatCompactNumber(context) : "-";
        PopulateAgentPerformance(snapshot);
    }

    private void PopulateAgentPerformance(ArenaSnapshot snapshot)
    {
        AgentPerformanceItems.Children.Clear();
        var agentIds = snapshot.Agents
            .Where(agent => agent.Active || snapshot.Messages.Any(message => message.SpeakerId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(agent => agent.Id)
            .Append("narrator")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var agentId in agentIds)
        {
            var messages = snapshot.Messages
                .Where(message => message.SpeakerId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                    && !message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var internetRequests = snapshot.Messages.Count(message =>
                message.InternetRequester.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                || (message.SpeakerId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(message.InternetTool) || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase))));

            if (messages.Length == 0 && internetRequests == 0)
            {
                continue;
            }

            AgentPerformanceItems.Children.Add(CreateAgentPerformanceRow(agentId, messages, internetRequests));
        }

        if (AgentPerformanceItems.Children.Count == 0)
        {
            AgentPerformanceItems.Children.Add(new TextBlock
            {
                Text = "No agent metrics yet.",
                Foreground = ResourceBrush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private Border CreateAgentPerformanceRow(string agentId, IReadOnlyList<TranscriptMessage> messages, int internetRequests)
    {
        var failures = messages.Count(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase));
        var empty = messages.Count(message => string.IsNullOrWhiteSpace(message.Text) || message.Text.Contains("(empty model response)", StringComparison.OrdinalIgnoreCase));
        var latencies = messages.Where(message => message.LatencyMs > 0).Select(message => message.LatencyMs).ToArray();
        var avgLatency = latencies.Length == 0 ? "-" : FormatDuration((int)latencies.Average());
        var tokens = messages.Sum(message => Math.Max(message.CompletionTokens, 0));
        var accent = AccentForSpeaker(agentId);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 8, 2)
        });

        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        stack.Children.Add(new TextBlock
        {
            Text = DisplayStatusValue(agentId),
            Foreground = ResourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var metrics = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        metrics.Children.Add(CreateTinyMetric($"{messages.Count} calls", accent));
        metrics.Children.Add(CreateTinyMetric(avgLatency, ResourceBrush("AlphaAccentBrush")));
        metrics.Children.Add(CreateTinyMetric($"{FormatCompactNumber(tokens)} tok", ResourceBrush("PrimaryBorderBrush")));
        metrics.Children.Add(CreateTinyMetric($"{failures} fail", failures > 0 ? ResourceBrush("DangerTextBrush") : ResourceBrush("MutedTextBrush")));
        metrics.Children.Add(CreateTinyMetric($"{empty} empty", empty > 0 ? ResourceBrush("BetaAccentBrush") : ResourceBrush("MutedTextBrush")));
        metrics.Children.Add(CreateTinyMetric($"{internetRequests} web", internetRequests > 0 ? ResourceBrush("AssistBorderBrush") : ResourceBrush("MutedTextBrush")));
        stack.Children.Add(metrics);
        grid.Children.Add(stack);

        return new Border
        {
            Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };
    }

    private Border CreateTinyMetric(string text, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(ResourceBrush("CardBrush"), accent, 0.12),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private void PopulateFallbackState(string message)
    {
        TranscriptItems.Children.Clear();
        AgentItems.Children.Clear();
        NewsItems.Children.Clear();
        _agentTurnButtons.Clear();
        _narratorActionButtons.Clear();
        TranscriptItems.Children.Add(CreateCard("Transcript", message, ResourceBrush("CardBrush"), ResourceBrush("AlphaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Alpha", "waiting", ResourceBrush("AlphaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Beta", "waiting", ResourceBrush("BetaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Gamma", "waiting", ResourceBrush("GammaAccentBrush")));
        AgentItems.Children.Add(CreateAgentStatusCard("Delta", "waiting", ResourceBrush("DeltaAccentBrush")));
        NewsItems.Children.Add(CreateCard("News", "No live snapshot data.", ResourceBrush("CardBrush"), ResourceBrush("NarratorAccentBrush")));
    }

    private void UpdateTopBarStatus(ArenaSnapshot snapshot)
    {
        TopMatchValue.Text = DisplayStatusValue(snapshot.MatchType);
        TopProviderValue.Text = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        TopProviderValue.Foreground = snapshot.ProviderOnline ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DangerTextBrush");
        var current = CurrentTurnAgent(snapshot);
        TopCurrentTurnValue.Text = current?.Id.ToUpperInvariant() ?? "-";
        TopCurrentTurnValue.Foreground = current is null ? ResourceBrush("TextBrush") : AccentForSpeaker(current.Id);
        TopTurnsValue.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        TopBarStatus.ToolTip = $"Session: {snapshot.SessionId}\nModel: {snapshot.ProviderModel}";
        UpdateSettingsProviderStatus(snapshot);
    }

    private void UpdateSettingsProviderStatus(ArenaSnapshot snapshot)
    {
        var status = snapshot.ProviderOnline ? "ONLINE" : "OFFLINE";
        var detail = snapshot.ProviderOnline
            ? snapshot.ProviderModel
            : string.IsNullOrWhiteSpace(snapshot.ProviderLastError)
                ? snapshot.ProviderBaseUrl
                : snapshot.ProviderLastError;
        SettingsProviderStatusText.Text = $"Provider {status} - {detail}";
        SettingsProviderStatusText.Foreground = snapshot.ProviderOnline
            ? ResourceBrush("PrimaryBorderBrush")
            : ResourceBrush("DangerTextBrush");
    }

    private static AgentState? CurrentTurnAgent(ArenaSnapshot snapshot)
    {
        var active = snapshot.Agents.Where(agent => agent.Active).ToArray();
        return active.Length == 0
            ? null
            : active[Math.Clamp(snapshot.TurnIndex, 0, int.MaxValue) % active.Length];
    }

    private static string DisplayStatusValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-"
            ? "-"
            : value.Trim().ToUpperInvariant();
    }

    private void PopulateTranscript(IReadOnlyList<TranscriptMessage> messages)
    {
        _lastRenderedMessages = messages;
        TranscriptItems.Children.Clear();
        _transcriptActionButtons.Clear();

        var visibleMessages = FilterTranscriptMessages(messages).ToArray();
        UpdateTranscriptResultCount(visibleMessages.Length, messages.Count);
        UpdateTranscriptSearchState();
        if (IsDiagnosticsDisplayed())
        {
            UpdateFrictionDiagnostics(messages);
        }

        if (messages.Count == 0)
        {
            TranscriptItems.Children.Add(CreateEmptyStateCard(
                "Arena is ready",
                "Start with 1 TURN, AUTO CHAT, an agent turn, or write directly in Operator Turn.",
                ResourceBrush("AlphaAccentBrush")));
            return;
        }

        if (visibleMessages.Length == 0)
        {
            TranscriptItems.Children.Add(CreateEmptyStateCard(
                "No transcript matches",
                "Adjust the search text, turn preset, or speaker filters to widen the view.",
                ResourceBrush("MutedTextBrush")));
            return;
        }

        var retryableTurns = visibleMessages
            .Where(message => IsAgentSpeaker(message.SpeakerId))
            .OrderByDescending(message => message.Turn)
            .Take(3)
            .Select(message => message.Turn)
            .ToHashSet();

        var latestTurn = visibleMessages.Max(message => message.Turn);
        foreach (var message in visibleMessages.OrderByDescending(message => message.Turn))
        {
            TranscriptItems.Children.Add(CreateTranscriptCard(
                message,
                retryableTurns.Contains(message.Turn),
                HasActiveTranscriptSearch(),
                message.Turn == latestTurn));
        }

        if (FollowChatCheckBox.IsChecked == true)
        {
            Dispatcher.BeginInvoke(() => TranscriptScrollViewer.ScrollToTop(), DispatcherPriority.Background);
        }
    }

    private IEnumerable<TranscriptMessage> FilterTranscriptMessages(IEnumerable<TranscriptMessage> messages)
    {
        var search = TranscriptSearchText?.Text?.Trim() ?? "";
        var filtered = messages.Where(TranscriptSourceEnabled);
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(message => TranscriptMatchesSearch(message, search));
        }

        return ApplyTranscriptTurnFilter(filtered);
    }

    private IEnumerable<TranscriptMessage> ApplyTranscriptTurnFilter(IEnumerable<TranscriptMessage> messages)
    {
        var filter = (TranscriptTurnFilterPicker?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        return filter switch
        {
            "latest10" => messages.OrderByDescending(message => message.Turn).Take(10),
            "latest25" => messages.OrderByDescending(message => message.Turn).Take(25),
            "errors" => messages.Where(message => message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)),
            "pinned" => messages.Where(message => message.Pinned),
            _ => messages
        };
    }

    private void UpdateTranscriptResultCount(int visibleCount, int totalCount)
    {
        if (TranscriptResultCountText is null)
        {
            return;
        }

        var search = CurrentTranscriptSearch();
        var filter = (TranscriptTurnFilterPicker?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Turns";
        TranscriptResultCountText.Text = string.IsNullOrWhiteSpace(search)
            ? visibleCount == totalCount
                ? $"{visibleCount} shown"
                : $"{visibleCount} of {totalCount} - {filter}"
            : $"{visibleCount} {(visibleCount == 1 ? "match" : "matches")} \"{TrimSearchForDisplay(search)}\"";
    }

    private void TranscriptDashboardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTranscriptDashboardLayout(e.NewSize.Width);
    }

    private void UpdateTranscriptDashboardLayout(double width, bool force = false)
    {
        var mode = CurrentTopStripMode();
        var showTopStrip = !mode.Equals("hidden", StringComparison.OrdinalIgnoreCase) && width >= 1180;
        var showDiagnostics = showTopStrip && mode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        var showTelemetry = showTopStrip && mode.Equals("telemetry", StringComparison.OrdinalIgnoreCase);
        var layout = showDiagnostics ? "diagnostics" : showTelemetry ? "telemetry" : "hidden";
        if (!force && layout.Equals(_transcriptDashboardLayout, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _transcriptDashboardLayout = layout;
        Grid.SetRow(TranscriptDiagnosticsHost, 0);
        Grid.SetColumn(TranscriptDiagnosticsHost, 0);
        Grid.SetColumnSpan(TranscriptDiagnosticsHost, 1);
        Grid.SetRow(TranscriptTelemetryHost, 0);
        Grid.SetColumn(TranscriptTelemetryHost, 0);
        Grid.SetColumnSpan(TranscriptTelemetryHost, 1);
        Grid.SetRow(TranscriptFiltersHost, 0);
        Grid.SetColumn(TranscriptFiltersHost, 1);
        Grid.SetColumnSpan(TranscriptFiltersHost, 1);

        TranscriptDiagnosticsHost.Visibility = showDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        TranscriptTelemetryHost.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
        TranscriptDiagnosticsHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        TranscriptTelemetryHost.CornerRadius = new CornerRadius(8, 0, 0, 8);
        TranscriptFiltersHost.HorizontalAlignment = showTopStrip
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        TranscriptFiltersHost.CornerRadius = !showTopStrip
            ? new CornerRadius(8)
            : new CornerRadius(0, 8, 8, 0);
        TranscriptFiltersHost.BorderThickness = !showTopStrip
            ? new Thickness(1)
            : new Thickness(0, 1, 1, 1);
        if (showDiagnostics)
        {
            UpdateFrictionDiagnostics(_lastRenderedMessages);
        }

        UpdateTelemetryTimerState();
    }

    private void UpdateTranscriptSearchState()
    {
        if (ClearTranscriptSearchButton is null)
        {
            return;
        }

        var active = HasActiveTranscriptSearch();
        ClearTranscriptSearchButton.Opacity = active ? 1.0 : 0.45;
        ClearTranscriptSearchButton.IsEnabled = active;
        TranscriptSearchText.BorderBrush = active
            ? ResourceBrush("PrimaryBorderBrush")
            : ResourceBrush("ControlBorderBrush");
    }

    private bool HasActiveTranscriptSearch()
    {
        return !string.IsNullOrWhiteSpace(CurrentTranscriptSearch());
    }

    private bool IsDiagnosticsDisplayed()
    {
        return _transcriptDashboardLayout.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
            && TranscriptDiagnosticsHost.Visibility == Visibility.Visible
            && TranscriptDiagnosticsHost.IsVisible;
    }

    private bool IsTelemetryDisplayed()
    {
        return _transcriptDashboardLayout.Equals("telemetry", StringComparison.OrdinalIgnoreCase)
            && TranscriptTelemetryHost.Visibility == Visibility.Visible
            && TranscriptTelemetryHost.IsVisible;
    }

    private void UpdateTelemetryTimerState()
    {
        if (IsTelemetryDisplayed())
        {
            if (!_telemetryTimer.IsEnabled)
            {
                _ = UpdateSystemTelemetryAsync();
                _telemetryTimer.Start();
            }
        }
        else
        {
            _telemetryTimer.Stop();
        }
    }

    private async Task UpdateSystemTelemetryAsync()
    {
        if (!IsTelemetryDisplayed())
        {
            _telemetryTimer.Stop();
            return;
        }

        if (_telemetrySampleInFlight)
        {
            return;
        }

        _telemetrySampleInFlight = true;
        SystemTelemetrySample sample;
        try
        {
            sample = await Task.Run(() => _systemTelemetryService.Sample());
        }
        finally
        {
            _telemetrySampleInFlight = false;
        }

        SetTelemetryTile(
            TelemetryCpuValueText,
            TelemetryCpuSparkline,
            _cpuTelemetryHistory,
            sample.CpuPercent,
            sample.CpuPercent.HasValue ? $"{sample.CpuPercent.Value:0}%" : "—",
            ResourceBrush("AlphaAccentBrush"));
        SetTelemetryTile(
            TelemetryGpuValueText,
            TelemetryGpuSparkline,
            _gpuTelemetryHistory,
            sample.GpuPercent,
            sample.GpuPercent.HasValue ? $"{sample.GpuPercent.Value:0}%" : "—",
            ResourceBrush("DeltaAccentBrush"));
        TelemetryGpuDetailText.Text = !string.IsNullOrWhiteSpace(sample.GpuName)
            ? sample.GpuName
            : sample.GpuPercent.HasValue ? "Local GPU" : "Unavailable";
        SetTelemetryUsageBar(
            TelemetryVramValueText,
            TelemetryVramDetailText,
            TelemetryVramUsageBar,
            sample.VramUsedGb,
            sample.VramTotalGb,
            sample.VramUsedGb.HasValue && sample.VramTotalGb is > 0
                ? (sample.VramUsedGb.Value / sample.VramTotalGb.Value) * 100d
                : null);
        SetTelemetryUsageBar(
            TelemetryRamValueText,
            TelemetryRamDetailText,
            TelemetryRamUsageBar,
            sample.RamUsedGb,
            sample.RamTotalGb,
            sample.RamPercent);
    }

    private static void SetTelemetryTile(
        TextBlock valueText,
        MetricSparklineControl sparkline,
        List<double> history,
        double? graphValue,
        string displayValue,
        Brush accent)
    {
        valueText.Text = displayValue;
        valueText.Foreground = accent;
        sparkline.AccentBrush = accent;
        if (graphValue.HasValue)
        {
            history.Add(graphValue.Value);
            while (history.Count > TelemetryHistoryLimit)
            {
                history.RemoveAt(0);
            }
        }

        sparkline.Values = history.ToArray();
    }

    private static void SetTelemetryUsageBar(
        TextBlock valueText,
        TextBlock detailText,
        FrameworkElement usageBar,
        double? usedGb,
        double? totalGb,
        double? percent)
    {
        if (usedGb.HasValue)
        {
            valueText.Text = $"{usedGb.Value:0.#} GB";
            detailText.Text = totalGb.HasValue ? $"/ {totalGb.Value:0.#} GB" : "";
        }
        else
        {
            valueText.Text = "—";
            detailText.Text = "Unavailable";
        }

        var parentWidth = usageBar.Parent is FrameworkElement parent
            ? parent.ActualWidth
            : 0;
        usageBar.Width = percent.HasValue && parentWidth > 0
            ? parentWidth * Math.Clamp(percent.Value / 100d, 0, 1)
            : 0;
    }

    private string CurrentTranscriptSearch()
    {
        return TranscriptSearchText?.Text?.Trim() ?? "";
    }

    private static string TrimSearchForDisplay(string search)
    {
        return search.Length <= 24 ? search : $"{search[..24]}...";
    }

    private bool TranscriptSourceEnabled(TranscriptMessage message)
    {
        if (message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptFilterOperatorCheckBox?.IsChecked == true;
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptFilterNarratorCheckBox?.IsChecked == true;
        }

        if (IsAgentSpeaker(message.SpeakerId))
        {
            return TranscriptFilterAgentsCheckBox?.IsChecked == true;
        }

        return TranscriptFilterSystemCheckBox?.IsChecked == true;
    }

    private static bool TranscriptMatchesSearch(TranscriptMessage message, string search)
    {
        return ContainsSearch(message.Speaker, search)
            || ContainsSearch(message.SpeakerId, search)
            || ContainsSearch(message.Model, search)
            || ContainsSearch(message.Status, search)
            || ContainsSearch(message.Kind, search)
            || ContainsSearch(message.Text, search)
            || ContainsSearch(message.Reasoning, search)
            || ContainsSearch(message.InternetQuery, search)
            || ContainsSearch(message.InternetUrl, search)
            || message.InternetSources.Any(source => ContainsSearch(source, search));
    }

    private static bool ContainsSearch(string value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateAgents(ArenaSnapshot snapshot)
    {
        var agents = snapshot.Agents;
        var currentAgentId = CurrentTurnAgent(snapshot)?.Id;
        AgentItems.Children.Clear();
        _agentTurnButtons.Clear();
        _narratorActionButtons.Clear();

        if (agents.Count == 0)
        {
            AgentItems.Children.Add(CreateAgentStatusCard("No agents", "No active snapshot", ResourceBrush("ControlBorderBrush")));
            return;
        }

        foreach (var agent in agents)
        {
            AgentItems.Children.Add(CreateAgentCard(agent, currentAgentId));
        }

        AgentItems.Children.Add(CreateNarratorCard(snapshot));
    }

    private void PopulateCustomMatch(ArenaSnapshot snapshot)
    {
        ScenarioPreviewItems.Children.Clear();
        CastPreviewItems.Children.Clear();
        _lockControls.Clear();

        ScenarioPreviewItems.Children.Add(CreateLockCard(
            "topic",
            "Topic",
            string.IsNullOrWhiteSpace(snapshot.ScenarioTopic) ? "No topic is set for this match yet." : snapshot.ScenarioTopic,
            ResourceBrush("CardBrush"),
            snapshot.TopicLocked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            snapshot.TopicLocked));
        ScenarioPreviewItems.Children.Add(CreateLockCard(
            "global",
            "Global",
            string.IsNullOrWhiteSpace(snapshot.ScenarioGlobal) ? "No global instruction is set for this match yet." : snapshot.ScenarioGlobal,
            ResourceBrush("CardBrush"),
            snapshot.GlobalLocked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            snapshot.GlobalLocked));

        if (snapshot.Agents.Count == 0)
        {
            CastPreviewItems.Children.Add(CreateEmptyStateCard("Cast", "No active cast is available for this session yet.", ResourceBrush("MutedTextBrush")));
            return;
        }

        foreach (var agent in snapshot.Agents)
        {
            CastPreviewItems.Children.Add(CreateLockCard(
                agent.Id,
                $"{agent.Id}: {agent.Name}",
                string.IsNullOrWhiteSpace(agent.Persona) ? "(no persona)" : agent.Persona,
                BlendBrush(ResourceBrush("CardBrush"), AccentForSpeaker(agent.Id), 0.16),
                AccentForSpeaker(agent.Id),
                agent.Locked));
        }
    }

    private void PopulateNews(IReadOnlyList<TranscriptMessage> messages)
    {
        NewsItems.Children.Clear();
        var newsMessages = messages
            .Where(message => message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase)
                || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(message.InternetTool)
                || message.InternetSources.Count > 0)
            .OrderByDescending(message => message.Turn)
            .ToArray();

        if (newsMessages.Length == 0)
        {
            NewsSummaryText.Text = "No internet activity in this session.";
            NewsItems.Children.Add(CreateEmptyStateCard(
                "News inspector",
                "No fetched or curated news items in this session yet.",
                ResourceBrush("NarratorAccentBrush")));
            return;
        }

        var sourceCount = newsMessages.Sum(message => message.InternetSources.Count);
        var pendingCount = newsMessages.Count(message => message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
        NewsSummaryText.Text = $"{newsMessages.Length} internet item(s), {sourceCount} source(s)"
            + (pendingCount > 0 ? $", {pendingCount} waiting for approval" : "");

        foreach (var message in newsMessages)
        {
            NewsItems.Children.Add(CreateNewsInspectorCard(message));
        }
    }

    private Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return CreateCard(title, body, background, accent, null);
    }

    private Border CreateEmptyStateCard(string title, string body, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Quiet state",
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        return CreateCard(title, body, BlendBrush(ResourceBrush("CardBrush"), accent, 0.08), accent, panel);
    }

    private Border CreateLockCard(string lockKey, string title, string body, Brush background, Brush accent, bool locked)
    {
        var lockBox = new CheckBox
        {
            Content = locked ? "Locked" : "Unlocked",
            Foreground = locked ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush"),
            IsChecked = locked,
            IsEnabled = !_arenaBusy,
            Tag = lockKey,
            Style = CreateLockToggleStyle(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        lockBox.Checked += MatchLockChanged;
        lockBox.Unchecked += MatchLockChanged;
        _lockControls.Add(lockBox);

        var editButton = new Button
        {
            Content = "EDIT",
            Tag = lockKey,
            MinHeight = 28,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10, 0, 0, 0),
            Background = ResourceBrush("InputBrush"),
            BorderBrush = accent,
            Foreground = ResourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Edit this text and lock it"
        };
        editButton.Click += EditLockCardButton_Click;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(editButton, 1);
        header.Children.Add(editButton);
        Grid.SetColumn(lockBox, 2);
        header.Children.Add(lockBox);

        var content = new Grid();
        content.Margin = new Thickness(0, 10, 0, 0);
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Width = IsAgentSpeaker(lockKey) ? 8 : 5,
            Margin = new Thickness(0, 2, 12, 2)
        });
        var text = new TextBlock
        {
            Text = body,
            Foreground = ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(content);

        return new Border
        {
            Background = BlendBrush(background, accent, locked ? 0.2 : IsAgentSpeaker(lockKey) ? 0.12 : 0.07),
            BorderBrush = locked ? ResourceBrush("BetaAccentBrush") : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.32),
            BorderThickness = locked ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };
    }

    private Style CreateLockToggleStyle()
    {
        if (_lockToggleStyle is not null)
        {
            return _lockToggleStyle;
        }

        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush("MutedTextBrush")));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 132d));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(CheckBox));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        root.AppendChild(GridColumnDefinition(1, GridUnitType.Star));
        root.AppendChild(GridColumnDefinition(52, GridUnitType.Pixel));

        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        text.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        root.AppendChild(text);

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "LockTrack";
        track.SetValue(Grid.ColumnProperty, 1);
        track.SetValue(FrameworkElement.WidthProperty, 44d);
        track.SetValue(FrameworkElement.HeightProperty, 22d);
        track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        track.SetValue(Border.BackgroundProperty, ResourceBrush("InputBrush"));
        track.SetValue(Border.BorderBrushProperty, ResourceBrush("ControlBorderBrush"));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));

        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.Name = "LockThumb";
        thumb.SetValue(FrameworkElement.WidthProperty, 16d);
        thumb.SetValue(FrameworkElement.HeightProperty, 16d);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        thumb.SetValue(Border.BackgroundProperty, ResourceBrush("MutedTextBrush"));
        thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.Name = "LockGlyph";
        glyph.SetValue(TextBlock.TextProperty, "\uE785");
        glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 9d);
        glyph.SetValue(TextBlock.ForegroundProperty, ResourceBrush("DisabledBorderBrush"));
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.AppendChild(glyph);
        track.AppendChild(thumb);
        root.AppendChild(track);

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush("TextBrush")));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("PrimaryBrush"), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("BetaAccentBrush"), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, ResourceBrush("TextBrush"), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.TextProperty, "\uE72E", "LockGlyph"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, ResourceBrush("BetaAccentBrush"), "LockGlyph"));
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, ResourceBrush("HoverBorderBrush"), "LockTrack"));
        template.Triggers.Add(hoverTrigger);

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(disabledTrigger);

        template.VisualTree = root;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        _lockToggleStyle = style;
        return style;
    }

    private static FrameworkElementFactory GridColumnDefinition(double value, GridUnitType unit)
    {
        var definition = new FrameworkElementFactory(typeof(ColumnDefinition));
        definition.SetValue(ColumnDefinition.WidthProperty, new GridLength(value, unit));
        return definition;
    }

    private Border CreateTranscriptCard(TranscriptMessage message, bool retryable, bool searchMatch, bool isLatest)
    {
        var hasInternetDetails = !string.IsNullOrWhiteSpace(message.InternetTool)
            || !string.IsNullOrWhiteSpace(message.InternetQuery)
            || !string.IsNullOrWhiteSpace(message.InternetUrl)
            || message.InternetSources.Count > 0;
        var isInternet = message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase);
        var body = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text;
        var isSystemEvent = IsSystemEvent(message, isInternet);
        var accent = isSystemEvent
            ? ResourceBrush(message.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ? "DangerBorderBrush" : "AssistBorderBrush")
            : (isInternet || hasInternetDetails) ? ResourceBrush("AssistBorderBrush") : AccentForSpeaker(message.SpeakerId);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var canMutate = message.Turn > 0;
        actions.Children.Add(ActionButton("Copy", (_, _) => CopyTranscriptMessage(message), canMutate, iconGlyph: "\uE8C8"));
        actions.Children.Add(ActionButton(message.Pinned ? "Unpin" : "Pin", async (_, _) => await TogglePinTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Primary, "\uE718"));
        actions.Children.Add(ActionButton("Retry", async (_, _) => await RetryTranscriptMessageAsync(message), canMutate && retryable && IsAgentSpeaker(message.SpeakerId) && !isInternet, iconGlyph: "\uE72C"));
        actions.Children.Add(ActionButton("Delete", async (_, _) => await DeleteTranscriptMessageAsync(message), canMutate, TranscriptActionKind.Danger, "\uE74D"));
        if (message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase) && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            actions.Children.Add(ActionButton("Approve Once", async (_, _) => await ApproveInternetRequestAsync(message), canMutate, TranscriptActionKind.Primary));
            actions.Children.Add(ActionButton("Reject", async (_, _) => await RejectInternetRequestAsync(message), canMutate, TranscriptActionKind.Danger));
            actions.Children.Add(ActionButton("Copy URL", (_, _) => CopyInternetUrl(message), canMutate && !string.IsNullOrWhiteSpace(message.InternetUrl)));
        }

        var extras = new StackPanel();
        if (hasInternetDetails)
        {
            extras.Children.Add(CreateTranscriptExpander(
                message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase) ? "Source / selection" : "Internet details",
                accent: ResourceBrush("AssistBorderBrush"),
                content: CreateInternetDetails(message)));
        }
        if (!string.IsNullOrWhiteSpace(message.Reasoning))
        {
            extras.Children.Add(CreateTranscriptExpander(
                "Model reasoning",
                AccentForSpeaker(message.SpeakerId),
                new TextBlock
                {
                    Text = message.Reasoning,
                    Foreground = ResourceBrush("TextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                }));
        }
        extras.Children.Add(actions);
        return CreateTranscriptCardLayout(message, body, accent, isInternet, searchMatch, isLatest, isSystemEvent, extras);
    }

    private IReadOnlyList<Border> CreateTranscriptStatPills(TranscriptMessage message, bool isInternet)
    {
        var pills = new List<Border>
        {
            CreateTranscriptStatPill(FormatDuration(message.LatencyMs), isInternet)
        };

        pills.Add(CreateTranscriptStatPill(FormatGeneratedTokens(message), isInternet));

        if (message.PromptTokens > 0)
        {
            pills.Add(CreateTranscriptStatPill($"ctx: {FormatCompactNumber(message.PromptTokens)}", isInternet));
        }
        else if (message.TotalTokens > 0 && message.CompletionTokens <= 0)
        {
            pills.Add(CreateTranscriptStatPill($"total: {FormatCompactNumber(message.TotalTokens)}", isInternet));
        }

        if (!message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            pills.Add(CreateTranscriptStatPill(message.Status, isInternet, isDanger: message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)));
        }

        if (message.Pinned)
        {
            pills.Add(CreateTranscriptStatPill("pinned", isInternet));
        }

        return pills;
    }

    private Border CreateTranscriptStatPill(string text, bool isInternet, bool isDanger = false)
    {
        return new Border
        {
            Background = isDanger ? ResourceBrush("DangerBrush") : BlendBrush(ResourceBrush("TranscriptBodyBrush"), ResourceBrush(isInternet ? "AssistBorderBrush" : "PrimaryBorderBrush"), 0.1),
            BorderBrush = isDanger ? ResourceBrush("DangerBorderBrush") : BlendBrush(ResourceBrush("ControlBorderBrush"), ResourceBrush(isInternet ? "AssistBorderBrush" : "PrimaryBorderBrush"), 0.38),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = isDanger ? ResourceBrush("DangerTextBrush") : ResourceBrush("MutedTextBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private Expander CreateTranscriptExpander(string header, Brush accent, UIElement content)
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
                Background = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, 0.14),
                BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.42),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = content
            }
        };
    }

    private Border CreateTranscriptCardLayout(TranscriptMessage message, string body, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent, UIElement? extraContent)
    {
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accentWeight = isLatest ? 0.32 : 0.18;
        var normalBackground = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, isError ? 0.16 : accentWeight);
        var hoverBackground = BlendBrush(ResourceBrush("TranscriptBodyBrush"), accent, isError ? 0.22 : accentWeight + 0.06);
        var normalBorder = isError
            ? ResourceBrush("DangerBorderBrush")
            : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, searchMatch || isLatest ? 0.82 : 0.38);
        var hoverBorder = isError ? ResourceBrush("DangerTextBrush") : BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.82);
        var border = new Border
        {
            Style = null,
            Background = normalBackground,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(searchMatch || isLatest || isError ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 12),
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rail = new Grid
        {
            Background = BlendBrush(ResourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.22 : 0.11)
        };
        rail.Children.Add(new Border
        {
            Width = isLatest ? 5 : 3,
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8, 0, 0, 8)
        });
        var railStack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 13, 6, 12)
        };
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        railStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var turnNumber = new TextBlock
        {
            Text = message.Turn.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(turnNumber, 0);
        railStack.Children.Add(turnNumber);

        var avatar = CreateTranscriptAvatar(message, accent, isInternet, isSystemEvent);
        avatar.Margin = new Thickness(0, 7, 0, 5);
        avatar.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(avatar, 1);
        railStack.Children.Add(avatar);

        var railLabel = new TextBlock
        {
            Text = TranscriptRailLabel(message, isInternet),
            Foreground = accent,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(railLabel, 2);
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
            Background = BlendBrush(ResourceBrush("TranscriptHeaderBrush"), accent, isLatest ? 0.28 : isError ? 0.18 : 0.16),
            BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, isLatest ? 0.56 : 0.28),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(0, 8, 0, 0)
        };
        header.Child = CreateTranscriptHeader(message, accent, isInternet, searchMatch, isLatest, isSystemEvent);
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var bodyStack = new StackPanel
        {
            Margin = new Thickness(14, 13, 14, 13)
        };
        var bodyBlock = new TextBlock
        {
            Text = body,
            Foreground = isError ? ResourceBrush("DangerTextBrush") : ResourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            LineHeight = 22
        };
        if (isError)
        {
            bodyStack.Children.Add(new Border
            {
                Background = BlendBrush(ResourceBrush("DangerBrush"), accent, 0.08),
                BorderBrush = ResourceBrush("DangerBorderBrush"),
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

    private AgentAvatarControl CreateTranscriptAvatar(TranscriptMessage message, Brush accent, bool isInternet, bool isSystemEvent)
    {
        var avatar = new AgentAvatarControl
        {
            Width = 44,
            Height = 44,
            AgentId = message.SpeakerId,
            DisplayName = message.Speaker,
            Model = message.Model,
            Persona = PersonaForSpeaker(message.SpeakerId),
            AccentBrush = accent,
            BaseBrush = ResourceBrush("TranscriptBodyBrush"),
            IsSystem = isSystemEvent || isInternet,
            AvatarStyle = CurrentAvatarStyle(),
            UseChampionPortrait = _wpfSettings.ChampionAvatars,
            UseSystemGlyph = _wpfSettings.SystemEventGlyphs,
            FallbackText = SpeakerGlyph(message, isInternet),
            ToolTip = AvatarToolTip(message, isSystemEvent)
        };
        ToolTipService.SetShowDuration(avatar, 60000);
        return avatar;
    }

    private Grid CreateTranscriptHeader(TranscriptMessage message, Brush accent, bool isInternet, bool searchMatch, bool isLatest, bool isSystemEvent)
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
            identity.Children.Add(CreateTranscriptStatPill("Latest", isInternet));
        }
        if (!isInternet && !string.IsNullOrWhiteSpace(message.Model))
        {
            identity.Children.Add(CreateTranscriptStatPill(message.Model, isInternet));
        }
        foreach (var pill in CreateTranscriptStatPills(message, isInternet))
        {
            identity.Children.Add(pill);
        }
        if (searchMatch)
        {
            identity.Children.Add(CreateTranscriptStatPill("search match", isInternet));
        }
        Grid.SetColumn(identity, 0);
        header.Children.Add(identity);

        var time = new TextBlock
        {
            Text = DisplayTime(message.CreatedAt),
            Foreground = ResourceBrush("MutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(time, 1);
        header.Children.Add(time);

        return header;
    }

    private static string TranscriptSpeakerTitle(TranscriptMessage message, bool isInternet, bool isSystemEvent)
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

    private string PersonaForSpeaker(string speakerId)
    {
        return _lastAgentPersonas.TryGetValue(speakerId, out var persona) ? persona : "";
    }

    private static string AvatarToolTip(TranscriptMessage message, bool isSystemEvent)
    {
        var speaker = string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker;
        var model = string.IsNullOrWhiteSpace(message.Model) ? "-" : message.Model;
        var kind = isSystemEvent ? "System event" : "Deterministic procedural avatar";
        return $"{speaker}{Environment.NewLine}Model: {model}{Environment.NewLine}{kind}";
    }

    private static bool IsSystemEvent(TranscriptMessage message, bool isInternet)
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

    private static string DisplayTime(double createdAt)
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

    private Border CreateNewsInspectorCard(TranscriptMessage message)
    {
        var isPending = message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
        var isError = message.Status.Equals("error", StringComparison.OrdinalIgnoreCase);
        var accent = isError
            ? ResourceBrush("DangerBorderBrush")
            : isPending
                ? ResourceBrush("NarratorAccentBrush")
                : ResourceBrush("AssistBorderBrush");
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
        var extras = CreateTranscriptExpander("Internet details", accent, CreateInternetDetails(message));
        return CreateCard(title, string.IsNullOrWhiteSpace(body) ? "(no internet details)" : body, ResourceBrush("CardBrush"), accent, extras);
    }

    private static string InternetInspectorTitle(TranscriptMessage message)
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

    private UIElement CreateInternetDetails(TranscriptMessage message)
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
                Background = ResourceBrush("InputBrush"),
                BorderBrush = ResourceBrush("ControlBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = $"{label}: {value}",
                    Foreground = ResourceBrush("TextBrush"),
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
                Foreground = ResourceBrush("MutedTextBrush"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4)
            });
            foreach (var source in message.InternetSources)
            {
                panel.Children.Add(new Border
                {
                    Background = BlendBrush(ResourceBrush("TranscriptBodyBrush"), ResourceBrush("AssistBorderBrush"), 0.12),
                    BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), ResourceBrush("AssistBorderBrush"), 0.45),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = new TextBlock
                    {
                        Text = source,
                        Foreground = ResourceBrush("TextBrush"),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        return panel;
    }

    private Button ActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        var iconMode = !string.IsNullOrWhiteSpace(iconGlyph);
        var background = ResourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => ResourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("ControlBorderBrush")
        };
        var foreground = kind switch
        {
            TranscriptActionKind.Primary => ResourceBrush("TextBrush"),
            TranscriptActionKind.Danger => ResourceBrush("DangerTextBrush"),
            _ => ResourceBrush("TextBrush")
        };
        var hoverBackground = kind switch
        {
            TranscriptActionKind.Primary => BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("PrimaryBorderBrush"), 0.22),
            TranscriptActionKind.Danger => BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("DangerBorderBrush"), 0.2),
            _ => BlendBrush(ResourceBrush("InputBrush"), ResourceBrush("ControlBorderBrush"), 0.18)
        };
        var button = new Button
        {
            Content = iconMode
                ? new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : text,
            IsEnabled = enabled && !_arenaBusy,
            Tag = enabled,
            Background = iconMode ? Brushes.Transparent : background,
            BorderBrush = iconMode ? Brushes.Transparent : border,
            Foreground = foreground,
            FontSize = iconMode ? 15 : 13,
            FontWeight = FontWeights.SemiBold,
            MinWidth = iconMode ? 30 : 0,
            MinHeight = iconMode ? 30 : 32,
            Width = iconMode ? 30 : double.NaN,
            Height = iconMode ? 30 : double.NaN,
            Padding = iconMode ? new Thickness(0) : new Thickness(10, 5, 10, 5),
            Margin = iconMode ? new Thickness(0, 0, 5, 4) : new Thickness(0, 0, 8, 8),
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        button.MouseEnter += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Background = hoverBackground;
                button.BorderBrush = border;
            }
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = iconMode ? Brushes.Transparent : background;
            button.BorderBrush = iconMode ? Brushes.Transparent : border;
        };
        if (handler is not null)
        {
            button.Click += handler;
        }
        _transcriptActionButtons.Add(button);
        return button;
    }

    private enum TranscriptActionKind
    {
        Neutral,
        Primary,
        Danger
    }

    private Border CreateAgentStatusCard(string title, string status, Brush accent)
    {
        var card = new Border
        {
            Background = ResourceBrush("CardBrush"),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = ResourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title
        });
        text.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = status
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        return card;
    }

    private Border CreateAgentCard(AgentState agent, string? currentAgentId)
    {
        var isActive = agent.Active;
        var isCurrent = isActive && string.Equals(agent.Id, currentAgentId, StringComparison.OrdinalIgnoreCase);
        var isWorkingStatus = IsAgentWorkingStatus(agent.Status);
        var isRunning = isActive && (isWorkingStatus || (isCurrent && _arenaBusy));
        var showActivitySweep = isRunning;
        var speakerLabel = DisplayStatusValue(agent.Id);
        var activityLabel = isRunning ? "thinking" : isCurrent ? "current" : isActive ? "waiting" : "inactive";
        var playButton = new Button
        {
            Content = "▶",
            IsEnabled = isActive && !_arenaBusy && !string.IsNullOrWhiteSpace(agent.Id),
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = isActive ? ResourceBrush("PrimaryBrush") : ResourceBrush("DisabledBrush"),
            BorderBrush = isActive ? ResourceBrush("PrimaryBorderBrush") : ResourceBrush("DisabledBorderBrush"),
            Foreground = isActive ? Brushes.White : ResourceBrush("DisabledTextBrush"),
            ToolTip = isActive
                ? $"Run one turn for {agent.Name}"
                : "Inactive: increase Active participants in App Settings to include this agent."
        };
        playButton.Click += async (_, _) => await RunAgentTurnAsync(agent);
        _agentTurnButtons.Add(playButton);

        var accent = isActive ? AccentForSpeaker(agent.Id) : ResourceBrush("DisabledBorderBrush");
        var card = new Border
        {
            Background = isRunning
                ? BlendBrush(ResourceBrush("InputBrush"), accent, 0.18)
                : isCurrent
                    ? BlendBrush(ResourceBrush("InputBrush"), accent, 0.1)
                    : ResourceBrush("InputBrush"),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, isActive ? 0.75 : 0.16),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true
        };

        var cardLayer = new Grid();
        if (showActivitySweep)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(accent, isRunning));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{speakerLabel} - {activityLabel}",
            Foreground = isActive ? Brushes.White : ResourceBrush("DisabledTextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = agent.Name
        });
        var modelText = string.IsNullOrWhiteSpace(agent.Model) ? "model not set" : agent.Model;
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isRunning || isCurrent
                ? accent
                : isActive ? ResourceBrush("MutedTextBrush") : ResourceBrush("DisabledTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{modelText}\n{agent.Name}"
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = isActive ? accent : ResourceBrush("DisabledBorderBrush"),
            Opacity = isRunning ? 1.0 : isCurrent ? 0.85 : isActive ? 0.45 : 0.25,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = activityLabel
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        Grid.SetColumn(playButton, 2);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        if (!isActive)
        {
            card.Opacity = 0.62;
            card.ToolTip = "Inactive: increase Active participants in App Settings to include this agent.";
        }

        return card;
    }

    private Border CreateNarratorCard(ArenaSnapshot snapshot)
    {
        var accent = ResourceBrush("NarratorAccentBrush");
        var isRunning = IsAgentWorkingStatus(snapshot.NarratorStatus);
        var modelText = string.IsNullOrWhiteSpace(snapshot.NarratorModel) ? "model not set" : snapshot.NarratorModel;
        var status = string.IsNullOrWhiteSpace(snapshot.NarratorStatus) ? "idle" : snapshot.NarratorStatus;
        var buttonEnabled = !_arenaBusy || _autoChatCancellation is not null;
        var playButton = new Button
        {
            Content = "▶",
            IsEnabled = buttonEnabled,
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = ResourceBrush("NarratorAccentBrush"),
            BorderBrush = accent,
            Foreground = Brushes.White,
            ToolTip = "Narrate now"
        };
        playButton.Click += NarrateNowButton_Click;
        _narratorActionButtons.Add(playButton);

        var card = new Border
        {
            Background = isRunning
                ? BlendBrush(ResourceBrush("InputBrush"), accent, 0.18)
                : ResourceBrush("InputBrush"),
            BorderBrush = BlendBrush(ResourceBrush("DisabledBorderBrush"), accent, 0.65),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(snapshot.NarratorPersona)
                ? "Narrator"
                : snapshot.NarratorPersona
        };

        var cardLayer = new Grid();
        if (isRunning)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(accent, isRunning: true));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"Narrator - {DisplayStatusValue(status)}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Narrator"
        });
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isRunning ? accent : ResourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = modelText
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = accent,
            Opacity = isRunning ? 1.0 : 0.45,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = status
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        Grid.SetColumn(playButton, 2);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        return card;
    }

    private static bool IsAgentWorkingStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "thinking" or "generating" or "running" or "working" or "busy";
    }

    private Border CreateAgentActivitySweep(Brush accent, bool isRunning)
    {
        var accentColor = BrushColor(accent, Colors.DeepSkyBlue);
        var sweep = new Border
        {
            Width = 86,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Opacity = isRunning ? 0.92 : 0.56,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0),
                    new(Color.FromArgb(isRunning ? (byte)74 : (byte)38, accentColor.R, accentColor.G, accentColor.B), 0.48),
                    new(Colors.Transparent, 1)
                },
                new Point(0, 0.5),
                new Point(1, 0.5))
        };

        var translate = new TranslateTransform(-110, 0);
        sweep.RenderTransform = translate;
        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-110, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 1350 : 1900))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 2050 : 3000))));
        translate.BeginAnimation(TranslateTransform.XProperty, animation);
        return sweep;
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
            BorderBrush = ResourceBrush("ControlBorderBrush"),
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
            Foreground = ResourceBrush("TextBrush"),
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

    private static string FormatDuration(int latencyMs)
    {
        if (latencyMs <= 0)
        {
            return "time unknown";
        }

        return latencyMs < 1000
            ? $"{latencyMs} ms"
            : $"{latencyMs / 1000.0:0.0}s";
    }

    private static string FormatGeneratedTokens(TranscriptMessage message)
    {
        return message.CompletionTokens > 0
            ? $"{FormatCompactNumber(message.CompletionTokens)} Tok"
            : "Tok unknown";
    }

    private static string FormatCompactNumber(int value)
    {
        return value >= 1000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            ProviderTestStatus.Text = "No active session.";
            return;
        }

        await RunBusyAsync(TestProviderButton, async () =>
        {
            ProviderTestStatus.Text = "Testing provider...";
            var config = await LoadSharedProviderConfigAsync();
            if (config is null)
            {
                ProviderTestStatus.Text = "No provider config found.";
                return;
            }

            var result = await _providerHealth.TestCompletionAsync(config);
            if (result.Ok)
            {
                await PersistProviderReachabilityAsync(true, "", result.LatencyMs, "Provider online.");
            }
            else
            {
                var health = await _providerHealth.CheckAsync(ProviderHealthProbeConfig(config));
                await PersistProviderReachabilityAsync(
                    health.Ok,
                    health.Ok ? result.Error : health.Error,
                    result.LatencyMs,
                    health.Ok ? "Provider online; completion test failed." : "Provider offline.");
            }

            ProviderTestStatus.Text = result.Ok
                ? $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Text}"
                : $"Provider failed: {result.Error}";
        });
    }

    private async void PreloadSelectedModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(PreloadSelectedModelsButton, async () =>
        {
            SaveRoleModelDrafts();
            var models = SelectedModelsForPreload();
            PreloadModelsStatusText.Foreground = ResourceBrush("MutedTextBrush");
            PreloadModelsStatusText.Text = $"Preloading {models.Count} selected model(s)...";
            PreloadModelsItems.Children.Clear();

            var results = await _modelPreloadService.PreloadAsync(ProviderBaseUrlText.Text.Trim(), models);
            var failures = results.Count(result => result.IsFailure);
            PreloadModelsStatusText.Foreground = failures > 0
                ? ResourceBrush("DangerTextBrush")
                : ResourceBrush("AlphaAccentBrush");
            PreloadModelsStatusText.Text = $"Last preload: {DateTime.Now:h:mm:ss tt} - {results.Count} model(s), {failures} warning(s).";
            PopulatePreloadModelBadges(results);
            ProviderTestStatus.Text = failures > 0
                ? "Model preload finished with warnings. See preload telemetry."
                : "Selected models preloaded or already available.";

            await RefreshAdvertisedModelsAsync();
        });
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || _activeSession is null)
        {
            return;
        }

        var baseUrl = ProviderBaseUrlText.Text.Trim();
        var model = ProviderModelText.Text.Trim();
        SaveRoleModelDrafts();
        var alphaModel = RoleModel("alpha");
        var betaModel = RoleModel("beta");
        var gammaModel = RoleModel("gamma");
        var deltaModel = RoleModel("delta");
        var narratorModel = RoleModel("narrator");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            ProviderTestStatus.Text = "Base URL and model are required.";
            return;
        }

        if (!int.TryParse(ProviderTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout))
        {
            ProviderTestStatus.Text = "Timeout must be a whole number.";
            return;
        }

        if (!double.TryParse(ProviderTemperatureText.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var temperature))
        {
            ProviderTestStatus.Text = "Temperature must be a number.";
            return;
        }

        if (!int.TryParse(ProviderMaxOutputText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var maxOutput))
        {
            ProviderTestStatus.Text = "Max output must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextTranscriptWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var transcriptWindow))
        {
            ProviderTestStatus.Text = "Transcript window must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextPrivateWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var privateWindow))
        {
            ProviderTestStatus.Text = "Private notes window must be a whole number.";
            return;
        }

        if (!int.TryParse(ContextNotesWindowText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var notesWindow))
        {
            ProviderTestStatus.Text = "Pinned notes window must be a whole number.";
            return;
        }

        if (!int.TryParse(InternetMaxResultsText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var internetMaxResults))
        {
            ProviderTestStatus.Text = "Internet results must be a whole number.";
            return;
        }

        timeout = Math.Clamp(timeout, 1, 3600);
        temperature = Math.Clamp(temperature, 0, 2);
        maxOutput = Math.Clamp(maxOutput, 1, 32768);
        transcriptWindow = Math.Clamp(transcriptWindow, 1, 60);
        privateWindow = Math.Clamp(privateWindow, 0, 60);
        notesWindow = Math.Clamp(notesWindow, 0, 60);
        internetMaxResults = Math.Clamp(internetMaxResults, 1, 10);
        var activeParticipants = ParseActiveParticipants();
        var internetMode = (InternetModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual";
        var internetSourceScope = (InternetSourceScopePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "trusted";

        await RunArenaBusyAsync("Applying settings...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ProviderTestStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            snapshot.Configs["shared"] = new CoreModelProviderConfig
            {
                BaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl),
                Model = model,
                Timeout = timeout,
                Temperature = temperature,
                MaxOutputTokens = maxOutput,
                LastError = snapshot.Configs.TryGetValue("shared", out var existing) ? existing.LastError : "",
                LastLatencyMs = snapshot.Configs.TryGetValue("shared", out existing) ? existing.LastLatencyMs : 0,
                LastTestOk = snapshot.Configs.TryGetValue("shared", out existing) && existing.LastTestOk
            };
            SaveRoleModelConfig(snapshot.Configs, "alpha", alphaModel, snapshot.Configs["shared"]);
            SaveRoleModelConfig(snapshot.Configs, "beta", betaModel, snapshot.Configs["shared"]);
            SaveRoleModelConfig(snapshot.Configs, "gamma", gammaModel, snapshot.Configs["shared"]);
            SaveRoleModelConfig(snapshot.Configs, "delta", deltaModel, snapshot.Configs["shared"]);
            SaveRoleModelConfig(snapshot.Configs, "narrator", narratorModel, snapshot.Configs["shared"]);
            snapshot.Engine.TranscriptWindow = transcriptWindow;
            snapshot.Engine.PrivateWindow = privateWindow;
            snapshot.Engine.NotesWindow = notesWindow;
            snapshot.Engine.ModelRss.UseInternet = UseInternetCheckBox.IsChecked == true && !internetMode.Equals("off", StringComparison.OrdinalIgnoreCase);
            snapshot.Engine.ModelRss.Mode = internetMode;
            snapshot.Engine.ModelRss.SourceScope = internetSourceScope;
            snapshot.Engine.ModelRss.MaxResults = internetMaxResults;
            snapshot.Engine.ModelRss.AllowParticipantRequests = AllowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowModelRss = AllowParticipantInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.AllowNarratorRequests = AllowNarratorInternetCheckBox.IsChecked == true;
            snapshot.Engine.ModelRss.RequireApproval = RequireInternetApprovalCheckBox.IsChecked == true;
            if (activeParticipants >= 4)
            {
                EnsureDeltaAgent(snapshot);
            }
            for (var index = 0; index < snapshot.Engine.Agents.Count; index++)
            {
                snapshot.Engine.Agents[index].Active = index < activeParticipants;
            }

            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_settings_applied", new
            {
                snapshot.Configs["shared"].BaseUrl,
                snapshot.Configs["shared"].Model,
                snapshot.Configs["shared"].Timeout,
                snapshot.Configs["shared"].Temperature,
                snapshot.Configs["shared"].MaxOutputTokens,
                AlphaModel = alphaModel,
                BetaModel = betaModel,
                GammaModel = gammaModel,
                DeltaModel = deltaModel,
                NarratorModel = narratorModel,
                snapshot.Engine.ModelRss.UseInternet,
                snapshot.Engine.ModelRss.Mode,
                snapshot.Engine.ModelRss.SourceScope,
                snapshot.Engine.ModelRss.MaxResults,
                snapshot.Engine.ModelRss.AllowParticipantRequests,
                snapshot.Engine.ModelRss.AllowNarratorRequests,
                snapshot.Engine.ModelRss.RequireApproval,
                ActiveParticipants = activeParticipants
            });
            ProviderTestStatus.Text = "Settings applied.";
            RefreshActiveSession("Settings applied.");
            _ = RefreshProviderReachabilityAsync(force: true);
        });
    }

    private async void AutoChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null || _autoChatCancellation is not null)
        {
            return;
        }

        _autoChatCancellation = new CancellationTokenSource();
        var token = _autoChatCancellation.Token;
        SetArenaBusy(true, "Auto Chat running...", stopEnabled: true, AutoChatButton);

        try
        {
            while (!token.IsCancellationRequested && _activeSession is not null)
            {
                await _arenaOperationLock.WaitAsync(token);
                OneTurnResult result;
                try
                {
                    result = await _turnRunner.RunOneTurnAsync(_activeSession.Id, token);
                }
                finally
                {
                    _arenaOperationLock.Release();
                }

                var status = result.Ok && result.Message is not null
                    ? $"Auto Chat: {result.Message.Speaker} spoke ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                    : $"Auto Chat stopped: {result.Error}";
                RefreshActiveSession(status);
                if (!result.Ok)
                {
                    break;
                }

                if (result.Message is not null
                    && result.Message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                    && result.Message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    var resolved = await HandleInternetApprovalDialogAsync(result.Message, resumeAutoChat: true);
                    if (!resolved)
                    {
                        ArenaRunStatus.Text = "Auto Chat paused for internet approval.";
                        LoadStatus.Text = ArenaRunStatus.Text;
                        break;
                    }
                }

                await Task.Delay(AutoChatCadence(), token);
            }
        }
        catch (OperationCanceledException)
        {
            ArenaRunStatus.Text = "Auto Chat stopped.";
            LoadStatus.Text = "Auto Chat stopped.";
        }
        finally
        {
            _autoChatCancellation?.Dispose();
            _autoChatCancellation = null;
            SetArenaBusy(false, ArenaRunStatus.Text, stopEnabled: false);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _autoChatCancellation?.Cancel();
        ArenaRunStatus.Text = "Stopping Auto Chat...";
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Reset Arena",
            "Reset the current arena transcript and live state?\n\nScenario, cast, locks, provider settings, and checkpoints are preserved.",
            "Reset",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            ArenaRunStatus.Text = "Reset cancelled.";
            return;
        }

        await RunArenaBusyAsync("Resetting arena...", ResetButton, async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ArenaRunStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Narration.Clear();
            snapshot.Engine.TurnCount = 0;
            snapshot.Engine.TurnIndex = 0;
            snapshot.Engine.LastError = "";
            snapshot.Engine.Narrator.Status = "idle";
            snapshot.Engine.Narrator.LastError = "";
            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.Status = "waiting";
                agent.PrivateNotes.Clear();
            }

            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_arena_reset", new { session = _activeSession.Id });
            RefreshActiveSession("Arena reset.");
        });
    }

    private async void RandomSeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Generating random seed match...", RandomSeedButton, async () =>
        {
            var result = await _matchGeneration.GenerateRandomSeedAsync(_activeSession.Id);
            var status = result.Ok
                ? $"Random seed match generated: {result.Label}"
                : $"Random seed failed: {result.Error}";
            RefreshActiveSession(status);
        }, allowDuringAutoChat: true);
    }

    private async void AiChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "AI Choice",
            "Ask the narrator model to generate an AI Choice match?\n\nThe current transcript will be preserved.",
            "Generate",
            tone: ConfirmDialogTone.Normal);
        if (!confirm)
        {
            return;
        }

        await RunArenaBusyAsync("Asking narrator for AI Choice match...", AiChoiceButton, async () =>
        {
            var result = await _matchGeneration.GenerateAiChoiceAsync(_activeSession.Id);
            var status = result.Ok
                ? $"AI Choice match generated: {result.Label}"
                : $"AI Choice failed: {result.Error}";
            RefreshActiveSession(status);
        }, allowDuringAutoChat: true);
    }

    private async void NarrateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Narrator thinking...", NarrateNowButton, async () =>
        {
            var result = await _narratorService.NarrateNowAsync(_activeSession.Id);
            var status = result.Ok && result.Message is not null
                ? $"Narrator added turn {result.Message.Turn} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"Narrator failed: {result.Error}";
            RefreshActiveSession(status);
        }, allowDuringAutoChat: true);
    }

    private async void CurateNewsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Curating news...", CurateNewsButton, async () =>
        {
            var result = await _curatedNewsService.CurateNowAsync(_activeSession.Id);
            var status = result.Ok && result.Message is not null
                ? $"Curated news added at turn {result.Message.Turn}."
                : $"Curate news failed: {result.Error}";
            RefreshActiveSession(status);
        }, allowDuringAutoChat: true);
    }

    private async void OneTurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Running native 1 TURN...", OneTurnButton, async () =>
        {
            var result = await _turnRunner.RunOneTurnAsync(_activeSession.Id);
            var status = result.Ok && result.Message is not null
                ? $"1 TURN complete: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"1 TURN failed: {result.Error}";
            await RefreshActiveSessionAsync(status);
            ArenaRunStatus.Text = status;
            LoadStatus.Text = status;
            if (result.Message is not null
                && result.Message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
                && result.Message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInternetApprovalDialogAsync(result.Message, resumeAutoChat: false);
            }
        });
    }

    private async Task RunAgentTurnAsync(AgentState agent)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync($"Running {agent.Name} once...", async () =>
        {
            var result = await _turnRunner.RunAgentTurnAsync(_activeSession.Id, agent.Id);
            var status = result.Ok && result.Message is not null
                ? $"{agent.Name} one-shot complete: {result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms"
                : $"{agent.Name} one-shot failed: {result.Error}";
            RefreshActiveSession(status);
        });
    }

    private async void SendTurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session.";
            return;
        }

        var text = OperatorTurnText.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ArenaRunStatus.Text = "Operator turn is empty.";
            return;
        }

        await RunArenaBusyAsync("Injecting operator turn...", SendTurnButton, async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ArenaRunStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            var message = _transcriptService.CreateOperatorMessage(text, snapshot.Engine.TurnCount + 1);
            snapshot.Engine.Messages.Add(message);
            snapshot.Engine.TurnCount = message.Turn;
            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_operator_turn_added", new { message.Turn, message.Text });
            OperatorTurnText.Clear();
            RefreshActiveSession("Operator turn added.");
        }, allowDuringAutoChat: true);
    }

    private void CopyTranscriptMessage(TranscriptMessage message)
    {
        if (_arenaBusy)
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.Text);
            LoadStatus.Text = $"Copied turn {message.Turn}.";
            ArenaRunStatus.Text = LoadStatus.Text;
        }
        catch (Exception ex)
        {
            LoadStatus.Text = $"Copy failed: {ex.Message}";
            ArenaRunStatus.Text = LoadStatus.Text;
        }
    }

    private void CopyInternetUrl(TranscriptMessage message)
    {
        if (_arenaBusy || string.IsNullOrWhiteSpace(message.InternetUrl))
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.InternetUrl);
            LoadStatus.Text = $"Copied URL from turn {message.Turn}.";
            ArenaRunStatus.Text = LoadStatus.Text;
        }
        catch (Exception ex)
        {
            LoadStatus.Text = $"Copy URL failed: {ex.Message}";
            ArenaRunStatus.Text = LoadStatus.Text;
        }
    }

    private void ExportTranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session to export.";
            ArenaRunStatus.Text = LoadStatus.Text;
            ExportStatusText.Text = "Export unavailable: no active session.";
            return;
        }

        var visibleMessages = FilterTranscriptMessages(_lastRenderedMessages)
            .OrderBy(message => message.Turn)
            .ToArray();
        var messages = visibleMessages.Length > 0
            ? visibleMessages
            : _lastRenderedMessages.OrderBy(message => message.Turn).ToArray();
        if (messages.Length == 0)
        {
            LoadStatus.Text = "No transcript messages to export.";
            ArenaRunStatus.Text = LoadStatus.Text;
            ExportStatusText.Text = "Export skipped: no transcript messages.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            Filter = "Markdown transcript (*.md)|*.md|Text transcript (*.txt)|*.txt",
            FileName = $"AI Arena - {SafeFilePart(_activeSession.Id)} - transcript.md",
            AddExtension = true,
            DefaultExt = ".md"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var markdown = BuildTranscriptExport(messages);
            System.IO.File.WriteAllText(dialog.FileName, markdown);
            var fileName = System.IO.Path.GetFileName(dialog.FileName);
            var scope = visibleMessages.Length > 0 ? "visible" : "all";
            LoadStatus.Text = $"Exported {messages.Length} {scope} transcript message(s) to {fileName}.";
            ArenaRunStatus.Text = LoadStatus.Text;
            ExportStatusText.Text = $"Last export: {fileName} -> {dialog.FileName}";
        }
        catch (Exception ex)
        {
            LoadStatus.Text = $"Export failed: {ex.Message}";
            ArenaRunStatus.Text = LoadStatus.Text;
            ExportStatusText.Text = LoadStatus.Text;
        }
    }

    private string BuildTranscriptExport(IReadOnlyList<TranscriptMessage> messages)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"# AI Arena Transcript - {_activeSession?.Id ?? "session"}");
        builder.AppendLine();
        builder.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Visible messages: {messages.Count}");
        builder.AppendLine();

        foreach (var message in messages)
        {
            builder.AppendLine($"## Turn {message.Turn} - {message.Speaker} - {message.Status}");
            if (!string.IsNullOrWhiteSpace(message.Model))
            {
                builder.AppendLine($"Model: `{message.Model}`");
            }

            var stats = string.Join(
                " | ",
                new[]
                {
                    message.LatencyMs > 0 ? $"Generated: {FormatDuration(message.LatencyMs)}" : "",
                    message.CompletionTokens > 0 ? $"Tokens: {FormatCompactNumber(message.CompletionTokens)}" : "",
                    message.PromptTokens > 0 ? $"Context: {FormatCompactNumber(message.PromptTokens)}" : ""
                }.Where(item => !string.IsNullOrWhiteSpace(item)));
            if (!string.IsNullOrWhiteSpace(stats))
            {
                builder.AppendLine(stats);
            }

            builder.AppendLine();
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text.Trim());
            if (!string.IsNullOrWhiteSpace(message.Reasoning))
            {
                builder.AppendLine();
                builder.AppendLine("<details><summary>Model reasoning</summary>");
                builder.AppendLine();
                builder.AppendLine(message.Reasoning.Trim());
                builder.AppendLine();
                builder.AppendLine("</details>");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string SafeFilePart(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }

    private async Task ApproveInternetRequestAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await RunArenaBusyAsync($"Approving internet request from turn {message.Turn}...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ArenaRunStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            var original = TranscriptService.FindMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (original is null)
            {
                ArenaRunStatus.Text = $"Could not find approval turn {message.Turn}.";
                return;
            }

            var request = _transcriptService.InternetRequestFor(original);
            if (request is null)
            {
                ArenaRunStatus.Text = $"Approval turn {message.Turn} has no valid internet request.";
                return;
            }

            var result = await _internetToolService.ExecuteAsync(snapshot, request, _activeSession.Id);
            var replacement = _transcriptService.CreateInternetToolMessage(request, result, original.Turn);
            if (!_transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
            {
                ArenaRunStatus.Text = $"Could not replace approval turn {message.Turn}.";
                return;
            }

            snapshot.Engine.LastError = result.Ok ? "" : result.Error;
            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(
                _activeSession.Id,
                result.Ok ? "native_internet_request_approved" : "native_internet_request_approval_failed",
                new { message.Turn, request.Tool, request.Query, request.Url, result.Ok, result.Error });
            RefreshActiveSession(result.Ok
                ? $"Approved internet request at turn {message.Turn}."
                : $"Internet request failed after approval: {result.Error}");
        });
    }

    private async Task<bool> HandleInternetApprovalDialogAsync(CoreDialogueMessage approvalMessage, bool resumeAutoChat)
    {
        if (_activeSession is null)
        {
            return false;
        }

        var request = _transcriptService.InternetRequestFor(approvalMessage);
        if (request is null)
        {
            return false;
        }

        ArenaRunStatus.Text = resumeAutoChat ? "Auto Chat paused for internet approval." : "Internet approval required.";
        LoadStatus.Text = ArenaRunStatus.Text;
        var choice = InternetApprovalDialog.Show(this, _theme, request);
        if (choice == InternetApprovalChoice.AlwaysApprove)
        {
            RequireInternetApprovalCheckBox.IsChecked = false;
        }

        return choice switch
        {
            InternetApprovalChoice.ApproveOnce => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: false, approve: true),
            InternetApprovalChoice.AlwaysApprove => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: true, approve: true),
            _ => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: false, approve: false)
        };
    }

    private async Task<bool> ResolveInternetApprovalAsync(CoreDialogueMessage approvalMessage, bool alwaysApprove, bool approve)
    {
        if (_activeSession is null)
        {
            return false;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            ArenaRunStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
            return false;
        }

        var original = TranscriptService.FindMessage(snapshot, approvalMessage.Turn, approvalMessage.SpeakerId, approvalMessage.CreatedAt);
        if (original is null)
        {
            ArenaRunStatus.Text = $"Could not find approval turn {approvalMessage.Turn}.";
            return false;
        }

        var request = _transcriptService.InternetRequestFor(original);
        if (request is null)
        {
            ArenaRunStatus.Text = $"Approval turn {approvalMessage.Turn} has no valid internet request.";
            return false;
        }

        if (alwaysApprove)
        {
            snapshot.Engine.ModelRss.RequireApproval = false;
        }

        if (!approve)
        {
            var rejected = new CoreDialogueMessage
            {
                Turn = original.Turn,
                Speaker = original.Speaker,
                SpeakerId = original.SpeakerId,
                Text = original.Text + Environment.NewLine + "Status: rejected by operator",
                Status = "rejected",
                Pinned = original.Pinned,
                Kind = original.Kind,
                CreatedAt = original.CreatedAt,
                Model = original.Model,
                Metadata = original.Metadata,
                Extra = original.Extra
            };
            if (!_transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, rejected))
            {
                ArenaRunStatus.Text = $"Could not reject approval turn {approvalMessage.Turn}.";
                return false;
            }

            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_internet_request_rejected", new { original.Turn, request.Tool, request.Query, request.Url });
            RefreshActiveSession($"Rejected internet request at turn {approvalMessage.Turn}.");
            return true;
        }

        var result = await _internetToolService.ExecuteAsync(snapshot, request, _activeSession.Id);
        var replacement = _transcriptService.CreateInternetToolMessage(request, result, original.Turn);
        if (!_transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
        {
            ArenaRunStatus.Text = $"Could not replace approval turn {approvalMessage.Turn}.";
            return false;
        }

        snapshot.Engine.LastError = result.Ok ? "" : result.Error;
        await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
        await _eventLogStore.AppendAsync(
            _activeSession.Id,
            result.Ok ? "native_internet_request_approved" : "native_internet_request_approval_failed",
            new { original.Turn, request.Tool, request.Query, request.Url, result.Ok, result.Error, AlwaysApprove = alwaysApprove });
        RefreshActiveSession(result.Ok
            ? $"Approved internet request at turn {approvalMessage.Turn}."
            : $"Internet request failed after approval: {result.Error}");
        return true;
    }

    private async Task RejectInternetRequestAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync($"Rejected internet request at turn {message.Turn}.", async snapshot =>
        {
            var original = TranscriptService.FindMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (original is null)
            {
                LoadStatus.Text = $"Could not find approval turn {message.Turn}.";
                return false;
            }

            var replacement = new CoreDialogueMessage
            {
                Turn = original.Turn,
                Speaker = original.Speaker,
                SpeakerId = original.SpeakerId,
                Text = original.Text + Environment.NewLine + "Status: rejected by operator",
                Status = "rejected",
                Pinned = original.Pinned,
                Kind = original.Kind,
                CreatedAt = original.CreatedAt,
                Model = original.Model,
                Metadata = original.Metadata,
                Extra = original.Extra
            };
            if (!_transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
            {
                LoadStatus.Text = $"Could not reject approval turn {message.Turn}.";
                return false;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, "native_internet_request_rejected", new { message.Turn, message.InternetTool, message.InternetQuery, message.InternetUrl });
            return true;
        });
    }

    private async Task DeleteTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync($"Deleted turn {message.Turn}.", async snapshot =>
        {
            var deleted = _transcriptService.DeleteMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (!deleted)
            {
                LoadStatus.Text = $"Could not find turn {message.Turn} to delete.";
                return false;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, "native_transcript_message_deleted", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
        });
    }

    private async Task RetryTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0 || !IsAgentSpeaker(message.SpeakerId))
        {
            return;
        }

        await RunArenaBusyAsync($"Retrying turn {message.Turn} with {message.Speaker}...", async () =>
        {
            var result = await _turnRunner.RetryTurnAsync(_activeSession.Id, message.Turn, message.SpeakerId, message.CreatedAt);
            var status = result.Ok && result.Message is not null
                ? $"Retry replaced turn {message.Turn}: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
                : $"Retry failed: {result.Error}";
            RefreshActiveSession(status);
        });
    }

    private async Task TogglePinTranscriptMessageAsync(TranscriptMessage message)
    {
        if (_arenaBusy || _activeSession is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync(message.Pinned ? $"Unpinned turn {message.Turn}." : $"Pinned turn {message.Turn}.", async snapshot =>
        {
            var changed = _transcriptService.TogglePinned(snapshot, message.Turn, message.SpeakerId, message.CreatedAt, out var pinned);
            if (!changed)
            {
                LoadStatus.Text = $"Could not find turn {message.Turn} to pin.";
                return false;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, pinned ? "native_transcript_message_pinned" : "native_transcript_message_unpinned", new { message.Turn, message.Speaker, message.SpeakerId });
            return true;
        });
    }

    private async Task MutateTranscriptAsync(string successStatus, Func<AIArena.Core.Models.ArenaSnapshot, Task<bool>> mutation)
    {
        if (_activeSession is null)
        {
            return;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
            return;
        }

        if (!await mutation(snapshot))
        {
            return;
        }

        await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
        RefreshActiveSession(successStatus);
    }

    private async Task<CoreModelProviderConfig?> LoadSharedProviderConfigAsync()
    {
        if (_activeSession is null)
        {
            return null;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            return null;
        }

        return snapshot.Configs.TryGetValue("shared", out var shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
    }

    private async Task RefreshProviderReachabilityAsync(bool force = false)
    {
        if (_activeSession is null || _isCheckingProviderHealth || (_arenaBusy && !force))
        {
            return;
        }

        _isCheckingProviderHealth = true;
        var nextInterval = TimeSpan.FromSeconds(3);
        try
        {
            var sessionId = _activeSession.Id;
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(sessionId);
            if (snapshot is null || !snapshot.Configs.TryGetValue("shared", out var shared))
            {
                return;
            }

            var socket = await ProbeProviderSocketAsync(shared);
            if (!socket.Ok)
            {
                await PersistProviderReachabilityAsync(false, socket.Error, socket.LatencyMs, "Provider offline.", snapshot, sessionId);
                return;
            }

            if (!shared.LastTestOk)
            {
                var health = await _providerHealth.CheckAsync(ProviderHealthProbeConfig(shared));
                await PersistProviderReachabilityAsync(
                    health.Ok,
                    health.Ok ? "" : health.Error,
                    socket.LatencyMs,
                    health.Ok ? "Provider online." : "Provider reachable; model list unavailable.",
                    snapshot,
                    sessionId);
                nextInterval = health.Ok ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
                return;
            }

            nextInterval = TimeSpan.FromSeconds(10);
            await PersistProviderReachabilityAsync(true, "", socket.LatencyMs, "Provider online.", snapshot, sessionId);
        }
        finally
        {
            _isCheckingProviderHealth = false;
            _providerHealthTimer.Interval = nextInterval;
        }
    }

    private async Task PersistProviderReachabilityAsync(
        bool online,
        string error,
        int latencyMs,
        string status,
        AIArena.Core.Models.ArenaSnapshot? snapshot = null,
        string? sessionId = null)
    {
        if (_activeSession is null)
        {
            return;
        }

        sessionId ??= _activeSession.Id;
        snapshot ??= await _coreSessionStore.LoadSnapshotAsync(sessionId);
        if (snapshot is null || !snapshot.Configs.TryGetValue("shared", out var shared))
        {
            return;
        }

        error = online ? "" : error;
        if (shared.LastTestOk == online
            && shared.LastError.Equals(error, StringComparison.Ordinal))
        {
            await UpdateActiveProviderStatusOnlyAsync(status);
            return;
        }

        snapshot.Configs["shared"] = CopyProviderConfigWithStatus(shared, online, error, latencyMs);
        await _coreSessionStore.SaveSnapshotAsync(snapshot, sessionId);
        _providerHealthTimer.Interval = online ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
        await _eventLogStore.AppendAsync(sessionId, "native_provider_reachability_changed", new
        {
            Online = online,
            Error = error,
            LatencyMs = latencyMs
        });
        await UpdateActiveProviderStatusOnlyAsync(status);
    }

    private async Task UpdateActiveProviderStatusOnlyAsync(string status)
    {
        if (_activeSession is null)
        {
            return;
        }

        var latest = (await _coreSessionStore.ListSessionsAsync()).FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latest is null)
        {
            return;
        }

        var coreSnapshot = await _coreSessionStore.LoadSnapshotAsync(latest.Id);
        if (coreSnapshot is null)
        {
            return;
        }

        var snapshot = SnapshotViewMapper.FromCore(latest, coreSnapshot);
        _activeSession = latest;
        _activeSnapshotWriteUtc = latest.LastModified;
        UpdateTopBarStatus(snapshot);
        if (!_arenaBusy)
        {
            ArenaRunStatus.Text = status;
        }
    }

    private async void CreateSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            LoadStatus.Text = "No active session to use as a template.";
            return;
        }

        var newSessionId = SessionStore.SafeSessionId(NewSessionText.Text);
        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            LoadStatus.Text = "New session name is empty.";
            return;
        }

        await RunArenaBusyAsync($"Creating session {newSessionId}...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            await _coreSessionStore.CreateSessionAsync(newSessionId, snapshot);
            await _eventLogStore.AppendAsync(newSessionId, "native_session_created", new { source = _activeSession.Id });
            NewSessionText.Clear();
            LoadSessions(newSessionId);
            LoadStatus.Text = $"Created and switched to session {newSessionId}.";
            ArenaRunStatus.Text = $"Session: {newSessionId}.";
        });
    }

    private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            return;
        }

        var sessionId = _activeSession.Id;
        if (sessionId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            LoadStatus.Text = "Default session cannot be deleted from WPF.";
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Delete Session",
            $"Delete session \"{sessionId}\"?\n\nThis removes the session folder and cannot be undone.",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            LoadStatus.Text = "Session delete cancelled.";
            return;
        }

        await RunArenaBusyAsync($"Deleting session {sessionId}...", async () =>
        {
            var deleted = await _coreSessionStore.DeleteSessionAsync(sessionId);
            LoadSessions("default");
            LoadStatus.Text = deleted ? $"Deleted session {sessionId}." : $"Could not delete session {sessionId}.";
            ArenaRunStatus.Text = LoadStatus.Text;
        });
    }

    private async void SaveScenarioTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            ScenarioTemplateStatus.Text = "No active session.";
            return;
        }

        await RunArenaBusyAsync("Saving scenario template...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ScenarioTemplateStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            var template = _scenarioTemplateStore.Save(ScenarioTemplateNameText.Text, snapshot);
            ScenarioTemplateNameText.Clear();
            LoadScenarioTemplates(template.Id);
            ScenarioTemplateStatus.Text = $"Saved template: {template.Name}.";
            ArenaRunStatus.Text = ScenarioTemplateStatus.Text;
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_scenario_template_saved", new { template.Id, template.Name });
        });
    }

    private async void ApplyScenarioTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null || ScenarioTemplatePicker.SelectedItem is not ScenarioTemplate template)
        {
            ScenarioTemplateStatus.Text = "Choose a scenario template to apply.";
            return;
        }

        await RunArenaBusyAsync($"Applying scenario template {template.Name}...", async () =>
        {
            var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (snapshot is null)
            {
                ScenarioTemplateStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            ScenarioTemplateStore.Apply(template, snapshot);
            await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_scenario_template_applied", new { template.Id, template.Name });
            RefreshActiveSession($"Applied template: {template.Name}.");
            ScenarioTemplateStatus.Text = $"Applied template: {template.Name}.";
        });
    }

    private void DeleteScenarioTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScenarioTemplatePicker.SelectedItem is not ScenarioTemplate template)
        {
            ScenarioTemplateStatus.Text = "Choose a scenario template to delete.";
            return;
        }

        var deleted = _scenarioTemplateStore.Delete(template.Id);
        LoadScenarioTemplates();
        ScenarioTemplateStatus.Text = deleted ? $"Deleted template: {template.Name}." : "Template delete failed.";
    }

    private async void SaveCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null)
        {
            SetCheckpointStatus("No active session.", isDanger: true);
            return;
        }

        await RunArenaBusyAsync("Saving checkpoint...", async () =>
        {
            var checkpoint = await _coreSessionStore.SaveCheckpointAsync(_activeSession.Id, CheckpointNameText.Text);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_checkpoint_saved", new { checkpoint.Id, checkpoint.Name });
            CheckpointNameText.Clear();
            await RefreshCheckpointsAsync(checkpoint.Id);
            SetCheckpointStatus($"Saved checkpoint: {checkpoint.Name}");
            ArenaRunStatus.Text = CheckpointStatus.Text;
        });
    }

    private async void RestoreCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null || CheckpointPicker.SelectedItem is not CheckpointSummary checkpoint)
        {
            SetCheckpointStatus("Choose a checkpoint to restore.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Restore Checkpoint",
            $"Restore \"{checkpoint.Name}\"?\n\nThe current arena will return to that saved state, including transcript, cast, locks, and settings.",
            "Restore",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetCheckpointStatus("Restore cancelled.");
            return;
        }

        await RunArenaBusyAsync($"Restoring checkpoint {checkpoint.Name}...", async () =>
        {
            var restored = await _coreSessionStore.RestoreCheckpointAsync(_activeSession.Id, checkpoint.Id);
            if (restored is null)
            {
                SetCheckpointStatus("Checkpoint restore failed.", isDanger: true);
                return;
            }

            await _eventLogStore.AppendAsync(_activeSession.Id, "native_checkpoint_restored", new { restored.Id, restored.Name });
            RefreshActiveSession($"Restored: {restored.Name}");
            await RefreshCheckpointsAsync(restored.Id);
            SetCheckpointStatus($"Restored checkpoint: {restored.Name}");
        });
    }

    private async void DeleteCheckpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSession is null || CheckpointPicker.SelectedItem is not CheckpointSummary checkpoint)
        {
            SetCheckpointStatus("Choose a checkpoint to delete.", isDanger: true);
            return;
        }

        var confirm = ConfirmDialog.Show(
            this,
            _theme,
            "Delete Checkpoint",
            $"Delete \"{checkpoint.Name}\"?\n\nThis removes only the saved checkpoint. The current arena state is not changed.",
            "Delete",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            SetCheckpointStatus("Delete cancelled.");
            return;
        }

        await RunArenaBusyAsync($"Deleting checkpoint {checkpoint.Name}...", async () =>
        {
            var deleted = await _coreSessionStore.DeleteCheckpointAsync(_activeSession.Id, checkpoint.Id);
            if (deleted)
            {
                await _eventLogStore.AppendAsync(_activeSession.Id, "native_checkpoint_deleted", new { checkpoint.Id, checkpoint.Name });
            }

            await RefreshCheckpointsAsync();
            SetCheckpointStatus(deleted ? $"Deleted checkpoint: {checkpoint.Name}" : "Checkpoint delete failed.", isDanger: !deleted);
            ArenaRunStatus.Text = CheckpointStatus.Text;
        });
    }

    private static async Task RunBusyAsync(Control control, Func<Task> action)
    {
        control.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            control.IsEnabled = true;
        }
    }

    private async Task RunArenaBusyAsync(string status, Func<Task> action)
    {
        await RunArenaBusyAsync(status, null, action);
    }

    private async Task RunArenaBusyAsync(string status, Button? operationButton, Func<Task> action, bool allowDuringAutoChat = false)
    {
        var ownsBusyState = !_arenaBusy;
        var runsDuringAutoChat = !ownsBusyState && allowDuringAutoChat && _autoChatCancellation is not null;
        if (!ownsBusyState && !runsDuringAutoChat)
        {
            return;
        }

        if (ownsBusyState)
        {
            SetArenaBusy(true, status, stopEnabled: false, operationButton);
        }
        else
        {
            ArenaRunStatus.Text = status;
            LoadStatus.Text = status;
            SetBreathingOperationButton(operationButton);
            if (operationButton is not null)
            {
                operationButton.IsEnabled = false;
            }
        }

        try
        {
            await _arenaOperationLock.WaitAsync();
            await action();
        }
        finally
        {
            _arenaOperationLock.Release();
            if (ownsBusyState)
            {
                SetArenaBusy(false, ArenaRunStatus.Text, stopEnabled: false);
            }
            else if (runsDuringAutoChat)
            {
                if (operationButton is not null)
                {
                    operationButton.IsEnabled = true;
                }

                SetBreathingOperationButton(_autoChatCancellation is not null ? AutoChatButton : null);
            }
        }
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled)
    {
        SetArenaBusy(busy, status, stopEnabled, null);
    }

    private void SetArenaBusy(bool busy, string status, bool stopEnabled, Button? operationButton)
    {
        _arenaBusy = busy;
        SetBreathingOperationButton(busy ? operationButton : null);
        SetButtonBreathing(StopButton, busy && stopEnabled);
        var autoChatRunning = _autoChatCancellation is not null;
        AutoChatButton.IsEnabled = !busy;
        OneTurnButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        RandomSeedButton.IsEnabled = !busy || autoChatRunning;
        AiChoiceButton.IsEnabled = !busy || autoChatRunning;
        NarrateNowButton.IsEnabled = !busy || autoChatRunning;
        CurateNewsButton.IsEnabled = !busy || autoChatRunning;
        StopButton.IsEnabled = stopEnabled;
        if (busy && operationButton is not null)
        {
            operationButton.IsEnabled = true;
        }
        SendTurnButton.IsEnabled = true;
        OperatorTurnText.IsEnabled = true;
        TestProviderButton.IsEnabled = !busy;
        PreloadSelectedModelsButton.IsEnabled = !busy;
        ApplySettingsButton.IsEnabled = !busy;
        ActiveParticipantsPicker.IsEnabled = !busy;
        ProviderBaseUrlText.IsEnabled = !busy;
        ProviderModelText.IsEnabled = !busy;
        AlphaRoleModelText.IsEnabled = !busy;
        BetaRoleModelText.IsEnabled = !busy;
        GammaRoleModelText.IsEnabled = !busy;
        DeltaRoleModelText.IsEnabled = !busy;
        NarratorRoleModelText.IsEnabled = !busy;
        ProviderTimeoutText.IsEnabled = !busy;
        ProviderTemperatureText.IsEnabled = !busy;
        ProviderMaxOutputText.IsEnabled = !busy;
        ContextTranscriptWindowText.IsEnabled = !busy;
        ContextPrivateWindowText.IsEnabled = !busy;
        ContextNotesWindowText.IsEnabled = !busy;
        UseInternetCheckBox.IsEnabled = !busy;
        InternetModePicker.IsEnabled = !busy;
        InternetSourceScopePicker.IsEnabled = !busy;
        InternetMaxResultsText.IsEnabled = !busy;
        AllowParticipantInternetCheckBox.IsEnabled = !busy;
        AllowNarratorInternetCheckBox.IsEnabled = !busy;
        RequireInternetApprovalCheckBox.IsEnabled = !busy;
        AutoChatCadencePicker.IsEnabled = !busy;
        AvatarStylePicker.IsEnabled = !busy;
        SystemGlyphStylePicker.IsEnabled = !busy;
        TopStripModePicker.IsEnabled = !busy;
        SessionPicker.IsEnabled = !busy;
        CreateSessionButton.IsEnabled = !busy;
        DeleteSessionButton.IsEnabled = !busy;
        NewSessionText.IsEnabled = !busy;
        SaveScenarioTemplateButton.IsEnabled = !busy;
        ApplyScenarioTemplateButton.IsEnabled = !busy && ScenarioTemplatePicker.Items.Count > 0;
        DeleteScenarioTemplateButton.IsEnabled = !busy && ScenarioTemplatePicker.Items.Count > 0;
        ScenarioTemplateNameText.IsEnabled = !busy;
        ScenarioTemplatePicker.IsEnabled = !busy;
        SaveCheckpointButton.IsEnabled = !busy;
        RestoreCheckpointButton.IsEnabled = !busy && CheckpointPicker.Items.Count > 0;
        DeleteCheckpointButton.IsEnabled = !busy && CheckpointPicker.Items.Count > 0;
        CheckpointNameText.IsEnabled = !busy;
        CheckpointPicker.IsEnabled = !busy;
        foreach (var button in _agentTurnButtons)
        {
            button.IsEnabled = !busy;
        }
        foreach (var button in _narratorActionButtons)
        {
            button.IsEnabled = !busy || autoChatRunning;
        }
        foreach (var button in _transcriptActionButtons)
        {
            button.IsEnabled = !busy && button.Tag is true;
        }
        foreach (var checkBox in _lockControls)
        {
            checkBox.IsEnabled = !busy;
        }
        ArenaRunStatus.Text = status;
    }

    private void SetBreathingOperationButton(Button? button)
    {
        if (_breathingOperationButton == button)
        {
            return;
        }

        if (_breathingOperationButton is not null)
        {
            SetButtonBreathing(_breathingOperationButton, false);
        }

        _breathingOperationButton = button;
        if (_breathingOperationButton is not null)
        {
            SetButtonBreathing(_breathingOperationButton, true);
        }
    }

    private static void SetButtonBreathing(Button button, bool breathing)
    {
        if (!breathing)
        {
            if (button.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }

            if (button.Effect is DropShadowEffect glow && !glow.IsFrozen)
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            }

            button.Effect = null;
            return;
        }

        var scaleTransform = new ScaleTransform(1, 1);
        button.RenderTransform = scaleTransform;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var borderColor = button.BorderBrush is SolidColorBrush borderBrush
            ? borderBrush.Color
            : Colors.White;
        var glowEffect = new DropShadowEffect
        {
            Color = borderColor,
            Direction = 0,
            ShadowDepth = 0,
            BlurRadius = 9,
            Opacity = 0.2
        };
        button.Effect = glowEffect;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var scaleAnimation = new DoubleAnimation(1, 1.025, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var glowAnimation = new DoubleAnimation(0.18, 0.62, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };
        var blurAnimation = new DoubleAnimation(8, 15, TimeSpan.FromMilliseconds(760))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, glowAnimation);
        glowEffect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnimation);
    }

    private async void RefreshActiveSession(string status)
    {
        await RefreshActiveSessionAsync(status);
    }

    private async Task RefreshActiveSessionAsync(string status)
    {
        if (_activeSession is null)
        {
            return;
        }

        var latest = (await _coreSessionStore.ListSessionsAsync()).FirstOrDefault(session => session.Id == _activeSession.Id);
        if (latest is not null)
        {
            await LoadSessionAsync(latest, force: true);
        }

        LoadStatus.Text = status;
        ArenaRunStatus.Text = status;
    }

    private void RefreshCheckpoints()
    {
        _ = RefreshCheckpointsAsync();
    }

    private async Task RefreshCheckpointsAsync(string? selectedCheckpointId = null)
    {
        if (_activeSession is null)
        {
            ClearCheckpoints("No active session.");
            return;
        }

        var checkpoints = await _coreSessionStore.ListCheckpointsAsync(_activeSession.Id);
        CheckpointPicker.ItemsSource = checkpoints;
        CheckpointPicker.SelectedItem = checkpoints.FirstOrDefault(checkpoint => checkpoint.Id == selectedCheckpointId)
            ?? checkpoints.FirstOrDefault();
        RestoreCheckpointButton.IsEnabled = !_arenaBusy && checkpoints.Count > 0;
        DeleteCheckpointButton.IsEnabled = !_arenaBusy && checkpoints.Count > 0;
        CheckpointCountText.Text = checkpoints.Count == 1 ? "1 saved checkpoint" : $"{checkpoints.Count} saved checkpoints";
        CheckpointCountText.Foreground = checkpoints.Count > 0 ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush");
        if (checkpoints.Count == 0)
        {
            SetCheckpointStatus("No checkpoints saved for this session.");
        }
    }

    private void ClearCheckpoints(string status)
    {
        CheckpointPicker.ItemsSource = Array.Empty<CheckpointSummary>();
        RestoreCheckpointButton.IsEnabled = false;
        DeleteCheckpointButton.IsEnabled = false;
        CheckpointCountText.Text = "0 saved checkpoints";
        CheckpointCountText.Foreground = ResourceBrush("MutedTextBrush");
        SetCheckpointStatus(status);
    }

    private void SetCheckpointStatus(string status, bool isDanger = false)
    {
        CheckpointStatus.Text = status;
        CheckpointStatus.Foreground = isDanger ? ResourceBrush("DangerTextBrush") : ResourceBrush("MutedTextBrush");
    }

    private async void MatchLockChanged(object sender, RoutedEventArgs e)
    {
        if (_arenaBusy || _activeSession is null || sender is not CheckBox checkBox || checkBox.Tag is not string key)
        {
            return;
        }

        var locked = checkBox.IsChecked == true;
        await RunArenaBusyAsync($"Updating {key} lock...", async () =>
        {
            await _matchGeneration.ToggleLockAsync(_activeSession.Id, key, locked);
            RefreshActiveSession($"{key} lock {(locked ? "enabled" : "disabled")}.");
        });
    }

    private async void EditLockCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_arenaBusy || _activeSession is null || sender is not Button button || button.Tag is not string key)
        {
            return;
        }

        var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
        if (snapshot is null)
        {
            LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
            ArenaRunStatus.Text = LoadStatus.Text;
            return;
        }

        var current = CurrentMatchText(snapshot, key);
        var edited = TextEditDialog.Show(this, _theme, $"Edit {DisplayLockKey(key)}", current);
        if (edited is null)
        {
            LoadStatus.Text = "Edit cancelled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(edited))
        {
            LoadStatus.Text = "Edit text is empty.";
            ArenaRunStatus.Text = LoadStatus.Text;
            return;
        }

        await RunArenaBusyAsync($"Updating {key}...", async () =>
        {
            var latest = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
            if (latest is null)
            {
                LoadStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                return;
            }

            if (!ApplyMatchTextEdit(latest, key, edited))
            {
                LoadStatus.Text = $"Could not edit {key}.";
                ArenaRunStatus.Text = LoadStatus.Text;
                return;
            }

            latest.MatchLocks[NormalizeMatchLockKey(key)] = true;
            await _coreSessionStore.SaveSnapshotAsync(latest, _activeSession.Id);
            var normalizedKey = NormalizeMatchLockKey(key);
            await _eventLogStore.AppendAsync(_activeSession.Id, "native_match_text_edited", new
            {
                key = normalizedKey,
                locked = true
            });
            RefreshActiveSession($"Updated and locked {DisplayLockKey(key)}.");
        });
    }

    private static string CurrentMatchText(AIArena.Core.Models.ArenaSnapshot snapshot, string key)
    {
        return NormalizeMatchLockKey(key) switch
        {
            "topic" => snapshot.Engine.Steering.Topic,
            "global" => snapshot.Engine.Steering.Global,
            var agentId when agentId is "alpha" or "beta" or "gamma" or "delta" or "narrator" =>
                snapshot.Engine.Agents.FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))?.Persona
                ?? (agentId == "narrator" ? snapshot.Engine.Narrator.Persona : ""),
            _ => ""
        };
    }

    private static bool ApplyMatchTextEdit(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeMatchLockKey(key);
        switch (normalizedKey)
        {
            case "topic":
                snapshot.Engine.Steering.Topic = value;
                return true;
            case "global":
                snapshot.Engine.Steering.Global = value;
                return true;
            case "narrator":
                snapshot.Engine.Narrator.Persona = value;
                return true;
            case "alpha":
            case "beta":
            case "gamma":
            case "delta":
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.Persona = value;
                return true;
            default:
                return false;
        }
    }

    private void UpdateFrictionDiagnostics(IReadOnlyList<TranscriptMessage> messages)
    {
        var diagnostics = _discourseDiagnostics.Analyze(messages.Select(ToDiscourseTurn), _lastAgentPersonas);
        var series = BuildDiagnosticSeries(messages);
        SetDiagnosticTile(
            FrictionChip,
            FrictionValueText,
            FrictionTrendText,
            null,
            diagnostics.StateLabel,
            FrictionScore(diagnostics.StateLabel),
            series,
            diagnostics.Details["friction"],
            DiagnosticAccentForState(diagnostics.StateLabel));
        SetDiagnosticTile(
            ConsensusChip,
            ConsensusValueText,
            ConsensusTrendText,
            ConsensusSparkline,
            $"{diagnostics.ConsensusPercent}%",
            diagnostics.ConsensusPercent,
            series,
            diagnostics.Details["consensus"],
            DiagnosticAccentForRisk(diagnostics.ConsensusLabel));
        SetDiagnosticTile(
            RoleDriftChip,
            RoleDriftValueText,
            RoleDriftTrendText,
            RoleDriftSparkline,
            $"{diagnostics.RoleDriftPercent}%",
            diagnostics.RoleDriftPercent,
            series,
            diagnostics.Details["roleDrift"],
            DiagnosticAccentForRisk(diagnostics.RoleDriftLabel));
        SetDiagnosticTile(
            UnsupportedClaimsChip,
            UnsupportedClaimsValueText,
            UnsupportedClaimsTrendText,
            UnsupportedClaimsSparkline,
            diagnostics.UnsupportedClaimCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            diagnostics.UnsupportedClaimCount,
            series,
            diagnostics.Details["unsupportedClaims"],
            DiagnosticAccentForRisk(diagnostics.UnsupportedClaimSeverity),
            maxValue: Math.Max(4, series.UnsupportedClaimsMax));
        SetDiagnosticTile(
            EvidencePressureChip,
            EvidencePressureValueText,
            EvidencePressureTrendText,
            EvidencePressureSparkline,
            diagnostics.EvidencePressureLabel,
            diagnostics.EvidencePressureScore,
            series,
            diagnostics.Details["evidencePressure"],
            DiagnosticAccentForEvidence(diagnostics.EvidencePressureLabel));
        SetDiagnosticTile(
            NarrativeHeatChip,
            NarrativeHeatValueText,
            NarrativeHeatTrendText,
            NarrativeHeatSparkline,
            diagnostics.NarrativeHeatLabel,
            diagnostics.NarrativeHeatScore,
            series,
            diagnostics.Details["narrativeHeat"],
            DiagnosticAccentForNarrative(diagnostics.NarrativeHeatLabel));
    }

    private static CoreDiscourseTurn ToDiscourseTurn(TranscriptMessage message)
    {
        return new CoreDiscourseTurn(
            message.Turn,
            message.SpeakerId,
            message.Speaker,
            message.Kind,
            message.Text,
            message.InternetSources,
            message.CreatedAt);
    }

    private void SetDiagnosticTile(
        Border chip,
        TextBlock valueText,
        TextBlock trendText,
        MetricSparklineControl? sparkline,
        string value,
        int score,
        DiagnosticSeriesSet series,
        CoreMetricDiagnostic metric,
        Brush accent,
        int maxValue = 100)
    {
        chip.BorderBrush = BlendBrush(ResourceBrush("ControlBorderBrush"), accent, 0.62);
        chip.Background = BlendBrush(ResourceBrush("InputBrush"), accent, 0.1);
        valueText.Text = value;
        valueText.Foreground = accent;
        trendText.Text = FormatDiagnosticTrend(score, PreviousDiagnosticScore(sparkline, series));
        trendText.Foreground = BlendBrush(ResourceBrush("MutedTextBrush"), accent, 0.55);
        if (sparkline is not null)
        {
            sparkline.Values = DiagnosticSeries(sparkline, series);
            sparkline.AccentBrush = accent;
            sparkline.MaxValue = Math.Max(1, maxValue);
        }

        chip.ToolTip = DiagnosticToolTip(metric);
        ToolTipService.SetShowDuration(chip, 60000);
    }

    private DiagnosticSeriesSet BuildDiagnosticSeries(IReadOnlyList<TranscriptMessage> messages)
    {
        var ordered = messages
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new DiagnosticSeriesSet([], 0);
        }

        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)DiagnosticHistoryLimit));
        var points = new List<DiagnosticHistoryPoint>();
        for (var end = 1; end <= ordered.Length; end += stride)
        {
            points.Add(DiagnosticPointForWindow(ordered, end));
        }

        if (points.Count == 0 || points[^1] != DiagnosticPointForWindow(ordered, ordered.Length))
        {
            points.Add(DiagnosticPointForWindow(ordered, ordered.Length));
        }

        if (points.Count > DiagnosticHistoryLimit)
        {
            points = points.Skip(points.Count - DiagnosticHistoryLimit).ToList();
        }

        return new DiagnosticSeriesSet(
            points,
            points.Select(point => point.UnsupportedClaims).DefaultIfEmpty(0).Max());
    }

    private DiagnosticHistoryPoint DiagnosticPointForWindow(IReadOnlyList<TranscriptMessage> orderedMessages, int endExclusive)
    {
        var end = Math.Clamp(endExclusive, 1, orderedMessages.Count);
        var start = Math.Max(0, end - DiagnosticWindowSize);
        var window = orderedMessages
            .Skip(start)
            .Take(end - start)
            .Select(ToDiscourseTurn);
        var diagnostics = _discourseDiagnostics.Analyze(window, _lastAgentPersonas);
        return new DiagnosticHistoryPoint(
            FrictionScore(diagnostics.StateLabel),
            diagnostics.ConsensusPercent,
            diagnostics.RoleDriftPercent,
            diagnostics.UnsupportedClaimCount,
            diagnostics.EvidencePressureScore,
            diagnostics.NarrativeHeatScore);
    }

    private int? PreviousDiagnosticScore(MetricSparklineControl? sparkline, DiagnosticSeriesSet series)
    {
        if (series.Points.Count < 2)
        {
            return null;
        }

        var previous = series.Points[^2];
        if (sparkline == ConsensusSparkline)
        {
            return previous.Consensus;
        }

        if (sparkline == RoleDriftSparkline)
        {
            return previous.RoleDrift;
        }

        if (sparkline == UnsupportedClaimsSparkline)
        {
            return previous.UnsupportedClaims;
        }

        if (sparkline == EvidencePressureSparkline)
        {
            return previous.EvidencePressure;
        }

        if (sparkline == NarrativeHeatSparkline)
        {
            return previous.NarrativeHeat;
        }

        return previous.Friction;
    }

    private IReadOnlyList<double> DiagnosticSeries(MetricSparklineControl sparkline, DiagnosticSeriesSet series)
    {
        if (sparkline == ConsensusSparkline)
        {
            return series.Points.Select(point => (double)point.Consensus).ToArray();
        }

        if (sparkline == RoleDriftSparkline)
        {
            return series.Points.Select(point => (double)point.RoleDrift).ToArray();
        }

        if (sparkline == UnsupportedClaimsSparkline)
        {
            return series.Points.Select(point => (double)point.UnsupportedClaims).ToArray();
        }

        if (sparkline == EvidencePressureSparkline)
        {
            return series.Points.Select(point => (double)point.EvidencePressure).ToArray();
        }

        if (sparkline == NarrativeHeatSparkline)
        {
            return series.Points.Select(point => (double)point.NarrativeHeat).ToArray();
        }

        return series.Points.Select(point => (double)point.Friction).ToArray();
    }

    private static string FormatDiagnosticTrend(int current, int? previous)
    {
        if (previous is null)
        {
            return "new";
        }

        var delta = current - previous.Value;
        if (delta == 0)
        {
            return "0";
        }

        return delta > 0
            ? $"+{delta}"
            : $"-{Math.Abs(delta)}";
    }

    private static int FrictionScore(string label)
    {
        return label switch
        {
            "Healthy" => 35,
            "Productive Conflict" => 58,
            "Too Cold" => 12,
            "Harmony Risk" => 76,
            "Theatre Risk" => 82,
            "Evidence-Starved" => 88,
            "Role Drift" => 74,
            "Unsupported Claims Spike" => 86,
            _ => 40
        };
    }

    private static string DiagnosticToolTip(CoreMetricDiagnostic metric)
    {
        var details = metric.Details.Count == 0
            ? "No detail available."
            : string.Join(Environment.NewLine, metric.Details.Select(detail => $"- {detail}"));
        return $"{metric.Label} ({metric.Score}){Environment.NewLine}Heuristic live discourse diagnostics, not factual correctness.{Environment.NewLine}{details}";
    }

    private Brush DiagnosticAccentForState(string label)
    {
        return label switch
        {
            "Healthy" => ResourceBrush("PrimaryBorderBrush"),
            "Productive Conflict" => ResourceBrush("AlphaAccentBrush"),
            "Too Cold" => ResourceBrush("BetaAccentBrush"),
            "Harmony Risk" or "Theatre Risk" or "Evidence-Starved" or "Role Drift" or "Unsupported Claims Spike" => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private Brush DiagnosticAccentForRisk(string label)
    {
        return label switch
        {
            "Low" or "Healthy" => ResourceBrush("PrimaryBorderBrush"),
            "Medium" or "Moderate" or "High" => ResourceBrush("BetaAccentBrush"),
            "Collapse Risk" => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private Brush DiagnosticAccentForEvidence(string label)
    {
        return label switch
        {
            "Strong" => ResourceBrush("PrimaryBorderBrush"),
            "Medium" => ResourceBrush("BetaAccentBrush"),
            "Weak" => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private Brush DiagnosticAccentForNarrative(string label)
    {
        return label switch
        {
            "Low" => ResourceBrush("PrimaryBorderBrush"),
            "Medium" or "Rising" => ResourceBrush("AssistBorderBrush"),
            "High" => ResourceBrush("DangerBorderBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private static string NormalizeMatchLockKey(string key)
    {
        var cleaned = string.IsNullOrWhiteSpace(key) ? "" : key.Trim().ToLowerInvariant();
        return cleaned is "alpha" or "beta" or "gamma" or "delta" or "narrator" or "topic" or "global" or "scenario"
            ? cleaned
            : "scenario";
    }

    private static string DisplayLockKey(string key)
    {
        return NormalizeMatchLockKey(key) switch
        {
            "topic" => "Topic",
            "global" => "Global",
            "alpha" => "Alpha",
            "beta" => "Beta",
            "gamma" => "Gamma",
            "delta" => "Delta",
            "narrator" => "Narrator",
            _ => "Scenario"
        };
    }

    private TimeSpan AutoChatCadence()
    {
        var value = (AutoChatCadencePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromMilliseconds(Math.Clamp(seconds, 0.1, 30) * 1000)
            : TimeSpan.FromMilliseconds(1200);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSelectingTheme || ThemePicker.SelectedValue is not string themeId)
        {
            return;
        }

        ApplyTheme(themeId, persist: true, rerender: true);
    }

    private void ApplyTheme(string themeId, bool persist, bool rerender)
    {
        _theme = ThemePalette.Resolve(themeId);
        SetBrush("AppBackgroundBrush", _theme.AppBackground);
        SetBrush("TopBarBrush", _theme.TopBar);
        SetBrush("PanelBrush", _theme.Panel);
        SetBrush("CardBrush", _theme.Card);
        SetBrush("InputBrush", _theme.Input);
        SetBrush("TranscriptHeaderBrush", _theme.Panel);
        SetBrush("TranscriptBodyBrush", _theme.Card);
        SetBrush("ControlBorderBrush", _theme.Border);
        SetBrush("TextBrush", _theme.Text);
        SetBrush("MutedTextBrush", _theme.MutedText);
        SetBrush("PrimaryBrush", _theme.Primary);
        SetBrush("PrimaryBorderBrush", _theme.PrimaryBorder);
        SetBrush("AssistBrush", _theme.Assist);
        SetBrush("AssistBorderBrush", _theme.AssistBorder);
        SetBrush("DangerBrush", _theme.Danger);
        SetBrush("DangerBorderBrush", _theme.DangerBorder);
        SetBrush("DangerTextBrush", _theme.DangerText);
        SetBrush("DisabledBrush", _theme.Disabled);
        SetBrush("DisabledBorderBrush", _theme.DisabledBorder);
        SetBrush("DisabledTextBrush", _theme.DisabledText);
        SetBrush("HoverBorderBrush", _theme.HoverBorder);
        SetBrush("NavHoverBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.18));
        SetBrush("NavActiveBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.24));
        SetBrush("NavPressedBrush", BlendBrush(new SolidColorBrush(_theme.Input), new SolidColorBrush(_theme.PrimaryBorder), 0.12));
        SetBrush("PressedPrimaryBrush", _theme.PressedPrimary);
        SetBrush("OverlayBrush", _theme.Overlay);
        SetBrush("AlphaAccentBrush", _theme.AlphaAccent);
        SetBrush("BetaAccentBrush", _theme.BetaAccent);
        SetBrush("GammaAccentBrush", _theme.GammaAccent);
        SetBrush("DeltaAccentBrush", _theme.DeltaAccent);
        SetBrush("NarratorAccentBrush", _theme.NarratorAccent);
        SetBrush("OperatorAccentBrush", _theme.OperatorAccent);

        if (persist)
        {
            _wpfSettings.ThemeId = themeId;
            _wpfSettingsStore.Save(_wpfSettings);
        }

        UpdateNavigationTheme();
        if (rerender && _activeSession is not null)
        {
            RefreshActiveSession($"Theme applied: {_theme.Name}");
        }
    }

    private void SetBrush(string key, Color color)
    {
        Resources[key] = new SolidColorBrush(color);
    }

    private void SetBrush(string key, Brush brush)
    {
        Resources[key] = brush;
    }

    private Brush ResourceBrush(string key)
    {
        return TryFindResource(key) as Brush ?? Brushes.White;
    }

    private static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        var baseColor = BrushColor(baseBrush, Colors.Transparent);
        var accentColor = BrushColor(accentBrush, baseColor);
        var amount = Math.Clamp(accentAmount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            BlendChannel(baseColor.R, accentColor.R, amount),
            BlendChannel(baseColor.G, accentColor.G, amount),
            BlendChannel(baseColor.B, accentColor.B, amount)));
    }

    private static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static byte BlendChannel(byte baseline, byte accent, double amount)
    {
        return (byte)Math.Round(baseline + ((accent - baseline) * amount));
    }

    private Brush AccentForSpeaker(string speaker)
    {
        return speaker.ToLowerInvariant() switch
        {
            "alpha" => ResourceBrush("AlphaAccentBrush"),
            "beta" => ResourceBrush("BetaAccentBrush"),
            "gamma" => ResourceBrush("GammaAccentBrush"),
            "delta" => ResourceBrush("DeltaAccentBrush"),
            "narrator" => ResourceBrush("NarratorAccentBrush"),
            "operator" => ResourceBrush("OperatorAccentBrush"),
            _ => ResourceBrush("MutedTextBrush")
        };
    }

    private void ArenaNavButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptPanel.Visibility = Visibility.Visible;
        CustomMatchPanel.Visibility = Visibility.Collapsed;
        NewsPanel.Visibility = Visibility.Collapsed;
        UpdateNavigationTheme();
    }

    private void CustomMatchNavButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptPanel.Visibility = Visibility.Collapsed;
        CustomMatchPanel.Visibility = Visibility.Visible;
        NewsPanel.Visibility = Visibility.Collapsed;
        UpdateNavigationTheme();
    }

    private void NewsNavButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptPanel.Visibility = Visibility.Collapsed;
        CustomMatchPanel.Visibility = Visibility.Collapsed;
        NewsPanel.Visibility = Visibility.Visible;
        UpdateNavigationTheme();
    }

    private void TranscriptFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot || TranscriptItems is null)
        {
            return;
        }

        PopulateTranscript(_lastRenderedMessages);
    }

    private void ClearTranscriptSearchButton_Click(object sender, RoutedEventArgs e)
    {
        TranscriptSearchText.Clear();
    }

    private void UpdateNavigationTheme()
    {
        var arenaActive = TranscriptPanel.Visibility == Visibility.Visible;
        var customActive = CustomMatchPanel.Visibility == Visibility.Visible;
        var newsActive = NewsPanel.Visibility == Visibility.Visible;
        ApplyNavigationButtonState(ArenaNavButton, arenaActive);
        ApplyNavigationButtonState(CustomMatchNavButton, customActive);
        ApplyNavigationButtonState(NewsNavButton, newsActive);
        ApplyNavigationButtonState(ExportNavButton, false);
    }

    private void ApplyNavigationButtonState(Button button, bool active)
    {
        button.Background = active
            ? ResourceBrush("NavActiveBrush")
            : Brushes.Transparent;
        button.BorderBrush = active ? ResourceBrush("PrimaryBorderBrush") : Brushes.Transparent;
        button.Foreground = active ? ResourceBrush("TextBrush") : ResourceBrush("MutedTextBrush");
    }

    private void AppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AnimateSettingsGear();
        SetAppSettingsVisible(AppSettingsPanel.Visibility != Visibility.Visible);
    }

    private void CloseAppSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SetAppSettingsVisible(false);
    }

    private void VisualSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRenderingSnapshot
            || AvatarStylePicker is null
            || SystemGlyphStylePicker is null
            || TopStripModePicker is null)
        {
            return;
        }

        var avatarStyle = (AvatarStylePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pack";
        _wpfSettings.AvatarStyle = avatarStyle;
        _wpfSettings.ChampionAvatars = avatarStyle is "pack" or "procedural";
        _wpfSettings.SystemEventGlyphs = (SystemGlyphStylePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "fallback";
        _wpfSettings.TopStripMode = (TopStripModePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "diagnostics";
        _wpfSettings.ShowTranscriptDiagnostics = _wpfSettings.TopStripMode.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
        _wpfSettingsStore.Save(_wpfSettings);
        UpdateTranscriptDashboardLayout(TranscriptDashboardGrid.ActualWidth, force: true);
        UpdateTelemetryTimerState();
        if (_lastRenderedMessages.Count > 0)
        {
            PopulateTranscript(_lastRenderedMessages);
        }
    }

    private string CurrentAvatarStyle()
    {
        var style = _wpfSettings.AvatarStyle?.Trim().ToLowerInvariant();
        return style switch
        {
            "pack" or "procedural" or "simple" or "initials" => style,
            "champion" => "procedural",
            _ => _wpfSettings.ChampionAvatars ? "pack" : "simple"
        };
    }

    private string CurrentTopStripMode()
    {
        var mode = _wpfSettings.TopStripMode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "diagnostics" or "telemetry" or "hidden" => mode,
            _ => _wpfSettings.ShowTranscriptDiagnostics ? "diagnostics" : "hidden"
        };
    }

    private async void ProviderBaseUrlText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        await PersistModelRoutingAsync("Provider base URL saved.", refreshModels: true);
    }

    private async void ProviderBaseUrlText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await PersistModelRoutingAsync("Provider base URL saved.", refreshModels: true);
    }

    private async void ProviderModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingSnapshot || _isUpdatingRoleModelEditor)
        {
            return;
        }

        await PersistModelRoutingAsync("Default model saved.");
    }

    private async void ProviderModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        await PersistModelRoutingAsync("Default model saved.");
    }

    private async void ProviderModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await PersistModelRoutingAsync("Default model saved.");
    }

    private async void ParticipantModelText_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRenderingSnapshot || _isUpdatingRoleModelEditor || sender is not ComboBox comboBox)
        {
            return;
        }

        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.");
    }

    private async void ParticipantModelText_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.");
    }

    private async void ParticipantModelText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ComboBox comboBox)
        {
            return;
        }

        e.Handled = true;
        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.");
    }

    private async Task PersistModelRoutingAsync(string successStatus, bool refreshModels = false)
    {
        if (_isRenderingSnapshot || _isUpdatingRoleModelEditor || _isPersistingModelRouting || _activeSession is null)
        {
            return;
        }

        var baseUrl = ProviderBaseUrlText.Text.Trim();
        var defaultModel = ProviderModelText.Text.Trim();
        SaveRoleModelDrafts();
        UpdateRoleModelSummary();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ProviderTestStatus.Text = "Provider base URL is required.";
            return;
        }

        _isPersistingModelRouting = true;
        try
        {
            await _arenaOperationLock.WaitAsync();
            try
            {
                var snapshot = await _coreSessionStore.LoadSnapshotAsync(_activeSession.Id);
                if (snapshot is null)
                {
                    ProviderTestStatus.Text = $"No snapshot found for session {_activeSession.Id}.";
                    return;
                }

                var existingShared = snapshot.Configs.TryGetValue("shared", out var shared)
                    ? shared
                    : new CoreModelProviderConfig();
                var updatedShared = new CoreModelProviderConfig
                {
                    BaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl),
                    Model = defaultModel,
                    Timeout = existingShared.Timeout,
                    Temperature = existingShared.Temperature,
                    MaxOutputTokens = existingShared.MaxOutputTokens,
                    LastError = existingShared.LastError,
                    LastLatencyMs = existingShared.LastLatencyMs,
                    LastTestOk = existingShared.LastTestOk,
                    Extra = existingShared.Extra
                };

                snapshot.Configs["shared"] = updatedShared;
                SaveRoleModelConfig(snapshot.Configs, "alpha", RoleModel("alpha"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "beta", RoleModel("beta"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "gamma", RoleModel("gamma"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "delta", RoleModel("delta"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "narrator", RoleModel("narrator"), updatedShared);

                await _coreSessionStore.SaveSnapshotAsync(snapshot, _activeSession.Id);
                await _eventLogStore.AppendAsync(_activeSession.Id, "native_model_routing_applied", new
                {
                    updatedShared.BaseUrl,
                    updatedShared.Model,
                    AlphaModel = RoleModel("alpha"),
                    BetaModel = RoleModel("beta"),
                    GammaModel = RoleModel("gamma"),
                    DeltaModel = RoleModel("delta"),
                    NarratorModel = RoleModel("narrator")
                });
            }
            finally
            {
                _arenaOperationLock.Release();
            }

            await RefreshActiveSessionAsync(successStatus);
            ProviderTestStatus.Text = successStatus;
            if (refreshModels)
            {
                await RefreshAdvertisedModelsAsync();
            }

            _ = RefreshProviderReachabilityAsync(force: true);
        }
        finally
        {
            _isPersistingModelRouting = false;
        }
    }

    private void SetAppSettingsVisible(bool visible)
    {
        AppSettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppSettingsButton.ToolTip = visible ? "Hide Settings" : "App Settings";
        if (visible)
        {
            _modelRefreshTimer.Start();
            _ = RefreshAdvertisedModelsAsync();
        }
        else
        {
            _modelRefreshTimer.Stop();
        }
    }

    private void AnimateSettingsGear()
    {
        var animation = new DoubleAnimation(
            SettingsGearRotate.Angle,
            SettingsGearRotate.Angle + 120,
            TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        SettingsGearRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private static bool IsAgentSpeaker(string speakerId)
    {
        return speakerId.Equals("alpha", StringComparison.OrdinalIgnoreCase)
            || speakerId.Equals("beta", StringComparison.OrdinalIgnoreCase)
            || speakerId.Equals("gamma", StringComparison.OrdinalIgnoreCase)
            || speakerId.Equals("delta", StringComparison.OrdinalIgnoreCase);
    }

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private int ParseActiveParticipants()
    {
        var selected = (ActiveParticipantsPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, 1, 4)
            : 3;
    }

    private static void EnsureDeltaAgent(AIArena.Core.Models.ArenaSnapshot snapshot)
    {
        if (snapshot.Engine.Agents.Any(agent => agent.Id.Equals("delta", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var delta = new AIArena.Core.Models.DialogueAgent
        {
            Id = "delta",
            Name = "Delta: Boundary tester",
            Persona = "Boundary tester. Thinking style: identifies limits, misuse cases, escalation paths, and operational failure boundaries. Temperament: calm and exacting. Priority/bias: make constraints explicit before conclusions are accepted. Blind spot: may over-index on edge cases and slow convergence.",
            Active = false,
            Status = "waiting"
        };
        var insertAt = snapshot.Engine.Agents.FindIndex(agent => agent.Id.Equals("gamma", StringComparison.OrdinalIgnoreCase));
        if (insertAt >= 0 && insertAt < snapshot.Engine.Agents.Count - 1)
        {
            snapshot.Engine.Agents.Insert(insertAt + 1, delta);
        }
        else
        {
            snapshot.Engine.Agents.Add(delta);
        }
    }

    private async Task RefreshAdvertisedModelsAsync()
    {
        if (_isRefreshingModels)
        {
            return;
        }

        _isRefreshingModels = true;
        try
        {
            var config = new CoreModelProviderConfig
            {
                BaseUrl = ProviderBaseUrlText.Text.Trim(),
                Model = ProviderModelText.Text.Trim(),
                Timeout = int.TryParse(ProviderTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout)
                    ? Math.Clamp(timeout, 1, 3600)
                    : 5,
                Temperature = 0,
                MaxOutputTokens = 16
            };
            var result = await _providerHealth.ListModelsAsync(config);
            if (result.Ok)
            {
                _advertisedModels = result.Models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray();
                ProviderModelsStatus.Text = $"{_advertisedModels.Count} advertised models found. Refreshes every 5s while settings are open.";
                _isUpdatingRoleModelEditor = true;
                try
                {
                    UpdateModelComboItems(ProviderModelText);
                    foreach (var comboBox in RoleModelComboBoxes())
                    {
                        UpdateModelComboItems(comboBox);
                    }
                }
                finally
                {
                    _isUpdatingRoleModelEditor = false;
                }
            }
            else
            {
                ProviderModelsStatus.Text = $"Model list unavailable: {result.Error}";
            }
        }
        finally
        {
            _isRefreshingModels = false;
        }
    }

    private void UpdateModelComboItems(ComboBox comboBox)
    {
        if (comboBox is null)
        {
            return;
        }

        var current = comboBox.Text;
        var values = _advertisedModels
            .Append(current)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        comboBox.ItemsSource = values;
        comboBox.Text = current;
    }

    private void UpdateRoleModelEditors()
    {
        _isUpdatingRoleModelEditor = true;
        try
        {
            foreach (var comboBox in RoleModelComboBoxes())
            {
                UpdateModelComboItems(comboBox);
                comboBox.Text = RoleModel(comboBox.Tag?.ToString() ?? "");
            }
        }
        finally
        {
            _isUpdatingRoleModelEditor = false;
        }
    }

    private void UpdateRoleModelSummary()
    {
        if (RoleModelSummaryText is null)
        {
            return;
        }

        var defaultModel = ProviderModelText.Text.Trim();
        RoleModelSummaryText.Text = string.Join(
            Environment.NewLine,
            $"Default: {DisplayRoleModel(defaultModel, "not selected")}",
            $"Alpha: {DisplayParticipantModel(RoleModel("alpha"), defaultModel)}",
            $"Beta: {DisplayParticipantModel(RoleModel("beta"), defaultModel)}",
            $"Gamma: {DisplayParticipantModel(RoleModel("gamma"), defaultModel)}",
            $"Delta: {DisplayParticipantModel(RoleModel("delta"), defaultModel)}",
            $"Narrator: {DisplayParticipantModel(RoleModel("narrator"), defaultModel)}");
    }

    private void SaveRoleModelDrafts()
    {
        foreach (var comboBox in RoleModelComboBoxes())
        {
            SaveRoleModelDraft(comboBox);
        }

        UpdateRoleModelSummary();
    }

    private void SaveRoleModelDraft(ComboBox comboBox)
    {
        if (comboBox.Tag is string key)
        {
            _roleModels[key] = comboBox.Text.Trim();
        }
    }

    private IEnumerable<ComboBox> RoleModelComboBoxes()
    {
        yield return AlphaRoleModelText;
        yield return BetaRoleModelText;
        yield return GammaRoleModelText;
        yield return DeltaRoleModelText;
        yield return NarratorRoleModelText;
    }

    private IReadOnlyList<string> SelectedModelsForPreload()
    {
        var models = new List<string>();
        var defaultModel = ProviderModelText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(defaultModel))
        {
            models.Add(defaultModel);
        }

        foreach (var key in RoleModelKeys())
        {
            var model = RoleModel(key);
            if (!string.IsNullOrWhiteSpace(model))
            {
                models.Add(model);
            }
        }

        return models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatPreloadResults(IReadOnlyList<ModelPreloadResult> results)
    {
        return string.Join(
            Environment.NewLine,
            results.Select(result =>
            {
                if (string.IsNullOrWhiteSpace(result.Model))
                {
                    return $"{TitleCaseStatus(result.Status)}: {result.Detail}";
                }

                return $"{TitleCaseStatus(result.Status)}: {result.Model} - {result.Detail}";
            }));
    }

    private static string TitleCaseStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status)
            ? "Status"
            : string.Concat(status[..1].ToUpperInvariant(), status[1..]);
    }

    private void PopulatePreloadModelBadges(IReadOnlyList<ModelPreloadResult> results)
    {
        PreloadModelsItems.Children.Clear();
        foreach (var result in results)
        {
            var accent = result.Status.ToLowerInvariant() switch
            {
                "loaded" or "ready" => ResourceBrush("PrimaryBorderBrush"),
                "skipped" => ResourceBrush("MutedTextBrush"),
                "unsupported" => ResourceBrush("BetaAccentBrush"),
                _ => ResourceBrush("DangerTextBrush")
            };
            var label = string.IsNullOrWhiteSpace(result.Model)
                ? TitleCaseStatus(result.Status)
                : $"{ShortModelName(result.Model)} - {TitleCaseStatus(result.Status)}";

            PreloadModelsItems.Children.Add(new Border
            {
                Background = BlendBrush(ResourceBrush("InputBrush"), accent, result.IsFailure ? 0.2 : 0.12),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = $"{result.Model}{Environment.NewLine}{result.Detail}",
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = accent,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                }
            });
        }
    }

    private static string ShortModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "-";
        }

        var trimmed = model.Trim();
        return trimmed.Length <= 28 ? trimmed : string.Concat(trimmed.AsSpan(0, 25), "...");
    }

    private string RoleModel(string key)
    {
        return _roleModels.TryGetValue(key, out var model) ? model.Trim() : "";
    }

    private static IEnumerable<string> RoleModelKeys()
    {
        yield return "alpha";
        yield return "beta";
        yield return "gamma";
        yield return "delta";
        yield return "narrator";
    }

    private static void SaveRoleModelConfig(IDictionary<string, CoreModelProviderConfig> configs, string key, string model, CoreModelProviderConfig shared)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            configs.Remove(key);
            return;
        }

        configs[key] = new CoreModelProviderConfig
        {
            BaseUrl = shared.BaseUrl,
            Model = model,
            Timeout = shared.Timeout,
            Temperature = shared.Temperature,
            MaxOutputTokens = shared.MaxOutputTokens,
            LastError = configs.TryGetValue(key, out var existing) ? existing.LastError : "",
            LastLatencyMs = configs.TryGetValue(key, out existing) ? existing.LastLatencyMs : 0,
            LastTestOk = configs.TryGetValue(key, out existing) && existing.LastTestOk
        };
    }

    private static CoreModelProviderConfig ProviderHealthProbeConfig(CoreModelProviderConfig source)
    {
        return new CoreModelProviderConfig
        {
            BaseUrl = source.BaseUrl,
            Model = source.Model,
            Timeout = Math.Clamp(Math.Min(source.Timeout, 3), 1, 3),
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            LastError = source.LastError,
            LastLatencyMs = source.LastLatencyMs,
            LastTestOk = source.LastTestOk,
            Extra = source.Extra
        };
    }

    private static CoreModelProviderConfig CopyProviderConfigWithStatus(CoreModelProviderConfig source, bool online, string error, int latencyMs)
    {
        return new CoreModelProviderConfig
        {
            BaseUrl = source.BaseUrl,
            Model = source.Model,
            Timeout = source.Timeout,
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            LastError = error,
            LastLatencyMs = latencyMs,
            LastTestOk = online,
            Extra = source.Extra
        };
    }

    private static async Task<ProviderSocketProbe> ProbeProviderSocketAsync(CoreModelProviderConfig config)
    {
        var baseUrl = ModelProviderHealthService.NormalizeBaseUrl(config.BaseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return new ProviderSocketProbe(false, 0, $"Provider URL is invalid: {baseUrl}");
        }

        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            watch.Stop();
            return new ProviderSocketProbe(true, (int)watch.ElapsedMilliseconds, "");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            watch.Stop();
            return new ProviderSocketProbe(
                false,
                (int)watch.ElapsedMilliseconds,
                $"Provider unreachable at {baseUrl}. Start LM Studio server or check the base URL.");
        }
    }

    private sealed record ProviderSocketProbe(bool Ok, int LatencyMs, string Error);

    private static string DisplayRoleModel(string model)
    {
        return DisplayRoleModel(model, "default");
    }

    private static string DisplayRoleModel(string model, string fallback)
    {
        return string.IsNullOrWhiteSpace(model) ? fallback : model;
    }

    private static string DisplayParticipantModel(string model, string defaultModel)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        return string.IsNullOrWhiteSpace(defaultModel)
            ? "default"
            : $"default ({ShortModelName(defaultModel)})";
    }

    private static string DisplayInternetMode(string mode)
    {
        return string.IsNullOrWhiteSpace(mode) ? "manual" : mode.Replace('_', ' ');
    }

    private static string DisplayInternetScope(string scope)
    {
        return scope.Equals("open_web", StringComparison.OrdinalIgnoreCase) ? "open web" : "trusted sources";
    }

    private static string InternetModeDescription(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "off" => "all internet fetches blocked",
            "model_requested" => "approved model tool requests only",
            "auto" => "manual buttons plus automatic/model-requested fetches",
            _ => "manual user actions only"
        };
    }

    private void ApplyNativeChromeColor()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkGray = ColorRef(0x20, 0x20, 0x20);
        var borderGray = ColorRef(0x58, 0x58, 0x58);
        var white = ColorRef(0xFF, 0xFF, 0xFF);
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.BorderColor, ref borderGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.CaptionColor, ref darkGray, Marshal.SizeOf<int>());
        _ = DwmSetWindowAttribute(handle, DwmWindowAttribute.TextColor, ref white, Marshal.SizeOf<int>());
    }

    private static int ColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int attributeValue, int attributeSize);

    private enum DwmWindowAttribute
    {
        BorderColor = 34,
        CaptionColor = 35,
        TextColor = 36
    }

}
