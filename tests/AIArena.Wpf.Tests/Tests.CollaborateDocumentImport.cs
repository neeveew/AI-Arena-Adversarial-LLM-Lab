using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

internal static partial class Program
{
    static void CollaborateDocumentImportBoundsFragmentedStreamsAndBom()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var hugeBytes = Encoding.UTF8.GetBytes(new string('x', 20_000));
                var bomBytes = Encoding.Unicode.GetPreamble()
                    .Concat(Encoding.Unicode.GetBytes("BOM text ✓"))
                    .ToArray();
                FragmentedDocumentStream? hugeStream = null;
                var fixture = CreateDocumentImportFixture((path, _) =>
                {
                    var fileName = Path.GetFileName(path);
                    Stream stream = fileName.Equals("huge.txt", StringComparison.OrdinalIgnoreCase)
                        ? hugeStream = new FragmentedDocumentStream(hugeBytes, maxFragmentBytes: 7, yieldBetweenReads: true)
                        : new FragmentedDocumentStream(bomBytes, maxFragmentBytes: 1, yieldBetweenReads: true);
                    return Task.FromResult(stream);
                });

                var receipt = PumpDocumentImportTask(fixture.Coordinator.ImportDocumentsAsync(
                [
                    FixtureDocumentPath("huge.txt"),
                    FixtureDocumentPath("unicode.md")
                ]));

