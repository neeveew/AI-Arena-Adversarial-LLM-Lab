using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Services;

namespace AIArena.VerificationLab;

internal static class VerificationEvidenceValidator
{
    private const int MaximumEvidenceBytes = 4 * 1024 * 1024;

    public static async Task<int> ValidateFileAsync(
        string path,
        TextWriter output,
        CancellationToken cancellationToken) =>
        await ValidateCoreAsync(path, repositoryRoot: null, output, cancellationToken);

    public static async Task<int> ValidateCurrentFileAsync(
        string path,
        string repositoryRoot,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            await output.WriteLineAsync("FAIL current.repository_unavailable");
            return 2;
        }

        return await ValidateCoreAsync(path, repositoryRoot, output, cancellationToken);
    }

    private static async Task<int> ValidateCoreAsync(
        string path,
        string? repositoryRoot,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await output.WriteLineAsync("FAIL evidence.path");
            return 2;
        }

        string json;
        try
        {
            json = await ReadBoundedUtf8Async(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            await output.WriteLineAsync("FAIL evidence.not_found");
            return 2;
        }
        catch (DirectoryNotFoundException)
        {
            await output.WriteLineAsync("FAIL evidence.not_found");
            return 2;
        }
        catch (InvalidDataException)
        {
            await output.WriteLineAsync("FAIL evidence.too_large");
            return 2;
        }
        catch (DecoderFallbackException)
        {
            await output.WriteLineAsync("FAIL evidence.utf8");
            return 2;
        }
        catch (IOException)
        {
            await output.WriteLineAsync("FAIL evidence.read");
            return 2;
        }
        catch (UnauthorizedAccessException)
        {
            await output.WriteLineAsync("FAIL evidence.read");
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            await output.WriteLineAsync("FAIL evidence.path");
            return 2;
        }

        if (!ArenaContractCodec.TryDeserialize<ArenaQaEvidenceContract>(json, out var contract, out var issues))
        {
            foreach (var issue in issues)
            {
                // Codes and JSON paths are authoritative and content-free. Do
                // not echo validation messages because the input is untrusted.
                await output.WriteLineAsync($"FAIL {issue.Code} {issue.Path}");
            }

            return 1;
        }

        var bundleIssues = await VerificationEvidenceBundleValidator.ValidateAsync(
            path,
            contract!,
            cancellationToken);
        foreach (var issue in bundleIssues)
        {
            await output.WriteLineAsync(issue.SafeDisplay);
        }

        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var currentIssues = await VerificationRepositoryCurrentValidator.ValidateAsync(
                repositoryRoot,
                contract!,
                cancellationToken);
            foreach (var issue in currentIssues)
            {
                await output.WriteLineAsync(issue.SafeDisplay);
            }
            if (!currentIssues.IsEmpty || !bundleIssues.IsEmpty)
            {
                return 1;
            }
        }
        else if (!bundleIssues.IsEmpty)
        {
            return 1;
        }

        var mode = repositoryRoot is null ? "bundle" : "bundle+current";
        await output.WriteLineAsync($"PASS {ArenaContractSchemas.QaEvidence} {mode} ({contract!.Gates.Length} gates)");
        return 0;
    }

    private static async Task<string> ReadBoundedUtf8Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumEvidenceBytes)
        {
            throw new InvalidDataException("Evidence exceeds the bounded read limit.");
        }

        using var buffer = new MemoryStream(capacity: (int)Math.Min(stream.Length, MaximumEvidenceBytes));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumEvidenceBytes)
            {
                throw new InvalidDataException("Evidence exceeds the bounded read limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        return json.Length > 0 && json[0] == '\uFEFF' ? json[1..] : json;
    }
}
