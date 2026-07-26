using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static void CollaborateHistoryKeepsDuplicateDisplayTitles()
{
    Require(CollaborateCoordinator.ConversationMutationAllowed(isRunning: false), "idle collaborate should allow conversation switching");
    Require(!CollaborateCoordinator.ConversationMutationAllowed(isRunning: true), "running collaborate should block conversation switching");

    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();
    var firstCreated = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
    var secondCreated = firstCreated.AddMinutes(5);
    var conversations = new List<CollaborateCoordinator.CollaborateConversation>();

    CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        firstId,
        [new CollaborateCoordinator.CollaborateExchange("Review this plan: add animated robots", "answer-a", [])],
        firstCreated);
    CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        secondId,
        [new CollaborateCoordinator.CollaborateExchange("Review this plan: improve LM Studio setup", "answer-b", [])],
        secondCreated);

    Require(conversations.Count == 2, "same generated recent title should not remove a different conversation");
    Require(conversations[0].Id == secondId, "newest duplicate-title conversation should appear first");
    Require(conversations[1].Id == firstId, "older duplicate-title conversation should be retained");
    Require(conversations[0].Title == "Review plan", "display title shortcut should remain stable");
    Require(conversations[1].Title == "Review plan", "duplicate display titles should be allowed");

    var refreshedAt = secondCreated.AddMinutes(10);
    CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        firstId,
        [new CollaborateCoordinator.CollaborateExchange("Review this plan: add animated robots", "answer-a2", [])],
        refreshedAt);

    Require(conversations.Count == 2, "updating an existing conversation should replace only that id");
    Require(conversations[0].Id == firstId, "updated conversation should move to the top");
    Require(conversations[0].CreatedAt == firstCreated, "updating should preserve original created time");
    Require(conversations[0].UpdatedAt == refreshedAt, "updating should refresh updated time");
    Require(conversations[0].Exchanges[0].Answer == "answer-a2", "updated conversation should replace saved exchanges");
}

static void CollaborateHistoryFiltersEmptyExchangesBeforeCap()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-collab-history-cap", Guid.NewGuid().ToString("N"));
    var store = new CollaborateHistoryStore(Path.Combine(root, "history.json"));
    try
    {
        var conversations = new List<CollaborateHistoryConversation>();
        var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 30; index++)
        {
            conversations.Add(new CollaborateHistoryConversation
            {
                Id = Guid.NewGuid(),
                Title = $"Empty {index}",
                CreatedAt = now.AddMinutes(index),
                UpdatedAt = now.AddMinutes(index),
                Exchanges =
                [
                    new CollaborateHistoryExchange
                    {
                        Prompt = "   ",
                        Answer = ""
                    }
                ]
            });
        }

        conversations.Add(new CollaborateHistoryConversation
        {
            Id = Guid.NewGuid(),
            Title = "Older valid chat",
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1),
            Exchanges =
            [
                new CollaborateHistoryExchange
                {
                    Prompt = "Keep this valid exchange",
                    Answer = "Still useful."
                }
            ]
        });

        store.Save(conversations);
        var loaded = store.Load();

        Require(loaded.Count == 1, "empty exchanges should be removed before the history cap is applied");
        Require(loaded[0].Title == "Older valid chat", "valid older chats should survive invalid newer history noise");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CollaborateHistorySavePreservesCallerObjects()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-collab-history-immutability", Guid.NewGuid().ToString("N"));
    var store = new CollaborateHistoryStore(Path.Combine(root, "history.json"));
    try
    {
        var emptyExchange = new CollaborateHistoryExchange
        {
            Prompt = "   ",
            Answer = ""
        };
        var validExchange = new CollaborateHistoryExchange
        {
            Prompt = "Keep this prompt",
            Answer = "Keep this answer.",
            TraceSteps =
            [
                new CollaborateHistoryStep
                {
                    RoleId = "alpha",
                    RoleName = "Alpha",
                    Label = "Review",
                    Text = "Looks good.",
                    Ok = true,
                    TotalTokens = 12
                }
            ]
        };
        var conversation = new CollaborateHistoryConversation
        {
            Id = Guid.NewGuid(),
            Title = "  Mutable title  ",
            CreatedAt = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 6, 11, 9, 5, 0, TimeSpan.Zero),
            Exchanges = [emptyExchange, validExchange],
            MemoryNotes = [" saved note ", "SAVED NOTE"]
        };

        store.Save([conversation]);
        var loaded = store.Load().Single();

        Require(conversation.Title == "  Mutable title  ", "history save should not trim the caller conversation title in place");
        Require(conversation.Exchanges.Count == 2, "history save should not remove caller exchanges in place");
        Require(ReferenceEquals(conversation.Exchanges[0], emptyExchange), "history save should preserve caller exchange instances");
        Require(conversation.MemoryNotes.SequenceEqual([" saved note ", "SAVED NOTE"]), "history save should not normalize caller memory notes in place");
        Require(loaded.Title == "Mutable title", "saved history should still trim persisted titles");
        Require(loaded.Exchanges.Count == 1, "saved history should still filter empty exchanges");
        Require(loaded.MemoryNotes.SequenceEqual(["saved note"]), "saved history should still normalize persisted memory notes");
        Require(loaded.Exchanges[0].TraceSteps.Count == 1, "saved history should preserve persisted trace steps");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CollaborateHistoryLoadSkipsNullRecords()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-collab-history-null-records", Guid.NewGuid().ToString("N"));
    var historyPath = Path.Combine(root, "history.json");
    var store = new CollaborateHistoryStore(historyPath);
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(historyPath, """
        {
          "conversations": [
            null,
            {
              "id": "00000000-0000-0000-0000-000000000000",
              "title": " Null-tolerant chat ",
              "createdAt": "2026-06-11T09:00:00+00:00",
              "updatedAt": "2026-06-11T09:05:00+00:00",
              "exchanges": [
                null,
                {
                  "prompt": "Keep this prompt",
                  "answer": "Keep this answer.",
                  "traceSteps": [
                    null,
                    {
                      "roleId": "alpha",
                      "roleName": "Alpha",
                      "label": "Review",
                      "text": "Trace survived.",
                      "ok": true,
                      "totalTokens": 9
                    }
                  ]
                }
              ],
              "memoryNotes": [null, " saved note ", "SAVED NOTE"]
            }
          ]
        }
        """);

        var loaded = store.Load();

        Require(store.LastLoadWarning == "", "null history rows should not mark the whole file corrupt");
        Require(loaded.Count == 1, "valid history row should survive null siblings");
        Require(loaded[0].Id != Guid.Empty, "empty history ids should be regenerated");
        Require(loaded[0].Title == "Null-tolerant chat", "loaded history should normalize surviving title");
        Require(loaded[0].Exchanges.Count == 1, "loaded history should skip null exchanges");
        Require(loaded[0].Exchanges[0].TraceSteps.Count == 1, "loaded history should skip null trace steps");
        Require(loaded[0].MemoryNotes.SequenceEqual(["saved note"]), "loaded history should normalize surviving notes");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void TransientCollaborateHistoryReadsPreserveFile()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-collab-history-lock", Guid.NewGuid().ToString("N"));
    var historyPath = Path.Combine(root, "history.json");
    var store = new CollaborateHistoryStore(historyPath);
    try
    {
        store.Save(
        [
            new CollaborateHistoryConversation
            {
                Id = Guid.NewGuid(),
                Title = "Preserve this chat",
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                Exchanges =
                [
                    new CollaborateHistoryExchange { Prompt = "Keep", Answer = "Safe" }
                ]
            }
        ]);
        var original = File.ReadAllText(historyPath);

        using (File.Open(historyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var loaded = store.Load();
            Require(loaded.Count == 0, "a transiently locked history file should return an empty fallback");
        }

        Require(File.Exists(historyPath), "a transient read failure moved the valid history file");
        Require(File.ReadAllText(historyPath) == original, "a transient read failure changed valid history content");
        Require(!Directory.EnumerateFiles(root, "*.corrupt-*", SearchOption.TopDirectoryOnly).Any(), "a transient read failure created a corrupt backup");
        Require(store.LastLoadWarning.Contains("left unchanged", StringComparison.OrdinalIgnoreCase), "transient read warning should explain preservation");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CollaborateSearchFindsSavedChatContent()
{
    var olderId = Guid.NewGuid();
    var newerId = Guid.NewGuid();
    var older = new CollaborateCoordinator.CollaborateConversation(
        olderId,
        "Robot stage plan",
        new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 11, 8, 5, 0, TimeSpan.Zero),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Give the robots legs and keep their speech readable.",
                "Use a collision solver so speakers do not overlap.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed(
                        "alpha",
                        "Alpha strategist",
                        "gemma-3-local",
                        "Gesture pass",
                        "Add a wave gesture while the active speaker jumps.",
                        120,
                        40),
                    CollaborateCoordinator.CollaborateStep.Failed(
                        "beta",
                        "Beta critic",
                        "qwen-local",
                        "Failure review",
                        "Trace error while resolving clip-space bubble placement.")
                ])
        ],
        ["Prioritize stage collision grid"]);
    var newer = new CollaborateCoordinator.CollaborateConversation(
        newerId,
        "LM Studio setup",
        new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 11, 9, 10, 0, TimeSpan.Zero),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Use native LM Studio model loading.",
                "Collision checks still belong in the visual stage.",
                [])
        ],
        ["Watch native model status"]);

    var conversations = new[] { older, newer };
    var collisionResults = CollaborateCoordinator.SearchConversations(conversations, "collision");
    Require(collisionResults.Count == 2, "collaborate search should return one result per matching conversation");
    Require(collisionResults[0].Id == newerId, "collaborate search should order matches newest first");
    Require(collisionResults[1].Id == olderId, "older matching conversations should remain visible");
    Require(collisionResults[1].MatchCount >= 2, "collaborate search should count multiple fields in one conversation");

    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "wave gesture"), "search should match trace text");
    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "gemma-3-local"), "search should match trace model metadata");
    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "Beta critic"), "search should match trace role names");
    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "clip-space"), "search should match trace errors");
    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "stage collision grid"), "search should match memory notes");
    Require(CollaborateCoordinator.ConversationMatchesSearch(older, "Payload: prompt"), "search should match generated run review text");
    Require(!CollaborateCoordinator.ConversationMatchesSearch(older, "totally unrelated"), "search should reject unrelated queries");

    var recent = CollaborateCoordinator.SearchConversations(conversations, "", 1);
    Require(recent.Count == 1 && recent[0].Id == newerId, "blank collaborate search should show recent chats newest first");
    Require(CollaborateCoordinator.SearchConversations(conversations, "missing", 8).Count == 0, "missing collaborate search should return no chats");
}

