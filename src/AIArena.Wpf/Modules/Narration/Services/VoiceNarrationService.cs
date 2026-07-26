using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace AIArena.Wpf.Services;

public sealed class VoiceNarrationService : IDisposable
{
    private readonly object sync = new();
    private readonly Func<IVoiceNarrationSynthesizer> createSynthesizer;
    private VoiceSynthesizerSession? currentSession;
    private long operationVersion;
    private bool disposed;

    public VoiceNarrationService()
        : this(static () => new SystemSpeechVoiceNarrationSynthesizer())
    {
    }

    internal VoiceNarrationService(Func<IVoiceNarrationSynthesizer> createSynthesizer)
    {
        this.createSynthesizer = createSynthesizer ?? throw new ArgumentNullException(nameof(createSynthesizer));
    }

    /// <summary>Raised on any speaking-state transition; may fire on a non-UI thread.</summary>
    public event Action? SpeakingChanged;

    public bool IsSpeaking
    {
        get
        {
            lock (sync)
            {
                return currentSession is not null;
            }
        }
    }

    public IReadOnlyList<string> InstalledVoiceNames()
    {
        lock (sync)
        {
            if (disposed)
            {
                return [];
            }
        }

        try
        {
            using var synthesizer = createSynthesizer();
            return synthesizer.InstalledVoiceNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public VoiceNarrationResult Speak(string text, VoiceNarrationOptions options)
    {
        long version;
        lock (sync)
        {
            if (disposed)
            {
                return VoiceNarrationResult.Failed("Voice narration service has been disposed.");
            }

            version = ++operationVersion;
        }

        var narrationText = PrepareText(text);
        if (string.IsNullOrWhiteSpace(narrationText))
        {
            return VoiceNarrationResult.Failed("No narration text to speak.");
        }

        IVoiceNarrationSynthesizer? synthesizer = null;
        VoiceSynthesizerSession? session = null;
        try
        {
            synthesizer = createSynthesizer();
            synthesizer.Rate = NormalizeRate(options.Rate);
            synthesizer.Volume = NormalizeVolume(options.Volume);

            SelectVoiceIfAvailable(synthesizer, options.VoiceName);
            var selectedVoiceLabel = VoiceLabel(synthesizer.VoiceName);
            var createdSession = new VoiceSynthesizerSession(synthesizer);
            session = createdSession;
            synthesizer.SpeakCompleted += () => DisposeCompletedSynthesizer(createdSession);

            VoiceSynthesizerSession? previous;
            string? rejection = null;
            lock (sync)
            {
                if (disposed)
                {
                    previous = null;
                    rejection = "Voice narration service has been disposed.";
                }
                else if (version != operationVersion)
                {
                    previous = null;
                    rejection = "Voice narration request was superseded.";
                }
                else
                {
                    previous = currentSession;
                    currentSession = session;
                }
            }

            if (rejection is not null)
            {
                session.CancelAndDispose();
                return VoiceNarrationResult.Failed(rejection);
            }

            previous?.CancelAndDispose();
            if (!session.TryStart(narrationText))
            {
                var clearedCurrent = false;
                string rejectionMessage;
                lock (sync)
                {
                    if (ReferenceEquals(currentSession, session))
                    {
                        currentSession = null;
                        clearedCurrent = true;
                    }

                    rejectionMessage = disposed
                        ? "Voice narration service has been disposed."
                        : "Voice narration request was stopped or superseded.";
                }

                session.CancelAndDispose();
                if (clearedCurrent)
                {
                    RaiseSpeakingChanged();
                }

                return VoiceNarrationResult.Failed(rejectionMessage);
            }

            string? ownershipFailure;
            lock (sync)
            {
                if (disposed || !ReferenceEquals(currentSession, session))
                {
                    ownershipFailure = disposed
                        ? "Voice narration service has been disposed."
                        : "Voice narration request was superseded.";
                }
                else
                {
                    ownershipFailure = null;
                }
            }

            if (ownershipFailure is not null)
            {
                session.CancelAndDispose();
                return VoiceNarrationResult.Failed(ownershipFailure);
            }

            RaiseSpeakingChanged();
            return VoiceNarrationResult.Started($"Speaking with {selectedVoiceLabel}.");
        }
        catch (Exception ex)
        {
            var clearedCurrent = false;
            lock (sync)
            {
                if (ReferenceEquals(currentSession, session))
                {
                    currentSession = null;
                    clearedCurrent = true;
                }
            }

            if (session is not null)
            {
                session.CancelAndDispose();
            }
            else if (synthesizer is not null)
            {
                try
                {
                    synthesizer.Dispose();
                }
                catch
                {
                }
            }

            if (clearedCurrent)
            {
                RaiseSpeakingChanged();
            }

            return VoiceNarrationResult.Failed($"Voice narration failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        VoiceSynthesizerSession? previous;
        lock (sync)
        {
            operationVersion++;
            previous = currentSession;
            currentSession = null;
        }

        previous?.CancelAndDispose();
        if (previous is not null)
        {
            RaiseSpeakingChanged();
        }
    }

    public void Dispose()
    {
        VoiceSynthesizerSession? previous;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            operationVersion++;
            previous = currentSession;
            currentSession = null;
        }

        previous?.CancelAndDispose();
        if (previous is not null)
        {
            RaiseSpeakingChanged();
        }

        SpeakingChanged = null;
    }

    public static string PrepareText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var cleaned = Regex.Replace(text, @"```[\s\S]*?```", " code block omitted ");
        cleaned = Regex.Replace(cleaned, @"`([^`]+)`", "$1");
        cleaned = Regex.Replace(cleaned, @"\[(?<label>[^\]]+)\]\([^)]+\)", "${label}");
        cleaned = cleaned.Replace("**", "").Replace("__", "").Replace("*", "");
        cleaned = Regex.Replace(cleaned, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, @"^\s*[-+]\s+", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }

    public static int NormalizeRate(int rate)
    {
        return Math.Clamp(rate, -4, 4);
    }

    public static int NormalizeVolume(int volume)
    {
        return Math.Clamp(volume, 0, 100);
    }

    public static string VoiceLabel(string? voiceName)
    {
        return string.IsNullOrWhiteSpace(voiceName) ? "default Windows voice" : voiceName.Trim();
    }

    private static void SelectVoiceIfAvailable(IVoiceNarrationSynthesizer synthesizer, string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return;
        }

        var installed = synthesizer.InstalledVoiceNames()
            .Any(name => name.Equals(voiceName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (installed)
        {
            synthesizer.SelectVoice(voiceName.Trim());
        }
    }

    private void DisposeCompletedSynthesizer(VoiceSynthesizerSession session)
    {
        var wasCurrent = false;
        lock (sync)
        {
            if (ReferenceEquals(currentSession, session))
            {
                currentSession = null;
                wasCurrent = true;
            }
        }

        session.Dispose();

        if (wasCurrent)
        {
            RaiseSpeakingChanged();
        }
    }

    private void RaiseSpeakingChanged()
    {
        var handlers = SpeakingChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // A UI observer must not fail narration startup, stopping, or a
                // speech-completion callback running on a synthesizer thread.
            }
        }
    }

    private sealed class VoiceSynthesizerSession(IVoiceNarrationSynthesizer synthesizer) : IDisposable
    {
        private readonly object startSync = new();
        private int disposalClaimed;

        internal bool TryStart(string text)
        {
            lock (startSync)
            {
                if (Volatile.Read(ref disposalClaimed) != 0)
                {
                    return false;
                }

                synthesizer.SpeakAsync(text);
                return Volatile.Read(ref disposalClaimed) == 0;
            }
        }

        internal void CancelAndDispose()
        {
            if (Interlocked.Exchange(ref disposalClaimed, 1) != 0)
            {
                return;
            }

            // Claim disposal before waiting so no later start can enter. If a
            // start is already inside the synthesizer call, let it unwind first.
            lock (startSync)
            {
            }

            try
            {
                synthesizer.CancelAll();
            }
            catch
            {
            }
            finally
            {
                DisposeSynthesizer();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposalClaimed, 1) == 0)
            {
                DisposeSynthesizer();
            }
        }

        private static void IgnoreDisposeFailure(Action dispose)
        {
            try
            {
                dispose();
            }
            catch
            {
            }
        }

        private void DisposeSynthesizer()
        {
            IgnoreDisposeFailure(synthesizer.Dispose);
        }
    }
}

internal interface IVoiceNarrationSynthesizer : IDisposable
{
    event Action? SpeakCompleted;

