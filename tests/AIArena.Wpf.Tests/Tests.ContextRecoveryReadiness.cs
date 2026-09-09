using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void SnapshotViewMapperPreservesUnresolvedContextRecoveryEvidence()
    {
        var session = new SessionSummary("context-recovery", "snapshot.json", true, 0, 0, 0, DateTimeOffset.UtcNow);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        Require(!SnapshotViewMapper.FromCore(session, snapshot).HasUnresolvedContextFailure,
            "a fresh session should not project a context-recovery blocker");
        var failure = new DialogueMessage
        {
            Turn = 1,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Status = "error",
            Kind = "error",
            Text = "Context limit reached.",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["completion_failure_kind"] = JsonSerializer.SerializeToElement("context_limit_exceeded")
            }
        };
        snapshot.Engine.Messages.Add(failure);
        snapshot.Engine.Messages.Add(new TranscriptService().CreateOperatorMessage("A later message does not resolve the failure.", 2));
        Require(SnapshotViewMapper.FromCore(session, snapshot).HasUnresolvedContextFailure,
            "readiness evidence must retain an unresolved context failure even after later transcript messages");
        foreach (var disposition in new[] { "skipped", "retried" })
        {
            failure.Metadata[ContextRecoveryService.RecoveryDispositionMetadataKey] = JsonSerializer.SerializeToElement(disposition);
            Require(!SnapshotViewMapper.FromCore(session, snapshot).HasUnresolvedContextFailure,
                $"Core's {disposition} recovery disposition must clear the projected blocker");
        }
        failure.Metadata.Remove(ContextRecoveryService.RecoveryDispositionMetadataKey);
        failure = new DialogueMessage { Turn = failure.Turn, Speaker = failure.Speaker, SpeakerId = failure.SpeakerId, Kind = failure.Kind, Text = failure.Text, Metadata = failure.Metadata, Status = "ok" };
        snapshot.Engine.Messages[0] = failure;
        Require(!SnapshotViewMapper.FromCore(session, snapshot).HasUnresolvedContextFailure,
            "historical failure metadata on a successful result must not create a blocker");
        failure = new DialogueMessage { Turn = failure.Turn, Speaker = failure.Speaker, SpeakerId = failure.SpeakerId, Kind = failure.Kind, Text = failure.Text, Metadata = failure.Metadata, Status = "error" };
        snapshot.Engine.Messages[0] = failure;
        failure.Metadata["completion_failure_kind"] = JsonSerializer.SerializeToElement("provider_loading");
        Require(!SnapshotViewMapper.FromCore(session, snapshot).HasUnresolvedContextFailure,
            "other provider failures must not be reclassified as context-recovery failures");
    }
}