static void CollaborateRecentChatMetadataSummarizesRuns()
{
    var conversation = new CollaborateCoordinator.CollaborateConversation(
        Guid.NewGuid(),
        "Release plan",
        new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 11, 8, 10, 0, TimeSpan.Zero),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Plan the release",
                "Ship it",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 12, 900),
                    CollaborateCoordinator.CollaborateStep.Completed("beta", "Beta", "model-b", "Critique", "critique", 20, 1300)
                ]),
            new CollaborateCoordinator.CollaborateExchange(
                "Check failure",
                "One model failed.",
                [
                    CollaborateCoordinator.CollaborateStep.Failed("gamma", "Gamma", "model-c", "Evidence", "timeout")
                ])
        ],
        ["Remember installer path"]);

    Require(
        CollaborateCoordinator.ConversationMetaText(conversation) == "2 turns / 3 steps / ~2.2k tok / 1 note / 1 issue",
        "recent chat metadata should summarize turns, steps, tokens, memory, and issues");
    Require(
        CollaborateCoordinator.ConversationMetaText(conversation with { MemoryNotes = [] }) == "2 turns / 3 steps / ~2.2k tok / 1 issue",
        "recent chat metadata should omit empty memory note counts");
}

static void CollaborateRecentCompareSummarizesRunDeltas()
{
    var savedId = Guid.NewGuid();
    var openId = Guid.NewGuid();
    var saved = new CollaborateCoordinator.CollaborateConversation(
        savedId,
        "Release plan v1",
        new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 11, 8, 10, 0, TimeSpan.Zero),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Plan the release | with caveats",
                "Ship it, but manually review the weak trace.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 12, 900),
                    CollaborateCoordinator.CollaborateStep.Failed("beta", "Beta", "model-b", "Critique", "timeout")
                ])
        ],
        ["Remember installer path"]);
    var open = new CollaborateCoordinator.CollaborateConversation(
        openId,
        "Release plan v2",
        new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 6, 11, 9, 15, 0, TimeSpan.Zero),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Plan the release with rollback.",
                "Ship the installer after smoke testing.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 14, 1000),
                    CollaborateCoordinator.CollaborateStep.Completed("beta", "Beta", "model-c", "Critique", "critique", 20, 500)
                ]),
            new CollaborateCoordinator.CollaborateExchange(
                "Add final launch notes.",
                "Ship the release, publish the hash, and keep rollback notes ready.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("narrator", "Narrator", "model-c", "Synthesis", "final", 16, 400)
                ])
        ],
        ["Remember installer path", "Publish hash"]);

    var savedMetrics = CollaborateCoordinator.ConversationMetrics(saved);
    Require(savedMetrics.TurnCount == 1 && savedMetrics.StepCount == 2 && savedMetrics.IssueCount == 1, "conversation metrics should count turns, trace steps, and issues");
    Require(savedMetrics.TotalTokens == 900, "conversation metrics should ignore negative or missing failed-step tokens");
    Require(savedMetrics.Models.SequenceEqual(["model-a", "model-b"]), "conversation metrics should expose sorted model mix");
    Require(CollaborateCoordinator.ConversationReviewState(saved) == "Needs review", "failed trace steps should mark a saved chat for review");
    Require(CollaborateCoordinator.ConversationReviewState(open) == "Ready", "clean traced chats should be ready");
    Require(CollaborateCoordinator.ConversationReviewState(open with { Exchanges = [new CollaborateCoordinator.CollaborateExchange("Prompt", "Answer", [])] }) == "No trace", "trace-free chats should be searchable as no trace");
    Require(CollaborateCoordinator.ConversationReviewState(open with { Exchanges = [new CollaborateCoordinator.CollaborateExchange("Prompt", "", [])] }) == "Needs answer", "blank final answers should be searchable as needs answer");
    Require(CollaborateCoordinator.ConversationStatusBadgeText(saved, isCurrent: true) == "Open now", "current saved chats should prioritize the open badge");
    Require(CollaborateCoordinator.ConversationStatusBadgeText(open, isCurrent: false) == "Ready", "saved rows should expose ready state badges");

    Require(CollaborateCoordinator.SearchConversations([saved], "needs review").Count == 1, "recent search should match triage state");
    Require(CollaborateCoordinator.SearchConversations([open], "model-c").Count == 1, "recent search should match model mix metadata");
    Require(CollaborateCoordinator.HasComparableOpenConversation(savedId, openId, 2), "saved rows should compare against a different open chat");
    Require(CollaborateCoordinator.HasComparableOpenConversation(savedId, null, 2), "saved rows should compare against an unsaved open draft");
    Require(!CollaborateCoordinator.HasComparableOpenConversation(savedId, savedId, 2), "current row should not compare against itself");
    Require(!CollaborateCoordinator.HasComparableOpenConversation(savedId, openId, 0), "empty open chats should not expose compare");

    var summary = CollaborateCoordinator.ConversationComparisonSummary(saved, open);
    Require(summary.Contains("turns +1", StringComparison.Ordinal), "comparison summary should expose turn delta");
    Require(summary.Contains("issues -1", StringComparison.Ordinal), "comparison summary should expose issue delta");
    Require(summary.Contains("tokens +1k", StringComparison.Ordinal), "comparison summary should compact token delta");

    var markdown = CollaborateCoordinator.BuildConversationComparisonMarkdown(saved, open);
    Require(markdown.StartsWith("# AI Arena Collaborate Compare", StringComparison.Ordinal), "comparison markdown should use a stable title");
    Require(markdown.Contains("Saved chat: Release plan v1 (Needs review)", StringComparison.Ordinal), "comparison markdown should name saved chat health");
    Require(markdown.Contains("Open chat: Release plan v2 (Ready)", StringComparison.Ordinal), "comparison markdown should name open chat health");
    Require(markdown.Contains("| Trace issues | 1 | 0 | -1 better |", StringComparison.Ordinal), "comparison markdown should show issue improvement");
    Require(markdown.Contains("Recommendation: Prefer the open chat for fewer trace issues", StringComparison.Ordinal), "comparison markdown should include a readable recommendation");
    Require(markdown.Contains("- Saved: Plan the release \\| with caveats", StringComparison.Ordinal), "comparison markdown should escape table-hostile prompt snippets");

    var result = new CollaborateCoordinator.CollaborateSearchResult(savedId, saved.Title, "Prompt: Plan", saved.UpdatedAt, 1);
    var tooltip = CollaborateCoordinator.ConversationTooltip(saved, result, isCurrent: false, canCompare: true);
    Require(tooltip.Contains("Review: Needs review", StringComparison.Ordinal), "recent tooltip should include triage state");
    Require(tooltip.Contains("Models: model-a, model-b", StringComparison.Ordinal), "recent tooltip should include model mix");
    Require(tooltip.Contains("Right-click for Open, Fork, Repeat, Compare, Copy, or Delete.", StringComparison.Ordinal), "recent tooltip should advertise compare when available");
    Require(
        CollaborateCoordinator.RecentConversationAutomationName(result, saved, isCurrent: false, canCompare: true).Contains("compare available", StringComparison.Ordinal),
        "recent automation name should expose compare availability");
}

