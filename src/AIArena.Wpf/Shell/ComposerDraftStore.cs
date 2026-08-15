using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Persistence;

namespace AIArena.Wpf;

internal interface IComposerDraftProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class WindowsComposerDraftProtector : IComposerDraftProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIArena.ComposerDrafts.v1");

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// Current-user protected storage for unsent composer text. Each process owns a
/// coherent local generation; durable flushes merge key-level mutations under a
/// cross-process lease and only ever write protected bytes.
/// </summary>
internal sealed class ComposerDraftStore : IAsyncDisposable
{
    internal const int MaxDraftCharacters = 32 * 1024;
    internal const int MaxEntries = 96;
    internal const int MaxScopeKeyCharacters = 192;
    internal const int MaxPlaintextBytes = 512 * 1024;
    internal const int MaxCiphertextBytes = MaxPlaintextBytes + (128 * 1024);
    private const int FileVersion = 1;
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CrossProcessFlushTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 8
    };

    private readonly object sync = new();
    private readonly Dictionary<string, DraftEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingMutation> pendingMutations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private readonly IComposerDraftProtector protector;
    private readonly TimeSpan debounceDelay;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> atomicWriter;
    private readonly bool mergeWithLatestFile;
    private CancellationTokenSource? debounceCancellation;
    private Task debounceTask = Task.CompletedTask;
    private long nextSequence;
    private long generation;
    private long persistedGeneration;
    private ObservedInvalidGeneration? observedInvalidGeneration;
    private bool disposing;
    private bool disposed;

    public ComposerDraftStore(
        string? storePath = null,
        IComposerDraftProtector? protector = null,
        TimeSpan? debounceDelay = null,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? atomicWriter = null)
    {
        StorePath = string.IsNullOrWhiteSpace(storePath)
            ? NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "composer-drafts.dat")
            : Path.GetFullPath(storePath);
        this.protector = protector ?? new WindowsComposerDraftProtector();
        this.debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        mergeWithLatestFile = atomicWriter is null;
        this.atomicWriter = atomicWriter ?? WriteProtectedBytesAtomicAsync;
        LoadSafely();
    }

    public string StorePath { get; }

    internal string LastDiagnosticCode { get; private set; } = "ready";

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    internal long Generation
    {
        get
        {
            lock (sync)
            {
                return generation;
            }
        }
    }

    public string Get(string? scopeKey)
    {
        var key = NormalizeScopeKey(scopeKey);
        if (key.Length == 0)
        {
            return "";
        }

        lock (sync)
        {
            return entries.TryGetValue(key, out var entry) ? entry.Text : "";
        }
    }

    public void Set(string? scopeKey, string? text)
    {
        var key = NormalizeScopeKey(scopeKey);
        if (key.Length == 0)
        {
            return;
        }

        var normalizedText = NormalizeDraftText(text);
        lock (sync)
        {
            ThrowIfUnavailableForMutation();
            if (normalizedText.Length == 0)
            {
                if (!entries.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (entries.TryGetValue(key, out var current)
                    && current.Text.Equals(normalizedText, StringComparison.Ordinal))
                {
                    return;
                }

                if (nextSequence >= long.MaxValue - MaxEntries - 1)
                {
                    CompactSequencesLocked();
                }

                entries[key] = new DraftEntry(key, normalizedText, checked(++nextSequence));
                EnforceBoundsLocked();
            }

            generation++;
            if (mergeWithLatestFile)
            {
                pendingMutations[key] = new PendingMutation(key, normalizedText, generation);
            }

            ScheduleDebouncedFlushLocked();
        }
    }

    public void Remove(string? scopeKey)
    {
        Set(scopeKey, "");
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long targetGeneration;
            lock (sync)
            {
                ThrowIfDisposed();
                targetGeneration = generation;
                if (persistedGeneration >= targetGeneration)
                {
                    return true;
                }
            }

            await flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (mergeWithLatestFile)
                {
                    if (!await FlushMergedGenerationAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    lock (sync)
                    {
                        if (persistedGeneration >= generation)
                        {
                            return true;
                        }
                    }

                    continue;
                }

                DraftFile snapshot;
                long snapshotGeneration;
                lock (sync)
                {
                    snapshotGeneration = generation;
                    if (persistedGeneration >= snapshotGeneration)
                    {
                        continue;
                    }

                    snapshot = BuildSnapshotLocked();
                }

                byte[] plaintext = [];
                byte[] ciphertext = [];
                try
                {
                    plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                    if (plaintext.Length > MaxPlaintextBytes)
                    {
                        SetDiagnostic("plaintext-bound");
                        return false;
                    }

                    ciphertext = protector.Protect(plaintext);
                    if (ciphertext.Length == 0 || ciphertext.Length > MaxCiphertextBytes)
                    {
                        SetDiagnostic("ciphertext-bound");
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await atomicWriter(StorePath, ciphertext, cancellationToken).ConfigureAwait(false);
                    lock (sync)
                    {
                        persistedGeneration = Math.Max(persistedGeneration, snapshotGeneration);
                        LastDiagnosticCode = "ready";
                        if (persistedGeneration >= generation)
                        {
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsSafePersistenceFailure(ex))
                {
                    SetDiagnostic("write-failed");
                    return false;
                }
                finally
                {
                    if (plaintext.Length > 0)
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    if (ciphertext.Length > 0)
                    {
                        CryptographicOperations.ZeroMemory(ciphertext);
                    }
                }
            }
            finally
            {
                flushGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        Task pendingTask;
        lock (sync)
        {
            if (disposed || disposing)
            {
                return;
            }

            disposing = true;
            pending = debounceCancellation;
            pendingTask = debounceTask;
            debounceCancellation = null;
        }

        pending?.Cancel();
        try
        {
            await pendingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Draft persistence must never prevent application shutdown.
        }

        lock (sync)
        {
            disposed = true;
            disposing = false;
        }
    }

    internal Task PendingDebounceTask
    {
        get
        {
            lock (sync)
            {
                return debounceTask;
            }
        }
    }

    private async Task<bool> FlushMergedGenerationAsync(CancellationToken cancellationToken)
    {
        long snapshotGeneration;
        PendingMutation[] mutations;
        lock (sync)
        {
            snapshotGeneration = generation;
            mutations = pendingMutations.Values
                .Where(mutation => mutation.Generation <= snapshotGeneration)
                .OrderBy(mutation => mutation.Generation)
                .ThenBy(mutation => mutation.Key, StringComparer.Ordinal)
                .ToArray();
            if (mutations.Length == 0)
            {
                persistedGeneration = Math.Max(persistedGeneration, snapshotGeneration);
                return true;
            }
        }

        byte[] plaintext = [];
        byte[] ciphertext = [];
        try
        {
            using var lease = await DraftWriteLease.AcquireAsync(
                    StorePath,
                    CrossProcessFlushTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var readSucceeded = TryReadCurrentFile(
                out var currentFile,
                out var currentFingerprint,
                out var readDiagnostic);
            ObservedInvalidGeneration? invalidGeneration;
            lock (sync)
            {
                invalidGeneration = observedInvalidGeneration;
            }

            if (invalidGeneration is not null)
            {
                if (invalidGeneration.Fingerprint is null
                    || currentFingerprint is null
                    || currentFingerprint != invalidGeneration.Fingerprint)
                {
                    SetDiagnostic("recovery-conflict");
                    return false;
                }

                if (!readSucceeded)
                {
                    // The constructor already failed closed on this exact protected
                    // generation. Under the cross-process lease it is safe to use an
                    // empty merge base; a changed generation is rejected above.
                    currentFile = new DraftFile();
                }
            }
            else if (!readSucceeded)
            {
                SetDiagnostic(readDiagnostic);
                return false;
            }

            var mergedFile = MergeDraftFile(currentFile, mutations);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(mergedFile, JsonOptions);
            if (plaintext.Length > MaxPlaintextBytes)
            {
                SetDiagnostic("plaintext-bound");
                return false;
            }

            ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length == 0 || ciphertext.Length > MaxCiphertextBytes)
            {
                SetDiagnostic("ciphertext-bound");
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await atomicWriter(StorePath, ciphertext, cancellationToken).ConfigureAwait(false);

            lock (sync)
            {
                foreach (var mutation in mutations)
                {
                    if (pendingMutations.TryGetValue(mutation.Key, out var pending)
                        && pending.Generation == mutation.Generation)
                    {
                        pendingMutations.Remove(mutation.Key);
                    }
                }

                entries.Clear();
                foreach (var entry in mergedFile.Entries)
                {
                    entries.Add(entry.Key, new DraftEntry(entry.Key, entry.Text, entry.Sequence));
                }

                nextSequence = mergedFile.NextSequence;
                foreach (var pending in pendingMutations.Values
                             .OrderBy(mutation => mutation.Generation)
                             .ThenBy(mutation => mutation.Key, StringComparer.Ordinal))
                {
                    ApplyPendingMutationLocked(pending);
                }

                EnforceBoundsLocked();
                persistedGeneration = Math.Max(persistedGeneration, snapshotGeneration);
                observedInvalidGeneration = null;
                LastDiagnosticCode = "ready";
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsSafePersistenceFailure(ex))
        {
            SetDiagnostic("write-failed");
            return false;
        }
        finally
        {
            if (plaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    private bool TryReadCurrentFile(
        out DraftFile file,
        out DraftFileFingerprint? fingerprint,
        out string diagnostic)
    {
        file = new DraftFile();
        fingerprint = null;
        diagnostic = "ready";
        byte[] ciphertext = [];
        byte[] plaintext = [];
        try
        {
            if (!File.Exists(StorePath))
            {
                return true;
            }

            using var stream = new FileStream(
                StorePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.SequentialScan);
            var cappedBytes = new byte[MaxCiphertextBytes + 1];
            var totalRead = 0;
            while (totalRead < cappedBytes.Length)
            {
                var read = stream.Read(cappedBytes, totalRead, cappedBytes.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead <= MaxCiphertextBytes)
            {
                fingerprint = CreateFingerprint(cappedBytes.AsSpan(0, totalRead));
            }

            if (totalRead == 0 || totalRead > MaxCiphertextBytes)
            {
                CryptographicOperations.ZeroMemory(cappedBytes);
                diagnostic = "read-bound";
                return false;
            }

            ciphertext = cappedBytes[..totalRead];
            CryptographicOperations.ZeroMemory(cappedBytes);
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length == 0 || plaintext.Length > MaxPlaintextBytes)
            {
                diagnostic = "plaintext-bound";
                return false;
            }

            var deserialized = JsonSerializer.Deserialize<DraftFile>(plaintext, JsonOptions);
            if (!TryValidateFile(deserialized, plaintext.Length, out var loadedEntries, out var loadedSequence))
            {
                diagnostic = "invalid-payload";
                return false;
            }

            file = new DraftFile
            {
                Version = FileVersion,
                NextSequence = loadedSequence,
                Entries = loadedEntries
                    .Select(entry => new DraftEntry(entry.Key, entry.Text, entry.Sequence))
                    .ToList()
            };
            return true;
        }
        catch (Exception ex) when (IsSafePersistenceFailure(ex))
        {
            diagnostic = "read-failed";
            return false;
        }
        finally
        {
            if (ciphertext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (plaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static DraftFile MergeDraftFile(DraftFile currentFile, IReadOnlyList<PendingMutation> mutations)
    {
        var merged = currentFile.Entries.ToDictionary(
            entry => entry.Key,
            entry => new DraftEntry(entry.Key, entry.Text, entry.Sequence),
            StringComparer.Ordinal);
        var next = currentFile.NextSequence;
        foreach (var mutation in mutations)
        {
            if (mutation.Text.Length == 0)
            {
                merged.Remove(mutation.Key);
                continue;
            }

            if (next >= long.MaxValue - MaxEntries - 1)
            {
                var compacted = merged.Values
                    .OrderBy(entry => entry.Sequence)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToArray();
                merged.Clear();
                next = 0;
                foreach (var entry in compacted)
                {
                    merged.Add(entry.Key, entry with { Sequence = ++next });
                }
            }

            merged[mutation.Key] = new DraftEntry(mutation.Key, mutation.Text, checked(++next));
        }

        var result = new DraftFile
        {
            Version = FileVersion,
            NextSequence = next,
            Entries = merged.Values
                .OrderBy(entry => entry.Sequence)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList()
        };
        EnforceFileBounds(result);
        return result;
    }

    private static void EnforceFileBounds(DraftFile file)
    {
        while (file.Entries.Count > MaxEntries || SerializedPlaintextBytes(file) > MaxPlaintextBytes)
        {
            if (file.Entries.Count == 0)
            {
                break;
            }

            file.Entries.RemoveAt(0);
        }
    }

    private static int SerializedPlaintextBytes(DraftFile file)
    {
        byte[] plaintext = [];
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
            return plaintext.Length;
        }
        finally
        {
            if (plaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private void ApplyPendingMutationLocked(PendingMutation mutation)
    {
        if (mutation.Text.Length == 0)
        {
            entries.Remove(mutation.Key);
            return;
        }

        if (nextSequence >= long.MaxValue - MaxEntries - 1)
        {
            CompactSequencesLocked();
        }

        entries[mutation.Key] = new DraftEntry(mutation.Key, mutation.Text, checked(++nextSequence));
    }

    private void LoadSafely()
    {
        byte[] ciphertext = [];
        byte[] plaintext = [];
        DraftFileFingerprint? invalidFingerprint = null;
        try
        {
            if (!File.Exists(StorePath))
            {
                return;
            }

            using var stream = new FileStream(
                StorePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.SequentialScan);
            var cappedBytes = new byte[MaxCiphertextBytes + 1];
            var totalRead = 0;
            while (totalRead < cappedBytes.Length)
            {
                var read = stream.Read(cappedBytes, totalRead, cappedBytes.Length - totalRead);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            if (totalRead <= MaxCiphertextBytes)
            {
                invalidFingerprint = CreateFingerprint(cappedBytes.AsSpan(0, totalRead));
            }

            if (totalRead == 0 || totalRead > MaxCiphertextBytes)
            {
                CryptographicOperations.ZeroMemory(cappedBytes);
                observedInvalidGeneration = new ObservedInvalidGeneration(invalidFingerprint);
                LastDiagnosticCode = "read-bound";
                return;
            }

            ciphertext = cappedBytes[..totalRead];
            CryptographicOperations.ZeroMemory(cappedBytes);

            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length == 0 || plaintext.Length > MaxPlaintextBytes)
            {
                observedInvalidGeneration = new ObservedInvalidGeneration(invalidFingerprint);
                LastDiagnosticCode = "plaintext-bound";
                return;
            }

            var file = JsonSerializer.Deserialize<DraftFile>(plaintext, JsonOptions);
            if (!TryValidateFile(file, plaintext.Length, out var loadedEntries, out var loadedSequence))
            {
                observedInvalidGeneration = new ObservedInvalidGeneration(invalidFingerprint);
                LastDiagnosticCode = "invalid-payload";
                return;
            }

            lock (sync)
            {
                entries.Clear();
                foreach (var entry in loadedEntries)
                {
                    entries.Add(entry.Key, entry);
                }

                nextSequence = loadedSequence;
                generation = 0;
                persistedGeneration = 0;
                observedInvalidGeneration = null;
                LastDiagnosticCode = "ready";
            }
        }
        catch (Exception ex) when (IsSafePersistenceFailure(ex))
        {
            lock (sync)
            {
                entries.Clear();
                nextSequence = 0;
                generation = 0;
                persistedGeneration = 0;
                observedInvalidGeneration = new ObservedInvalidGeneration(invalidFingerprint);
                LastDiagnosticCode = "read-failed";
            }
        }
        finally
        {
            if (ciphertext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            if (plaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private void ScheduleDebouncedFlushLocked()
    {
        var previous = debounceCancellation;
        debounceCancellation = new CancellationTokenSource();
        var current = debounceCancellation;
        previous?.Cancel();
        previous?.Dispose();
        debounceTask = DebouncedFlushAsync(current);
    }

    private async Task DebouncedFlushAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(debounceDelay, cancellation.Token).ConfigureAwait(false);
            await FlushAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(debounceCancellation, cancellation))
                {
                    debounceCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private DraftFile BuildSnapshotLocked()
    {
        return new DraftFile
        {
            Version = FileVersion,
            NextSequence = nextSequence,
            Entries = entries.Values
                .OrderBy(entry => entry.Sequence)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new DraftEntry(entry.Key, entry.Text, entry.Sequence))
                .ToList()
        };
    }

    private void EnforceBoundsLocked()
    {
        while (entries.Count > MaxEntries || SerializedPlaintextBytesLocked() > MaxPlaintextBytes)
        {
            var oldest = entries.Values
                .OrderBy(entry => entry.Sequence)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            entries.Remove(oldest.Key);
        }
    }

    private void CompactSequencesLocked()
    {
        var ordered = entries.Values
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        entries.Clear();
        nextSequence = 0;
        foreach (var entry in ordered)
        {
            entries.Add(entry.Key, entry with { Sequence = ++nextSequence });
        }
    }

    private int SerializedPlaintextBytesLocked()
    {
        byte[] plaintext = [];
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(BuildSnapshotLocked(), JsonOptions);
            return plaintext.Length;
        }
        finally
        {
            if (plaintext.Length > 0)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static bool TryValidateFile(
        DraftFile? file,
        int plaintextLength,
        out IReadOnlyList<DraftEntry> loadedEntries,
        out long loadedSequence)
    {
        loadedEntries = [];
        loadedSequence = 0;
        if (file is null
            || file.Version != FileVersion
            || plaintextLength > MaxPlaintextBytes
            || file.Entries is null
            || file.Entries.Count > MaxEntries
            || file.NextSequence < 0
            || file.NextSequence > long.MaxValue - MaxEntries - 1)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<DraftEntry>(file.Entries.Count);
        long highestSequence = 0;
        foreach (var entry in file.Entries)
        {
            if (entry is null
                || string.IsNullOrEmpty(entry.Key)
                || entry.Key.Length > MaxScopeKeyCharacters
                || string.IsNullOrEmpty(entry.Text)
                || entry.Text.Length > MaxDraftCharacters
                || entry.Sequence <= 0
                || !keys.Add(entry.Key))
            {
                return false;
            }

            highestSequence = Math.Max(highestSequence, entry.Sequence);
            normalized.Add(new DraftEntry(entry.Key, entry.Text, entry.Sequence));
        }

        if (file.NextSequence < highestSequence)
        {
            return false;
        }

        loadedEntries = normalized;
        loadedSequence = file.NextSequence;
        return true;
    }

    private static string NormalizeScopeKey(string? scopeKey)
    {
        var key = scopeKey?.Trim() ?? "";
        if (key.Length == 0)
        {
            return "";
        }

        if (key.Length <= MaxScopeKeyCharacters && !key.Any(char.IsControl))
        {
            return key;
        }

        return $"bounded:{ComposerDraftScopes.HashIdentity(key)}";
    }

    private static string NormalizeDraftText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var bounded = text.Length <= MaxDraftCharacters ? text : text[..MaxDraftCharacters];
        return string.IsNullOrWhiteSpace(bounded) ? "" : bounded;
    }

    private static DraftFileFingerprint CreateFingerprint(ReadOnlySpan<byte> protectedBytes)
    {
        return new DraftFileFingerprint(
            protectedBytes.Length,
            Convert.ToHexString(SHA256.HashData(protectedBytes)));
    }

    private static async Task WriteProtectedBytesAtomicAsync(
        string path,
        ReadOnlyMemory<byte> protectedBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException("Draft storage directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void SetDiagnostic(string code)
    {
        lock (sync)
        {
            LastDiagnosticCode = code;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void ThrowIfUnavailableForMutation()
    {
        ObjectDisposedException.ThrowIf(disposed || disposing, this);
    }

    private static bool IsSafePersistenceFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or JsonException
            or NotSupportedException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or OverflowException
            or System.Security.SecurityException;
    }

    private sealed class DraftFile
    {
        public int Version { get; set; } = FileVersion;

        public long NextSequence { get; set; }

        public List<DraftEntry> Entries { get; set; } = [];
    }

    private sealed record DraftEntry(string Key, string Text, long Sequence);

    private sealed record PendingMutation(string Key, string Text, long Generation);

    private sealed record DraftFileFingerprint(int Length, string Sha256);

    private sealed record ObservedInvalidGeneration(DraftFileFingerprint? Fingerprint);

    private sealed class DraftWriteLease : IDisposable
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
        private readonly string path;
        private FileStream? stream;

        private DraftWriteLease(string path, FileStream stream)
        {
            this.path = path;
            this.stream = stream;
        }

        public static async Task<DraftWriteLease> AcquireAsync(
            string targetPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var fullPath = Path.GetFullPath(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var leasePath = fullPath + ".write.lock";
            var startedAt = Stopwatch.GetTimestamp();
            Exception? lastFailure = null;
            while (Stopwatch.GetElapsedTime(startedAt) < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new DraftWriteLease(
                        leasePath,
                        new FileStream(
                            leasePath,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.Delete,
                            bufferSize: 1,
                            FileOptions.None));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastFailure = ex;
                }

                var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < RetryDelay ? remaining : RetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new IOException("Timed out waiting for another app process to finish saving composer drafts.", lastFailure);
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref stream, null);
            if (owned is null)
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
            finally
            {
                owned.Dispose();
            }
        }
    }
}

internal static class ComposerDraftScopes
{
    public static string Operator(
        string? sessionId,
        string? sessionInstanceId,
        string? route)
    {
        return BuildSessionScope(
            "operator",
            sessionId,
            sessionInstanceId,
            NormalizeComponent(route).ToLowerInvariant());
    }

    public static string AgentWorkspace(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return "";
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(workspacePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = workspacePath.Trim().ToUpperInvariant();
        }

        return $"agent:{HashIdentity(normalized)}";
    }

    public static string Collaborate(
        string? sessionId,
        string? sessionInstanceId,
        Guid? conversationId,
        string? mode)
    {
        var conversation = conversationId is Guid id && id != Guid.Empty
            ? id.ToString("N")
            : "new-chat";
        return BuildSessionScope(
            "collaborate",
            sessionId,
            sessionInstanceId,
            conversation,
            NormalizeComponent(mode).ToLowerInvariant());
    }

    internal static string HashIdentity(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")))
            .ToLowerInvariant();
    }

    private static string Build(string kind, params string[] components)
    {
        if (components.Length == 0 || string.IsNullOrWhiteSpace(components[0]))
        {
            return "";
        }

        var identity = string.Join('\u001f', components);
        return $"{kind}:{HashIdentity(identity)}";
    }

    private static string BuildSessionScope(
        string kind,
        string? sessionId,
        string? sessionInstanceId,
        params string[] components)
    {
        var normalizedSession = NormalizeComponent(sessionId);
        var normalizedInstance = NormalizeComponent(sessionInstanceId);
        if (normalizedSession.Length == 0
            || !SessionStore.IsValidSessionInstanceId(normalizedInstance))
        {
            return "";
        }

        return Build(kind, [normalizedSession, normalizedInstance, .. components]);
    }

    private static string NormalizeComponent(string? value)
    {
        return value?.Trim() ?? "";
    }
}
