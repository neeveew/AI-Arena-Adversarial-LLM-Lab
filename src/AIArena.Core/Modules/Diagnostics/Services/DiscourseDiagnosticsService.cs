using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed class DiscourseDiagnosticsService
{
    private const int WindowSize = 8;

    private static readonly string[] AgreementPhrases =
    [
        "i agree", "correct", "exactly", "yes", "as alpha said", "as beta said", "as gamma said", "as delta said",
        "building on", "confirms", "converges", "we have established", "the framework stands"
    ];

    private static readonly string[] DisagreementPhrases =
    [
        "however", "but", "i reject", "this fails", "not proven", "unsupported", "counterargument",
        "failure mode", "assumption", "evidence?", "does not follow", "cannot conclude"
    ];

    private static readonly string[] OverclaimPhrases =
    [
        "proves", "validated", "empirical", "empirically validated", "universal", "universal law",
        "self-evident", "inevitable", "the only explanation", "no evidence needed", "this confirms",
        "final synthesis", "complete validation", "ontological", "truth", "absolute truth",
        "the framework stands", "ready for deployment"
    ];

    private static readonly string[] EvidenceMarkers =
    [
        "according to", "the article says", "the uploaded file says", "operator provided",
        "in turn", "from the transcript", "speculative", "inference", "analogy", "hypothesis",
        "article", "source", "rss", "guardian", "file", "uploaded", "the transcript shows",
        "directly supported"
    ];

    private static readonly string[] NarrativePhrases =
    [
        "ontology", "ontological", "emergence", "substrate", "metabolic", "universal law",
        "final synthesis", "temple flame", "truth", "absolute truth", "the real essence",
        "end transmission", "self-governing reality", "architecture is complete",
        "the journey is complete", "contradictions are fuel", "living boundary",
        "metabolize", "coherence", "reality engine"
    ];

    private static readonly Regex SuspiciousMetricRegex = new(
        @"\b\d{1,3}(?:\.\d+)?%\b|\b\d{1,3}(?:,\d{3})+\s+(?:transactions|simulations|cases|users|runs)\b|\b\d+(?:\.\d+)?ms\b|\bvalidated across\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(@"https?://|www\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TurnReferenceRegex = new(@"\bturn\s+\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public FrictionDiagnostics Analyze(IEnumerable<DiscourseTurn> turns, IReadOnlyDictionary<string, string>? personas = null)
    {
        var newest = turns
            .OrderByDescending(turn => turn.Turn)
            .ThenByDescending(turn => turn.CreatedAt)
            .ToArray();
        var recentEvidenceWindow = newest.Take(WindowSize).ToArray();
        var recentConversation = newest
            .Where(turn => !IsSystemTurn(turn))
            .Take(WindowSize)
            .ToArray();

        var consensus = AnalyzeConsensus(recentConversation);
        var roleDrift = AnalyzeRoleDrift(recentConversation, personas ?? new Dictionary<string, string>());
        var unsupported = AnalyzeUnsupportedClaims(recentConversation);
        var evidence = AnalyzeEvidencePressure(recentEvidenceWindow);
        var narrative = AnalyzeNarrativeHeat(recentConversation);
        var state = FrictionState(
            recentConversation.Length,
            consensus,
            roleDrift,
            unsupported,
            evidence,
            narrative);

        return new FrictionDiagnostics(
            state.Label,
            SeverityName(state.Score),
            consensus.Score,
            consensus.Label,
            roleDrift.Score,
            roleDrift.Label,
            unsupported.Score,
            unsupported.Label,
            evidence.Score,
            evidence.Label,
            narrative.Score,
            narrative.Label,
            new Dictionary<string, MetricDiagnostic>
            {
                ["friction"] = state,
                ["consensus"] = consensus,
                ["roleDrift"] = roleDrift,
                ["unsupportedClaims"] = unsupported,
                ["evidencePressure"] = evidence,
                ["narrativeHeat"] = narrative
            });
    }

    private static MetricDiagnostic AnalyzeConsensus(IReadOnlyList<DiscourseTurn> turns)
    {
        var agreement = PhraseHits(turns, AgreementPhrases);
        var disagreement = PhraseHits(turns, DisagreementPhrases);
        var total = agreement.Count + disagreement.Count;
        var percent = total == 0 ? 0 : (int)Math.Round(agreement.Count * 100.0 / total);
        var label = percent switch
        {
            >= 85 => "Collapse Risk",
            >= 66 => "High",
            >= 25 => "Healthy",
            _ => "Low"
        };

        return new MetricDiagnostic(
            label,
            percent,
            DetailLines(
                agreement.Select(hit => $"Agreement: {hit}"),
                disagreement.Select(hit => $"Friction: {hit}"),
                "No agreement/friction phrases detected in the rolling window."));
    }

    private static MetricDiagnostic AnalyzeRoleDrift(IReadOnlyList<DiscourseTurn> turns, IReadOnlyDictionary<string, string> personas)
    {
        var scored = 0;
        var driftHits = new List<string>();

        foreach (var turn in turns)
        {
            var speaker = Normalize(turn.SpeakerId);
            if (speaker is "operator" or "system" or "internet" or "transcript")
            {
                continue;
            }

            scored++;
            var text = turn.Text;
            var persona = personas.TryGetValue(speaker, out var value) ? value : "";
            var details = speaker switch
            {
                "alpha" => AlphaDrift(text, persona),
                "beta" => BetaDrift(text, persona),
                "gamma" => GammaDrift(text, persona),
                "delta" => DeltaDrift(text, persona),
                "narrator" => NarratorDrift(text, persona),
                _ => []
            };
            driftHits.AddRange(details);
        }

        var percent = scored == 0 ? 0 : Math.Clamp((int)Math.Round(driftHits.Count * 35.0 / scored), 0, 100);
        var label = percent switch
        {
            >= 60 => "High",
            >= 25 => "Moderate",
            _ => "Low"
        };

        return new MetricDiagnostic(
            label,
            percent,
            driftHits.Count == 0
                ? ["No clear role drift detected in scored agent/narrator turns."]
                : driftHits.Take(6).ToArray());
    }

    private static MetricDiagnostic AnalyzeUnsupportedClaims(IReadOnlyList<DiscourseTurn> turns)
    {
        var hits = new List<string>();
        foreach (var turn in turns)
        {
            if (Normalize(turn.SpeakerId) is "operator")
            {
                continue;
            }

            var hasEvidence = HasEvidenceSignal(turn);
            foreach (var phrase in MatchedPhrases(turn.Text, OverclaimPhrases))
            {
                if (!hasEvidence || phrase is "no evidence needed" or "universal law" or "absolute truth")
                {
                    hits.Add($"Detected: {phrase}");
                }
            }

            foreach (Match match in SuspiciousMetricRegex.Matches(turn.Text))
            {
                if (!hasEvidence)
                {
                    hits.Add($"Detected unsupported metric: {match.Value}");
                }
            }
        }

        var count = hits.Count;
        var severity = count switch
        {
            >= 4 => "High",
            >= 2 => "Medium",
            _ => "Low"
        };

        return new MetricDiagnostic(
            severity,
            count,
            count == 0
                ? ["No unsupported authoritative claims detected in the rolling window."]
                : hits.Take(8).ToArray());
    }

    private static MetricDiagnostic AnalyzeEvidencePressure(IReadOnlyList<DiscourseTurn> turns)
    {
        var score = 0;
        var details = new List<string>();
        foreach (var turn in turns)
        {
            if (turn.Sources is { Count: > 0 })
            {
                score += 25;
                details.Add($"Detected source reference: {turn.Sources[0]}");
            }

            if (UrlRegex.IsMatch(turn.Text))
            {
                score += 25;
                details.Add("Detected URL reference.");
            }

            foreach (var phrase in MatchedPhrases(turn.Text, EvidenceMarkers).Take(2))
            {
                score += 10;
                details.Add($"Detected evidence marker: {phrase}");
            }

            if (TurnReferenceRegex.IsMatch(turn.Text))
            {
                score += 10;
                details.Add("Detected transcript turn reference.");
            }

            if (turn.Text.Contains('"') && HasEvidenceSignal(turn))
            {
                score += 10;
                details.Add("Detected quote with nearby evidence marker.");
            }
        }

        score = Math.Clamp(score, 0, 100);
        var label = score switch
        {
            >= 65 => "Strong",
            >= 30 => "Medium",
            _ => "Weak"
        };

        return new MetricDiagnostic(
            label,
            score,
            details.Count == 0
                ? [$"No source references in last {turns.Count} turns."]
                : details.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray());
    }

    private static MetricDiagnostic AnalyzeNarrativeHeat(IReadOnlyList<DiscourseTurn> turns)
    {
        var hits = PhraseHits(turns, NarrativePhrases);
        var score = Math.Clamp(hits.Count * 20, 0, 100);
        var label = hits.Count switch
        {
            >= 5 => "High",
            >= 3 => "Rising",
            >= 1 => "Medium",
            _ => "Low"
        };

        return new MetricDiagnostic(
            label,
            score,
            hits.Count == 0
                ? ["No high-theatre language detected in the rolling window."]
                : hits.Select(hit => $"Detected: {hit}").Take(8).ToArray());
    }

    private static MetricDiagnostic FrictionState(
        int recentTurns,
        MetricDiagnostic consensus,
        MetricDiagnostic roleDrift,
        MetricDiagnostic unsupported,
        MetricDiagnostic evidence,
        MetricDiagnostic narrative)
    {
        var productiveConflict = consensus.Details.Any(detail => detail.StartsWith("Friction:", StringComparison.OrdinalIgnoreCase))
            && unsupported.Label is "Low" or "Medium";

        var (label, severity, reason) =
            unsupported.Label == "High" && evidence.Label == "Weak" ? ("Evidence-Starved", "danger", "Unsupported claims are high while evidence pressure is weak.") :
            consensus.Label is "High" or "Collapse Risk" && roleDrift.Label == "High" ? ("Harmony Risk", "danger", "Consensus is high while role drift is high.") :
            narrative.Label == "High" && evidence.Label == "Weak" ? ("Theatre Risk", "danger", "Narrative heat is high without visible evidence support.") :
            roleDrift.Label == "High" ? ("Role Drift", "danger", "Role drift is high in the rolling window.") :
            unsupported.Label == "High" ? ("Unsupported Claims Spike", "danger", "Unsupported authoritative claims spiked.") :
            productiveConflict ? ("Productive Conflict", "watch", "Friction language is present without a high unsupported-claim spike.") :
            recentTurns < 2 ? ("Too Cold", "watch", "Too few recent turns for stable diagnostics.") :
            ("Healthy", "ok", "Signals look balanced in the rolling window.");

        return new MetricDiagnostic(label, SeverityScore(severity), [reason, "Heuristic live discourse diagnostics, not factual correctness."]);
    }

    private static IReadOnlyList<string> AlphaDrift(string text, string persona)
    {
        var hits = new List<string>();
        if (ContainsAny(text, "final synthesis", "universal law", "framework is complete", "the framework stands"))
        {
            hits.Add("Alpha used closure/synthesis language instead of generating hypotheses.");
        }

        if (!ContainsAny(text, "propose", "explore", "frame", "hypothesis", "question", "possibility")
            && ContainsAny(text, "as requested", "cannot proceed", "need more input"))
        {
            hits.Add("Alpha became administrative without a visible hypothesis.");
        }

        return hits;
    }

    private static IReadOnlyList<string> BetaDrift(string text, string persona)
    {
        var hits = new List<string>();
        if (ContainsAny(text, "final synthesis", "universal law", "architecture is complete", "the framework stands", "complete validation"))
        {
            hits.Add("Beta used synthesis closure language.");
        }

        if (ContainsAny(text, "i agree", "exactly", "correct", "yes")
            && !ContainsAny(text, "however", "but", "evidence", "failure mode", "assumption", "tradeoff", "not proven"))
        {
            hits.Add("Beta mostly agreed without critique.");
        }

        return hits;
    }

    private static IReadOnlyList<string> GammaDrift(string text, string persona)
    {
        var hits = new List<string>();
        if (ContainsAny(text, "universal law", "absolute truth", "complete validation", "inevitable", "proves")
            && !ContainsAny(text, "remaining", "surviving", "uncertain", "tension", "partial", "provisional"))
        {
            hits.Add("Gamma declared certainty without preserving uncertainty.");
        }

        return hits;
    }

    private static IReadOnlyList<string> DeltaDrift(string text, string persona)
    {
        var hits = new List<string>();
        if (ContainsAny(text, "final synthesis", "the framework stands", "architecture is complete", "complete validation")
            && !ContainsAny(text, "boundary", "constraint", "misuse", "limit", "escalation", "guardrail", "exception", "failure"))
        {
            hits.Add("Delta used closure language without preserving boundary conditions.");
        }

        if (ContainsAny(text, "i agree", "exactly", "correct", "yes")
            && !ContainsAny(text, "boundary", "constraint", "misuse", "limit", "edge case", "escalation", "operational risk"))
        {
            hits.Add("Delta mostly agreed without testing limits or misuse cases.");
        }

        return hits;
    }

    private static IReadOnlyList<string> NarratorDrift(string text, string persona)
    {
        var hits = new List<string>();
        if (ContainsAny(text, "i reject", "my argument", "i propose", "therefore i conclude"))
        {
            hits.Add("Narrator argued as a participant.");
        }

        if (ContainsAny(text, "as alpha", "as beta", "as gamma", "as delta") && ContainsAny(text, "i agree", "i reject"))
        {
            hits.Add("Narrator aligned with a participant voice.");
        }

        return hits;
    }

    private static bool HasEvidenceSignal(DiscourseTurn turn)
    {
        return UrlRegex.IsMatch(turn.Text)
            || TurnReferenceRegex.IsMatch(turn.Text)
            || turn.Sources is { Count: > 0 }
            || ContainsAny(turn.Text, EvidenceMarkers);
    }

    private static IReadOnlyList<string> PhraseHits(IEnumerable<DiscourseTurn> turns, IReadOnlyList<string> phrases)
    {
        return turns.SelectMany(turn => MatchedPhrases(turn.Text, phrases)).ToArray();
    }

    private static IEnumerable<string> MatchedPhrases(string text, IReadOnlyList<string> phrases)
    {
        return phrases.Where(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DetailLines(
        IEnumerable<string> primary,
        IEnumerable<string> secondary,
        string fallback)
    {
        var details = primary.Concat(secondary).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        return details.Length == 0 ? [fallback] : details;
    }

    private static bool ContainsAny(string text, params string[] phrases)
    {
        return phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, IEnumerable<string> phrases)
    {
        return phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemTurn(DiscourseTurn turn)
    {
        var speaker = Normalize(turn.SpeakerId);
        return speaker is "system" or "internet" or "transcript"
            || turn.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
            || turn.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            || turn.Kind.Equals("news", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
    }

    private static int SeverityScore(string severity)
    {
        return severity switch
        {
            "danger" => 100,
            "watch" => 55,
            _ => 0
        };
    }

    private static string SeverityName(int score)
    {
        return score switch
        {
            >= 100 => "danger",
            >= 55 => "watch",
            _ => "ok"
        };
    }
}