static void CollaborateRecentFiltersOrganizeSavedRuns()
{
    var readyId = Guid.NewGuid();
    var reviewId = Guid.NewGuid();
    var answerId = Guid.NewGuid();
    var noTraceId = Guid.NewGuid();
    var redTeamId = Guid.NewGuid();
    var fastId = Guid.NewGuid();
    var now = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
    var ready = new CollaborateCoordinator.CollaborateConversation(
        readyId,
        "Ready release plan",
        now.AddMinutes(-6),
        now.AddMinutes(-6),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Plan the release",
                "Ship it.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 20, 100),
                    CollaborateCoordinator.CollaborateStep.Completed("narrator", "Narrator", "model-a", "Synthesis", "final", 30, 120)
                ])
        ],
        []);
    var review = new CollaborateCoordinator.CollaborateConversation(
        reviewId,
        "Review needed",
        now.AddMinutes(-5),
        now.AddMinutes(-5),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Critique the launch",
                "One issue remains.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 20, 100),
                    CollaborateCoordinator.CollaborateStep.Failed("beta", "Beta", "model-b", "Critique", "timeout")
                ])
        ],
        []);
    var needsAnswer = new CollaborateCoordinator.CollaborateConversation(
        answerId,
        "Needs answer",
        now.AddMinutes(-4),
        now.AddMinutes(-4),
        [new CollaborateCoordinator.CollaborateExchange("Finish this", "", [])],
        []);
    var noTrace = new CollaborateCoordinator.CollaborateConversation(
        noTraceId,
        "Trace-free answer",
        now.AddMinutes(-3),
        now.AddMinutes(-3),
        [new CollaborateCoordinator.CollaborateExchange("Write notes", "Done.", [])],
        []);
    var redTeam = new CollaborateCoordinator.CollaborateConversation(
        redTeamId,
        "Rollback plan",
        now.AddMinutes(-2),
        now.AddMinutes(-2),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Red-team the release and add rollback.",
                "Rollback plan hardened.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Proposal", "proposal", 10, 60),
                    CollaborateCoordinator.CollaborateStep.Completed("beta", "Beta", "model-b", "Attack", "attack", 10, 80),
                    CollaborateCoordinator.CollaborateStep.Completed("gamma", "Gamma", "model-c", "Hardening", "rollback", 10, 90)
                ])
        ],
        ["Remember rollback threshold"]);
    var fast = new CollaborateCoordinator.CollaborateConversation(
        fastId,
        "Fast answer",
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Answer quickly",
                "Direct answer.",
                [CollaborateCoordinator.CollaborateStep.Completed("narrator", "Narrator", "model-n", "Direct answer", "direct", 10, 40)])
        ],
        []);
    var conversations = new[] { ready, review, needsAnswer, noTrace, redTeam, fast };

    Require(CollaborateCoordinator.ConversationModeLabel(ready) == "Team Draft", "draft/synthesis traces should infer Team Draft mode");
    Require(CollaborateCoordinator.ConversationModeLabel(review) == "Critique", "critique traces should infer Critique mode");
    Require(CollaborateCoordinator.ConversationModeLabel(redTeam) == "Red Team", "proposal/attack/hardening traces should infer Red Team mode");
    Require(CollaborateCoordinator.ConversationModeLabel(fast) == "Fast", "direct narrator traces should infer Fast mode");
    Require(CollaborateCoordinator.ConversationModeLabel(noTrace) == "No trace", "trace-free saved chats should infer No trace mode");

    var criteria = CollaborateCoordinator.RecentSearchCriteria("#needs-review rollback");
    Require(criteria.HasToken("#review") && criteria.Text == "rollback", "recent search criteria should split filter tokens from free text");
    Require(CollaborateCoordinator.RecentSearchCriteriaLabel("#red-team") == "Red Team mode", "recent filter labels should be readable");

    Require(CollaborateCoordinator.SearchConversations(conversations, "#ready", 10).Count == 3, "ready filter should include all clean traced chats");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#review", 10).Single().Id == reviewId, "review filter should find issue-bearing saved chats");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#answer", 10).Single().Id == answerId, "needs-answer filter should find blank final answers");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#notrace", 10).Single().Id == noTraceId, "no-trace filter should find trace-free chats");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#memory", 10).Single().Id == redTeamId, "memory filter should find chats with saved notes");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#redteam rollback", 10).Single().Id == redTeamId, "mode filters should combine with free text search");
    Require(CollaborateCoordinator.SearchConversations(conversations, "#compare", 10, readyId, 1).All(result => result.Id != readyId), "compare filter should exclude the open chat itself");

    var facets = CollaborateCoordinator.RecentFacetSnapshot(conversations, readyId, 1);
    Require(facets.Total == 6, "facet snapshot should count all saved chats");
    Require(facets.Ready == 3 && facets.NeedsReview == 1 && facets.NeedsAnswer == 1 && facets.NoTrace == 1, "facet snapshot should count health states");
    Require(facets.WithMemory == 1 && facets.Comparable == 5, "facet snapshot should count memory and compare-ready chats");
    Require(facets.Fast == 1 && facets.TeamDraft == 1 && facets.Critique == 1 && facets.RedTeam == 1, "facet snapshot should count inferred modes");

    var facetSummary = CollaborateCoordinator.RecentFacetSummary(facets, CollaborateCoordinator.RecentSearchCriteria("#memory"));
    Require(facetSummary.Contains("Has memory", StringComparison.Ordinal), "facet summary should name the active lens");
    Require(facetSummary.Contains("3 ready", StringComparison.Ordinal), "facet summary should keep health counts visible");
    Require(facetSummary.Contains("5 compare", StringComparison.Ordinal), "facet summary should expose compare-ready count");

    var summary = CollaborateCoordinator.BuildConversationSummary(redTeam);
    Require(summary.Contains("Mode: Red Team", StringComparison.Ordinal), "copied summary should include inferred mode");
    Require(summary.Contains("Review: Ready", StringComparison.Ordinal), "copied summary should include review state");
    Require(summary.Contains("Models: model-a, model-b, model-c", StringComparison.Ordinal), "copied summary should include model mix");
    Require(CollaborateCoordinator.ConversationTooltip(redTeam, CollaborateCoordinator.SearchConversations([redTeam], "#memory").Single(), false).Contains("Mode: Red Team", StringComparison.Ordinal), "recent tooltip should include inferred mode");
}

