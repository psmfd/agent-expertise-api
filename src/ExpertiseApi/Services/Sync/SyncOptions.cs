namespace ExpertiseApi.Services.Sync;

/// <summary>
/// Aggregator up-sync configuration (ADR-013), bound from the <c>Sync</c> section.
/// One section serves both roles: the SPOKE fields (<see cref="Enabled"/> through
/// <see cref="BatchSize"/>) drive <see cref="ExpertiseSyncWorker"/>, and the HUB field
/// (<see cref="KnownInstances"/>) drives server-side <c>OriginInstanceId</c> attribution —
/// a hub is typically <c>Enabled=false</c> with a populated map, a spoke the inverse.
/// Validation is a manual startup guard in <c>Program.cs</c> (repo convention — no
/// <c>ValidateOnStart</c>).
/// </summary>
internal sealed class SyncOptions
{
    /// <summary>Master switch for the spoke worker. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Hub base URL, e.g. <c>https://hub.example.com</c> (no trailing slash needed).</summary>
    public string? HubUrl { get; set; }

    /// <summary>OIDC token endpoint on the shared IdP (client_credentials grant).</summary>
    public string? TokenEndpoint { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Supply via environment (<c>Sync__ClientSecret</c> in secrets.env / k8s secret) — never in appsettings.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Optional <c>scope</c> parameter for the token request (IdP-dependent).</summary>
    public string? TokenScope { get; set; }

    /// <summary>Sync cadence. Default 5 minutes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Entries per POST. Hard-capped at the batch endpoint's limit of 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// HUB-side map: authenticated client identifier (<c>azp</c>/<c>appid</c>/<c>client_id</c>)
    /// → origin instance id recorded on <c>ExpertiseEntry.OriginInstanceId</c>. Server-side by
    /// design — the payload's claim about its own origin is never trusted (ADR-003 principle).
    /// </summary>
    public Dictionary<string, string> KnownInstances { get; } = new(StringComparer.Ordinal);
}
