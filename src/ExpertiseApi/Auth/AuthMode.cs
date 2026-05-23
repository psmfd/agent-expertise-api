using System.Diagnostics.CodeAnalysis;

namespace ExpertiseApi.Auth;

// Carve-out: this enum is consumed as a parameter type by [Theory] tests in
// AuthModeStartupGuardTests. xUnit 2 requires test methods to be public, and
// public methods cannot take internal parameter types. If/when the test
// project upgrades to xUnit 3 (which supports non-public test methods), this
// can flip to `internal`. See ADR-006.
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit 2 [Theory] parameter type; see ADR-006.")]
public enum AuthMode
{
    /// <summary>OIDC only. Required for non-Development environments.</summary>
    Oidc,

    /// <summary>Custom dev token format <c>Bearer dev:{tenant}:{scope1}+{scope2}</c>. Development only.</summary>
    LocalDev,

    /// <summary>Legacy static API key. Development only.</summary>
    ApiKey,

    /// <summary>Accepts API key, JWT, and LocalDev tokens. Development default.</summary>
    Hybrid
}