static void CollaborateRecentListSummarizesSavedRuns()
{
    var id = Guid.NewGuid();
    var updated = new DateTimeOffset(2026, 6, 11, 8, 10, 0, TimeSpan.Zero);
    var conversation = new CollaborateCoordinator.CollaborateConversation(
        id,
        "Release plan",
        new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
        updated,
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Plan the release",
                "Ship the smaller installer first.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Draft", "draft", 12, 900),
                    CollaborateCoordinator.CollaborateStep.Failed("beta", "Beta", "model-b", "Critique", "timeout")
                ])
        ],
        ["Remember installer path"]);
    var result = new CollaborateCoordinator.CollaborateSearchResult(
        id,
        conversation.Title,
        "Prompt: Plan the release",
        updated,
        2);

    Require(CollaborateCoordinator.RecentListSummary(0, 0, searchActive: false) == "No saved chats", "empty recent list should expose an explicit summary");
    Require(CollaborateCoordinator.RecentListSummary(8, 5, searchActive: false) == "5 recent / 8 saved", "recent list should summarize visible cap against saved count");
    Require(CollaborateCoordinator.RecentListSummary(8, 2, searchActive: true) == "2 shown / 8 saved", "recent search list should summarize filtered count");
    Require(CollaborateCoordinator.LatestPrompt(conversation) == "Plan the release", "latest prompt helper should return the newest saved prompt");

    var summary = CollaborateCoordinator.BuildConversationSummary(conversation);
    Require(summary.StartsWith("AI Arena Collaborate Summary - Release plan", StringComparison.Ordinal), "copied recent summary should use a stable title");
    Require(summary.Contains("Meta: 1 turn / 2 steps / ~900 tok / 1 note / 1 issue", StringComparison.Ordinal), "copied recent summary should include run metadata");
    Require(summary.Contains("Latest prompt: Plan the release", StringComparison.Ordinal), "copied recent summary should include latest prompt");
    Require(summary.Contains("Latest run review:", StringComparison.Ordinal), "copied recent summary should include run review lines");

    var tooltip = CollaborateCoordinator.ConversationTooltip(conversation, result, isCurrent: true);
    Require(tooltip.Contains("Open now", StringComparison.Ordinal), "recent row tooltip should flag the active chat");
    Require(tooltip.Contains("Right-click for Open, Fork, Repeat, Copy, or Delete.", StringComparison.Ordinal), "recent row tooltip should advertise the action menu");
    Require(
        CollaborateCoordinator.RecentConversationAutomationName(result, conversation, isCurrent: true).Contains("2 hits", StringComparison.Ordinal),
        "recent row automation name should expose search hits");
}

static void CollaborateRecentActionsForkAndRepeatChats()
{
    RunStaTest(() =>
    {
        var chatId = Guid.NewGuid();
        var store = new RecordingCollaborateHistoryStore
        {
            LoadConversations =
            [
                new CollaborateHistoryConversation
                {
                    Id = chatId,
                    Title = "Saved release plan",
                    CreatedAt = new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero),
                    UpdatedAt = new DateTimeOffset(2026, 6, 11, 8, 10, 0, TimeSpan.Zero),
                    Exchanges =
                    [
                        new CollaborateHistoryExchange
                        {
                            Prompt = "Plan the release",
                            Answer = "Ship it.",
                            TraceSteps =
                            [
                                new CollaborateHistoryStep
                                {
                                    RoleId = "alpha",
                                    RoleName = "Alpha",
                                    Model = "model-a",
                                    Label = "Draft",
                                    Text = "Draft it.",
                                    Ok = true,
                                    LatencyMs = 10,
                                    TotalTokens = 30
                                }
                            ]
                        },
                        new CollaborateHistoryExchange
                        {
                            Prompt = "Check risks",
                            Answer = "Add rollback.",
                            TraceSteps = []
                        }
                    ],
                    MemoryNotes = ["Prefer local models"]
                }
            ]
        };
        var promptText = new TextBox();
        var statusText = new TextBlock();
        var recentItems = new StackPanel();
        var shellStatus = "";
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("ok"),
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            message => shellStatus = message,
            store,
            recentItems: recentItems);

        coordinator.Initialize();

        Require(recentItems.Children.OfType<TextBlock>().Any(text => text.Text == "1 saved"), "recent list should render saved count summary");
        Require(recentItems.Children.OfType<TextBlock>().Any(text => text.Text.Contains("1 ready", StringComparison.Ordinal)), "recent list should render health facet counts");
        var filterPanel = recentItems.Children.OfType<WrapPanel>().Single();
        var readyFilter = filterPanel.Children.OfType<Button>().First(button => button.Content?.ToString()?.StartsWith("Ready", StringComparison.Ordinal) == true);
        Require(AutomationProperties.GetName(readyFilter) == "Recent Collaborate filter Ready", "recent filter chips should expose automation names");
        readyFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(coordinator.DebugRecentSearchText == "#ready", "recent filter chips should apply their saved-run search token");
        Require(recentItems.Children.OfType<TextBlock>().Any(text => text.Text.Contains("Ready", StringComparison.Ordinal)), "active recent filters should be reflected in the rail summary");
        coordinator.UpdateRecentSearch("");
        var recentButton = recentItems.Children.OfType<Button>().Single();
        Require(AutomationProperties.GetName(recentButton).Contains("2 turns", StringComparison.Ordinal), "recent row automation should include metadata");

        Require(coordinator.TryOpenConversation(chatId), "saved chat should open");
        Require(coordinator.DebugCurrentConversationId == chatId, "opening should set the current conversation id");
        Require(coordinator.DebugHistoryCount == 2, "opening should restore saved exchanges");
        Require(coordinator.DebugMemoryNotes.SequenceEqual(["Prefer local models"]), "opening should restore memory notes");

        Require(coordinator.ForkConversation(chatId), "fork action should succeed for saved chat");
        Require(coordinator.DebugCurrentConversationId is null, "forking should clear current id so the next reply saves a new chat");
        Require(coordinator.DebugHistoryCount == 2, "forking should keep prior exchanges as visible context");
        Require(statusText.Text.Contains("Forked:", StringComparison.Ordinal) && shellStatus == statusText.Text, "forking should update local and shell status");

        Require(coordinator.StageRecentConversationPrompt(chatId), "repeat prompt action should succeed for saved chat");
        Require(promptText.Text == "Check risks", "repeat prompt should stage the latest saved prompt");
        Require(coordinator.DebugHistoryCount == 0, "repeat prompt should start a clean chat");
        Require(coordinator.DebugCurrentConversationId is null, "repeat prompt should not overwrite the source chat");
        Require(coordinator.DebugMemoryNotes.SequenceEqual(["Prefer local models"]), "repeat prompt should carry saved memory notes into the new draft");
    });
}

