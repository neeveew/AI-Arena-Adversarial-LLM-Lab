using System.Security.Cryptography;
using System.Text.Json;

namespace AIArena.VerificationLab;

internal static class VerificationLabEvidence
{
    // Full QA artifacts are owned by the seal runner and validated through
    // ArenaContractCodec. The lab exports only this aggregate fingerprint;
    // individual content-free request hashes never leave the process.
    public static string RequestCaptureFingerprint(IReadOnlyList<ScriptedRequestCapture> captures)
    {
        var canonical = captures
            .OrderBy(item => item.Sequence)
            .Select(item => new
            {
                item.Sequence,
                item.Method,
                item.Path,
                item.BodyLength,
                item.BodySha256,
                item.ModelEvidence,
                item.MessageCount,
                item.Streaming,
                item.AuthorizationSupplied,
                Fault = item.Fault.ToString()
            })
            .ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