                Require(receipt.ImportedCount == 2 && receipt.Issues.Count == 0,
                    "fragmented UTF text documents should import successfully");
                Require(fixture.Coordinator.DebugToolDocuments.Count == 2,
                    "bounded document import should publish both successful files");
                var huge = fixture.Coordinator.DebugToolDocuments.Single(document => document.Title == "huge.txt");
                var unicode = fixture.Coordinator.DebugToolDocuments.Single(document => document.Title == "unicode.md");
                Require(huge.Text.Length == 6000 && huge.Truncated,
                    "a huge document should retain exactly the bounded 6,000-character prefix and disclose truncation");
                Require(hugeStream is not null && hugeStream.BytesRead < hugeBytes.Length,
                    "bounded document import should stop reading a huge stream after the chars-plus-one decision boundary");
                Require(unicode.Text == "BOM text ✓" && !unicode.Truncated,
                    "document import should detect a fragmented UTF-16 BOM and preserve its text");
                Require(fixture.Statuses.SequenceEqual(
                [
                    "Importing document 1 of 2: huge.txt.",
                    "Imported document 1 of 2: huge.txt.",
                    "Importing document 2 of 2: unicode.md.",
                    "Imported document 2 of 2: unicode.md.",
                    "Imported 2 documents."
                ]),
                    "document import progress should be deterministic from selection through completion");
            });
        });
    }

    static void CollaborateDocumentImportPreservesMixedResultsAndSanitizesErrors()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                const string privateError = "private path C:\\Users\\Secret\\locked.txt";
                var fixture = CreateDocumentImportFixture((path, _) =>
                {
                    return Path.GetFileName(path).ToLowerInvariant() switch
                    {
                        "first.txt" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("first success"))),
                        "locked.txt" => Task.FromException<Stream>(new IOException(privateError)),
                        "invalid.txt" => Task.FromResult<Stream>(new MemoryStream([(byte)0xC3, (byte)0x28])),
                        "last.md" => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("last success"))),
                        _ => throw new InvalidOperationException("unsupported files must fail before the stream seam")
                    };
                });
                var fixtureRoot = Path.Combine(Path.GetTempPath(), "collaborate-import-private-root");

                var receipt = PumpDocumentImportTask(fixture.Coordinator.ImportDocumentsAsync(
                [
                    Path.Combine(fixtureRoot, "first.txt"),
                    Path.Combine(fixtureRoot, "locked.txt"),
                    Path.Combine(fixtureRoot, "unsafe.exe"),
                    Path.Combine(fixtureRoot, "invalid.txt"),
                    Path.Combine(fixtureRoot, "last.md")
                ]));

                Require(receipt.ImportedCount == 2 && receipt.AddedCount == 2 && receipt.Issues.Count == 3,
                    "mixed document imports should keep both successes and report every failed file");
                Require(fixture.Coordinator.DebugToolDocuments.Select(document => document.Title).SequenceEqual(["first.txt", "last.md"]),
                    "a later failure should not roll back prior documents and a later success should still publish");
                Require(receipt.Issues.Any(issue => issue.FileName == "locked.txt" && issue.Message.Contains("Close it", StringComparison.Ordinal))
                        && receipt.Issues.Any(issue => issue.FileName == "unsafe.exe" && issue.Message.Contains("Choose TXT", StringComparison.Ordinal))
                        && receipt.Issues.Any(issue => issue.FileName == "invalid.txt" && issue.Message.Contains("Save the file as UTF-8", StringComparison.Ordinal)),
                    "each failed file should receive a safe, actionable error matched to its failure class");
                var visibleStatuses = string.Join("\n", fixture.Statuses);
                Require(!visibleStatuses.Contains(privateError, StringComparison.Ordinal)
                        && !visibleStatuses.Contains(fixtureRoot, StringComparison.OrdinalIgnoreCase)
                        && visibleStatuses.Contains("locked.txt", StringComparison.Ordinal)
                        && visibleStatuses.Contains("unsafe.exe", StringComparison.Ordinal)
                        && visibleStatuses.Contains("invalid.txt", StringComparison.Ordinal),
                    "document errors should identify the safe file name without leaking raw exceptions or full paths");
            });
        });
    }

    static void CollaborateDocumentImportDeduplicatesAndEnforcesOverallLimit()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var opens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var fixture = CreateDocumentImportFixture((path, _) =>
                {
                    opens[path] = opens.GetValueOrDefault(path) + 1;
                    return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes($"content {Path.GetFileName(path)}")));
                });
                var duplicatePath = FixtureDocumentPath("duplicate.txt");
                var duplicateAlias = Path.Combine(Path.GetDirectoryName(duplicatePath)!, ".", "duplicate.txt");

                var duplicateReceipt = PumpDocumentImportTask(fixture.Coordinator.ImportDocumentsAsync(
                    [duplicatePath, duplicateAlias]));
                Require(duplicateReceipt.ImportedCount == 1
                        && duplicateReceipt.DuplicateCount == 1
                        && fixture.Coordinator.DebugToolDocuments.Count == 1
                        && opens.Values.Sum() == 1,
                    "canonical duplicate selections should open and publish the same file exactly once");

                var updateReceipt = PumpDocumentImportTask(fixture.Coordinator.ImportDocumentsAsync([duplicatePath]));
                Require(updateReceipt.ImportedCount == 1
                        && updateReceipt.UpdatedCount == 1
                        && updateReceipt.AddedCount == 0
                        && fixture.Coordinator.DebugToolDocuments.Count == 1,
                    "re-importing an existing path should update its single document entry instead of duplicating it");

                var limitReceipt = PumpDocumentImportTask(fixture.Coordinator.ImportDocumentsAsync(
                    Enumerable.Range(1, 5).Select(index => FixtureDocumentPath($"limit-{index}.txt")).ToArray()));
                Require(limitReceipt.ImportedCount == 4
                        && limitReceipt.Issues.Count == 1
                        && fixture.Coordinator.DebugToolDocuments.Count == 5
                        && limitReceipt.Issues[0].Message.Contains("5-document limit", StringComparison.Ordinal),
                    "document import should enforce the five-document overall cap without opening the excess file");

                var boundedOpens = 0;
                var boundedFixture = CreateDocumentImportFixture((_, _) =>
                {
                    boundedOpens++;
                    return Task.FromException<Stream>(new IOException("private failure detail"));
                });
                var boundedReceipt = PumpDocumentImportTask(boundedFixture.Coordinator.ImportDocumentsAsync(
                    Enumerable.Range(1, 40).Select(index => FixtureDocumentPath($"bounded-{index}.txt")).ToArray()));
                Require(boundedReceipt.ImportedCount == 0
                        && boundedReceipt.Issues.Count == 32
                        && boundedReceipt.OmittedSelectionCount == 8
                        && boundedOpens == 32,
                    "an oversized all-failing selection should bound stream opens and retained issues at 32 while reporting the omitted remainder");

                var reimportOpenCount = 0;
                var slowReimport = new BlockingDocumentStream(Encoding.UTF8.GetBytes("replacement that must not publish"));
                var transitionFixture = CreateDocumentImportFixture((_, _) =>
                {
                    reimportOpenCount++;
                    return Task.FromResult<Stream>(reimportOpenCount == 1
                        ? new MemoryStream(Encoding.UTF8.GetBytes("seed"))
                        : slowReimport);
                });
                var transitionPath = FixtureDocumentPath("transition.txt");
                PumpDocumentImportTask(transitionFixture.Coordinator.ImportDocumentsAsync([transitionPath]));
                var slowReimportTask = transitionFixture.Coordinator.ImportDocumentsAsync([transitionPath]);
                Require(slowReimport.WaitingForCancellation.Task.IsCompleted,
                    "the context-transition fixture did not reach its awaited re-import");
                transitionFixture.Coordinator.Clear();
                var transitionReceipt = PumpDocumentImportTask(slowReimportTask);
                Require(transitionReceipt.Cancelled
                        && transitionFixture.Coordinator.DebugToolDocuments.Count == 0
                        && transitionFixture.StatusText.Text == "Ready."
                        && !transitionFixture.Statuses.Any(status => status.Contains("replacement", StringComparison.Ordinal)),
                    "clearing context during a slow re-import should cancel atomically, avoid stale-index publication, and preserve the transition status");
            });
        });
    }

    static void CollaborateDocumentImportCancellationIsResponsiveAndAtomic()
    {
        RunStaTest(() =>
        {
            RunWithDispatcherContext(() =>
            {
                var blockingStream = new BlockingDocumentStream(Encoding.UTF8.GetBytes("partial current file"));
                var fixture = CreateDocumentImportFixture((path, _) =>
                {
                    return Path.GetFileName(path).Equals("first.txt", StringComparison.OrdinalIgnoreCase)
                        ? Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("first complete file")))
                        : Task.FromResult<Stream>(blockingStream);
                });
                var importTask = fixture.Coordinator.ImportDocumentsAsync(
                [
                    FixtureDocumentPath("first.txt"),
                    FixtureDocumentPath("blocked.txt")
                ]);

                Require(blockingStream.WaitingForCancellation.Task.IsCompleted
                        && fixture.Coordinator.DebugIsDocumentImporting
                        && fixture.AddDocumentButton.IsEnabled
                        && Equals(fixture.AddDocumentButton.Content, "Cancel import")
                        && AutomationProperties.GetName(fixture.AddDocumentButton) == "Cancel document import",
                    "a slow current file should expose an enabled, accessible cancellation action");
                Require(!fixture.ClearDocumentsButton.IsEnabled
                        && fixture.CalculatorText.IsEnabled
                        && fixture.RunCalculatorButton.IsEnabled,
                    "an active import should disable only document mutation controls, not the rest of Collaborate tools");

                var dispatcherResponded = false;
                var responseFrame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    dispatcherResponded = true;
                    responseFrame.Continue = false;
                }, DispatcherPriority.Background);
                Dispatcher.PushFrame(responseFrame);
                Require(dispatcherResponded && !importTask.IsCompleted,
                    "the dispatcher should remain responsive while an injected slow document stream is awaiting data");

                fixture.Coordinator.CancelDocumentImport();
                Require(!fixture.AddDocumentButton.IsEnabled
                        && AutomationProperties.GetName(fixture.AddDocumentButton) == "Cancelling document import",
                    "cancellation should expose a prompt in-progress state and suppress repeated cancellation clicks");
                var receipt = PumpDocumentImportTask(importTask);

                Require(receipt.Cancelled
                        && receipt.ImportedCount == 1
                        && fixture.Coordinator.DebugToolDocuments.Count == 1
                        && fixture.Coordinator.DebugToolDocuments[0].Title == "first.txt",
                    "cancellation should preserve prior successes without publishing the partial current file");
                Require(!fixture.Coordinator.DebugIsDocumentImporting
                        && fixture.AddDocumentButton.IsEnabled
                        && Equals(fixture.AddDocumentButton.Content, "Add Doc")
                        && fixture.ClearDocumentsButton.IsEnabled,
                    "document controls should return to their deterministic idle state after cancellation finishes");
                Require(fixture.Statuses[^1] == "Document import cancelled. Imported 1 document; blocked.txt was not added.",
                    "cancellation should finish with a safe status that explains retained and discarded work");

                var delayedOpen = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
                var delayedFixture = CreateDocumentImportFixture((_, _) => delayedOpen.Task);
                var delayedImport = delayedFixture.Coordinator.ImportDocumentsAsync(
                    [FixtureDocumentPath("delayed-open.txt")]);
                Require(delayedFixture.Coordinator.DebugIsDocumentImporting && !delayedImport.IsCompleted,
                    "the delayed-open fixture should await the injected stream factory without blocking the dispatcher");
                delayedFixture.Coordinator.CancelDocumentImport();
                var delayedReceipt = PumpDocumentImportTask(delayedImport);
                Require(delayedReceipt.Cancelled && delayedFixture.Coordinator.DebugToolDocuments.Count == 0,
                    "cancellation should complete promptly even when a stream-open operation ignores its token");

                var lateStream = new DisposalTrackingStream();
                delayedOpen.TrySetResult(lateStream);
                Require(lateStream.Disposed.Wait(TimeSpan.FromSeconds(2)),
                    "a stream returned after cancellation should be disposed instead of leaking the abandoned open result");

                var runImportStream = new BlockingDocumentStream(Encoding.UTF8.GetBytes("partial run context"));
                var runClient = new CancellationBlockingModelClient();
                var runFixture = CreateDocumentImportFixture(
                    (_, _) => Task.FromResult<Stream>(runImportStream),
                    runClient,
                    () => SnapshotForOverviewTest(true, "local-model", "", 0, [], []),
                    prompt: "Review this now");
                var runImport = runFixture.Coordinator.ImportDocumentsAsync(
                    [FixtureDocumentPath("run-context.txt")]);
                Require(runImportStream.WaitingForCancellation.Task.IsCompleted,
                    "the run-transition fixture did not reach its awaited document read");
                var sendTask = runFixture.Coordinator.SendAsync();
                Require(runClient.Started.Task.IsCompleted && runFixture.Coordinator.IsRunning,
                    "starting Collaborate should proceed while atomically cancelling the unfinished document import");
                var runImportReceipt = PumpDocumentImportTask(runImport);
                Require(runImportReceipt.Cancelled
                        && runFixture.Coordinator.DebugToolDocuments.Count == 0
                        && !runFixture.AddDocumentButton.IsEnabled
                        && !runFixture.ClearDocumentsButton.IsEnabled,
                    "an import finishing after Send starts must not publish partial context or re-enable document controls mid-run");
                runFixture.Coordinator.Stop();
                PumpDocumentImportTask(sendTask);
                Require(!runFixture.Coordinator.IsRunning && runFixture.AddDocumentButton.IsEnabled,
                    "normal provider-state refresh should restore document import controls after the run finishes");
            });
        });
    }

    static void CollaborateDocumentClickHandlerAwaitsAsyncImport()
    {
        var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
        var coordinatorSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/CollaborateCoordinator.cs");
        Require(source.Contains(
                "private async void CollaborateAddDocumentButton_Click(object sender, RoutedEventArgs e)",
                StringComparison.Ordinal)
                && source.Contains("await Collaborate.AddDocumentsAsync(this);", StringComparison.Ordinal),
            "the WPF click handler should keep file selection on the dispatcher and await the asynchronous import workflow");
        Require(coordinatorSource.Contains("restart AI Arena - Lite.", StringComparison.Ordinal)
                && !coordinatorSource.Contains("restart AI Arena.", StringComparison.Ordinal),
            "document picker recovery should use the current AI Arena - Lite product name");
    }

    private static DocumentImportFixture CreateDocumentImportFixture(
        Func<string, CancellationToken, Task<Stream>> streamFactory,
        IModelProviderClient? modelClient = null,
        Func<ArenaViewSnapshot?>? snapshot = null,
        string prompt = "")
    {
        var statuses = new List<string>();
        var addDocumentButton = new Button { Content = "Add Doc" };
        AutomationProperties.SetName(addDocumentButton, "Add context documents");
        var clearDocumentsButton = new Button { Content = "Clear" };
        var calculatorText = new TextBox();
        var runCalculatorButton = new Button();
        var statusText = new TextBlock();
        var promptText = new TextBox { Text = prompt };
        var coordinator = CreateCollaborateCoordinatorForTest(
            modelClient ?? new FixedCollaborateModelClient("unused"),
            promptText,
            statusText,
            snapshot ?? (() => null),
            statuses.Add,
            new RecordingCollaborateHistoryStore(),
            addDocumentButton: addDocumentButton,
            clearDocumentsButton: clearDocumentsButton,
            calculatorText: calculatorText,
            runCalculatorButton: runCalculatorButton,
            toolDocumentStreamFactory: streamFactory);
        coordinator.Initialize();
        statuses.Clear();
        return new DocumentImportFixture(
            coordinator,
            statuses,
            addDocumentButton,
            clearDocumentsButton,
            calculatorText,
            runCalculatorButton,
            statusText,
            promptText);
    }

    private static string FixtureDocumentPath(string name)
    {
        return Path.Combine(Path.GetTempPath(), "ai-arena-document-import-fixtures", name);
    }

    private static void RunWithDispatcherContext(Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static T PumpDocumentImportTask<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    private static void PumpDocumentImportTask(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private sealed record DocumentImportFixture(
        CollaborateCoordinator Coordinator,
        List<string> Statuses,
        Button AddDocumentButton,
        Button ClearDocumentsButton,
        TextBox CalculatorText,
        Button RunCalculatorButton,
        TextBlock StatusText,
        TextBox PromptText);

    private sealed class FragmentedDocumentStream(
        byte[] bytes,
        int maxFragmentBytes,
        bool yieldBetweenReads) : Stream
    {
        private int position;

        internal int BytesRead => position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(count, maxFragmentBytes), bytes.Length - position);
            if (available <= 0)
            {
                return 0;
            }

            bytes.AsSpan(position, available).CopyTo(buffer.AsSpan(offset, available));
            position += available;
            return available;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (yieldBetweenReads)
            {
                await Task.Yield();
            }

            var available = Math.Min(Math.Min(buffer.Length, maxFragmentBytes), bytes.Length - position);
            if (available <= 0)
            {
                return 0;
            }

            bytes.AsMemory(position, available).CopyTo(buffer);
            position += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingDocumentStream(byte[] firstFragment) : Stream
    {
        private bool fragmentReturned;

        internal TaskCompletionSource WaitingForCancellation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => firstFragment.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!fragmentReturned)
            {
                fragmentReturned = true;
                var count = Math.Min(buffer.Length, firstFragment.Length);
                firstFragment.AsMemory(0, count).CopyTo(buffer);
                return count;
            }

            WaitingForCancellation.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposalTrackingStream : MemoryStream
    {
        internal ManualResetEventSlim Disposed { get; } = new(initialState: false);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Disposed.Set();
        }
    }
}
