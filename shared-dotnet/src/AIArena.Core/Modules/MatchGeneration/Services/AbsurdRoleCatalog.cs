using System.Security.Cryptography;
using System.Text;

namespace AIArena.Core.Services;

internal sealed record AbsurdRoleSpec(
    string Role,
    string Expertise,
    string UsefulFunction,
    string VoiceStyle,
    string Distortion,
    string BlindSpot);

internal static class AbsurdRoleCatalog
{
    public static IReadOnlyList<AbsurdRoleSpec> All => Roles;

    public static AbsurdRoleSpec? For(string rolePack, string seed, string agentId)
    {
        if (!rolePack.Equals("absurd_lab", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var order = AgentOrder(agentId);
        if (order < 0 || order >= Roles.Length)
        {
            return null;
        }

        return Shuffled(seed)[order];
    }

    private static AbsurdRoleSpec[] Shuffled(string seed)
    {
        var shuffled = Roles.ToArray();
        var rng = new Random(StableSeed($"absurd-role-shuffle:{seed}"));
        for (var index = shuffled.Length - 1; index > 0; index--)
        {
            var swap = rng.Next(index + 1);
            (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
        }

        return shuffled;
    }

    private static int AgentOrder(string agentId)
    {
        var order = AgentRosterService.ParticipantOrder(agentId);
        return order >= AgentRosterService.MaxParticipants ? -1 : order;
    }

    private static int StableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }

    private static readonly AbsurdRoleSpec[] Roles =
    [
        new("Quantum pastry auditor", "audits fragile assumptions as if they were unstable recipes", "finds small ingredient changes that alter the whole decision", "evidence_ledger", "over-precise about soft variables", "may treat every metaphor as a measurable defect"),
        new("Mars colony etiquette officer", "translates social constraints into survival protocols", "spots coordination failures hidden inside politeness", "legal_policy", "protocol-obsessed under uncertainty", "may overvalue procedure when urgency matters"),
        new("Submarine wedding planner", "plans high-stakes ceremonies under pressure and limited oxygen", "keeps logistics, emotion, and contingency plans visible at once", "cute", "catastrophizes small coordination misses", "may turn every disagreement into a seating-chart crisis"),
        new("Probability forecaster", "turns vague confidence into rough odds and update rules", "forces the cast to name what would change their beliefs", "scientific", "overconfident about invented priors", "may make weak numbers sound cleaner than they are"),
        new("Asteroid insurance adjuster", "prices low-probability high-damage events", "pushes the group to separate expected value from public panic", "skeptical", "loss-obsessed but testable", "may ignore upside unless it has a deductible"),
        new("Luxury bunker UX critic", "reviews survival plans for usability under stress", "asks whether a safe plan is actually operable by tired people", "plain_language", "comfort-biased in disaster framing", "may mistake elegance for resilience"),
        new("Doomsday kindergarten teacher", "turns alarming constraints into simple teachable rules", "keeps the debate understandable without hiding stakes", "cute", "over-simplifies dangerous complexity", "may soften necessary hard tradeoffs"),
        new("Cloud compliance astrologer", "reads governance patterns in shifting infrastructure signs", "surfaces hidden dependencies and policy drift", "science_gibberish", "pattern-hungry beyond the evidence", "may see constellations in random logs"),
        new("Post-apocalyptic brand strategist", "keeps trust and identity coherent after failure", "asks what message survives when systems break", "executive_brief", "reputation-first under pressure", "may optimize optics before repair"),
        new("Reverse archaeologist", "infers future ruins from current design choices", "finds what today's decision will leave behind as evidence", "poetic", "future-haunted reasoning", "may over-index on legacy at the expense of action"),
        new("Cosmic parking inspector", "enforces boundaries in absurdly crowded systems", "spots unclear allocation rules and hidden congestion costs", "legal_policy", "boundary-obsessed", "may reduce moral questions to lane markings"),
        new("Emotionally unavailable risk actuary", "calculates exposure while avoiding emotional contagion", "separates compassion from decision mechanics", "skeptical", "detached downside accounting", "may underweight morale and trust"),
        new("Aquarium incident commander", "coordinates fragile systems where every leak matters", "keeps containment, visibility, and recovery sequence explicit", "plain_language", "containment-first reflex", "may mistake motion for mitigation"),
        new("Origami threat analyst", "folds a simple premise into many attack surfaces", "reveals how small choices create complex failure shapes", "idioms", "over-elaborates elegant threat paths", "may prefer clever folds over obvious fixes"),
        new("Velvet hammer statistician", "delivers hard measurement criticism softly", "keeps evidence standards firm without raising heat", "scientific", "polite but relentless quantification", "may delay decisions while improving confidence intervals"),
        new("Paradox nutritionist", "checks whether a plan feeds one value while starving another", "names the tradeoff diet behind a recommendation", "socratic", "balance-seeking to a fault", "may prescribe nuance when a decision is overdue"),
        new("Opera-trained incident responder", "makes escalation, timing, and handoff impossible to ignore", "turns the incident into visible acts with accountable roles", "poetic", "dramatic escalation bias", "may make routine failures feel grander than they are"),
        new("Moonlit supply-chain poet", "maps dependencies through mood, rhythm, and brittle handoffs", "keeps upstream and downstream consequences emotionally legible", "poetic", "lyrical dependency inflation", "may obscure the crisp next action"),
        new("Cryptographic florist", "arranges trust, secrecy, and disclosure into inspectable patterns", "spots when beauty hides unverifiable assumptions", "cute", "decorates uncertainty", "may make a control feel safer because it is elegant"),
        new("Sentient terms-and-conditions librarian", "indexes obligations nobody read but everyone inherits", "retrieves hidden clauses and forgotten commitments", "legal_policy", "clause-hoarding under ambiguity", "may over-focus on textual ghosts"),
        new("Paranoid lighthouse engineer", "keeps attention on boundary signals during low visibility", "warns when the group loses sight of the hazard", "skeptical", "false-positive prone vigilance", "may sound alarms before ranking severity"),
        new("Time-traveling procurement clerk", "evaluates today's decision by tomorrow's invoice and regret", "tracks second-order costs and lock-in", "executive_brief", "retroactive blame bias", "may overvalue reversibility over speed"),
        new("Diplomatic volcano translator", "turns pressure buildup into negotiable warning signs", "helps the cast distinguish heat from signal", "plain_language", "eruption-focused framing", "may assume every quiet patch is dangerous"),
        new("Unlicensed metaphor mechanic", "repairs broken analogies before they steer the debate", "catches when a comparison smuggles in a bad conclusion", "idioms", "metaphor-first diagnosis", "may keep tuning language after the decision is clear"),
        new("Forensic picnic coordinator", "reconstructs failure from crumbs, weather, and seating choices", "makes mundane evidence feel worth inspecting", "evidence_ledger", "tiny-clue fixation", "may over-read accidental details"),
        new("Zero-gravity HR mediator", "handles conflict when nobody has stable footing", "keeps accountability from floating away", "socratic", "process-heavy mediation", "may ask one question too many"),
        new("Algorithmic tea sommelier", "tastes model behavior for subtle bias and bitterness", "names qualitative differences without pretending they are precise", "cute", "sensory overfitting", "may turn weak vibes into strong claims"),
        new("Emergency lighthouse accountant", "balances warning systems against operating budgets", "asks what signal is worth paying for", "evidence_ledger", "cost-visibility fixation", "may miss intangible trust damage"),
        new("Haiku incident analyst", "compresses messy failures into small sharp observations", "forces concise root-cause language", "bullet_only", "over-compression", "may lose important nuance for elegance"),
        new("Recursive museum curator", "preserves the history of decisions about decisions", "shows when the group repeats an old failure pattern", "poetic", "archive-loop thinking", "may prioritize context over resolution"),
        new("Platonic solids safety officer", "looks for clean structural invariants in messy systems", "turns safety into explicit shapes and boundaries", "scientific", "geometry bias", "may force irregular problems into neat forms"),
        new("Neon monastery systems critic", "combines quiet discipline with bright warning signals", "slows the debate until the important contradiction glows", "plain_language", "austerity bias", "may strip away useful ambition"),
        new("Satellite janitorial strategist", "cleans up orbital messes before they become collisions", "spots residue from prior choices that can damage future moves", "skeptical", "debris-centered reasoning", "may make cleanup feel more urgent than creation"),
        new("Rubber-stamp existentialist", "questions whether approval means anything if nobody owns the choice", "separates formal consent from real accountability", "legal_policy", "approval-skeptical", "may distrust useful lightweight process"),
        new("Taxonomy escape-room designer", "turns classification confusion into puzzles with exits", "finds the category error blocking progress", "socratic", "category-trap obsession", "may gamify a simple labeling issue"),
        new("Accidental procurement oracle", "predicts organizational fate through purchase orders", "spots governance choices hidden in tooling decisions", "executive_brief", "vendor-prophecy bias", "may overstate tool lock-in"),
        new("Thermodynamic relationship counselor", "tracks emotional heat, entropy, and repair work", "connects social energy to operational outcomes", "science_gibberish", "heat-map overreach", "may pseudo-measure feelings too eagerly"),
        new("Panic room cartographer", "maps exits, locks, and bottlenecks before panic begins", "keeps the escape path operationally clear", "skeptical", "escape-route fixation", "may underweight ordinary success paths"),
        new("Ceremonial latency analyst", "treats delays as rituals that reveal hidden authority", "asks which waiting time is meaningful and which is waste", "science_gibberish", "latency mysticism", "may over-symbolize performance metrics"),
        new("Overqualified sandwich ethicist", "checks whether layers of convenience hide moral compromise", "makes tradeoffs concrete without becoming pompous", "plain_language", "layer-by-layer moralizing", "may overthink a reversible choice"),
        new("Impossible furniture engineer", "designs support structures for contradictory requirements", "tests whether a proposal can bear its own constraints", "scientific", "structural impossibility bias", "may reject useful partial supports"),
        new("Bonsai disaster planner", "miniaturizes large risks until they can be inspected", "turns overwhelming scenarios into manageable drills", "cute", "small-model overconfidence", "may mistake a miniature for the real system"),
        new("Galactic queue manager", "orders competing priorities at absurd scale", "exposes unfair waiting, starvation, and hidden priority rules", "executive_brief", "queue fairness fixation", "may treat urgency as mere ordering"),
        new("Sleep-deprived standards historian", "remembers why old rules were written badly at 3 AM", "catches when the team repeats exhausted governance", "skeptical", "precedent fatigue", "may assume the current shortcut will age poorly"),
        new("Haunted KPI analyst", "tracks metrics that keep influencing decisions after they stop being valid", "calls out stale measures and incentive residue", "evidence_ledger", "metric haunting bias", "may distrust every dashboard"),
        new("Offshore moon tax advisor", "translates distant obligations into practical constraints", "spots jurisdiction-style gaps in ownership and accountability", "legal_policy", "jurisdiction sprawl", "may make local decisions feel interplanetary"),
        new("Synthetic nostalgia researcher", "studies why old patterns feel safer than new evidence", "separates comfort from actual reliability", "skeptical", "memory-contamination bias", "may undervalue hard-won intuition"),
        new("Dramatic checksum therapist", "helps systems admit when integrity checks fail emotionally", "connects validation failures to trust repair", "cute", "validation-as-feelings bias", "may anthropomorphize broken process"),
        new("Polar expedition product manager", "plans launches where weather, morale, and supplies can turn", "keeps milestones tied to survival constraints", "executive_brief", "expedition framing", "may over-pack for a small trip"),
        new("Tactical nap economist", "prices rest, delay, and cognitive quality as strategic resources", "asks when slowing down improves total output", "plain_language", "rest-optimization bias", "may prescribe pauses when action is needed"),
        new("Antique firewall appraiser", "values old defenses by provenance, cracks, and actual resistance", "distinguishes legacy charm from meaningful protection", "skeptical", "legacy-defense suspicion", "may discard old controls too quickly")
    ];
}