static void CollaboratePreflightAcceptsRoleSpecificModels()
{
    var roleModels = SnapshotForOverviewTest(true, "", "", 0, [], [])
        with
        {
            AlphaModel = "alpha-local",
            BetaModel = "beta-local",
            GammaModel = "gamma-local",
            NarratorModel = "narrator-local"
        };

    Require(CollaborateCoordinator.MissingConfiguredModelRoles(roleModels, "team").Count == 0, "team mode should accept complete role-specific models without a shared model");
    Require(CollaborateCoordinator.MissingConfiguredModelRoles(roleModels, "critique").Count == 0, "critique mode should accept complete role-specific models without a shared model");
    Require(CollaborateCoordinator.MissingConfiguredModelRoles(roleModels, "redteam").Count == 0, "red team mode should accept complete role-specific models without a shared model");

    var fastNarratorOnly = SnapshotForOverviewTest(true, "", "", 0, [], [])
        with
        {
            NarratorModel = "narrator-local"
        };
    Require(CollaborateCoordinator.MissingConfiguredModelRoles(fastNarratorOnly, "fast").Count == 0, "fast mode should only require the narrator model");

    var sharedOnly = SnapshotForOverviewTest(true, "shared-local", "", 0, [], [])
        with
        {
            AlphaModel = "",
            BetaModel = "",
            GammaModel = "",
            NarratorModel = ""
        };
    Require(CollaborateCoordinator.MissingConfiguredModelRoles(sharedOnly, "team").Count == 0, "shared model should satisfy all collaborate roles");

    var missingGamma = roleModels with { GammaModel = "" };
    var missing = CollaborateCoordinator.MissingConfiguredModelRoles(missingGamma, "team");
    Require(missing.Count == 1 && missing[0] == "Gamma", "team preflight should report the specific missing role model");
    Require(CollaborateCoordinator.MissingModelStatus(missing) == "No model configured for Gamma.", "single missing role status should name the role");

    var missingFast = CollaborateCoordinator.MissingConfiguredModelRoles(SnapshotForOverviewTest(true, "", "", 0, [], []), "fast");
    Require(missingFast.Count == 1 && missingFast[0] == "Narrator", "fast preflight should report narrator when no shared or narrator model exists");
    Require(
        CollaborateCoordinator.MissingModelStatus(["Gamma", "Narrator"]) == "No model configured for Gamma, Narrator.",
        "multi-role missing status should name the affected roles");
}

static void CollaborateRunPlanSummarizesModelCalls()
{
    Require(CollaborateCoordinator.RunPlanSummary("fast", 8) == "1 narrator / 1 call", "fast mode should preview a single narrator call");
    Require(CollaborateCoordinator.RunPlanSummary("team", 1) == "4 agents / 1 round / 4 calls", "one team round should preview three role calls plus synthesis");
    Require(CollaborateCoordinator.RunPlanSummary("critique", 3) == "4 agents / 3 rounds / 10 calls", "critique mode should preview multi-round model calls");
    Require(CollaborateCoordinator.RunPlanSummary("redteam", 2) == "4 agents / red team / 2 rounds / 7 calls", "red team mode should preview proposal, attack, hardening, and synthesis calls");
    Require(CollaborateCoordinator.RunPlanSummary("team", 99) == "4 agents / 12 rounds / 37 calls", "run plan should clamp oversized round counts");
}

static void CollaborateInterruptionExchangesRemainPersistable()
{
    var exchange = CollaborateCoordinator.InterruptedExchange("Stop after this thought", "Collaboration stopped.");
    Require(exchange.Prompt == "Stop after this thought", "interrupted exchange should preserve the user prompt");
    Require(exchange.Answer == "Collaboration stopped.", "interrupted exchange should preserve the visible assistant status");
    Require(exchange.TraceSteps.Count == 0, "interrupted exchange should not invent trace steps");

    var conversations = new List<CollaborateCoordinator.CollaborateConversation>();
    var now = new DateTimeOffset(2026, 6, 11, 10, 30, 0, TimeSpan.Zero);
    var id = CollaborateCoordinator.UpsertConversationSnapshot(conversations, null, [exchange], now);

    Require(id != Guid.Empty, "interrupted exchange should upsert into a conversation");
    Require(conversations.Count == 1, "interrupted exchange should create one saved conversation");
    Require(conversations[0].Exchanges.Count == 1, "interrupted exchange should remain in saved history");
    Require(conversations[0].Exchanges[0].Answer == "Collaboration stopped.", "saved interrupted exchange should match the visible card");
}

static void CollaborateMemoryNotesPersistWithConversations()
{
    var exchange = CollaborateCoordinator.InterruptedExchange("Remember this", "Done.");
    var normalized = CollaborateCoordinator.NormalizeMemoryNotes([
        "  Alpha note  ",
        "alpha NOTE",
        "",
        new string('x', 1300),
        "Beta note"
    ]);

    Require(normalized.Count == 3, "memory notes should trim, dedupe, omit blanks, and keep unique notes");
    Require(normalized[0] == "Alpha note", "memory notes should trim whitespace");
    Require(normalized[1].Length <= 1200, "memory notes should cap long entries");
    Require(normalized[1].EndsWith("[truncated]", StringComparison.Ordinal), "memory notes should mark truncated entries");
    Require(CollaborateCoordinator.MemoryNoteSavedStatus(1) == "Memory note saved to this chat.", "memory save status should acknowledge persisted chats");
    Require(CollaborateCoordinator.MemoryNoteSavedStatus(0) == "Memory note added to current prompt context.", "memory save status should avoid promising persistence before a chat exists");
    Require(CollaborateCoordinator.MemoryNotesClearedStatus(1) == "Memory notes cleared for this chat.", "memory clear status should acknowledge persisted chats");

    var conversations = new List<CollaborateCoordinator.CollaborateConversation>();
    var createdAt = new DateTimeOffset(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);
    var id = CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        null,
        [exchange],
        createdAt,
        ["First note", "Second note"]);

    Require(conversations.Single().MemoryNotes.SequenceEqual(["First note", "Second note"]), "conversation snapshot should save memory notes");

    CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        id,
        [exchange with { Answer = "Updated." }],
        createdAt.AddMinutes(5));
    Require(conversations.Single().MemoryNotes.SequenceEqual(["First note", "Second note"]), "conversation update without memory input should preserve existing notes");

    CollaborateCoordinator.UpsertConversationSnapshot(
        conversations,
        id,
        [exchange],
        createdAt.AddMinutes(10),
        ["Fresh note"]);
    Require(conversations.Single().MemoryNotes.SequenceEqual(["Fresh note"]), "conversation update should replace memory notes when provided");

    var root = Path.Combine(Path.GetTempPath(), "ai-arena-collab-history", Guid.NewGuid().ToString("N"));
    var store = new CollaborateHistoryStore(Path.Combine(root, "history.json"));
    try
    {
        store.Save([
            new CollaborateHistoryConversation
            {
                Id = id,
                Title = "Memory chat",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Exchanges =
                [
                    new CollaborateHistoryExchange
                    {
                        Prompt = "Remember this",
                        Answer = "Done."
                    }
                ],
                MemoryNotes = [" saved note ", "SAVED NOTE", "other note"]
            }
        ]);

        var loaded = store.Load().Single();
        Require(loaded.MemoryNotes.SequenceEqual(["saved note", "other note"]), "history store should round-trip normalized memory notes");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CollaboratePromptBudgetSummarizesContext()
{
    Require(
        CollaborateCoordinator.PromptBudgetText("", 0, 0, 0, 0) == "Prompt 0 chars / ~0 tok | no added context",
        "blank prompt budget should show an empty no-context state");
    Require(
        CollaborateCoordinator.PromptBudgetText(new string('x', 17), 2, 1, 3, 4820) == "Prompt 17 chars / ~5 tok | Context 2 docs, 1 calc, 3 notes / ~1.2k tok",
        "prompt budget should summarize prompt size and added context");
    Require(
        CollaborateCoordinator.PromptBudgetText("ship", 1, 0, 0, 13000).EndsWith("will truncate", StringComparison.Ordinal),
        "prompt budget should warn when context will be truncated before prompting");

    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "hello" };
        var promptBudgetText = new TextBlock();
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("ok"),
            promptText,
            new TextBlock(),
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            _ => { },
            new RecordingCollaborateHistoryStore(),
            promptBudgetText);

        coordinator.Initialize();
        var initialBudget = promptBudgetText.Text;
        promptText.Text = "hello world";

        Require(initialBudget == "Prompt 5 chars / ~2 tok | no added context", "prompt budget should initialize from existing prompt text");
        Require(promptBudgetText.Text == "Prompt 11 chars / ~3 tok | no added context", "prompt budget should update live as the prompt changes");
    });
}

