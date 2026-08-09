using AIArena.VerificationLab;

if (args.Length > 0)
{
    if (args.Length == 2 && args[0].Equals("--validate-evidence", StringComparison.Ordinal))
    {
        return await VerificationEvidenceValidator.ValidateFileAsync(args[1], Console.Out, CancellationToken.None);
    }

    if (args.Length == 3 && args[0].Equals("--validate-evidence-current", StringComparison.Ordinal))
    {
        return await VerificationEvidenceValidator.ValidateCurrentFileAsync(
            args[1],
            args[2],
            Console.Out,
            CancellationToken.None);
    }

    if (args.Length == 2 && args[0].Equals("--measure", StringComparison.Ordinal))
    {
        return await VerificationPerformanceRunner.RunAndWriteAsync(args[1], Console.Out, CancellationToken.None);
    }

    Console.Error.WriteLine("Usage: AIArena.VerificationLab [--validate-evidence <qa-evidence.json> | --validate-evidence-current <qa-evidence.json> <repoRoot> | --measure <output.json>]");
    return 2;
}

var result = await VerificationLabHarness.RunAsync(Console.Out, CancellationToken.None);
return result.FailedChecks == 0 ? 0 : 1;
