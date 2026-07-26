using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class AgentRunbookService
{
    internal const int MaxCheckpoints = 24;

    private static readonly (string Id, string Owner, string Title, string[] DependsOn)[] StepTemplates =
    [
        ("plan", "Planner", "Plan", []),
        ("review", "Reviewer", "Review", ["plan"]),
        ("build", "Builder", "Build proposal", ["review"]),
        ("approval", "Operator", "Approve command", ["build"]),
        ("execute", "PowerShell", "Execute", ["approval"]),
        ("verify", "Reviewer", "Verify result", ["execute"])
    ];

    public WpfAgentRunbookState State { get; private set; } = new();

    public bool HasActiveRun => !string.IsNullOrWhiteSpace(State.RunId);

    public void Restore(WpfAgentRunbookState? saved, string workspacePath, DateTimeOffset now)
    {
        if (saved is null
            || string.IsNullOrWhiteSpace(saved.RunId)
            || !AgentWorkspaceConversationStore.WorkspaceMatches(saved.WorkspacePath, workspacePath))
        {
            State = new WpfAgentRunbookState { WorkspacePath = workspacePath };
            return;
        }

        State = saved;
        var interrupted = false;
        foreach (var step in State.Steps.Where(step => step.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            step.Status = "Blocked";
            step.Evidence = "Interrupted by application restart; resume explicitly.";
            step.UpdatedAt = now;
            interrupted = true;
        }

        if (interrupted)
        {
            State.Status = "Interrupted";
            AddCheckpoint("interrupted", "Run restored after an interrupted active step.", now);
        }
    }

    public void Begin(string workspacePath, string objective, bool builderOnly, DateTimeOffset now)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var runId = $"run-{now:yyyyMMddHHmmss}-{suffix}";
        State = new WpfAgentRunbookState
        {
            RunId = runId,
            WorkspacePath = workspacePath,
            Objective = Clean(objective, 1200),
            Status = "Running",
            CreatedAt = now,
            UpdatedAt = now,
            Steps = StepTemplates.Select((template, index) => new WpfAgentRunbookStep
            {
                Id = template.Id,
                Sequence = index + 1,
                Owner = template.Owner,
                Title = template.Title,
                Status = builderOnly && template.Id is "plan" or "review" ? "Skipped" : "Pending",
                DependsOn = template.DependsOn.ToList(),
                UpdatedAt = now
            }).ToList()
        };
        AddCheckpoint("started", "Agent runbook created.", now);
    }

    public void Reset(string workspacePath)
    {
        State = new WpfAgentRunbookState { WorkspacePath = workspacePath };
    }

    public void UpdateStep(string stepId, string status, string evidence, DateTimeOffset now)
    {
        var step = State.Steps.FirstOrDefault(item => item.Id.Equals(stepId, StringComparison.OrdinalIgnoreCase));
        if (step is null)
        {
            return;
        }

        step.Status = NormalizeStatus(status);
        step.Evidence = Clean(evidence, 2000);
        step.UpdatedAt = now;
        State.UpdatedAt = now;
        State.Status = step.Status switch
        {
            "Running" => "Running",
            "Waiting" => stepId == "approval" ? "Awaiting approval" : "Waiting",
            "Blocked" or "Failed" => "Blocked",
            _ => State.Status
        };
    }

    public void MarkApprovalReady(string evidence, DateTimeOffset now)
    {
        UpdateStep("approval", "Waiting", evidence, now);
        AddCheckpoint("approval", "Command is waiting for operator approval.", now, evidence);
    }

    public void MarkApprovalRejected(string evidence, DateTimeOffset now)
    {
        UpdateStep("approval", "Blocked", evidence, now);
        AddCheckpoint("rejected", "Command proposal rejected.", now, evidence);
    }

    public void MarkExecutionStarted(string evidence, DateTimeOffset now)
    {
        UpdateStep("approval", "Completed", "Command approved.", now);
        UpdateStep("execute", "Running", evidence, now);
        AddCheckpoint("executing", "Approved command started.", now, evidence);
    }

    public void MarkExecutionFinished(bool ok, bool canceled, string receipt, DateTimeOffset now)
    {
        UpdateStep("execute", ok ? "Completed" : canceled ? "Blocked" : "Failed", receipt, now);
        UpdateStep("verify", ok ? "Waiting" : "Blocked", ok ? "Verification is the next durable step." : "Repair or retry before verification.", now);
        State.Status = ok ? "Needs verification" : "Blocked";
        State.UpdatedAt = now;
        AddCheckpoint(ok ? "receipt" : "execution-failed", ok ? "File receipt captured; verification is pending." : "Command did not complete successfully.", now, receipt);
    }

    public void MarkVerificationStaged(string evidence, DateTimeOffset now)
    {
        UpdateStep("verify", "Waiting", evidence, now);
        State.Status = "Needs verification";
        AddCheckpoint("verification", "Verification prompt staged.", now, evidence);
    }

    public void MarkCompleted(string evidence, DateTimeOffset now)
    {
        UpdateStep("verify", "Completed", evidence, now);
        State.Status = "Completed";
        State.UpdatedAt = now;
        AddCheckpoint("completed", "Runbook completed.", now, evidence);
    }

    public void MarkConsultationCompleted(string evidence, DateTimeOffset now)
    {
        foreach (var id in new[] { "approval", "execute", "verify" })
        {
            UpdateStep(id, "Skipped", "No workspace command was required for this consultation.", now);
        }

        State.Status = "Completed";
        State.UpdatedAt = now;
        AddCheckpoint("completed", "Consultation run completed without workspace execution.", now, evidence);
    }

    public void MarkInterrupted(string evidence, DateTimeOffset now)
    {
        foreach (var step in State.Steps.Where(step => step.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            UpdateStep(step.Id, "Blocked", evidence, now);
        }

        State.Status = "Interrupted";
        State.UpdatedAt = now;
        AddCheckpoint("interrupted", "Runbook stopped before completion.", now, evidence);
    }

    public void AddCheckpoint(string kind, string summary, DateTimeOffset now, string evidence = "")
    {
        if (!HasActiveRun)
        {
            return;
        }

        var sequence = State.Checkpoints.Count == 0 ? 1 : State.Checkpoints.Max(item => item.Sequence) + 1;
        State.Checkpoints.Add(new WpfAgentRunbookCheckpoint
        {
            Id = $"cp-{sequence:000}",
            Sequence = sequence,
            Kind = Clean(kind, 80),
            Summary = Clean(summary, 600),
            Evidence = Clean(evidence, 2000),
            CreatedAt = now
        });
        if (State.Checkpoints.Count > MaxCheckpoints)
        {
            State.Checkpoints.RemoveRange(0, State.Checkpoints.Count - MaxCheckpoints);
        }

        State.UpdatedAt = now;
    }

    public object ControlState => new
    {
        State.RunId,
        State.WorkspacePath,
        State.Objective,
        State.Status,
        State.CreatedAt,
        State.UpdatedAt,
        Steps = State.Steps.Select(step => new
        {
            step.Id,
            step.Sequence,
            step.Owner,
            step.Title,
            step.Status,
            step.DependsOn,
            step.Evidence,
            step.UpdatedAt
        }).ToArray(),
        Checkpoints = State.Checkpoints.ToArray()
    };

    public static string PhaseStepId(string roleId)
    {
        return roleId.Trim().ToLowerInvariant() switch
        {
            "planner" => "plan",
            "reviewer" => "review",
            "builder" => "build",
            _ => roleId.Trim().ToLowerInvariant()
        };
    }

    public static bool IsGeneratedContinuationPrompt(string prompt)
    {
        return prompt.Contains("Latest work brief:", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Rescue this Agent run", StringComparison.OrdinalIgnoreCase)
            || prompt.StartsWith("Verify the app", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Recommended next action:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStatus(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "done" or "complete" or "completed" => "Completed",
            "running" => "Running",
            "waiting" or "ready" or "staged" => "Waiting",
            "skipped" => "Skipped",
            "error" or "failed" => "Failed",
            "blocked" or "rejected" or "cancelled" or "canceled" => "Blocked",
            _ => "Pending"
        };
    }

    private static string Clean(string? value, int maxChars)
    {
        var clean = value?.Trim() ?? "";
        return clean.Length <= maxChars ? clean : clean[..maxChars];
    }
}