static void CollaborateContextReceiptSummarizesPayload()
{
    var receiptLines = CollaborateCoordinator.ContextReceiptLines(
        "4 agents / 2 rounds / 7 calls",
        "Review the launch",
        [
            new CollaborateCoordinator.ContextReceiptItem("Document", "release.md", "ship checklist", true),
            new CollaborateCoordinator.ContextReceiptItem("Calculation", "2+2", "4", false),
            new CollaborateCoordinator.ContextReceiptItem("Memory", "Note", "Prefer local models", false)
        ],
        13000,
        2);

    Require(receiptLines.Any(line => line == "Run: 4 agents / 2 rounds / 7 calls"), "context receipt should include the run plan");
    Require(receiptLines.Any(line => line == "Prompt: 17 chars / ~5 tok"), "context receipt should include prompt size");
    Require(receiptLines.Any(line => line == "Prior chat: 2 turns"), "context receipt should include prior chat size");
    Require(receiptLines.Any(line => line.StartsWith("Review: final answer will include", StringComparison.Ordinal)), "context receipt should preview the run review packet");
    Require(receiptLines.Any(line => line == "Context: 3 items / ~3.3k tok"), "context receipt should include context token estimate");
    Require(receiptLines.Any(line => line.Contains("will be truncated", StringComparison.Ordinal)), "context receipt should warn when tool context exceeds the prompt cap");
    Require(receiptLines.Any(line => line.Contains("release.md [truncated]", StringComparison.Ordinal)), "context receipt should flag truncated documents");
    Require(receiptLines.Any(line => line.Contains("Calculation: 2+2 - 4", StringComparison.Ordinal)), "context receipt should include calculation details");
    var receiptText = CollaborateCoordinator.ContextReceiptText(receiptLines);
    Require(receiptText.StartsWith("AI Arena Context Receipt", StringComparison.Ordinal), "copied context receipt should include a stable title");
    Require(receiptText.Contains("Run: 4 agents / 2 rounds / 7 calls", StringComparison.Ordinal), "copied context receipt should include the run plan");
    Require(CollaborateCoordinator.ContextReceiptLines("1 narrator / 1 call", "", [], 0).SequenceEqual(["Run: 1 narrator / 1 call", "Prompt: 0 chars / ~0 tok", "Prior chat: none", "Review: final answer will include a run review with trace health, token use, latency, model mix, and next action", "Context: none"]), "context receipt should expose an explicit no-context state");

    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "hello" };
        var receiptButton = new Button();
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("ok"),
            promptText,
            new TextBlock(),
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            _ => { },
            new RecordingCollaborateHistoryStore(),
            new TextBlock(),
            receiptButton);

        coordinator.Initialize();
        Require(AutomationProperties.GetName(receiptButton) == "Context receipt", "context receipt button should expose an automation name");
        receiptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var receiptText = coordinator.DebugContextReceiptText;

        Require(coordinator.DebugContextReceiptVisible, "receipt button should open the context receipt popup");
        Require(receiptText.Contains("Context Receipt", StringComparison.Ordinal), "receipt popup should expose a clear heading");
        Require(receiptText.Contains("Copy", StringComparison.Ordinal), "receipt popup should expose a copy action");
        Require(receiptText.Contains("Run: 1 narrator / 1 call", StringComparison.Ordinal), "receipt popup should include run plan text");
        Require(receiptText.Contains("Context: none", StringComparison.Ordinal), "receipt popup should include no-context state");

        receiptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!coordinator.DebugContextReceiptVisible, "second receipt click should close the popup");
    });
}

static void CollaborateTableSummariesSkipMarkdownDividers()
{
    var ok = CollaborateCoordinator.TryBuildTableSummary("""
        | Metric | Value |
        | :--- | ---: |
        | Latency | 320 ms |
        | Tokens | 1,024 |
        """, out var summary);

    Require(ok, "markdown table should be summarized as tool context");
    Require(summary.Contains("3 rows x 2 columns", StringComparison.Ordinal), "table summary should count header and data rows but skip markdown divider rows");
    Require(!summary.Contains(":---", StringComparison.Ordinal), "table preview should not include markdown alignment dividers");
    Require(summary.Contains("Latency | 320 ms", StringComparison.Ordinal), "table preview should keep data rows");
}

static void CollaborateEmptyStateStaysCompact()
{
    RunStaTest(() =>
    {
        var stagedPrompt = "";
        var card = CollaborateCoordinator.BuildEmptyStateCard(
            AccentResourceBrush,
            prompt => stagedPrompt = prompt);

        Require(card.MaxWidth <= 520, "Collaborate welcome card should not exceed the compact 520-DIP content width");
        Require(card.Padding.Left <= 16 && card.Padding.Top <= 16 && card.Padding.Right <= 16 && card.Padding.Bottom <= 16, "Collaborate welcome card padding should remain at or below 16 DIP");
        Require(card.Margin.Top <= 24, "Collaborate welcome card should not use a large fixed top offset");

        var content = card.Child as StackPanel;
        Require(content is not null && content.Children.Count == 3, "Collaborate welcome should contain only a title, sentence, and starter actions");
        Require(content!.Children[0] is TextBlock title && title.Text == "Start a collaboration", "Collaborate welcome should use a concise title");
        Require(content.Children[1] is TextBlock sentence && !string.IsNullOrWhiteSpace(sentence.Text), "Collaborate welcome should include one concise guidance sentence");
        Require(content.Children[2] is WrapPanel, "Collaborate welcome should end with starter actions");
        var actions = (WrapPanel)content.Children[2];
        Require(actions.Children.Count is > 0 and <= 3, "Collaborate welcome should expose no more than three starter actions");
        Require(actions.Children.OfType<Button>().All(button => button.MinHeight <= 32 && button.Padding.Top <= 4), "Collaborate starter actions should use compact button metrics");

        ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(stagedPrompt.StartsWith("Review this plan", StringComparison.Ordinal), "Collaborate welcome actions should stage the matching prompt");

        card.Measure(new Size(520, double.PositiveInfinity));
        Require(540 - card.DesiredSize.Height >= 360, "Collaborate welcome should leave enough of a 540-DIP viewport for the composer to remain visible");
    });
}

