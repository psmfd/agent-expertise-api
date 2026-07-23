using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Data;

internal class ExpertiseDbContext(
    DbContextOptions<ExpertiseDbContext> options,
    ITenantContextAccessor tenantAccessor) : DbContext(options)
{
    public DbSet<ExpertiseEntry> ExpertiseEntries => Set<ExpertiseEntry>();
    public DbSet<EmbeddingMetadata> EmbeddingMetadata => Set<EmbeddingMetadata>();
    public DbSet<ExpertiseAuditLog> ExpertiseAuditLogs => Set<ExpertiseAuditLog>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<ExpertiseEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

            entity.Property(e => e.Domain).IsRequired();
            entity.Property(e => e.Title).IsRequired();
            entity.Property(e => e.Body).IsRequired();
            entity.Property(e => e.Source).IsRequired();

            entity.Property(e => e.Tags).HasColumnType("text[]");
            entity.HasIndex(e => e.Tags).HasMethod("gin");

            entity.Property(e => e.EntryType)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(e => e.Severity)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.Embedding).HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");

            entity.HasIndex(e => e.Domain);
            entity.HasIndex(e => e.DeprecatedAt);

            entity.Property(e => e.Tenant).IsRequired();
            entity.HasIndex(e => e.Tenant);

            entity.Property(e => e.Visibility)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(e => e.AuthorPrincipal).IsRequired();

            entity.Property(e => e.ReviewState)
                .HasConversion<string>()
                .IsRequired();

            entity.HasIndex(e => new { e.Tenant, e.ReviewState })
                .IncludeProperties(e => new { e.Id, e.EntryType, e.Severity });

            entity.HasGeneratedTsVectorColumn(
                e => e.SearchVector,
                "english",
                e => new { e.Title, e.Body });

            entity.HasIndex(e => e.SearchVector)
                .HasMethod("gin");

            // Defense-in-depth tenant filter. Primary defense lives in IExpertiseRepository
            // (per ADR-001) — every read method constructs an explicit WHERE from its
            // TenantContext argument. This filter is the safety net for any future query
            // that forgets that explicit clause. When the accessor returns null (CLI,
            // design-time, test direct-context access) the predicate short-circuits and
            // no filter applies; the explicit repository WHERE then drives correctness.
            entity.HasQueryFilter(e =>
                tenantAccessor.Tenant == null ||
                e.Tenant == tenantAccessor.Tenant ||
                e.Tenant == "shared");

            // PostgreSQL xmin system column as the EF Core concurrency token. The column
            // already exists on every Postgres table (it's a system column); this
            // configuration just teaches EF to read it and emit `WHERE xmin = @original`
            // on UPDATE/DELETE. No schema migration is required for the column itself.
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .IsRowVersion()
                .ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<EmbeddingMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ModelName).IsRequired();
            // Singleton-row invariant is enforced by a raw-SQL unique index over a
            // constant expression (UX_EmbeddingMetadata_Singleton, #455) — EF's
            // fluent API cannot express it, so it lives only in the migration.
        });

        // Spoke-side up-sync cursor (ADR-013). Singleton-row semantics are enforced at
        // the call site (get-or-create) — sole writer is the sync worker, so the
        // EmbeddingMetadata-style DB guard (#455) is not replicated here.
        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<ExpertiseAuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Timestamp).HasDefaultValueSql("now()");

            entity.Property(e => e.Action)
                .HasConversion<string>()
                .IsRequired();

            // Part D C6 — actor classification. Stored as text via HasConversion<string>()
            // to match the convention used for Action / Visibility / ReviewState (text
            // columns, not PgEnum) so the migration style stays consistent. The defense
            // here is that any future code path that constructs an audit row without
            // setting ActorClass gets ActorClass.Human (enum 0) via .NET default, which
            // matches the C6 "default human" rule.
            entity.Property(e => e.ActorClass)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(e => e.Tenant).IsRequired();
            entity.Property(e => e.Principal).IsRequired();

            entity.HasOne<ExpertiseEntry>()
                .WithMany()
                .HasForeignKey(e => e.EntryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.EntryId, e.Timestamp })
                .IncludeProperties(e => e.Action);

            entity.HasIndex(e => new { e.Principal, e.Timestamp });

            // Supports the /audit?actor_class= filter introduced with C6. Composite with
            // Timestamp keeps the index aligned with the (Timestamp DESC, Id) sort order.
            entity.HasIndex(e => new { e.ActorClass, e.Timestamp });
        });
    }
}