    int Rate { set; }

    int Volume { set; }

    string VoiceName { get; }

    IReadOnlyList<string> InstalledVoiceNames();

    void SelectVoice(string voiceName);

    void SpeakAsync(string text);

    void CancelAll();
}

internal sealed class SystemSpeechVoiceNarrationSynthesizer : IVoiceNarrationSynthesizer
{
    private readonly SpeechSynthesizer synthesizer = new();
    private int disposed;

    internal SystemSpeechVoiceNarrationSynthesizer()
    {
        synthesizer.SpeakCompleted += (_, _) => SpeakCompleted?.Invoke();
    }

    public event Action? SpeakCompleted;

    public int Rate
    {
        set => synthesizer.Rate = value;
    }

    public int Volume
    {
        set => synthesizer.Volume = value;
    }

    public string VoiceName => synthesizer.Voice?.Name ?? "";

    public IReadOnlyList<string> InstalledVoiceNames()
    {
        return synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => voice.VoiceInfo.Name)
            .ToArray();
    }

    public void SelectVoice(string voiceName)
    {
        synthesizer.SelectVoice(voiceName);
    }

    public void SpeakAsync(string text)
    {
        synthesizer.SpeakAsync(text);
    }

    public void CancelAll()
    {
        synthesizer.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            synthesizer.Dispose();
        }
    }
}

public sealed record VoiceNarrationOptions(string VoiceName, int Rate, int Volume);

public sealed record VoiceNarrationResult(bool Ok, string Status)
{
    public static VoiceNarrationResult Started(string status) => new(true, status);
    public static VoiceNarrationResult Failed(string status) => new(false, status);
}