static void CollaboratePromptTemplatesPreserveDrafts()
{
    var starter = "Review this plan and identify risks.";
    var merged = CollaborateCoordinator.MergeStarterPrompt("Existing context", starter);
    Require(merged.StartsWith($"Existing context{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal), "starter prompts should append after existing drafts");
    Require(merged.EndsWith(starter, StringComparison.Ordinal), "starter prompts should keep the selected template text");
    Require(CollaborateCoordinator.MergeStarterPrompt("", starter) == starter, "blank drafts should receive only the starter prompt");
    Require(CollaborateCoordinator.BuildPromptTemplate("plan", "Fix Collaborate UI").Contains("implementation plan", StringComparison.OrdinalIgnoreCase), "plan template should request an implementation plan");
    Require(CollaborateCoordinator.BuildPromptTemplate("ship", "Fix Collaborate UI").StartsWith($"Fix Collaborate UI{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal), "prompt templates should preserve user draft text first");
    Require(CollaborateCoordinator.BuildPromptTemplate("missing", "Keep me") == "Keep me", "unknown templates should leave existing prompt text alone");
    Require(CollaborateCoordinator.PromptTemplateLabel("critique") == "Critique", "template status label should be human-readable");

    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Investigate prompt assist chips" };
        var statusText = new TextBlock();
        var shellStatus = "";
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("ok"),
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            message => shellStatus = message,
            new RecordingCollaborateHistoryStore());

        coordinator.Initialize();
        coordinator.ApplyPromptTemplate("critique");

        Require(promptText.Text.StartsWith($"Investigate prompt assist chips{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal), "template button should preserve existing prompt text");
        Require(promptText.Text.Contains("Strongest assumption", StringComparison.Ordinal), "template button should add the selected critique structure");
        Require(statusText.Text == "Critique prompt staged.", "template button should acknowledge the staged prompt");
        Require(shellStatus == statusText.Text, "template button should mirror status into the shell status bar");
    });
}

static void CollaborateTraceFollowUpPromptPreservesDrafts()
{
    var step = CollaborateCoordinator.CollaborateStep.Completed(
        "beta",
        "Beta",
        "beta-model",
        "Round 2 - Critique",
        "The plan needs a rollback path.",
        42,
        128);
    var prompt = CollaborateCoordinator.BuildTraceFollowUpPrompt("Existing draft", step);

    Require(prompt.StartsWith($"Existing draft{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal), "trace follow-up should preserve existing draft text first");
    Require(prompt.Contains("Beta Critique note", StringComparison.Ordinal), "trace follow-up should identify role and trace label");
    Require(prompt.Contains("rollback path", StringComparison.Ordinal), "trace follow-up should include completed trace text");

    var failed = CollaborateCoordinator.CollaborateStep.Failed(
        "gamma",
        "Gamma",
        "gamma-model",
        "Evidence refinement",
        "provider timeout");
    var failedPrompt = CollaborateCoordinator.BuildTraceFollowUpPrompt("", failed);
    Require(failedPrompt.Contains("Gamma Evidence refinement note", StringComparison.Ordinal), "failed trace follow-up should identify the failed role and label");
    Require(failedPrompt.Contains("provider timeout", StringComparison.Ordinal), "failed trace follow-up should include the model error");
}

static void CollaborateRunReviewSummarizesTraces()
{
    var review = CollaborateCoordinator.BuildRunReview(
        "Choose an installer strategy",
        "Ship the smaller installer first.",
        [
            CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Round 1 - Proposal", "ship small", 120, 800),
            CollaborateCoordinator.CollaborateStep.Completed("beta", "Beta", "model-b", "Round 1 - Attack", "rollback risk", 340, 1200),
            CollaborateCoordinator.CollaborateStep.Completed("gamma", "Gamma", "model-a", "Round 1 - Hardening", "add rollback", 180, 500)
        ],
        "Ready.");
    var lines = CollaborateCoordinator.RunReviewLines(review);
    var copied = CollaborateCoordinator.RunReviewText(review);
    var followUp = CollaborateCoordinator.BuildRunReviewFollowUpPrompt("Existing draft", review);

    Require(review.Verdict == "Ready to use", "healthy trace review should mark the answer ready");
    Require(!review.NeedsReview, "healthy trace review should not request manual review");
    Require(review.StepCount == 3 && review.IssueCount == 0, "run review should count trace steps and issues");
    Require(review.TotalTokens == 2500, "run review should sum token use");
    Require(review.TotalLatencyMs == 640, "run review should sum latency");
    Require(review.SlowestStepLabel == "Beta Attack" && review.SlowestLatencyMs == 340, "run review should identify the slowest visible step");
    Require(review.Models.SequenceEqual(["model-a", "model-b"]), "run review should dedupe and sort models");
    Require(lines.Any(line => line == "Verdict: Ready to use"), "run review lines should include a verdict");
    Require(lines.Any(line => line.Contains("~2.5k tok", StringComparison.Ordinal)), "run review lines should compact token totals");
    Require(copied.StartsWith("AI Arena Run Review", StringComparison.Ordinal), "copied run review should include a stable title");
    Require(followUp.StartsWith($"Existing draft{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal), "run review follow-up should preserve drafts first");
    Require(followUp.Contains("Next:", StringComparison.Ordinal), "run review follow-up should include the next action");

    var failed = CollaborateCoordinator.BuildRunReview(
        "Check the release",
        "One model failed.",
        [
            CollaborateCoordinator.CollaborateStep.Failed("gamma", "Gamma", "model-c", "Evidence", "timeout")
        ],
        "Answer completed with model errors.");
    Require(failed.Verdict == "Needs review", "failed trace review should request review");
    Require(failed.NeedsReview, "failed trace review should expose a review flag");
    Require(failed.IssueCount == 1, "failed trace review should count model issues");
    Require(failed.NextAction.Contains("repair", StringComparison.OrdinalIgnoreCase), "failed trace review should recommend repair");

    var interrupted = CollaborateCoordinator.BuildRunReview("Stop", "Collaboration stopped.", [], "Collaboration stopped.");
    Require(interrupted.NeedsReview && interrupted.StepCount == 0, "interrupted runs should produce a no-trace review warning");

    var exported = CollaborateCoordinator.BuildRunReview(
        "Check",
        "Checked.",
        [CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model-a", "Direct answer", "checked", 10, 20)],
        "Exported.");
    Require(!exported.NeedsReview, "exporting a healthy run should not create a false review warning");
}

static void CollaborateConversationExportIncludesRunReviewAndTrace()
{
    var export = CollaborateCoordinator.BuildConversationExport(
        "Robot stage plan",
        [
            new CollaborateCoordinator.CollaborateExchange(
                "Give the robots readable stage directions.",
                "Use concise bubbles and collision-aware placement.",
                [
                    CollaborateCoordinator.CollaborateStep.Completed(
                        "alpha",
                        "Alpha",
                        "model-a",
                        "Round 1 - Proposal",
                        "Add concise stage directions.",
                        340,
                        1200),
                    CollaborateCoordinator.CollaborateStep.Failed(
                        "beta",
                        "Beta",
                        "model-b",
                        "Evidence",
                        "provider timeout")
                ])
        ],
        ["Keep the arena readable"]);

    Require(export.StartsWith("# AI Arena Collaborate - Robot stage plan", StringComparison.Ordinal), "collaborate export should use a stable markdown title");
    Require(export.Contains("Exchanges: 1", StringComparison.Ordinal), "collaborate export should count exchanges");
    Require(export.Contains("Memory notes: 1", StringComparison.Ordinal), "collaborate export should count memory notes");
    Require(export.Contains("## Memory Notes", StringComparison.Ordinal), "collaborate export should include memory notes");
    Require(export.Contains("### Prompt", StringComparison.Ordinal), "collaborate export should include each prompt");
    Require(export.Contains("### Final Answer", StringComparison.Ordinal), "collaborate export should include each final answer");
    Require(export.Contains("AI Arena Run Review", StringComparison.Ordinal), "collaborate export should include a run review packet");
    Require(export.Contains("Verdict: Needs review", StringComparison.Ordinal), "collaborate export should flag failed trace steps");
    Require(export.Contains("### Team Trace", StringComparison.Ordinal), "collaborate export should include trace details");
    Require(export.Contains("#### Alpha - Proposal", StringComparison.Ordinal), "collaborate export should use human-readable trace labels");
    Require(export.Contains("Model: `model-a`", StringComparison.Ordinal), "collaborate export should include trace model metadata");
    Require(export.Contains("Tokens: 1.2k", StringComparison.Ordinal), "collaborate export should compact token counts");
    Require(export.Contains("Latency: 340 ms", StringComparison.Ordinal), "collaborate export should format trace latency");
    Require(export.Contains("Error: provider timeout", StringComparison.Ordinal), "collaborate export should include trace errors");
}

static void CollaborateControlReviewExposesSavedTrace()
{
    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Audit this deployment plan" };
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("Deployment plan reviewed."),
            promptText,
            new TextBlock(),
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            _ => { },
            new RecordingCollaborateHistoryStore());

        coordinator.Initialize();
        coordinator.SendAsync().GetAwaiter().GetResult();

        var review = coordinator.CaptureControlReview("");
        Require(review.Available, "control review should expose the newest saved collaboration");
        Require(review.TurnCount == 1 && review.StepCount == 1, "control review should expose turn and trace counts");
        Require(review.LatestPrompt == "Audit this deployment plan", "control review should preserve the latest prompt");
        Require(review.LatestAnswer == "Deployment plan reviewed.", "control review should preserve the final answer");
        Require(review.Trace.Count == 1 && review.Trace[0].Ok, "control review should expose the full successful trace step");
        Require(!review.NeedsReview && review.Verdict == "Ready to use", "a healthy saved run should be ready to use");

        var missing = coordinator.CaptureControlReview(Guid.NewGuid().ToString("N"));
        Require(!missing.Available && missing.NeedsReview, "an unknown explicit run id should return an unavailable review");
    });
}

static void CollaborateClearSyncsShellStatus()
{
    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Draft context" };
        var statusText = new TextBlock();
        var promptBudgetText = new TextBlock();
        var shellStatus = "";
        var coordinator = CreateCollaborateCoordinatorForTest(
            new FixedCollaborateModelClient("ok"),
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            message => shellStatus = message,
            new RecordingCollaborateHistoryStore(),
            promptBudgetText);

        coordinator.Initialize();
        coordinator.ApplyPromptTemplate("critique");
        Require(shellStatus == "Critique prompt staged.", "template staging should update shell status before clear");

        coordinator.Clear();

        Require(statusText.Text == "Ready.", "clear should reset Collaborate status text");
        Require(shellStatus == "Ready.", "clear should reset shell status too");
        Require(promptBudgetText.Text == "Prompt 0 chars / ~0 tok | no added context", "clear should reset the prompt budget readout");
    });
}

static void CollaborateTeamDebateHeaderSummarizesTrace()
{
    Require(CollaborateCoordinator.TeamDebateHeader(0, 0, hasErrors: false) == "Team Debate", "empty trace header should stay compact");
    Require(CollaborateCoordinator.TeamDebateHeader(1, 42, hasErrors: false) == "Team Debate - 1 step / 42 tok", "single-step trace header should use singular step copy");
    Require(CollaborateCoordinator.TeamDebateHeader(4, 2130, hasErrors: false) == "Team Debate - 4 steps / 2.1k tok", "multi-step trace header should compact token counts");
    Require(CollaborateCoordinator.TeamDebateHeader(2, -10, hasErrors: true) == "Team Debate - 2 steps / 0 tok / needs review", "trace header should flag model errors and clamp negative tokens");
}

static void CollaborateBlankRoleCompletionFallsBackToSharedModel()
{
    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Give me a robust answer" };
        var statusText = new TextBlock();
        var store = new RecordingCollaborateHistoryStore();
        var client = new ModelMapCollaborateModelClient(new Dictionary<string, ModelCompletionResult>(StringComparer.OrdinalIgnoreCase)
        {
            ["blank-role-model"] = new(
                true,
                "http://127.0.0.1:1234/v1",
                "blank-actual-model",
                "   ",
                "",
                10,
                1,
                0,
                1,
                "",
                DateTimeOffset.Now),
            ["shared-fallback-model"] = new(
                true,
                "http://127.0.0.1:1234/v1",
                "shared-actual-model",
                "Fallback answer.",
                "",
                20,
                2,
                3,
                5,
                "",
                DateTimeOffset.Now)
        });

        var coordinator = CreateCollaborateCoordinatorForTest(
            client,
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "shared-fallback-model", "", 0, [], [])
                with
                {
                    NarratorModel = "blank-role-model"
                },
            _ => { },
            store);

        coordinator.Initialize();
        coordinator.SendAsync().GetAwaiter().GetResult();

        Require(client.CompletedModels.SequenceEqual(["blank-role-model", "shared-fallback-model"]), "blank role completion should retry with the shared fallback model");
        var exchange = store.LastConversations.Single().Exchanges.Single();
        Require(exchange.Answer == "Fallback answer.", "collaborate final answer should come from the nonblank fallback response");
        var trace = exchange.TraceSteps.Single();
        Require(trace.Ok, "fallback trace step should be successful");
        Require(trace.Model == "shared-actual-model", "trace should record the actual model returned by the fallback provider");
        Require(trace.TotalTokens == 5, "trace should preserve fallback provider telemetry");
        Require(statusText.Text == "Ready.", "successful fallback should leave Collaborate ready");
    });
}

