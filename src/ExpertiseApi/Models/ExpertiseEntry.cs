using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using NpgsqlTypes;
using Pgvector;

namespace ExpertiseApi.Models;

internal class ExpertiseEntry
{
    public Guid Id { get; set; }

    public required string Domain { get; set; }

    public List<string> Tags { get; set; } = [];

    public required string Title { get; set; }

    public required string Body { get; set; }

    public EntryType EntryType { get; set; }

    public Severity Severity { get; set; }

    public required string Source { get; set; }

    public string? SourceVersion { get; set; }

    [Column(TypeName = "vector(512)")]
    public Vector? Embedding { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeprecatedAt { get; set; }

    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public required string Tenant { get; set; }

    public Visibility Visibility { get; set; } = Visibility.Private;

    public required string AuthorPrincipal { get; set; }

    public string? AuthorAgent { get; set; }

    public string? IntegrityHash { get; set; }

    public ReviewState ReviewState { get; set; } = ReviewState.Draft;

    public string? ReviewedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }

    public string? RejectionReason { get; set; }

    /// <summary>
    /// Identifier of the origin instance for entries that arrived via aggregator up-sync
    /// (ADR-013). Set SERVER-SIDE on the hub from the authenticated client's
    /// <c>Sync:KnownInstances</c> mapping — never from the request body (ADR-003 principle).
    /// Informational only: excluded from <c>IntegrityHashService.Compute</c>'s canonical
    /// fields and from dedup equality, like <see cref="Source"/>/<see cref="SourceVersion"/>.
    /// Null for locally-authored entries.
    /// </summary>
    public string? OriginInstanceId { get; set; }

    /// <summary>
    /// The author principal on the ORIGIN instance for up-synced entries (ADR-013) —
    /// reviewer context so a hub curator can see who wrote the entry on the spoke.
    /// Accepted from the request body as informational (unlike <see cref="OriginInstanceId"/>);
    /// excluded from canonical hash and dedup equality. Distinct from
    /// <see cref="AuthorPrincipal"/>, which records the hub-side (sync service) principal.
    /// </summary>
    public string? OriginAuthorPrincipal { get; set; }

    /// <summary>
    /// PostgreSQL <c>xmin</c> system column used as an EF Core optimistic concurrency token.
    /// Two reviewers racing on <c>POST /approve</c> + <c>POST /reject</c> against the same draft
    /// would both observe <c>ReviewState = Draft</c>; the second <c>SaveChangesAsync</c> throws
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> and the endpoint
    /// returns 409 Conflict instead of silently last-write-wins.
    /// Not included in API responses — it is an internal EF concurrency mechanism.
    /// </summary>
    [JsonIgnore]
    public uint Version { get; set; }
}
