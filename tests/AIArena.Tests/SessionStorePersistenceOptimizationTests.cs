using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

internal static class SessionStorePersistenceOptimizationTests
{
    public static void StreamsRevisionWithLegacySemanticsAndBoundedReceipts()
    {
        var cases = new (string Name, byte[] Json, long? Expected, Type? ExceptionType)[]
        {
            ("basic", Utf8("{\"persistence_revision\":7,\"engine\":{}}"), 7, null),
            ("missing revision", Utf8("{\"engine\":{}}"), 0, null),
            ("negative clamps", Utf8("{\"persistence_revision\":-7}"), 0, null),
            ("comments and trailing comma", Utf8("{/*before*/\"persistence_revision\":8,// after\n}"), 8, null),
            ("exact case only", Utf8("{\"PERSISTENCE_REVISION\":9}"), 0, null),
            ("differently cased then exact", Utf8("{\"PERSISTENCE_REVISION\":9,\"persistence_revision\":10}"), 10, null),
            ("last duplicate wins", Utf8("{\"persistence_revision\":1,\"persistence_revision\":2}"), 2, null),
            ("invalid last duplicate wins", Utf8("{\"persistence_revision\":2,\"persistence_revision\":\"3\"}"), 0, null),
            ("nested property ignored", Utf8("{\"nested\":{\"persistence_revision\":99},\"persistence_revision\":4}"), 4, null),
            ("escaped property is exact", Utf8("{\"persistence\\u005frevision\":11}"), 11, null),
            ("number outside Int64", Utf8("{\"persistence_revision\":9223372036854775808}"), 0, null),
            ("root array", Utf8("[1,2,3]"), null, typeof(InvalidOperationException)),
            ("root null", Utf8("null"), null, typeof(InvalidOperationException)),
            ("empty", [], null, typeof(JsonException)),
            ("malformed suffix", Utf8("{\"persistence_revision\":12,\"engine\":"), null, typeof(JsonException)),
            ("multiple roots", Utf8("{\"persistence_revision\":12} {}"), null, typeof(JsonException))
        };

        foreach (var testCase in cases)
        {
            var legacy = LegacyRevisionOutcome(testCase.Json);
            Require(
                legacy.Value == testCase.Expected && ExceptionsMatch(testCase.ExceptionType, legacy.ExceptionType),
                $"{testCase.Name}: fixture does not describe legacy JsonDocument behavior " +
                $"(value={legacy.Value}, exception={legacy.ExceptionType?.Name})");
            AssertReaderOutcome(testCase.Name, testCase.Json, testCase.Expected, testCase.ExceptionType);
            AssertReaderOutcome(
                $"{testCase.Name} fragmented",
                testCase.Json,
                testCase.Expected,
                testCase.ExceptionType,
                maximumReadBytes: 1);
        }

        var bomJson = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Utf8("{\"persistence_revision\":13}"))
            .ToArray();
        AssertReaderOutcome("UTF-8 BOM", bomJson, 13, null, maximumReadBytes: 2);

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                PersistenceRevisionReader
                    .ReadAsync(new MemoryStream(Utf8("{\"persistence_revision\":15}")), cancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("a pre-cancelled revision scan completed successfully");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        foreach (var megabytes in new[] { 10, 50, 100 })
        {
            var json = LargeSnapshotJson(megabytes, revision: megabytes + 1);
            var legacy = MeasureLegacyRevisionIsolated(json);
            var optimized = MeasureRevisionReader(json, useLegacyDocument: false);
            Require(legacy.Revision == megabytes + 1 && optimized.Revision == legacy.Revision,
                $"{megabytes}MiB reader receipt changed the revision");
            Require(optimized.AllocatedBytes < 2 * 1024 * 1024,
                $"{megabytes}MiB stream scan allocated {optimized.AllocatedBytes} bytes instead of remaining bounded");
            Console.WriteLine(
                $"BASELINE_RECEIPT persistence-revision size_mib={megabytes} " +
                $"allocated_bytes={legacy.AllocatedBytes} elapsed_ms={legacy.ElapsedMs:F3}");
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT persistence-revision size_mib={megabytes} " +
                $"allocated_bytes={optimized.AllocatedBytes} elapsed_ms={optimized.ElapsedMs:F3}");
        }

