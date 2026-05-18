namespace ExpertiseApi.Models;

/// <summary>
/// Classifies the actor on whose behalf a state-changing request was issued.
/// <para>
/// Recorded on <see cref="ExpertiseAuditLog.ActorClass"/> so the audit log can distinguish
/// agent-mediated traffic (LLM tool loops) from interactive human callers and from
/// non-interactive machine principals. The distinction is necessary because LLM-mediated
/// callers carry a different threat profile (prompt-injection-amplified actions, see
/// <c>docs/security/integration-threat-model.md#part-d</c> Part D C6).
/// </para>
/// <para>
/// Mutually exclusive in this order: <see cref="Agent"/> &#x21A3; <see cref="Service"/>
/// &#x21A3; <see cref="Human"/>. An OIDC scope of <c>expertise.agent</c> always classifies
/// as <see cref="Agent"/>; non-interactive credentials (API key, client_credentials with
/// <c>azp != sub</c>) default to <see cref="Service"/>; everything else defaults to
/// <see cref="Human"/>.
/// </para>
/// </summary>
internal enum ActorClass
{
    /// <summary>Default: interactive human caller (browser, ad-hoc curl, IDE).</summary>
    Human = 0,

    /// <summary>
    /// Agent-mediated caller (pi skill+curl, in-tree pi extension, future LLM tool loops).
    /// Requires the <c>expertise.agent</c> OIDC scope; corroborated by the
    /// <c>X-Actor-Class: agent</c> header.
    /// </summary>
    Agent = 1,

    /// <summary>
    /// Non-interactive machine principal (CI pipeline, scheduled batch, ApiKey caller).
    /// Identified by <c>client_credentials</c> grant type, missing user-subject claim,
    /// or use of the ApiKey scheme.
    /// </summary>
    Service = 2
}
