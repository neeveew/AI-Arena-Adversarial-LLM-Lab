using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class InternetWorkflowCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly TranscriptService transcriptService;
    private readonly InternetToolService internetToolService;
    private readonly CuratedNewsService curatedNewsService;
    private readonly CheckBox useInternetCheckBox;
    private readonly ComboBox modePicker;
    private readonly TextBlock modeHintText;
    private readonly ComboBox sourceScopePicker;
    private readonly TextBox maxResultsText;
    private readonly CheckBox allowParticipantRequestsCheckBox;
    private readonly CheckBox allowNarratorRequestsCheckBox;
    private readonly CheckBox requireApprovalCheckBox;
    private readonly Button curateNewsButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    private bool isUpdating;

    public InternetWorkflowCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        TranscriptService transcriptService,
        InternetToolService internetToolService,
        CuratedNewsService curatedNewsService,
        CheckBox useInternetCheckBox,
        ComboBox modePicker,
        TextBlock modeHintText,
        ComboBox sourceScopePicker,
        TextBox maxResultsText,
        CheckBox allowParticipantRequestsCheckBox,
        CheckBox allowNarratorRequestsCheckBox,
        CheckBox requireApprovalCheckBox,
        Button curateNewsButton,
        Func<CoreSessionSummary?> activeSession,
        Func<ThemePalette> theme,
        Func<bool> isArenaBusy,
        Func<bool> isRenderingSnapshot,
        Func<string, Brush> resourceBrush,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.transcriptService = transcriptService;
        this.internetToolService = internetToolService;
        this.curatedNewsService = curatedNewsService;
        this.useInternetCheckBox = useInternetCheckBox;
        this.modePicker = modePicker;
        this.modeHintText = modeHintText;
        this.sourceScopePicker = sourceScopePicker;
        this.maxResultsText = maxResultsText;
        this.allowParticipantRequestsCheckBox = allowParticipantRequestsCheckBox;
        this.allowNarratorRequestsCheckBox = allowNarratorRequestsCheckBox;
        this.requireApprovalCheckBox = requireApprovalCheckBox;
        this.curateNewsButton = curateNewsButton;
        this.activeSession = activeSession;
        this.theme = theme;
        this.isArenaBusy = isArenaBusy;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.resourceBrush = resourceBrush;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public void InitializeControls()
    {
        useInternetCheckBox.Checked += (_, _) => SyncSettingsUi(preferToggle: true);
        useInternetCheckBox.Unchecked += (_, _) => SyncSettingsUi(preferToggle: true);
        modePicker.SelectionChanged += (_, _) => SyncSettingsUi(preferToggle: false);
        UpdateSettingsHint();
    }

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        useInternetCheckBox.IsChecked = snapshot.InternetEnabled;
        ShellUiHelpers.SelectComboTag(modePicker, snapshot.InternetMode);
        ShellUiHelpers.SelectComboTag(sourceScopePicker, snapshot.InternetSourceScope);
        maxResultsText.Text = snapshot.InternetMaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture);
        allowParticipantRequestsCheckBox.IsChecked = snapshot.AllowParticipantInternetRequests;
        allowNarratorRequestsCheckBox.IsChecked = snapshot.AllowNarratorInternetRequests;
        requireApprovalCheckBox.IsChecked = snapshot.RequireInternetApproval;
    }

    public void UpdateBusyState(bool busy, bool autoChatRunning)
    {
        curateNewsButton.IsEnabled = !busy || autoChatRunning;
        useInternetCheckBox.IsEnabled = !busy;
        modePicker.IsEnabled = !busy;
        sourceScopePicker.IsEnabled = !busy;
        maxResultsText.IsEnabled = !busy;
        allowParticipantRequestsCheckBox.IsEnabled = !busy;
        allowNarratorRequestsCheckBox.IsEnabled = !busy;
        requireApprovalCheckBox.IsEnabled = !busy;
    }

    public void SyncSettingsUi(bool preferToggle)
    {
        if (isRenderingSnapshot() || isUpdating)
        {
            return;
        }

        isUpdating = true;
        try
        {
            var mode = ShellUiHelpers.SelectedComboTag(modePicker, "manual");
            if (preferToggle)
            {
                if (useInternetCheckBox.IsChecked == true)
                {
                    if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        ShellUiHelpers.SelectComboTag(modePicker, "auto");
                    }
                }
                else
                {
                    ShellUiHelpers.SelectComboTag(modePicker, "off");
                }
            }
            else
            {
                useInternetCheckBox.IsChecked = !mode.Equals("off", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            isUpdating = false;
        }

        UpdateSettingsHint();
    }

    public void UpdateSettingsHint()
    {
        var useInternet = useInternetCheckBox.IsChecked == true;
        var mode = ShellUiHelpers.SelectedComboTag(modePicker, "manual");
        if (!useInternet || mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            modeHintText.Text = "Internet is disabled. Agents will not be prompted to request tools, and manual internet actions are blocked.";
            modeHintText.Foreground = resourceBrush("DangerTextBrush");
            return;
        }

        modeHintText.Text = mode switch
        {
            "manual" => "Manual allows user-triggered internet actions. Agents will not request tools.",
            "model_requested" => "Model Requested allows participant/narrator tool calls when requester permissions allow it.",
            "auto" => "Auto allows manual actions, model-requested tool calls, and scheduled drops when configured.",
            _ => "Internet is enabled."
        };
        modeHintText.Foreground = resourceBrush("MutedTextBrush");
    }

    public async Task CurateNewsAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runArenaBusyAsync("Curating news...", curateNewsButton, async () =>
        {
            var result = await curatedNewsService.CurateNowAsync(session.Id);
            var status = result.Ok && result.Message is not null
                ? $"Curated news added at turn {result.Message.Turn}."
                : $"Curate news failed: {result.Error}";
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task ApproveInternetRequestAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null || message.Turn <= 0)
        {
            return;
        }

        await runArenaBusyAsync($"Approving internet request from turn {message.Turn}...", null, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                setArenaRunStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            var original = TranscriptService.FindMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (original is null)
            {
                setArenaRunStatus($"Could not find approval turn {message.Turn}.");
                return;
            }

            var request = transcriptService.InternetRequestFor(original);
            if (request is null)
            {
                setArenaRunStatus($"Approval turn {message.Turn} has no valid internet request.");
                return;
            }

            var result = await internetToolService.ExecuteAsync(snapshot, request, session.Id);
            var replacement = transcriptService.CreateInternetToolMessage(request, result, original.Turn);
            if (!transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
            {
                setArenaRunStatus($"Could not replace approval turn {message.Turn}.");
                return;
            }

            snapshot.Engine.LastError = result.Ok ? "" : result.Error;
            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(
                session.Id,
                result.Ok ? "native_internet_request_approved" : "native_internet_request_approval_failed",
                new { message.Turn, request.Tool, request.Query, request.Url, result.Ok, result.Error });
            await refreshActiveSessionAsync(result.Ok
                ? $"Approved internet request at turn {message.Turn}."
                : $"Internet request failed after approval: {result.Error}");
        }, false);
    }

    public async Task<bool> HandleInternetApprovalDialogAsync(DialogueMessage approvalMessage, bool resumeAutoChat)
    {
        if (activeSession() is null)
        {
            return false;
        }

        var request = transcriptService.InternetRequestFor(approvalMessage);
        if (request is null)
        {
            return false;
        }

        SetBothStatuses(resumeAutoChat ? "Auto Chat paused for internet approval." : "Internet approval required.");
        var choice = InternetApprovalDialog.Show(owner, theme(), request);
        if (choice == InternetApprovalChoice.AlwaysApprove)
        {
            requireApprovalCheckBox.IsChecked = false;
        }

        return choice switch
        {
            InternetApprovalChoice.ApproveOnce => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: false, approve: true),
            InternetApprovalChoice.AlwaysApprove => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: true, approve: true),
            _ => await ResolveInternetApprovalAsync(approvalMessage, alwaysApprove: false, approve: false)
        };
    }

    public async Task<bool> ResolveInternetApprovalAsync(DialogueMessage approvalMessage, bool alwaysApprove, bool approve)
    {
        var session = activeSession();
        if (session is null)
        {
            return false;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
        if (snapshot is null)
        {
            setArenaRunStatus($"No snapshot found for session {session.Id}.");
            return false;
        }

        var original = TranscriptService.FindMessage(snapshot, approvalMessage.Turn, approvalMessage.SpeakerId, approvalMessage.CreatedAt);
        if (original is null)
        {
            setArenaRunStatus($"Could not find approval turn {approvalMessage.Turn}.");
            return false;
        }

        var request = transcriptService.InternetRequestFor(original);
        if (request is null)
        {
            setArenaRunStatus($"Approval turn {approvalMessage.Turn} has no valid internet request.");
            return false;
        }

        if (alwaysApprove)
        {
            snapshot.Engine.ModelRss.RequireApproval = false;
        }

        if (!approve)
        {
            var rejected = RejectedApprovalMessage(original);
            if (!transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, rejected))
            {
                setArenaRunStatus($"Could not reject approval turn {approvalMessage.Turn}.");
                return false;
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_internet_request_rejected", new { original.Turn, request.Tool, request.Query, request.Url });
            await refreshActiveSessionAsync($"Rejected internet request at turn {approvalMessage.Turn}.");
            return true;
        }

        var result = await internetToolService.ExecuteAsync(snapshot, request, session.Id);
        var replacement = transcriptService.CreateInternetToolMessage(request, result, original.Turn);
        if (!transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
        {
            setArenaRunStatus($"Could not replace approval turn {approvalMessage.Turn}.");
            return false;
        }

        snapshot.Engine.LastError = result.Ok ? "" : result.Error;
        await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
        await eventLogStore.AppendAsync(
            session.Id,
            result.Ok ? "native_internet_request_approved" : "native_internet_request_approval_failed",
            new { original.Turn, request.Tool, request.Query, request.Url, result.Ok, result.Error, AlwaysApprove = alwaysApprove });
        await refreshActiveSessionAsync(result.Ok
            ? $"Approved internet request at turn {approvalMessage.Turn}."
            : $"Internet request failed after approval: {result.Error}");
        return true;
    }

    public async Task RejectInternetRequestAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null || message.Turn <= 0)
        {
            return;
        }

        await MutateTranscriptAsync($"Rejected internet request at turn {message.Turn}.", async snapshot =>
        {
            var original = TranscriptService.FindMessage(snapshot, message.Turn, message.SpeakerId, message.CreatedAt);
            if (original is null)
            {
                setLoadStatus($"Could not find approval turn {message.Turn}.");
                return false;
            }

            var replacement = RejectedApprovalMessage(original);
            if (!transcriptService.ReplaceMessage(snapshot, original.Turn, original.SpeakerId, original.CreatedAt, replacement))
            {
                setLoadStatus($"Could not reject approval turn {message.Turn}.");
                return false;
            }

            await eventLogStore.AppendAsync(session.Id, "native_internet_request_rejected", new { message.Turn, message.InternetTool, message.InternetQuery, message.InternetUrl });
            return true;
        });
    }

    private async Task MutateTranscriptAsync(string successStatus, Func<ArenaSnapshot, Task<bool>> mutation)
    {
        var session = activeSession();
        if (session is null)
        {
            return;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
        if (snapshot is null)
        {
            setLoadStatus($"No snapshot found for session {session.Id}.");
            return;
        }

        if (!await mutation(snapshot))
        {
            return;
        }

        await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
        await refreshActiveSessionAsync(successStatus);
    }

    private void SetBothStatuses(string status)
    {
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    private static DialogueMessage RejectedApprovalMessage(DialogueMessage original)
    {
        return new DialogueMessage
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
    }


}
