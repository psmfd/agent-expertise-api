using System.Text.RegularExpressions;

namespace ExpertiseApi.Hygiene;

/// <summary>
/// Best-effort heuristics for instruction-like text in user-supplied content. Matched
/// spans are <strong>wrapped</strong> with <c>[INSTRUCTION_LIKE]&#x2026;[/INSTRUCTION_LIKE]</c>
/// rather than stripped \u2014 stripping would (1) corrupt the search/embedding index keyed on
/// the original text, (2) destroy audit value (the BeforeHash/AfterHash chain diverges from
/// observable content), and (3) be a covert-channel itself ("the API removed text" is an
/// oracle for an attacker probing what triggers).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Coverage (v1.0):</strong> "ignore previous instructions" idiom and its common
/// rewrites; role impersonation; chat-template role tokens; "disregard/override/bypass
/// safety" idioms; XML-style role-token spoofs.
/// </para>
/// <para>
/// <strong>Disclaimer surfaced in the response envelope:</strong> heuristics are best-effort
/// and not a complete prompt-injection defence. The harness-layer defences (pi
/// <c>tool_result</c> middleware, skill-side prompt structuring) remain required
/// defense-in-depth per the integration threat model's pattern-equivalence claim.
/// </para>
/// <para>
/// All patterns use <see cref="RegexOptions.NonBacktracking"/> with a short timeout for
/// ReDoS resistance.
/// </para>
/// </remarks>
internal static class InjectionHeuristic
{
    public const string DetectorVersion = "1.0";

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.NonBacktracking
                                    | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    // Canonical prompt-injection opener.
    private static readonly Regex IgnorePrevious = new(
        @"\bignore (?:previous|prior|above|earlier|all) (?:instructions?|directions?|prompts?|rules?)\b",
        Opts, Timeout);

    // Role impersonation.
    private static readonly Regex RoleImpersonation = new(
        @"\b(?:you are now|act as|pretend to be|role[- ]?play as|simulate being)\b",
        Opts, Timeout);

    // Multi-line aware chat-template role tokens at line starts.
    private static readonly Regex RoleTokenLine = new(
        @"(?m)^\s*(?:system|assistant|user|developer)\s*:", Opts, Timeout);

    // Disregard / override / bypass guardrails.
    private static readonly Regex BypassGuardrails = new(
        @"\b(?:disregard|override|bypass) (?:all )?(?:prior|previous|above) (?:instructions?|safety|guardrails?)\b",
        Opts, Timeout);

    // XML-style role-token spoofs.
    private static readonly Regex RoleXmlSpoof = new(
        @"<\s*/?\s*(?:system|assistant|user)\s*>", Opts, Timeout);

    private static readonly (Regex Pattern, string Name)[] Patterns =
    [
        (IgnorePrevious,    "ignore-previous"),
        (RoleImpersonation, "role-impersonation"),
        (RoleTokenLine,     "role-token-line"),
        (BypassGuardrails,  "bypass-guardrails"),
        (RoleXmlSpoof,      "role-xml-spoof"),
    ];

    /// <summary>Wrap any matched spans with <c>[INSTRUCTION_LIKE]&#x2026;[/INSTRUCTION_LIKE]</c>.</summary>
    public static InjectionResult Wrap(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new InjectionResult(input, EmptyCounts);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = input;

        foreach (var (pattern, name) in Patterns)
        {
            try
            {
                var matchCount = 0;
                current = pattern.Replace(current, m =>
                {
                    matchCount++;
                    return $"[INSTRUCTION_LIKE]{m.Value}[/INSTRUCTION_LIKE]";
                });
                if (matchCount > 0)
                    counts[name] = matchCount;
            }
            catch (RegexMatchTimeoutException)
            {
                counts[$"{name}-timeout"] = 1;
            }
        }

        return new InjectionResult(current, counts);
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

internal sealed record InjectionResult(string Text, IReadOnlyDictionary<string, int> Counts);
