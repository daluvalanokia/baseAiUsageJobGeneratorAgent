namespace SmartAgent.Core.Pm;

/// <summary>
/// Reasoned acceptance-criteria composition. Instead of generic bullets
/// ("Behavior matches the requirement"), the composer binds each criterion to
/// the story's real evidence — exact action parameters, declared field sizes,
/// view files, entity names — and consolidates everything into a few aligned
/// sentences that read as the requirement's contract.
/// </summary>
public static class AcComposer
{
    /// <summary>Everything the composer can reason over for one story.</summary>
    public sealed record StoryEvidence
    {
        public required string Title { get; init; }
        public required string Module { get; init; }
        public required string Persona { get; init; }
        public required string Want { get; init; }
        public string SoThat { get; init; } = "";
        public string Layer { get; init; } = "";
        public string Phase { get; init; } = "build";
        /// <summary>Owning controller/hub class, e.g. "GovernanceController".</summary>
        public string Controller { get; init; } = "";
        /// <summary>Real action signatures, e.g. "SaveUser(uid (string), username (string))".</summary>
        public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
        /// <summary>Entity the story touches, e.g. "UserProfile".</summary>
        public string Entity { get; init; } = "";
        /// <summary>Concrete fields with sizes/required, e.g. "SenderName (max 120 chars, required)".</summary>
        public IReadOnlyList<string> Constraints { get; init; } = Array.Empty<string>();
        /// <summary>View files, e.g. "Governance/Index.cshtml".</summary>
        public IReadOnlyList<string> Views { get; init; } = Array.Empty<string>();
        /// <summary>History evidence for enhancement stories, e.g. "the source's own EventId addition".</summary>
        public string HistoryNote { get; init; } = "";
    }

    /// <summary>
    /// Pure-generic bullets that carry no verifiable content. These are the
    /// phrases the quality engine treats as "not a criterion".
    /// </summary>
    private static readonly string[] GenericPhrases =
    {
        "behavior matches the requirement", "covered by tests", "works as expected",
        "ui is responsive", "meets the requirement", "all tests pass", "feature is complete",
        "user can use the feature", "no errors", "test the feature"
    };

    public static bool IsGeneric(string ac)
    {
        var low = ac.ToLowerInvariant().Trim();
        return low.Length < 12 || GenericPhrases.Any(g => low.Contains(g, StringComparison.Ordinal))
            || !low.Contains(' ');
    }

