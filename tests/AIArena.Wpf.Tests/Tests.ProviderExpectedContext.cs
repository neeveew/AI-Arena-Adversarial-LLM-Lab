using System.IO;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderConfigurationRejectsStaleDiscoveryContext()
    {
        foreach (var changed in new[] { "session", "endpoint", "api-mode", "token" })
        {
            using var fixture = new ProviderExpectedContextFixture();
            fixture.OperationLock.Wait();
            var pending = fixture.Service.ApplyAsync(
                ProviderControlPatch(apiMode: ModelProviderApiModes.LmStudioNative),
                expectedContext: fixture.Captured);
            Require(!pending.IsCompleted, "discovery application should wait behind the existing mutation owner");
            if (changed == "session")
            {
                fixture.Active = fixture.Sessions.Single(session => session.Id == "session-b");
            }
            else
            {
                var snapshot = fixture.Store.LoadSnapshotAsync("session-a").GetAwaiter().GetResult()!;
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = ProviderExpectedContextFixture.Connection(
                    baseUrl: changed == "endpoint" ? "http://127.0.0.1:2234/v1" : null,
                    apiMode: changed == "api-mode" ? ModelProviderApiModes.OllamaNative : null,
                    apiToken: changed == "token" ? "new-test-credential" : null);
                fixture.Store.SaveSnapshotAsync(snapshot, "session-a").GetAwaiter().GetResult();
            }

            var beforeA = File.ReadAllBytes(fixture.Store.SnapshotPath("session-a"));
            var beforeB = File.ReadAllBytes(fixture.Store.SnapshotPath("session-b"));
            fixture.OperationLock.Release();
            var result = pending.GetAwaiter().GetResult();
            Require(!result.Ok && result.ErrorCode == "connection_changed" && result.ChangedFields.Count == 0,
                $"a changed {changed} must reject a previously captured discovery result");
            Require(beforeA.SequenceEqual(File.ReadAllBytes(fixture.Store.SnapshotPath("session-a")))
                && beforeB.SequenceEqual(File.ReadAllBytes(fixture.Store.SnapshotPath("session-b"))),
                $"stale {changed} discovery must not mutate either session");
            Require(fixture.Refreshes == 0, "a rejected detection must not announce a successful connection refresh");
        }
    }

    static void ProviderConfigurationAppliesCurrentDiscoveryAndPreservesAssignments()
    {
        using var fixture = new ProviderExpectedContextFixture();
        var before = fixture.Store.LoadSnapshotAsync("session-a").GetAwaiter().GetResult()!;
        var result = fixture.Service.ApplyAsync(
            ProviderControlPatch(apiMode: ModelProviderApiModes.LmStudioNative),
            expectedContext: fixture.Captured).GetAwaiter().GetResult();
        var after = fixture.Store.LoadSnapshotAsync("session-a").GetAwaiter().GetResult()!;
        Require(result.Ok && result.ChangedFields.SequenceEqual(new[] { "apiMode" }) && fixture.Refreshes == 1,
            "a discovery result for the current connection should update the native mode once");
        Require(after.PersistenceRevision == before.PersistenceRevision + 1,
            "applying current provider detection should make one atomic snapshot mutation");
        Require(after.Configs[ModelProviderRouting.SharedConfigKey].Model == "shared-model"
            && after.Configs["alpha"].Model == "alpha-model"
            && after.Configs["alpha"].ExplicitModelAssignment
            && after.Configs["alpha"].Temperature == 1.2
            && after.Configs["alpha"].MaxOutputTokens == 777
            && !after.Engine.DefaultForUnassignedAgentsEnabled,
            "connection detection must retain the user's model assignments, role generation overrides, and default policy");
    }

    static void ProviderConfigurationRechecksBusyAfterWaiting()
    {
        using var fixture = new ProviderExpectedContextFixture();
        fixture.OperationLock.Wait();
        var pending = fixture.Service.ApplyAsync(
            ProviderControlPatch(apiMode: ModelProviderApiModes.LmStudioNative),
            expectedContext: fixture.Captured);
        fixture.Busy = true;
        var before = File.ReadAllBytes(fixture.Store.SnapshotPath("session-a"));
        fixture.OperationLock.Release();
        var result = pending.GetAwaiter().GetResult();
        Require(!result.Ok && result.ErrorCode == "busy" && fixture.Refreshes == 0
            && before.SequenceEqual(File.ReadAllBytes(fixture.Store.SnapshotPath("session-a"))),
            "arena activity starting while detection waits must prevent the connection mutation");
    }

    private sealed class ProviderExpectedContextFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-context", Guid.NewGuid().ToString("N"));
        public SessionStore Store { get; }
        public SemaphoreSlim OperationLock { get; } = new(1, 1);
        public IReadOnlyList<SessionSummary> Sessions { get; }
        public SessionSummary? Active { get; set; }
        public bool Busy { get; set; }
        public int Refreshes { get; private set; }
        public ProviderOperationContext Captured { get; }
        public ProviderConfigurationControlService Service { get; }

        public ProviderExpectedContextFixture()
        {
            Store = new SessionStore(root);
            foreach (var id in new[] { "session-a", "session-b" })
            {
                var snapshot = SessionStore.CreateDefaultSnapshot();
                var shared = Connection();
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = shared;
                snapshot.Engine.DefaultForUnassignedAgentsEnabled = false;
                ProviderConfigurationControlService.SaveRoleModelConfig(
                    snapshot.Configs, "alpha", "alpha-model", shared,
                    temperatureOverride: 1.2, maxOutputTokensOverride: 777);
                Store.SaveSnapshotAsync(snapshot, id).GetAwaiter().GetResult();
            }

            Sessions = Store.ListSessionsAsync().GetAwaiter().GetResult();
            Active = Sessions.Single(session => session.Id == "session-a");
            Captured = new ProviderOperationContext(Active.Id, Connection());
            Service = new ProviderConfigurationControlService(
                Store, new EventLogStore(root), OperationLock, () => Active, () => Busy,
                (_, _, _) => { Refreshes++; return Task.CompletedTask; });
        }

        public static ModelProviderConfig Connection(string? baseUrl = null, string? apiMode = null, string? apiToken = null) => new()
        {
            BaseUrl = baseUrl ?? "http://127.0.0.1:1234/v1",
            ApiMode = apiMode ?? ModelProviderApiModes.OpenAiCompatible,
            ApiToken = apiToken ?? "old-test-credential",
            Model = "shared-model"
        };

        public void Dispose()
        {
            OperationLock.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
