namespace ExpertiseApi.Models;

/// <summary>
/// Singleton-row record of the most recent integrity verification run (ADR-020).
/// Written by the <c>verify</c> CLI so the API process — the only long-lived,
/// scrapeable process — can surface last-run gauges; a short-lived CLI's own
/// Prometheus counters die with it. Follows the <see cref="SyncState"/> singleton
/// pattern: get-or-create at the call site, sole writer is the verification sweep.
/// </summary>
internal class IntegrityVerificationState
{
    public int Id { get; set; }

    /// <summary>Wall-clock (UTC) when the last verification run finished.</summary>
    public DateTime LastRunAt { get; set; }

    /// <summary>Outcome of the last run: <c>clean</c> or <c>mismatch</c>.</summary>
    public required string LastResult { get; set; }

    /// <summary>Total mismatches (entry MACs + checkpoint root/MAC/chain) in the last run.</summary>
    public int MismatchCount { get; set; }

    /// <summary>Entries still carrying a legacy unkeyed (bare-hex) hash — non-fatal, reported.</summary>
    public int LegacyCount { get; set; }

    /// <summary>Entries with a null <c>IntegrityHash</c> — non-fatal, reported (run <c>rehash</c>).</summary>
    public int UnhashedCount { get; set; }
}
