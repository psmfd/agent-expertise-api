using ExpertiseApi.Models;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// Frozen enum-name guard (#355). <see cref="ReviewState"/>, <see cref="Visibility"/>,
/// <see cref="EntryType"/>, <see cref="Severity"/>, <see cref="AuditAction"/> and
/// <see cref="ActorClass"/> are ALL stored as strings (EF <c>HasConversion&lt;string&gt;()</c>)
/// AND serialized as strings (<c>JsonStringEnumConverter</c>), so a member NAME is both the
/// persisted DB value and the wire contract. Renaming or removing a member silently breaks
/// stored-row parsing and the JSON contract — the `oasdiff` gate shows the schema change but
/// a reviewer can approve it without registering the dual DB/wire drift.
/// <para>
/// These tests fail loudly on any name change so it becomes a DELIBERATE, migration-aware
/// edit: update the frozen set here AND account for existing persisted rows. Adding a member
/// is safe but still forces a conscious update of the list below.
/// </para>
/// </summary>
public class EnumContractTests
{
    [Fact]
    public void ReviewState_MemberNamesAreFrozen() =>
        Enum.GetNames<ReviewState>().Should().BeEquivalentTo("Draft", "Approved", "Rejected");

    [Fact]
    public void Visibility_MemberNamesAreFrozen() =>
        Enum.GetNames<Visibility>().Should().BeEquivalentTo("Private", "Shared");

    [Fact]
    public void EntryType_MemberNamesAreFrozen() =>
        Enum.GetNames<EntryType>().Should().BeEquivalentTo("IssueFix", "Caveat", "Requirement", "Pattern");

    [Fact]
    public void Severity_MemberNamesAreFrozen() =>
        Enum.GetNames<Severity>().Should().BeEquivalentTo("Info", "Warning", "Critical");

    [Fact]
    public void AuditAction_MemberNamesAreFrozen() =>
        Enum.GetNames<AuditAction>().Should().BeEquivalentTo(
            "Created", "Updated", "Approved", "Rejected", "Deleted", "RestoreQuarantined");

    [Fact]
    public void ActorClass_MemberNamesAreFrozen() =>
        Enum.GetNames<ActorClass>().Should().BeEquivalentTo("Human", "Agent", "Service");
}
