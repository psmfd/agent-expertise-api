using ExpertiseApi.Endpoints.Filters;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Pins the literal value of <see cref="IdempotencyEndpointFilter.BodyOmittedWarning"/>
/// so that any future drift between ADR-010 (which documents the wire value) and the
/// shipped constant breaks a build rather than silently changing the wire contract.
/// <para>
/// See ADR-010 Amendment 2 for the rationale behind code <c>199</c> vs <c>299</c>.
/// </para>
/// </summary>
public class IdempotencyWarningCodeTests
{
    [Fact]
    public void BodyOmittedWarning_pins_to_RFC7234_code_199_with_expected_text()
    {
        const string Expected = "199 - \"Idempotent response truncated; original body not replayable\"";

        IdempotencyEndpointFilter.BodyOmittedWarning.Should().Be(
            Expected,
            "ADR-010 Amendment 2 fixes the truncation Warning code at 199; changing it is a wire-contract change that requires an ADR amendment");
    }
}
