using System.Text;

namespace AIArena.Core.Providers;

/// <summary>Captures accepted public deltas before optional presentation queues can lose them.</summary>
public sealed class ModelPublicOutputCapture(IProgress<string>? presentation = null, int maximumCharacters = 1_048_576) : IProgress<string>
{
    private readonly object gate = new();
    private readonly StringBuilder text = new();
    private readonly int limit = maximumCharacters > 0 ? maximumCharacters : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
    private bool sealedOutput;
    private bool truncated;
    private string? sealedText;

    public int Characters { get { lock (gate) return text.Length; } }
    public bool Truncated { get { lock (gate) return truncated; } }
    public bool IsSealed { get { lock (gate) return sealedOutput; } }

    public void Report(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate)
        {
            if (sealedOutput) return;
            var remaining = limit - text.Length;
            text.Append(value, 0, Math.Min(remaining, value.Length));
            truncated |= value.Length > remaining;
        }
        // Presentation is optional and may enqueue asynchronously. Its failure
        // cannot change the accepted public output or the provider operation.
        try { presentation?.Report(value); }
        catch { }
    }

    public string Tail(int maximumCharacters)
    {
        lock (gate)
        {
            var count = Math.Min(text.Length, Math.Max(0, maximumCharacters));
            return text.ToString(text.Length - count, count);
        }
    }

    public string Seal()
    {
        lock (gate)
        {
            sealedOutput = true;
            return sealedText ??= text.ToString();
        }
    }
}