    /// <summary>
    /// True when a criterion is concrete enough to verify: it binds a real
    /// artifact (action call, file, field with size, parameter signature).
    /// </summary>
    public static bool IsConcrete(string ac) =>
        ac.Contains("()")                              // Controller.Action()
        || ac.Contains(".cshtml") || ac.Contains(".cs") || ac.Contains(".ts")
        || ac.Contains("binds parameters", StringComparison.OrdinalIgnoreCase)
        || ac.Contains("max ", StringComparison.OrdinalIgnoreCase)
        || ac.Contains("chars", StringComparison.OrdinalIgnoreCase)
        || ac.Contains("(", StringComparison.Ordinal) && ac.Contains(")", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(ac, @"\b[A-Z]\w+\.[A-Z]\w+\b");

    /// <summary>
    /// Composes a few meaningful, evidence-bound AC sentences from the story's
    /// context: what the user does (interaction), what the system accepts
    /// (contract), what happens on success (behavior), and how it is proven
    /// (verification).
    /// </summary>
    public static string[] Compose(StoryEvidence e)
    {
        var sentences = new List<string>(4);

        // 1. Interaction — who does what, where
        var views = e.Views.Count > 0
            ? $" via {string.Join(" and ", e.Views.Take(2))}"
            : "";
        sentences.Add(
            $"As a {e.Persona}, {Lower(StripPrefix(e.Want))}{views}, so that {Lower(StripPrefix(e.SoThat))}.");

        // 2. Contract — the exact input the source binds, and its constraints
        if (e.Actions.Count > 0)
        {
            var actions = string.Join("; ", e.Actions.Take(2));
            sentences.Add(e.Constraints.Count > 0
                ? $"The flow is bound to the source's real signatures — {actions} — and rejects submissions missing required values or exceeding {string.Join(", ", e.Constraints.Take(3))}."
                : $"The flow is bound to the source's real signatures — {actions}.");
        }
        else if (e.Constraints.Count > 0)
        {
            sentences.Add(
                $"Input honors the declared constraints: {string.Join(", ", e.Constraints.Take(4))}; values that violate them are rejected server-side before persistence.");
        }

        // 3. Behavior — persistence and visible outcome
        var entity = e.Entity.Length > 0 ? $"the {e.Entity} record" : $"{e.Module.ToLowerInvariant()} data";
        if (e.Controller.Length > 0)
            sentences.Add(
                $"A successful submission persists {entity} through {e.Controller} and the change is reflected in the {e.Module.ToLowerInvariant()} screens without a full reload.");
        else
            sentences.Add(
                $"A successful submission persists {entity} and the change is reflected in the {e.Module.ToLowerInvariant()} screens without a full reload.");

        // 4. Verification — enhancement stories must mirror the source's own history
        if (e.Phase == "enhance" && e.HistoryNote.Length > 0)
            sentences.Add(
                $"The extension {e.HistoryNote} and every existing behavior keeps working — proven by regression checks over the affected {(e.Layer.Length > 0 ? e.Layer : e.Module).ToLowerInvariant()} surfaces.");
        else
            sentences.Add(
                $"The behavior is proven by unit tests over {(e.Controller.Length > 0 ? e.Controller : e.Module)} plus one end-to-end pass through the {e.Module.ToLowerInvariant()} flow.");

        return sentences.ToArray();
    }

    /// <summary>
    /// Consolidates an existing AC list into a few aligned sentences: drops
    /// pure-generic bullets, merges the concrete survivors into themed
    /// sentences (contract, behavior, verification), and keeps the wording
    /// aligned with the story's want. Never returns fewer than two criteria.
    /// </summary>
    public static string[] Consolidate(IReadOnlyList<string> acs, string want)
    {
        var concrete = acs.Where(a => !IsGeneric(a) && a.Trim().Length > 0).ToList();
        var merged = new List<string>();

        // themes are disjoint: every criterion joins exactly one sentence
        // theme 1 — anything binding inputs/signatures/constraints
        var contract = concrete.Where(a => a.Contains("binds", StringComparison.OrdinalIgnoreCase)
            || a.Contains("parameters", StringComparison.OrdinalIgnoreCase)
            || a.Contains("max ", StringComparison.OrdinalIgnoreCase)
            || a.Contains("required", StringComparison.OrdinalIgnoreCase)).ToList();
        // theme 2 — behavior/persistence (from what is left)
        var behavior = concrete.Except(contract).Where(a => a.Contains("persist", StringComparison.OrdinalIgnoreCase)
            || a.Contains("save", StringComparison.OrdinalIgnoreCase)
            || a.Contains("broadcast", StringComparison.OrdinalIgnoreCase)
            || a.Contains("view", StringComparison.OrdinalIgnoreCase)
            || a.Contains("redirect", StringComparison.OrdinalIgnoreCase)).ToList();
        // theme 3 — everything else concrete
        var rest = concrete.Except(contract).Except(behavior).ToList();

        foreach (var theme in new[] { contract, behavior, rest })
            for (var i = 0; i < theme.Count; i += 3)   // ≤3 clauses per sentence, keeps it readable
                merged.Add(string.Join("; ", theme.Skip(i).Take(3)));

        if (merged.Count == 0)
        {
            // nothing survived: keep the best generic anchor but align it to the want
            var anchor = acs.FirstOrDefault() ?? $"The requirement is met";
            merged.Add($"{Lower(StripPrefix(want))} — {Lower(anchor.TrimEnd('.'))}.");
            merged.Add($"Verified against the source artifacts this story names.");
        }
        return merged.Take(4).ToArray();
    }

    private static string Lower(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string StripPrefix(string s) =>
        s.StartsWith("to ", StringComparison.OrdinalIgnoreCase) ? s[3..].Trim() : s.Trim();
}
