using ExpertiseApi.Auth;
using Microsoft.Extensions.Options;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Test double for <see cref="IOptionsMonitor{TOptions}"/> of <see cref="AgentUserAgentOptions"/>.
/// Returns an empty allowlist (UA corroboration disabled); tests that need a populated
/// allowlist construct their own with the patterns inline.
/// </summary>
internal sealed class StaticAgentUaOptionsMonitor : IOptionsMonitor<AgentUserAgentOptions>
{
    private readonly AgentUserAgentOptions _options;

    public StaticAgentUaOptionsMonitor() : this(new AgentUserAgentOptions()) { }

    public StaticAgentUaOptionsMonitor(AgentUserAgentOptions options) => _options = options;

    public AgentUserAgentOptions CurrentValue => _options;

    public AgentUserAgentOptions Get(string? name) => _options;

    public IDisposable? OnChange(Action<AgentUserAgentOptions, string?> listener) => null;
}