        var oversizedTokenBytes = PersistenceRevisionReader.MaximumBufferedTokenBytes + 1;
        var oversizedTokenJson = LargeStringSnapshotJson(oversizedTokenBytes, revision: 14);
        AssertReaderOutcome("oversize seekable fallback", oversizedTokenJson, 14, null);
        AssertReaderOutcome(
            "oversize nonseekable behavior is explicit",
            oversizedTokenJson,
            null,
            typeof(JsonException),
            seekable: false);
    }

    public static void BoundsUniqueSnapshotMutationStampsAcrossEvictionAndConcurrency()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            var targetPath = store.SnapshotPath("mutation-target");
            SessionStore.RecordSnapshotMutation(targetPath);
            var firstStamp = store.SnapshotMutationGeneration("mutation-target");
            Require(firstStamp > 0, "the first mutation should receive a positive process-unique stamp");

            for (var index = 0; index <= SessionStore.SnapshotMutationGenerationCapacity; index++)
            {
                SessionStore.RecordSnapshotMutation(store.SnapshotPath($"eviction-{index}"));
            }

            Require(store.SnapshotMutationGeneration("mutation-target") == 0,
                "the bounded mutation map should evict its least-recently-mutated path");
            SessionStore.RecordSnapshotMutation(targetPath);
            var replacementStamp = store.SnapshotMutationGeneration("mutation-target");
            Require(replacementStamp > firstStamp,
                "an evicted and re-added path must receive a new global stamp instead of recycling generation one");

            Parallel.For(
                0,
                SessionStore.SnapshotMutationGenerationCapacity * 2,
                index => SessionStore.RecordSnapshotMutation(store.SnapshotPath($"parallel-{index}")));
            Require(
                SessionStore.SnapshotMutationGenerationCount <= SessionStore.SnapshotMutationGenerationCapacity,
                "concurrent mutation admission exceeded the bounded generation capacity");

            var retainedStamps = Enumerable
                .Range(0, SessionStore.SnapshotMutationGenerationCapacity * 2)
                .Select(index => store.SnapshotMutationGeneration($"parallel-{index}"))
                .Where(stamp => stamp > 0)
                .ToArray();
            Require(retainedStamps.Length > 0, "concurrent mutation admission retained no observable entries");
            Require(retainedStamps.Distinct().Count() == retainedStamps.Length,
                "concurrent retained paths should never share a mutation stamp");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    public static void PreservesCorruptAndDuplicateRevisionSemantics()
    {
        var root = TestRoot();
        try
        {
            var store = new SessionStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.SnapshotPath())!);
            File.WriteAllText(store.SnapshotPath(), "{\"persistence_revision\":5,\"engine\":");
            var recovery = SessionStore.CreateDefaultSnapshot();
            store.SaveSnapshotAsync(recovery).GetAwaiter().GetResult();
            Require(recovery.PersistenceRevision == 1,
                "a valid leading revision in corrupt JSON must not block revision-zero recovery");

            File.WriteAllText(
                store.SnapshotPath(),
                "{\"persistence_revision\":1,\"persistence_revision\":2,\"configs\":{},\"engine\":{}}");
            var duplicate = SessionStore.CreateDefaultSnapshot();
            duplicate.PersistenceRevision = 2;
            store.SaveSnapshotAsync(duplicate).GetAwaiter().GetResult();
            Require(duplicate.PersistenceRevision == 3,
                "duplicate revision properties must preserve last-property JsonDocument semantics");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    public static void SerializesProtectedTokensWithoutCloningConfigData()
    {
        var configExtra = ParseExtra(
            """
            {
              "future_flag": true,
              "future_object": { "label": "preserved", "items": [1, 2, null] },
              "future_number": 123.456
            }
            """);
        var snapshotExtra = ParseExtra("{\"snapshot_extension\":{\"enabled\":true}}");
        var config = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = "plain-differential-token",
            Model = "publisher/model-q4",
            ExplicitModelAssignment = true,
            Timeout = 123,
            Temperature = 0.375,
            MaxOutputTokens = 4567,
            ContextLength = 32768,
            ConfiguredContextWindow = 24576,
            HistoryPolicy = "rolling_80",
            ResponseTone = "custom",
            CustomTone = "terse but warm",
            Reasoning = "high",
            NativeStatefulChat = false,
            NativeIdleTtlSeconds = 987,
            PreviousResponseId = "runtime-only-response-id",
            PreserveNativeInputWhitespace = true,
            LastError = "diagnostic",
            LastLatencyMs = 321,
            LastTestOk = true,
            Extra = configExtra
        };
        var snapshot = new ArenaSnapshot
        {
            PersistenceRevision = 19,
            MatchType = "differential",
            Extra = snapshotExtra
        };
        snapshot.Configs["alpha"] = config;
        snapshot.Configs["empty"] = new ModelProviderConfig
        {
            ApiToken = "",
            Model = "empty-token-model",
            Extra = ParseExtra("{\"empty_extension\":\"retained\"}")
        };
        var serializerOptions = SerializerOptions();
        var protectorCalls = 0;
        var persistenceOptions = SnapshotPersistenceJson.CreateOptions(
            serializerOptions,
            token =>
            {
                protectorCalls++;
                return $"protected::{token}";
            });

        var actualJson = JsonSerializer.Serialize(snapshot, persistenceOptions);
        var expectedNode = JsonNode.Parse(JsonSerializer.Serialize(snapshot, serializerOptions))!;
        expectedNode["configs"]!["alpha"]!["api_token"] = "protected::plain-differential-token";
        var actualNode = JsonNode.Parse(actualJson)!;

        Require(JsonNode.DeepEquals(expectedNode, actualNode), "persistence projection changed config or extension data beyond api_token");
        Require(protectorCalls == 1, $"protector should run once for the one non-empty token, observed {protectorCalls}");
        Require(ReferenceEquals(snapshot.Configs["alpha"], config), "serialization replaced the live provider config");
        Require(config.ApiToken == "plain-differential-token", "serialization mutated the live provider token");
        Require(!actualJson.Contains("runtime-only-response-id", StringComparison.Ordinal), "runtime-only provider state reached persistence");

        var roundTrip = JsonSerializer.Deserialize<ArenaSnapshot>(actualJson, serializerOptions)!;
        Require(roundTrip.Configs["alpha"].ApiToken == "protected::plain-differential-token", "protected token did not round trip");
        Require(roundTrip.Configs["alpha"].Extra is { Count: 3 }, "provider extension data did not round trip");
        Require(roundTrip.Extra is { Count: 1 }, "snapshot extension data did not round trip");

        var collisionSnapshot = new ArenaSnapshot
        {
            Extra = ParseExtra(
                "{\"CONFIGS\":{\"collision\":{\"api_token\":\"snapshot-extension-secret\"}},\"PERSISTENCE_REVISION\":999,\"safe_snapshot_extension\":true}")
        };
        collisionSnapshot.Configs["collision"] = new ModelProviderConfig
        {
            ApiToken = "declared-duplicate-secret",
            Model = "duplicate-token-model",
            Extra = ParseExtra("{\"API_TOKEN\":\"extension-duplicate-secret\",\"collision_extension\":42}")
        };
        var collisionJson = JsonSerializer.Serialize(collisionSnapshot, persistenceOptions);
        Require(protectorCalls == 2, $"protector should run once for each authoritative declared token, observed {protectorCalls}");
        Require(!collisionJson.Contains("\"api_token\":\"declared-duplicate-secret\"", StringComparison.Ordinal), "declared duplicate token reached persistence as plaintext");
        Require(!collisionJson.Contains("extension-duplicate-secret", StringComparison.Ordinal), "mixed-case extension-data token collision reached persistence as plaintext");
        Require(!collisionJson.Contains("snapshot-extension-secret", StringComparison.Ordinal), "snapshot config collision reached persistence as plaintext");
        Require(!collisionJson.Contains("\"PERSISTENCE_REVISION\"", StringComparison.Ordinal),
            "snapshot revision collision reached persistence after the authoritative revision");
        var collisionRoundTrip = JsonSerializer.Deserialize<ArenaSnapshot>(collisionJson, serializerOptions)!;
        Require(collisionRoundTrip.Configs["collision"].ApiToken == "protected::declared-duplicate-secret", "the protected authoritative token did not survive collision sanitization");
        Require(collisionRoundTrip.Configs["collision"].Extra is { Count: 1 }, "non-token collision extension data did not round trip");
        Require(collisionRoundTrip.Extra is { Count: 1 }
            && collisionRoundTrip.Extra.ContainsKey("safe_snapshot_extension"),
            "non-reserved snapshot extension data did not round trip");
        Require(collisionRoundTrip.PersistenceRevision == collisionSnapshot.PersistenceRevision,
            "reserved extension data overrode the authoritative persistence revision");
    }

    public static void KeepsSnapshotCheckpointAndTemporaryFilesPrivate()
    {
        var root = TestRoot();
        var previousProtector = SessionStore.ProtectSecret;
        var previousUnprotector = SessionStore.UnprotectSecret;
        FileStream? replacementBlocker = null;
        try
        {
            const string plaintext = "m8-plaintext-token-never-on-disk";
            const string protectedToken = "m8-protected-envelope";
            SessionStore.ProtectSecret = token => token == plaintext ? protectedToken : token;
            SessionStore.UnprotectSecret = token => token == protectedToken ? plaintext : token;

            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            var liveConfig = FullConfig(plaintext, "privacy-model");
            snapshot.Configs["shared"] = liveConfig;
            snapshot.Engine.Messages.Add(new DialogueMessage
            {
                MessageId = "large-private-save-fixture",
                Turn = 1,
                Speaker = "Alpha",
                SpeakerId = "alpha",
                Text = new string('x', 4 * 1024 * 1024),
                Status = "ok",
                Kind = "message",
                CreatedAt = 1
            });
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            Require(snapshot.PersistenceRevision == 1, "initial private save did not advance the live revision");
            Require(ReferenceEquals(snapshot.Configs["shared"], liveConfig), "private save replaced the live config");
            Require(liveConfig.ApiToken == plaintext, "private save mutated the usable live token");
            AssertFileProtected(store.SnapshotPath(), plaintext, protectedToken);

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Configs["shared"].ApiToken == plaintext, "protected token did not unprotect on load");

            var checkpoint = store
                .SaveCheckpointAsync("default", "private checkpoint")
                .GetAwaiter()
                .GetResult();
            AssertFileProtected(checkpoint.Path, plaintext, protectedToken);

            loaded.MatchType = "blocked-replacement";
            replacementBlocker = new FileStream(
                store.SnapshotPath(),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var cancellation = new CancellationTokenSource();
            var blockedSave = store.SaveSnapshotAsync(loaded, cancellationToken: cancellation.Token);
            var snapshotDirectory = Path.GetDirectoryName(store.SnapshotPath())!;
            string? tempPath = null;
            Require(
                SpinWait.SpinUntil(
                    () =>
                    {
                        tempPath = Directory.EnumerateFiles(snapshotDirectory, "snapshot.json.*.tmp").SingleOrDefault();
                        if (tempPath is null)
                        {
                            return false;
                        }

                        try
                        {
                            return File.ReadAllText(tempPath).Contains(protectedToken, StringComparison.Ordinal);
                        }
                        catch (IOException)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(5)),
                "blocked save did not produce a complete inspectable atomic temporary file");
            AssertFileProtected(tempPath!, plaintext, protectedToken);

            cancellation.Cancel();
            var canceled = false;
            try
            {
                blockedSave.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                canceled = true;
            }

            Require(canceled, "blocked atomic replacement did not honor cancellation");
            Require(loaded.PersistenceRevision == 1, "canceled save did not restore the caller revision");
            Require(!Directory.EnumerateFiles(snapshotDirectory, "*.tmp").Any(), "canceled save retained a temporary file");
        }
        finally
        {
            replacementBlocker?.Dispose();
            SessionStore.ProtectSecret = previousProtector;
            SessionStore.UnprotectSecret = previousUnprotector;
            DeleteTestRoot(root);
        }
    }

    public static void ProtectorFailuresRollbackWithoutPublishingPlaintext()
    {
        var root = TestRoot();
        var previousProtector = SessionStore.ProtectSecret;
        var previousUnprotector = SessionStore.UnprotectSecret;
        try
        {
            const string plaintext = "m8-protector-failure-plaintext";
            const string envelope = "m8-protector-failure-envelope";
            SessionStore.ProtectSecret = token => token == plaintext ? envelope : token;
            SessionStore.UnprotectSecret = token => token == envelope ? plaintext : token;

            var store = new SessionStore(root);
            var seed = SessionStore.CreateDefaultSnapshot();
            seed.Configs["shared"] = FullConfig(plaintext, "failure-model");
            store.SaveSnapshotAsync(seed).GetAwaiter().GetResult();
            var originalBytes = File.ReadAllBytes(store.SnapshotPath());
            var pending = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            pending.MatchType = "must-not-publish";

            SessionStore.ProtectSecret = _ => throw new InvalidOperationException("simulated persistence protector failure");
            var protectorFailure = false;
            try
            {
                store.SaveSnapshotAsync(pending).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("simulated persistence protector failure", StringComparison.Ordinal))
            {
                protectorFailure = true;
            }

            Require(protectorFailure, "protector exception was not surfaced unchanged");
            Require(pending.PersistenceRevision == 1, "protector failure did not restore the caller revision");
            Require(originalBytes.SequenceEqual(File.ReadAllBytes(store.SnapshotPath())), "protector failure replaced the durable snapshot");
            Require(pending.Configs["shared"].ApiToken == plaintext, "protector failure mutated the live token");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "protector failure retained a temporary file");
            Require(
                !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Any(path => File.ReadAllText(path).Contains(plaintext, StringComparison.Ordinal)),
                "protector failure published plaintext in a persistence artifact");
        }
        finally
        {
            SessionStore.ProtectSecret = previousProtector;
            SessionStore.UnprotectSecret = previousUnprotector;
            DeleteTestRoot(root);
        }
    }

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static void AssertReaderOutcome(
        string name,
        byte[] json,
        long? expected,
        Type? exceptionType,
        int maximumReadBytes = int.MaxValue,
        bool seekable = true)
    {
        using var stream = new FragmentedReadStream(json, maximumReadBytes, seekable);
        try
        {
            var actual = PersistenceRevisionReader.ReadAsync(stream).AsTask().GetAwaiter().GetResult();
            Require(exceptionType is null, $"{name}: expected {exceptionType?.Name} but returned {actual}");
            Require(actual == expected, $"{name}: expected revision {expected} but observed {actual}");
        }
        catch (Exception ex) when (exceptionType is not null && exceptionType.IsAssignableFrom(ex.GetType()))
        {
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{name}: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static (long? Value, Type? ExceptionType) LegacyRevisionOutcome(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64
                });
            var value = document.RootElement.TryGetProperty("persistence_revision", out var revision)
                && revision.ValueKind == JsonValueKind.Number
                && revision.TryGetInt64(out var parsed)
                    ? Math.Max(0, parsed)
                    : 0;
            return (value, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType());
        }
    }

    private static bool ExceptionsMatch(Type? expected, Type? actual) =>
        expected is null ? actual is null : actual is not null && expected.IsAssignableFrom(actual);

    private static byte[] LargeSnapshotJson(int megabytes, long revision)
    {
        var requestedBytes = megabytes * 1024 * 1024;
        var prefix = Utf8($"{{\"persistence_revision\":{revision},\"messages\":[");
        var entry = Utf8("{\"speaker\":\"alpha\",\"text\":\"bounded fixture payload\"},");
        var suffix = Utf8("{}]}");
        using var stream = new MemoryStream(requestedBytes + 1024);
        stream.Write(prefix);
        while (stream.Length + entry.Length + suffix.Length <= requestedBytes)
        {
            stream.Write(entry);
        }

        stream.Write(suffix);
        return stream.ToArray();
    }

    private static byte[] LargeStringSnapshotJson(int stringBytes, long revision)
    {
        var prefix = Utf8("{\"large\":\"");
        var suffix = Utf8($"\",\"persistence_revision\":{revision}}}");
        var json = GC.AllocateUninitializedArray<byte>(prefix.Length + stringBytes + suffix.Length);
        prefix.CopyTo(json, 0);
        json.AsSpan(prefix.Length, stringBytes).Fill((byte)'x');
        suffix.CopyTo(json, prefix.Length + stringBytes);
        return json;
    }

    private static RevisionMeasurement MeasureRevisionReader(byte[] json, bool useLegacyDocument)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        long revision;
        using (var stream = new MemoryStream(json, writable: false))
        {
            if (useLegacyDocument)
            {
                using var document = JsonDocument.Parse(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                        MaxDepth = 64
                    });
                revision = document.RootElement.TryGetProperty("persistence_revision", out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetInt64(out var value)
                        ? Math.Max(0, value)
                        : 0;
            }
            else
            {
                revision = PersistenceRevisionReader.ReadAsync(stream).AsTask().GetAwaiter().GetResult();
            }
        }

        watch.Stop();
        return new RevisionMeasurement(
            revision,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            watch.Elapsed.TotalMilliseconds);
    }

    private static RevisionMeasurement MeasureLegacyRevisionIsolated(byte[] json)
    {
        RevisionMeasurement? measurement = null;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    measurement = MeasureRevisionReader(json, useLegacyDocument: true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("isolated legacy revision receipt failed", failure);
        }

        return measurement ?? throw new InvalidOperationException("isolated legacy revision receipt produced no result");
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static Dictionary<string, JsonElement> ParseExtra(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static ModelProviderConfig FullConfig(string apiToken, string model) => new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        ApiToken = apiToken,
        Model = model,
        ExplicitModelAssignment = true,
        Timeout = 71,
        Temperature = 0.25,
        MaxOutputTokens = 2048,
        ContextLength = 16384,
        ConfiguredContextWindow = 12288,
        HistoryPolicy = "rolling_80",
        ResponseTone = "default",
        CustomTone = "",
        Reasoning = "medium",
        NativeStatefulChat = true,
        NativeIdleTtlSeconds = 360,
        LastError = "",
        LastLatencyMs = 42,
        LastTestOk = true,
        Extra = ParseExtra("{\"future_provider_value\":{\"nested\":[1,true,null]}}")
    };

    private static void AssertFileProtected(string path, string plaintext, string protectedToken)
    {
        var bytes = File.ReadAllBytes(path);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = Encoding.UTF8.GetBytes(protectedToken);
        Require(bytes.AsSpan().IndexOf(plaintextBytes) < 0, $"plaintext token reached {Path.GetFileName(path)}");
        Require(bytes.AsSpan().IndexOf(protectedBytes) >= 0, $"protected token was absent from {Path.GetFileName(path)}");
    }

    private static string TestRoot() => Path.Combine(
        Path.GetTempPath(),
        "ai-arena-session-persistence-optimization-tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record RevisionMeasurement(long Revision, long AllocatedBytes, double ElapsedMs);

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadBytes, bool seekable) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? inner.Length : throw new NotSupportedException();
        public override long Position
        {
            get => seekable ? inner.Position : throw new NotSupportedException();
            set
            {
                if (!seekable)
                {
                    throw new NotSupportedException();
                }

                inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumReadBytes));

        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer[..Math.Min(buffer.Length, maximumReadBytes)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadBytes)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            seekable ? inner.Seek(offset, origin) : throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
