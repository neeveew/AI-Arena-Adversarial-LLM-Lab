using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed class VoiceStyleAdherenceService
{
    private static readonly char[] SentenceSeparators = ['.', '?', '!', '\n'];
    private static readonly char[] WordSeparators = [' ', '\r', '\n', '\t', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\'];

    public VoiceAdherenceDiagnostic Analyze(string? voiceStyle, string? text)
    {
        var normalized = VoiceStyleInstructions.Normalize(voiceStyle);
        var label = VoiceStyleInstructions.Label(normalized);
        if (normalized.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return new VoiceAdherenceDiagnostic(normalized, label, "none", 0, "No voice style selected.", [], []);
        }

        var body = text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(body))
        {
            return new VoiceAdherenceDiagnostic(normalized, label, "broken", 0, $"{label} style has no text to evaluate.", [], ["non-empty response"]);
        }

        var lower = body.ToLowerInvariant();
        var evidence = new List<string>();
        var missing = new List<string>();
        var score = normalized switch
        {
            "scientific" => KeywordScore(lower, evidence, missing, "scientific markers", ["evidence", "inference", "uncertainty", "hypothesis", "test", "metric", "data", "causal", "observed", "variable", "confidence"], baseline: 34, perHit: 8, maxHits: 7),
            "legal_policy" => KeywordScore(lower, evidence, missing, "policy/legal markers", ["obligation", "exception", "threshold", "risk", "compliance", "review", "policy", "standard", "permission", "liability", "condition"], baseline: 34, perHit: 8, maxHits: 7),
            "plain_language" => PlainLanguageScore(lower, body, evidence, missing),
            "idioms" => FigurativeScore(lower, evidence, missing, "idiom/metaphor markers", ["rule of thumb", "red flag", "ballpark", "on the table", "line in the sand", "needle", "roadmap", "ground rules", "back to square one", "at stake", "under the hood", "smells like", "catch smoke", "sieve", "shifting like", "tug-of-war", "moat", "wrong side of the wall", "river", "water flow", "nets", "walls", "freeze", "banks", "washed away", "nail down", "crystal clear", "wind shifts", "fence"], baseline: 28, perHit: 9, maxHits: 8),
            "cute" => FigurativeScore(lower, evidence, missing, "gentle/cute language", ["gentle", "little", "tiny", "soft", "kind", "friendly", "tidy", "careful", "sweet", "cozy", "oooh", "honey", "nudge", "quilt", "jelly", "wobbly", "wiggles", "smudge", "snug", "sparkle"], baseline: 30, perHit: 9, maxHits: 8),
            "poetic" => FigurativeScore(lower, evidence, missing, "poetic imagery", ["like", "as if", "shadow", "shadows", "light", "thread", "terrain", "weight", "echo", "map", "horizon", "pulse", "breath", "bleed", "embrace", "currents", "ice", "gray", "carved", "stone"], baseline: 30, perHit: 8, maxHits: 8),
            "socratic" => SocraticScore(body, evidence, missing),
            "bullet_only" => BulletOnlyScore(body, evidence, missing),
            "skeptical" => KeywordScore(lower, evidence, missing, "skeptical challenge markers", ["however", "but", "assumption", "evidence", "unsupported", "risk", "challenge", "verify", "not proven", "counterexample", "fails"], baseline: 34, perHit: 8, maxHits: 7),
            "executive_brief" => KeywordScore(lower, evidence, missing, "executive brief markers", ["decision", "recommendation", "risk", "evidence", "next action", "tradeoff", "impact", "priority", "owner", "timeline"], baseline: 34, perHit: 8, maxHits: 7),
            "evidence_ledger" => RequiredSectionScore(lower, evidence, missing, ["evidence", "inference", "assumptions", "uncertainty", "next test"]),
            "no_analogies" => NoAnalogiesScore(lower, evidence, missing),
            "hedge_uncertainty" => KeywordScore(lower, evidence, missing, "uncertainty hedges", ["confidence", "uncertain", "unknown", "likely", "possibly", "might", "could", "if", "would change", "depends"], baseline: 34, perHit: 8, maxHits: 7),
            "bark_only" => KeywordScore(lower, evidence, missing, "bark markers", ["bark", "woof", "arf", "ruff", "grr"], baseline: 22, perHit: 14, maxHits: 6),
            "science_gibberish" => KeywordScore(lower, evidence, missing, "pseudo-science markers", ["quantum", "entropy", "vector", "coefficient", "phase", "isotope", "neutrino", "calibrat", "oscillat", "flux", "molecule", "hypothesis"], baseline: 30, perHit: 8, maxHits: 8),
            _ => 0
        };

        score = Math.Clamp(score, 0, 100);
        var state = score >= 74 ? "strong" : score >= 46 ? "drifting" : "broken";
        var summary = state switch
        {
            "strong" => $"{label} style has strong visible cues.",
            "drifting" => $"{label} style has partial visible cues.",
            _ => $"{label} style has low visible cues."
        };

        return new VoiceAdherenceDiagnostic(normalized, label, state, score, summary, evidence, missing);
    }

    private static int KeywordScore(string lower, List<string> evidence, List<string> missing, string markerLabel, IReadOnlyList<string> markers, int baseline, int perHit, int maxHits)
    {
        var hits = markers.Where(marker => lower.Contains(marker, StringComparison.OrdinalIgnoreCase)).Take(maxHits).ToArray();
        if (hits.Length > 0)
        {
            evidence.Add($"{markerLabel}: {string.Join(", ", hits.Take(4))}");
        }
        else
        {
            missing.Add(markerLabel);
        }

        return baseline + hits.Length * perHit;
    }

    private static int FigurativeScore(string lower, List<string> evidence, List<string> missing, string markerLabel, IReadOnlyList<string> markers, int baseline, int perHit, int maxHits)
    {
        var score = KeywordScore(lower, evidence, missing, markerLabel, markers, baseline, perHit, maxHits);
        var simileCount = CountOccurrences(lower, " like ") + CountOccurrences(lower, " as if ") + CountOccurrences(lower, " as ");
        if (simileCount > 0)
        {
            evidence.Add($"{Math.Min(simileCount, 4)} simile cue(s)");
            score += Math.Min(16, simileCount * 4);
        }

        return score;
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static int RequiredSectionScore(string lower, List<string> evidence, List<string> missing, IReadOnlyList<string> headings)
    {
        var present = headings.Where(heading => lower.Contains(heading, StringComparison.OrdinalIgnoreCase)).ToArray();
        var absent = headings.Except(present, StringComparer.OrdinalIgnoreCase).ToArray();
        if (present.Length > 0)
        {
            evidence.Add($"sections: {string.Join(", ", present)}");
        }
        if (absent.Length > 0)
        {
            missing.Add($"missing sections: {string.Join(", ", absent)}");
        }

        return 18 + present.Length * 16 - absent.Length * 4;
    }

    private static int BulletOnlyScore(string body, List<string> evidence, List<string> missing)
    {
        var lines = body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length == 0)
        {
            missing.Add("bullet lines");
            return 0;
        }

        var bulletLines = lines.Count(IsBulletLine);
        var ratio = bulletLines / (double)lines.Length;
        if (ratio >= 0.95)
        {
            evidence.Add("all lines are bullets");
        }
        else
        {
            missing.Add($"{lines.Length - bulletLines} non-bullet line(s)");
        }

        return (int)Math.Round(18 + ratio * 82);
    }

    private static bool IsBulletLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("-", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && (trimmed[1] == '.' || trimmed[1] == ')'));
    }

    private static int SocraticScore(string body, List<string> evidence, List<string> missing)
    {
        var questionCount = body.Count(character => character == '?');
        var lower = body.ToLowerInvariant();
        var starters = new[] { "what", "why", "how", "which", "where", "when", "could", "would", "should" }
            .Count(starter => lower.Contains(starter, StringComparison.OrdinalIgnoreCase));
        if (questionCount > 0)
        {
            evidence.Add($"{questionCount} question(s)");
        }
        else
        {
            missing.Add("questions");
        }

        return 28 + Math.Min(44, questionCount * 16) + Math.Min(24, starters * 4);
    }

    private static int PlainLanguageScore(string lower, string body, List<string> evidence, List<string> missing)
    {
        var wordCount = body.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;
        var sentenceCount = Math.Max(1, body.Split(SentenceSeparators, StringSplitOptions.RemoveEmptyEntries).Length);
        var averageSentence = wordCount / (double)sentenceCount;
        var score = 62;
        if (averageSentence <= 18)
        {
            score += 18;
            evidence.Add("short sentences");
        }
        else if (averageSentence > 30)
        {
            score -= 22;
            missing.Add("shorter sentences");
        }

        var jargon = new[] { "ontological", "epistemic", "teleological", "metacognitive", "operationalize", "invariant", "substrate" }
            .Count(term => lower.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (jargon == 0)
        {
            evidence.Add("low jargon");
            score += 10;
        }
        else
        {
            missing.Add("less jargon");
            score -= Math.Min(24, jargon * 8);
        }

        return score;
    }

    private static int NoAnalogiesScore(string lower, List<string> evidence, List<string> missing)
    {
        var analogyMarkers = new[] { " like ", " as if ", "metaphor", "analogy", "idiom", "echo", "shadow", "thread", "horizon", "terrain" };
        var hits = analogyMarkers.Where(marker => lower.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hits.Length == 0)
        {
            evidence.Add("no analogy markers");
        }
        else
        {
            missing.Add($"remove analogy markers: {string.Join(", ", hits.Take(3).Select(hit => hit.Trim()))}");
        }

        return 94 - hits.Length * 20;
    }
}