static void CollaborateSendPreservesHistorySaveFailures()
{
    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Draft a launch checklist" };
        var statusText = new TextBlock();
        var shellStatus = "";
        var store = new ThrowingCollaborateHistoryStore("disk offline");
        var client = new FixedCollaborateModelClient("Checklist ready.");
        var coordinator = CreateCollaborateCoordinatorForTest(
            client,
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            message => shellStatus = message,
            store);

        coordinator.Initialize();
        coordinator.SendAsync().GetAwaiter().GetResult();

        Require(client.CompleteCalls == 1, "collaborate send should call the provider once in fast mode");
        Require(store.SaveCalls == 1, "collaborate send should attempt to save history");
        Require(statusText.Text.Contains("Could not save Collaborate history:", StringComparison.Ordinal), "save failure should remain visible in collaborate status");
        Require(shellStatus == statusText.Text, "shell status should match the collaborate save warning");
        Require(store.LastConversations.Single().Exchanges.Single().Answer == "Checklist ready.", "failed save attempt should still receive the completed exchange");
    });
}

static void CollaborateFailedSendsAreSavedInHistory()
{
    RunStaTest(() =>
    {
        var promptText = new TextBox { Text = "Explain the provider failure" };
        var statusText = new TextBlock();
        var store = new RecordingCollaborateHistoryStore();
        var coordinator = CreateCollaborateCoordinatorForTest(
            new ThrowingCollaborateModelClient("provider down"),
            promptText,
            statusText,
            () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
            _ => { },
            store);

        coordinator.Initialize();
        coordinator.SendAsync().GetAwaiter().GetResult();

        var exchange = store.LastConversations.Single().Exchanges.Single();
        Require(statusText.Text == "Collaboration failed.", "provider exception should leave a failure status");
        Require(exchange.Prompt == "Explain the provider failure", "failed send should persist the user prompt");
        Require(exchange.Answer == "Collaboration failed: provider down", "failed send should persist the visible assistant failure");
    });
}

}
