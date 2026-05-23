namespace ExpertiseApi.Auth;

internal static class AuthConstants
{
    public const string ScopeClaimType = "scope";

    public const string ReadScope = "expertise.read";
    public const string WriteDraftScope = "expertise.write.draft";
    public const string WriteApproveScope = "expertise.write.approve";
    public const string AdminScope = "expertise.admin";

    /// <summary>
    /// Part D C6: corroborates an <c>X-Actor-Class: agent</c> header. Orthogonal to
    /// read/draft/approve/admin — agency ("who pulled the trigger") is independent of
    /// authority ("what the trigger is allowed to do"). A token can carry
    /// <c>expertise.read + expertise.agent</c> (a read-only agent caller) and nothing
    /// else; admin tokens are NOT implicitly agent. See ADR-008.
    /// </summary>
    public const string AgentScope = "expertise.agent";

    /// <summary>
    /// Legacy scope retained for one release cycle so that callers issued tokens before the
    /// scope split still pass <see cref="Policies.WriteAccess"/>. Removed in PR 6 alongside
    /// the production OIDC cutover.
    /// </summary>
    public const string LegacyWriteScope = "expertise.write";

    internal static class Policies
    {
        public const string ReadAccess = "ReadAccess";
        public const string WriteAccess = "WriteAccess";
        public const string WriteApproveAccess = "WriteApproveAccess";
        public const string AdminAccess = "AdminAccess";
    }

    internal static class Headers
    {
        /// <summary>Part D C6 actor-class self-attestation header. See ActorClassResolver.</summary>
        public const string ActorClass = "X-Actor-Class";
    }
}
