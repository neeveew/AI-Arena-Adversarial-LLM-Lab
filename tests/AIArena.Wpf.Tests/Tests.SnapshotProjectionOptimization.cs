using System.Diagnostics;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static void SnapshotProjectionOptimizationPreservesEquivalenceAndPerformance()
    {
        RunDifferentialEquivalenceFixtures();
        WriteLargeSnapshotPerformanceReceipt();
    }

    private static void RunDifferentialEquivalenceFixtures()
    {
        var fixtures = new[]
        {
            CreateEdgeCaseSnapshot(),
            CreateOrderedSnapshot(),
            new ArenaSnapshot()
        };

        var session = new SessionSummary("projection-equivalence", "snapshot.json", true, 1, 0, 0, DateTimeOffset.UnixEpoch);
        foreach (var snapshot in fixtures)
        {
            var expected = LegacyProject(snapshot);
            var actual = SnapshotViewMapper.FromCore(session, snapshot);
            var actualMessages = actual.Messages.Select(message => new MessageProjection(
                message.Turn,
                message.Speaker,
                message.SpeakerId,
                message.Kind,
                message.Text,
                message.VoiceStyle,
                message.InternetRequester,
                message.InternetTool,
                message.InternetQuery,
                message.InternetUrl,
                message.InternetReason,
                message.InternetSummary,
                message.InternetCheckedAt,
                message.InternetCached,
                message.InternetSources)).ToArray();
            TestRequire(
                JsonSerializer.Serialize(actualMessages) == JsonSerializer.Serialize(expected.Messages),
                "optimized projection should preserve transcript ordering, voice fallback, and internet metadata");

            var actualAgentSources = actual.Agents.Select(agent => new AgentSourceProjection(agent.Id, agent.InternetSources)).ToArray();
            var expectedAgentSources = snapshot.Engine.Agents.Select(agent => new AgentSourceProjection(
                agent.Id,
                expected.LatestInternetByAgent.TryGetValue(agent.Id, out var summary) ? summary : null)).ToArray();
            TestRequire(
                JsonSerializer.Serialize(actualAgentSources) == JsonSerializer.Serialize(expectedAgentSources),
                "optimized projection should preserve latest per-agent source summaries and item details");
        }

        var edgeProjection = SnapshotViewMapper.FromCore(session, fixtures[0]);
        TestRequire(
            edgeProjection.Agents.First(agent => agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase))
                .InternetSources?.Query == "same-turn-last",
            "equal-turn source ties should continue to select the last message in snapshot order");
        TestRequire(edgeProjection.Messages.Single(message => message.Text == "Factory voice").VoiceStyle.Length == 0,
            "Factory messages should continue to suppress voice fallback");
        TestRequire(edgeProjection.Messages.Single(message => message.Text == "Narrator voice").VoiceStyle == "narrator-voice",
            "Narrator messages should continue to use the Narrator fallback voice");
    }

    private static void WriteLargeSnapshotPerformanceReceipt()
    {
        const int agentCount = 32;
        const int messageCount = 5_000;
        var session = new SessionSummary("projection-performance", "snapshot.json", true, 1, 0, 0, DateTimeOffset.UnixEpoch);
        var snapshot = CreateLargeSnapshot(agentCount, messageCount);

        _ = SnapshotViewMapper.FromCore(session, snapshot);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var rendered = SnapshotViewMapper.FromCore(session, snapshot);
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        TestRequire(rendered.Messages.Count == messageCount, "large projection should retain every transcript message");
        TestRequire(rendered.Agents.Count == agentCount, "large projection should retain every agent");
        TestRequire(rendered.Agents.All(agent => agent.HasInternetSources), "large projection should attach latest source evidence to every agent");
        TestRequire(allocatedBytes < 8_000_000,
            "large projection should retain the measured allocation improvement over the 16,739,824-byte baseline");

        Console.WriteLine(
            $"RECEIPT snapshot-projection baseline_elapsed_ms=98.623 baseline_allocated_bytes=16739824 " +
            $"agents={agentCount} messages={messageCount} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocatedBytes}");
    }

    private static ArenaSnapshot CreateEdgeCaseSnapshot()
    {
        var snapshot = new ArenaSnapshot();
        snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model" };
        snapshot.Engine.Narrator.VoiceStyle = "narrator-voice";
        snapshot.Engine.Agents.Add(new DialogueAgent { Id = "alpha", Name = "Alpha", VoiceStyle = "alpha-first", Active = true });
        snapshot.Engine.Agents.Add(new DialogueAgent { Id = "ALPHA", Name = "Alpha duplicate", VoiceStyle = "alpha-second", Active = false });
        snapshot.Engine.Agents.Add(new DialogueAgent { Id = "beta", Name = "Beta", VoiceStyle = "beta-voice", Active = true });

        snapshot.Engine.Messages.Add(CreateInternetMessage(
            turn: 9,
            speakerId: "alpha",
            text: "Higher turn earlier in storage",
            requesterId: " alpha ",
            query: "higher-turn-first",
            sourceSuffix: "higher-first"));
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = 2,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Kind = "message",
            Status = "ok",
            Text = "Factory voice",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["prompt_mode"] = JsonSerializer.SerializeToElement("FaCtOrY"),
                ["voice_style"] = JsonSerializer.SerializeToElement("stored-voice")
            }
        });
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = 3,
            Speaker = "Narrator",
            SpeakerId = "narrator",
            Kind = "narration",
            Status = "ok",
            Text = "Narrator voice"
        });
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = 4,
            Speaker = "Beta",
            SpeakerId = "beta",
            Kind = "message",
            Status = "ok",
            Text = "Stored voice wins",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["voice_style"] = JsonSerializer.SerializeToElement(" stored-beta ")
            }
        });
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = 5,
            Speaker = "Malformed",
            SpeakerId = "beta",
            Kind = "internet",
            Status = "ok",
            Text = "Malformed metadata",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["tool_request"] = JsonSerializer.SerializeToElement("not-an-object"),
                ["tool_result"] = JsonSerializer.SerializeToElement(new { sources = "not-an-array", checked_at = 42 })
            }
        });
        snapshot.Engine.Messages.Add(CreateInternetMessage(
            turn: 9,
            speakerId: "alpha",
            text: "Equal turn winner",
            requesterId: "ALPHA",
            query: "same-turn-last",
            sourceSuffix: "tie-winner",
            publishedAt: "not-a-date"));
        snapshot.Engine.Messages.Add(CreateInternetMessage(
            turn: 7,
            speakerId: "beta",
            text: "Requester fallback",
            requesterId: " ",
            query: "fallback-requester",
            sourceSuffix: "fallback",
            checkedAt: "not-a-date"));

        return snapshot;
    }

    private static ArenaSnapshot CreateOrderedSnapshot()
    {
        var snapshot = new ArenaSnapshot();
        snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model" };
        snapshot.Engine.Agents.Add(new DialogueAgent { Id = "alpha", Name = "Alpha", VoiceStyle = "alpha-voice", Active = true });
        snapshot.Engine.Messages.Add(CreateInternetMessage(1, "alpha", "Earlier", "alpha", "earlier", "old"));
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = 2,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Kind = "internet",
            Status = "ok",
            Text = "Blank sources do not replace evidence",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["tool_request"] = JsonSerializer.SerializeToElement(new { requester_id = "alpha", query = "blank" }),
                ["tool_result"] = JsonSerializer.SerializeToElement(new { sources = new object[] { new { source = "", title = "", url = "", snippet = "" }, 42 } })
            }
        });
        snapshot.Engine.Messages.Add(CreateInternetMessage(3, "alpha", "Latest", "alpha", "latest", "new"));
        return snapshot;
    }

    private static DialogueMessage CreateInternetMessage(
        int turn,
        string speakerId,
        string text,
        string requesterId,
        string query,
        string sourceSuffix,
        string checkedAt = "2026-08-13T10:00:00Z",
        string publishedAt = "2026-08-12T10:00:00Z")
    {
        return new DialogueMessage
        {
            Turn = turn,
            Speaker = speakerId,
            SpeakerId = speakerId,
            Kind = "internet",
            Status = "ok",
            Text = text,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["tool_request"] = JsonSerializer.SerializeToElement(new
                {
                    requester_id = requesterId,
                    tool = "web_search",
                    query,
                    url = $"https://request.test/{sourceSuffix}",
                    reason = "verify"
                }),
                ["tool_result"] = JsonSerializer.SerializeToElement(new
                {
                    query = $"result-{query}",
                    url = $"https://result.test/{sourceSuffix}",
                    summary = $"Summary {sourceSuffix}",
                    checked_at = checkedAt,
                    cached = true,
                    sources = new object[]
                    {
                        new
                        {
                            source = "Source",
                            title = $"Title {sourceSuffix}",
                            url = $"https://example.test/{sourceSuffix}",
                            snippet = "Evidence",
                            published_at = publishedAt
                        },
                        new { source = "", title = "", url = "", snippet = "" },
                        17
                    }
                })
            }
        };
    }

    private static LegacyProjection LegacyProject(ArenaSnapshot snapshot)
    {
        var messages = snapshot.Engine.Messages.Select(message =>
        {
            var request = LegacyMetadataObject(message, "tool_request");
            var result = LegacyMetadataObject(message, "tool_result");
            return new MessageProjection(
                message.Turn,
                LegacyDisplayValue(string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker),
                LegacyDisplayValue(string.IsNullOrWhiteSpace(message.SpeakerId) ? message.Speaker : message.SpeakerId),
                string.IsNullOrWhiteSpace(message.Kind) ? "message" : message.Kind,
                message.Text,
                LegacyVoiceStyle(message, snapshot),
                LegacyJsonString(request, "requester_id"),
                LegacyJsonString(request, "tool"),
                LegacyJsonString(request, "query", LegacyJsonString(result, "query")),
                LegacyJsonString(request, "url", LegacyJsonString(result, "url")),
                LegacyJsonString(request, "reason"),
                LegacyJsonString(result, "summary"),
                LegacyFormatCheckedAt(LegacyJsonProperty(result, "checked_at")),
                LegacyJsonBool(result, "cached"),
                LegacyParseInternetSources(LegacyJsonProperty(result, "sources")));
        }).ToArray();

        var latest = new Dictionary<string, AgentInternetSourceSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in snapshot.Engine.Messages.OrderBy(message => message.Turn))
        {
            var request = LegacyMetadataObject(message, "tool_request");
            var result = LegacyMetadataObject(message, "tool_result");
            var sourcesElement = LegacyJsonProperty(result, "sources");
            var sources = LegacyParseInternetSources(sourcesElement);
            if (sources.Count == 0)
            {
                continue;
            }

            var requesterId = LegacyJsonString(request, "requester_id");
            if (string.IsNullOrWhiteSpace(requesterId))
            {
                requesterId = message.SpeakerId;
            }
            if (string.IsNullOrWhiteSpace(requesterId))
            {
                continue;
            }

            latest[requesterId.Trim()] = new AgentInternetSourceSummary(
                LegacyJsonString(request, "query", LegacyJsonString(result, "query")),
                LegacyFormatCheckedAt(LegacyJsonProperty(result, "checked_at")),
                sources,
                LegacyParseInternetSourceItems(sourcesElement));
        }

        return new LegacyProjection(messages, latest);
    }

    private static string LegacyVoiceStyle(DialogueMessage message, ArenaSnapshot snapshot)
    {
        if (LegacyMetadataString(message, "prompt_mode").Equals("factory", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var stored = LegacyMetadataString(message, "voice_style");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.Engine.Narrator.VoiceStyle;
        }

        return snapshot.Engine.Agents
            .FirstOrDefault(agent => agent.Id.Equals(message.SpeakerId, StringComparison.OrdinalIgnoreCase))
            ?.VoiceStyle ?? "";
    }

    private static JsonElement LegacyMetadataObject(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    private static string LegacyMetadataString(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static JsonElement LegacyJsonProperty(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value
            : default;
    }

    private static string LegacyJsonString(JsonElement element, string key, string fallback = "")
    {
        var value = LegacyJsonProperty(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static bool LegacyJsonBool(JsonElement element, string key)
    {
        var value = LegacyJsonProperty(element, key);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    }

    private static string LegacyFormatCheckedAt(JsonElement checkedAt)
    {
        if (checkedAt.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var value = checkedAt.GetString();
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            : value ?? "";
    }

    private static IReadOnlyList<string> LegacyParseInternetSources(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sources.EnumerateArray()
            .Select(source =>
            {
                var title = LegacyJsonString(source, "title");
                var url = LegacyJsonString(source, "url");
                var name = LegacyJsonString(source, "source");
                var snippet = LegacyJsonString(source, "snippet");
                return string.Join(" - ", new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyList<AgentInternetSourceItem> LegacyParseInternetSourceItems(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sources.EnumerateArray()
            .Select(source =>
            {
                var title = LegacyJsonString(source, "title");
                var url = LegacyJsonString(source, "url");
                var name = LegacyJsonString(source, "source");
                var snippet = LegacyJsonString(source, "snippet");
                var display = string.Join(" - ", new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return new AgentInternetSourceItem(
                    title,
                    LegacyDomainLabel(url),
                    url,
                    snippet,
                    LegacyFormatCheckedAt(LegacyJsonProperty(source, "published_at")),
                    display);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayText))
            .ToArray();
    }

    private static string LegacyDomainLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "";
    }

    private static string LegacyDisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static ArenaSnapshot CreateLargeSnapshot(int agentCount, int messageCount)
    {
        var snapshot = new ArenaSnapshot();
        snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model" };
        for (var agentIndex = 0; agentIndex < agentCount; agentIndex++)
        {
            snapshot.Engine.Agents.Add(new DialogueAgent
            {
                Id = $"agent-{agentIndex:D2}",
                Name = $"Agent {agentIndex:D2}",
                VoiceStyle = $"voice-{agentIndex:D2}",
                Active = true
            });
        }

        for (var messageIndex = 0; messageIndex < messageCount; messageIndex++)
        {
            var agentIndex = messageIndex % agentCount;
            snapshot.Engine.Messages.Add(new DialogueMessage
            {
                Turn = messageIndex / 2,
                Speaker = $"Agent {agentIndex:D2}",
                SpeakerId = $"agent-{agentIndex:D2}",
                Kind = "internet",
                Status = "ok",
                Text = $"Message {messageIndex:D5}",
                Metadata = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["tool_request"] = System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        requester_id = $"agent-{agentIndex:D2}",
                        tool = "web_search",
                        query = $"query-{messageIndex:D5}"
                    }),
                    ["tool_result"] = System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        query = $"query-{messageIndex:D5}",
                        checked_at = "2026-08-13T10:00:00Z",
                        sources = new[]
                        {
                            new
                            {
                                source = "Source",
                                title = $"Result {messageIndex:D5}",
                                url = $"https://example.test/{messageIndex:D5}",
                                snippet = "Evidence"
                            }
                        }
                    })
                }
            });
        }

        return snapshot;
    }

    private static void TestRequire(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record LegacyProjection(
        IReadOnlyList<MessageProjection> Messages,
        IReadOnlyDictionary<string, AgentInternetSourceSummary> LatestInternetByAgent);

    private sealed record MessageProjection(
        int Turn,
        string Speaker,
        string SpeakerId,
        string Kind,
        string Text,
        string VoiceStyle,
        string InternetRequester,
        string InternetTool,
        string InternetQuery,
        string InternetUrl,
        string InternetReason,
        string InternetSummary,
        string InternetCheckedAt,
        bool InternetCached,
        IReadOnlyList<string> InternetSources);

    private sealed record AgentSourceProjection(string Id, AgentInternetSourceSummary? Summary);
}
