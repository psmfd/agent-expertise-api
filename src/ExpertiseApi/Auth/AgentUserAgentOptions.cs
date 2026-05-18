namespace ExpertiseApi.Auth;

/// <summary>
/// User-Agent allowlist for Part D C6 actor-class corroboration. Bound from configuration
/// section <c>Auth:AgentUserAgents</c>; consumed via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
/// so changes to appsettings.json take effect without restart.
/// <para>
/// User-Agent is <strong>observability-only</strong> in the resolver \u2014 it never grants
/// authority on its own (UA is trivially client-set). It is used together with the
/// <c>expertise.agent</c> scope to corroborate an <c>X-Actor-Class: agent</c> header.
/// See <see cref="ActorClassResolver"/>.
/// </para>
/// <para>
/// Pattern syntax: literal substring match by default (case-insensitive); trailing <c>*</c>
/// makes the prefix match anchored to the start of the UA string. Empty list disables UA
/// corroboration entirely (header without scope always falls back to Human).
/// </para>
/// </summary>
internal sealed class AgentUserAgentOptions
{
    public const string SectionName = "Auth:AgentUserAgents";

    /// <summary>
    /// Allowlist patterns. Production deployments should ship the named harnesses
    /// (<c>pi-coding-agent</c>, <c>claude-code</c>, <c>codex-cli</c>) and omit
    /// development-only patterns like <c>curl/*</c>.
    /// </summary>
    public List<string> Patterns { get; set; } = new();
}
