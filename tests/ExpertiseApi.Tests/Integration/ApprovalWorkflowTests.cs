using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Approval workflow: <c>/approve</c>, <c>/reject</c>, state-machine guards, audit row
/// atomicity, optimistic concurrency, PATCH state regression, shared-entry soft-delete
/// scope, and rejection-reason validation.
/// </summary>
[Collection("Postgres")]
public class ApprovalWorkflowTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public ApprovalWorkflowTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientWithScopes(params string[] scopes)
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: scopes,
            groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ExpertiseEntry> SeedDraft(
        string tenant = "test",
        string title = "needs review",
        string body = "draft body content")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var entry = TestHelpers.SeedEntry(
            tenant: tenant, title: title, body: body, reviewState: ReviewState.Draft);
        db.ExpertiseEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private async Task<int> CountAuditRows(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        return await db.ExpertiseAuditLogs.CountAsync(a => a.EntryId == entryId);
    }

    private async Task<ExpertiseAuditLog?> LatestAudit(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        return await db.ExpertiseAuditLogs
            .Where(a => a.EntryId == entryId)
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Approve_DraftWithApproveScope_TransitionsToApproved()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Approved");
        json.GetProperty("reviewedBy").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("reviewedAt").GetString().Should().NotBeNullOrEmpty();

        var audit = await LatestAudit(draft.Id);
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(AuditAction.Approved);
    }

    [Fact]
    public async Task Approve_WithoutApproveScope_Returns403()
    {
        var draft = await SeedDraft();
        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await writer.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approve_AlreadyApprovedEntry_Returns409()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var first = await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_CrossTenantEntry_Returns404()
    {
        // Caller is in tenant "test"; entry is in tenant "other-team".
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);
        var draft = await SeedDraft(tenant: "other-team");

        var response = await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_WithVisibilityShared_SetsVisibility()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve",
            new { visibility = "Shared" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("visibility").GetString().Should().Be("Shared");
    }

    [Fact]
    public async Task Reject_DraftWithReason_TransitionsToRejected()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync(
            $"/expertise/{draft.Id}/reject",
            new { rejectionReason = "Lacks supporting context." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Rejected");
        json.GetProperty("rejectionReason").GetProperty("value").GetString().Should().Contain("Lacks supporting context.");

        var audit = await LatestAudit(draft.Id);
        audit!.Action.Should().Be(AuditAction.Rejected);
    }

    [Fact]
    public async Task Reject_WithoutReason_Returns400()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync(
            $"/expertise/{draft.Id}/reject",
            new { rejectionReason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reject_OverlongReason_Returns400()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync(
            $"/expertise/{draft.Id}/reject",
            new { rejectionReason = new string('x', 2001) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approve_WritesIntegrityHashAndAuditChain()
    {
        var draft = await SeedDraft();
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        await approver.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });

        var audit = await LatestAudit(draft.Id);
        audit!.BeforeHash.Should().NotBeNullOrEmpty();
        audit.AfterHash.Should().NotBeNullOrEmpty();
        // ReviewState is excluded from the canonical hash, so before == after on approve.
        audit.BeforeHash.Should().Be(audit.AfterHash);
    }

    [Fact]
    public async Task ConcurrentApproveAndReject_OneSucceedsOneConflicts()
    {
        var draft = await SeedDraft();
        using var c1 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);
        using var c2 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var t1 = c1.PostAsJsonAsync($"/expertise/{draft.Id}/approve", new { });
        var t2 = c2.PostAsJsonAsync(
            $"/expertise/{draft.Id}/reject",
            new { rejectionReason = "racey" });

        var results = await Task.WhenAll(t1, t2);
        var statuses = results.Select(r => (int)r.StatusCode).OrderBy(s => s).ToArray();

        // One must succeed (200), one must fail with 409 (either invalid-state or
        // concurrency token mismatch — both are 409).
        statuses.Should().BeEquivalentTo(new[] { 200, 409 });
    }

    [Fact]
    public async Task Patch_OnApprovedByDraftCaller_RegressesToDraft()
    {
        // Seed an Approved entry, then PATCH it as a write.draft caller. Per ADR-003 the
        // entry should regress to Draft so it requires re-approval.
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "test", title: "approved-content", reviewState: ReviewState.Approved);
            seeded.ReviewedBy = "previous-approver";
            seeded.ReviewedAt = DateTime.UtcNow;
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { body = "edited body — should regress to draft" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Draft");
    }

    [Fact]
    public async Task Patch_OnApprovedByApproveCaller_PreservesApproved()
    {
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "test", title: "approved-content", reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { body = "edited by approver — stays Approved" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task Patch_SharedEntryByWriteDraftCaller_Returns403_AndEntryNotStranded()
    {
        // #330: content-editing a shared entry requires write.approve, mirroring the
        // soft-delete gate. Without it, the ADR-003 regression demotes the entry to
        // Draft + Tenant="shared", which no tenant's draft queue can see — stranded.
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "shared", title: "shared-knowledge-patch-target", reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { title = "cross-tenant vandalism attempt" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The entry must be untouched: still Approved, still readable, original title.
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var after = await verifyDb.ExpertiseEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == seeded.Id);
        after.ReviewState.Should().Be(ReviewState.Approved);
        after.Title.Should().Be("shared-knowledge-patch-target");
    }

    [Fact]
    public async Task Patch_SharedEntryByApproveCaller_Succeeds()
    {
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "shared", title: "shared-approver-editable", reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var approver = ClientWithScopes(
            AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { body = "curator edit — permitted and stays Approved" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task Delete_SharedEntryByWriteDraftCaller_Returns403()
    {
        // Soft-delete on shared entries requires write.approve per ADR-003.
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "shared", title: "shared-knowledge", reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.DeleteAsync($"/expertise/{seeded.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_VisibilityByDraftCaller_Returns403()
    {
        // Issue #66 / ADR-003 clarification: changing Visibility (Private <-> Shared)
        // requires expertise.write.approve even for the entry's original writer.
        var draft = await SeedDraft();
        // Sanity check: seeded entries default to Private.
        draft.Visibility.Should().Be(Visibility.Private);

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{draft.Id}",
            new { visibility = "Shared" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Atomicity: no fields should have been applied. Reload and verify Visibility unchanged.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var reloaded = await db.ExpertiseEntries.AsNoTracking().FirstAsync(e => e.Id == draft.Id);
        reloaded.Visibility.Should().Be(Visibility.Private);
    }

    [Fact]
    public async Task Patch_VisibilityByApproveCaller_Succeeds()
    {
        var draft = await SeedDraft();
        draft.Visibility.Should().Be(Visibility.Private);

        using var approver = ClientWithScopes(
            AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.PatchAsJsonAsync(
            $"/expertise/{draft.Id}",
            new { visibility = "Shared" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var reloaded = await db.ExpertiseEntries.AsNoTracking().FirstAsync(e => e.Id == draft.Id);
        reloaded.Visibility.Should().Be(Visibility.Shared);
    }

    [Fact]
    public async Task Patch_NonVisibilityFieldsByDraftCaller_DoesNotEscalate()
    {
        // Regression guard: a PATCH that does NOT change Visibility must not require write.approve.
        // The scope escalation is value-based (entry.Visibility differs from snapshot), not
        // request-presence-based, so an absent visibility field is the common case.
        var draft = await SeedDraft(title: "original title");

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{draft.Id}",
            new { title = "revised title" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var reloaded = await db.ExpertiseEntries.AsNoTracking().FirstAsync(e => e.Id == draft.Id);
        reloaded.Title.Should().Be("revised title");
        reloaded.Visibility.Should().Be(Visibility.Private);
    }

    [Fact]
    public async Task Patch_NoOpVisibilityByDraftCaller_DoesNotEscalate()
    {
        // Value-based gating: supplying Visibility=Private on an entry that is already
        // Private is a no-op and must NOT require write.approve. Approval-tooling that
        // submits the full entry state on every save (Visibility included) would otherwise
        // be forced to hold write.approve just to edit titles.
        var draft = await SeedDraft();
        draft.Visibility.Should().Be(Visibility.Private);

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{draft.Id}",
            new { visibility = "Private", title = "updated" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var reloaded = await db.ExpertiseEntries.AsNoTracking().FirstAsync(e => e.Id == draft.Id);
        reloaded.Visibility.Should().Be(Visibility.Private);
        reloaded.Title.Should().Be("updated");
    }

    [Fact]
    public async Task Patch_VisibilityShared_to_Private_ByDraftCaller_Returns403()
    {
        // Symmetric case: demoting a Shared entry to Private is also a Visibility change
        // and must require write.approve. Seed an Approved+Shared entry so the inverse
        // direction is exercised AND the gate-before-state-regression ordering invariant
        // is locked in (a 403 must not leave behind Approved->Draft demotion side effects).
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "shared",
                title: "shared-entry",
                reviewState: ReviewState.Approved,
                visibility: Visibility.Shared);
            seeded.ReviewedBy = "reviewer-original";
            seeded.ReviewedAt = DateTime.UtcNow.AddMinutes(-5);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { visibility = "Private" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Ordering invariant: visibility gate fires before the ADR-003 state-regression
        // block, so a denied request must NOT have demoted the entry to Draft or cleared
        // ReviewedBy / ReviewedAt as a side effect.
        using var verifyScope = _factory.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var reloaded = await db2.ExpertiseEntries.AsNoTracking().FirstAsync(e => e.Id == seeded.Id);
        reloaded.Visibility.Should().Be(Visibility.Shared);
        reloaded.ReviewState.Should().Be(ReviewState.Approved);
        reloaded.ReviewedBy.Should().Be("reviewer-original");
        reloaded.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_SharedEntryByApproveCaller_Succeeds()
    {
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "shared", title: "shared-knowledge", reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.DeleteAsync($"/expertise/{seeded.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Create_WritesAuditRow()
    {
        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await writer.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title = "audit-on-create",
            body = "body content for audit on create",
            entryType = "Pattern",
            severity = "Info",
            source = "test"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadJsonElementAsync();
        var id = json.GetProperty("id").GetGuid();

        var audit = await LatestAudit(id);
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(AuditAction.Created);
        audit.BeforeHash.Should().BeNull();
        audit.AfterHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Update_WritesAuditRowWithDifferentHashes()
    {
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "test", title: "original-title", body: "original-body",
                reviewState: ReviewState.Approved);
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { title = "new-title-changes-hash" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await LatestAudit(seeded.Id);
        audit!.Action.Should().Be(AuditAction.Updated);
        audit.BeforeHash.Should().NotBeNullOrEmpty();
        audit.AfterHash.Should().NotBeNullOrEmpty();
        audit.BeforeHash.Should().NotBe(audit.AfterHash);
    }

    [Fact]
    public async Task Create_SharedEntryByApproveCaller_CreatesAsApproved()
    {
        // write.approve callers may specify Tenant="shared" — entry is created directly
        // as Approved (bypassing the draft queue which only surfaces the caller's own tenant).
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title = "shared-knowledge-direct",
            body = "authoritative cross-team content",
            entryType = "Pattern",
            severity = "Info",
            source = "test",
            tenant = "shared"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("tenant").GetString().Should().Be("shared");
        json.GetProperty("reviewState").GetString().Should().Be("Approved");
        json.GetProperty("reviewedBy").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("reviewedAt").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_SharedEntryByDraftCaller_Returns403()
    {
        // write.draft-only callers are not allowed to create Tenant="shared" entries.
        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var response = await writer.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title = "should-be-rejected",
            body = "draft caller cannot set shared tenant",
            entryType = "Pattern",
            severity = "Info",
            source = "test",
            tenant = "shared"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_WithNonSharedTenantOverride_Returns400()
    {
        // Only Tenant="shared" is a valid override — all other tenant values are rejected.
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title = "should-be-rejected",
            body = "cannot override to arbitrary tenant",
            entryType = "Pattern",
            severity = "Info",
            source = "test",
            tenant = "other-team"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_OnRejectedByDraftCaller_RegressesToDraft_ClearsRejectionReason()
    {
        // Symmetric to Patch_OnApprovedByDraftCaller_RegressesToDraft. A write.draft caller
        // editing a Rejected entry resets it to Draft and clears rejection metadata so the
        // author can resubmit content after addressing the rejection reason.
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "test", title: "rejected-content", reviewState: ReviewState.Rejected);
            seeded.ReviewedBy = "previous-rejector";
            seeded.ReviewedAt = DateTime.UtcNow;
            seeded.RejectionReason = "needs more detail";
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { body = "edited body — addresses rejection reason" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Draft");
        json.GetProperty("rejectionReason").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        json.GetProperty("reviewedBy").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        json.GetProperty("reviewedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Patch_OnRejectedByApproveCaller_PreservesRejected()
    {
        // Symmetric to Patch_OnApprovedByApproveCaller_PreservesApproved. A write.approve
        // caller editing a Rejected entry preserves the Rejected state — the regression
        // rule only fires for write.draft-only callers.
        ExpertiseEntry seeded;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            seeded = TestHelpers.SeedEntry(
                tenant: "test", title: "rejected-content", reviewState: ReviewState.Rejected);
            seeded.RejectionReason = "needs more detail";
            db.ExpertiseEntries.Add(seeded);
            await db.SaveChangesAsync();
        }

        using var approver = ClientWithScopes(
            AuthConstants.ReadScope, AuthConstants.WriteDraftScope, AuthConstants.WriteApproveScope);
        var response = await approver.PatchAsJsonAsync(
            $"/expertise/{seeded.Id}",
            new { body = "edited by approver — stays Rejected" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetProperty("reviewState").GetString().Should().Be("Rejected");
        json.GetProperty("rejectionReason").GetProperty("value").GetString().Should().Contain("needs more detail");
    }

    [Fact]
    public async Task ConcurrentPatch_RaceProducesAtLeastOne409()
    {
        // UpdateAsync catches DbUpdateConcurrencyException (xmin race) and returns 409
        // instead of bubbling as an unhandled 500. Fans out to several concurrent PATCHes
        // to make the race reliable — exactly two clients can occasionally serialize
        // (both 200) on a fast in-process test server.
        var seeded = await SeedDraft(title: "racey-patch");

        using var c0 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        using var c1 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        using var c2 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        using var c3 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        using var c4 = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);

        var tasks = new[]
        {
            c0.PatchAsJsonAsync($"/expertise/{seeded.Id}", new { body = "writer 0" }),
            c1.PatchAsJsonAsync($"/expertise/{seeded.Id}", new { body = "writer 1" }),
            c2.PatchAsJsonAsync($"/expertise/{seeded.Id}", new { body = "writer 2" }),
            c3.PatchAsJsonAsync($"/expertise/{seeded.Id}", new { body = "writer 3" }),
            c4.PatchAsJsonAsync($"/expertise/{seeded.Id}", new { body = "writer 4" })
        };

        var results = await Task.WhenAll(tasks);
        var statuses = results.Select(r => (int)r.StatusCode).ToList();

        statuses.Should().Contain(200, "at least one PATCH must win the race");
        statuses.Should().Contain(409, "at least one PATCH must lose the xmin race and map to 409");
        statuses.Should().OnlyContain(s => s == 200 || s == 409, "no other status is expected");
    }

    [Fact]
    public async Task Create_DuplicateTitleOfRejectedEntry_Succeeds()
    {
        // Dedup queries must exclude Rejected entries; otherwise a Rejected entry
        // permanently blocks resubmission of identical content. Same title + body as
        // the seeded entry — without the Rejected-exclusion fix this would 409.
        const string title = "previously-rejected";
        const string body = "exact body that would otherwise dedup";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var rejected = TestHelpers.SeedEntry(
                domain: "shared", tenant: "test", title: title, body: body,
                reviewState: ReviewState.Rejected);
            rejected.RejectionReason = "prior rejection";
            db.ExpertiseEntries.Add(rejected);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title,
            body,
            entryType = "Pattern",
            severity = "Info",
            source = "test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_DuplicateTitleOfDraftEntry_Conflicts()
    {
        // Dedup-against-Draft is preserved: a same-tenant user submitting the same content
        // as their own existing draft should still get a 409. Only Rejected entries are
        // excluded from dedup. Identical title + body triggers FindExactMatchAsync in
        // DeduplicationService.
        const string title = "in-progress-draft";
        const string body = "exact body content for dedup match";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var draft = TestHelpers.SeedEntry(
                domain: "shared", tenant: "test", title: title, body: body,
                reviewState: ReviewState.Draft);
            db.ExpertiseEntries.Add(draft);
            await db.SaveChangesAsync();
        }

        using var writer = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteDraftScope);
        var response = await writer.PostAsJsonAsync("/expertise", new
        {
            domain = "shared",
            title,
            body,
            entryType = "Pattern",
            severity = "Info",
            source = "test"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}

[Collection("Postgres")]
public class AuditEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public AuditEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientWithScopes(params string[] scopes)
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: scopes,
            groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ListAudit_WithoutAdminScope_Returns403()
    {
        using var approver = ClientWithScopes(AuthConstants.ReadScope, AuthConstants.WriteApproveScope);

        var response = await approver.GetAsync("/audit");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListAudit_WithAdminScope_ReturnsRows()
    {
        // Seed an entry + audit row via direct DbContext. Pre-generate the entry Id so
        // the audit row's FK can reference a known GUID before SaveChanges.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var entry = TestHelpers.SeedEntry(tenant: "test", reviewState: ReviewState.Approved);
            entry.Id = Guid.NewGuid();
            db.ExpertiseEntries.Add(entry);
            db.ExpertiseAuditLogs.Add(new ExpertiseAuditLog
            {
                Timestamp = DateTime.UtcNow,
                Action = AuditAction.Created,
                EntryId = entry.Id,
                Tenant = "test",
                Principal = "test-principal",
                BeforeHash = null,
                AfterHash = "deadbeef"
            });
            await db.SaveChangesAsync();
        }

        using var admin = ClientWithScopes(AuthConstants.AdminScope);

        var response = await admin.GetAsync("/audit");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListAudit_FiltersByEntryId()
    {
        Guid targetId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var keep = TestHelpers.SeedEntry(tenant: "test", title: "keep");
            keep.Id = Guid.NewGuid();
            var skip = TestHelpers.SeedEntry(tenant: "test", title: "skip");
            skip.Id = Guid.NewGuid();
            db.ExpertiseEntries.AddRange(keep, skip);
            await db.SaveChangesAsync();
            targetId = keep.Id;

            db.ExpertiseAuditLogs.AddRange(
                new ExpertiseAuditLog { Timestamp = DateTime.UtcNow, Action = AuditAction.Created, EntryId = keep.Id, Tenant = "test", Principal = "p" },
                new ExpertiseAuditLog { Timestamp = DateTime.UtcNow, Action = AuditAction.Created, EntryId = skip.Id, Tenant = "test", Principal = "p" });
            await db.SaveChangesAsync();
        }

        using var admin = ClientWithScopes(AuthConstants.AdminScope);

        var response = await admin.GetAsync($"/audit?entryId={targetId}");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("entryId").GetGuid().Should().Be(targetId);
    }

    [Fact]
    public async Task ListAudit_AdminSeesAllTenants()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
            var a = TestHelpers.SeedEntry(tenant: "test", title: "tenant-a");
            a.Id = Guid.NewGuid();
            var b = TestHelpers.SeedEntry(tenant: "other-team", title: "tenant-b");
            b.Id = Guid.NewGuid();
            db.ExpertiseEntries.AddRange(a, b);
            await db.SaveChangesAsync();

            db.ExpertiseAuditLogs.AddRange(
                new ExpertiseAuditLog { Timestamp = DateTime.UtcNow, Action = AuditAction.Created, EntryId = a.Id, Tenant = "test", Principal = "p" },
                new ExpertiseAuditLog { Timestamp = DateTime.UtcNow, Action = AuditAction.Created, EntryId = b.Id, Tenant = "other-team", Principal = "p" });
            await db.SaveChangesAsync();
        }

        using var admin = ClientWithScopes(AuthConstants.AdminScope);

        var response = await admin.GetAsync("/audit");

        var json = await response.Content.ReadJsonElementAsync();
        json.GetArrayLength().Should().Be(2);
    }
}
