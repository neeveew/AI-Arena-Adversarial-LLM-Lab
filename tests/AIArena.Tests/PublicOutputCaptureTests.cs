using AIArena.Core.Providers;

internal static class PublicOutputCaptureTests
{
    internal static void FreezeRetainsAcceptedTextBeforeQueuedPresentation()
    {
        var queued = new List<string>();
        var capture = new ModelPublicOutputCapture(new Recording(queued.Add));
        capture.Report(" \tFirst");
        capture.Report("\r\n    second ");
        var frozen = capture.Seal();
        Task.Run(() => capture.Report("late provider text")).GetAwaiter().GetResult();
        Require(frozen == " \tFirst\r\n    second " && string.Concat(queued) == frozen
                && capture.IsSealed && capture.Seal() == frozen && capture.Tail(7) == "second ",
            "Capture changed accepted whitespace or admitted text after the terminal boundary.");

        var failingObserver = new ModelPublicOutputCapture(new Recording(_ => throw new InvalidOperationException("optional observer")));
        failingObserver.Report("accepted");
        Require(failingObserver.Seal() == "accepted", "An optional presentation failure erased accepted output or escaped the provider path.");
    }

    internal static void BoundedCaptureReportsPrefixTruncation()
    {
        var capture = new ModelPublicOutputCapture(maximumCharacters: 8);
        capture.Report("first ");
        capture.Report("second");
        capture.Report("third");
        Require(capture.Characters == 8 && capture.Truncated && capture.Seal() == "first se"
                && capture.Tail(100) == "first se" && capture.Tail(0) == "",
            "Bounded capture lost its prefix, exceeded its cap, or hid truncation evidence.");
        var exact = new ModelPublicOutputCapture(maximumCharacters: 3);
        exact.Report("abc");
        exact.Report("");
        Require(!exact.Truncated && exact.Seal() == "abc", "An exact-fit capture fabricated truncation.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Recording(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
