using System.IO;
using AIArena.Core.Models;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void SessionRestorationRemembersExistingConfiguration()
    {
        SessionSummary Session(string id) => new(id, $"{id}/snapshot.json", true, 0, 0, 0, DateTimeOffset.UtcNow);
        var factory = Session("default");
        var remembered = Session("remote-work");
        var active = Session("local-work");
        SessionSummary[] sessions = [factory, remembered, active];

        WithTempSettingsStore(store =>
        {
            var settings = store.Load();
            Require(settings.LastSessionId.Length == 0, "legacy settings should not invent a remembered session");
            Require(store.RememberSession(settings, " remote-work "), "a completed selection should persist its existing session identity");
            Require(!store.RememberSession(settings, "remote-work"), "refreshing the same session should not rewrite settings");
            var restored = store.Load();
            Require(restored.LastSessionId == "remote-work", "the selected session should survive a settings-store restart");
            Require(ReferenceEquals(SessionLoadCoordinator.ResolveSession(sessions, null, null, restored.LastSessionId), remembered),
                "startup should restore the remembered session instead of the factory default, preserving its provider configuration");
            Require(ReferenceEquals(SessionLoadCoordinator.ResolveSession(sessions, "local-work", null, restored.LastSessionId), active),
                "an explicit requested session should take precedence over remembered state");
            Require(ReferenceEquals(SessionLoadCoordinator.ResolveSession(sessions, null, "local-work", restored.LastSessionId), active),
                "background enumeration should keep the current active session ahead of remembered state");
            Require(ReferenceEquals(SessionLoadCoordinator.ResolveSession(sessions, null, null, "deleted-session"), factory),
                "a deleted remembered session should fall back to an existing default");
            Require(ReferenceEquals(SessionLoadCoordinator.ResolveSession([remembered], null, null, "deleted-session"), remembered),
                "a missing remembered session and default should fall back to the available session");
            Require(SessionLoadCoordinator.ResolveSession([], null, null, restored.LastSessionId) is null,
                "an empty session list should not manufacture a configuration");

            Require(!store.RememberSession(settings, "../remote-work"), "an invalid reference must not be transformed into another session identity");
            Require(store.Load().LastSessionId == "remote-work", "invalid references must not replace the remembered session");
            store.Save(new WpfSettings { LastSessionId = "../remote-work" });
            Require(store.Load().LastSessionId.Length == 0, "unsafe imported session references should be discarded");
        });
    }

    static void SessionRestorationPreservesPreferenceAfterWriteFailure()
    {
        WithTempSettingsStore(store =>
        {
            var settings = new WpfSettings { LastSessionId = "previous-session" };
            Directory.CreateDirectory(store.SettingsPath);
            var failed = false;
            try
            {
                store.RememberSession(settings, "new-session");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed = true;
            }

            Require(failed, "the blocked settings destination should report that restart restoration could not be saved");
            Require(settings.LastSessionId == "previous-session", "a failed preference save must not pretend the new session was remembered");
        });
    }
}
