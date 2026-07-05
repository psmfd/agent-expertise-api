using System.Text.Json;
using ExpertiseApi.Services.Sync;

namespace ExpertiseApi.Tests.Unit;

public class SyncModelsTests
{
    [Fact]
    public void SyncBatchItem_SerializesCamelCase_OmittingNulls()
    {
        var items = new List<SyncBatchItem>
        {
            new()
            {
                Domain = "shared",
                Title = "t",
                Body = "b",
                EntryType = "Pattern",
                Severity = "Info",
                Source = "human",
                OriginAuthorPrincipal = "alice@spoke",
            },
        };

        var json = JsonSerializer.Serialize(items, SyncJsonContext.Default.ListSyncBatchItem);

        json.Should().Contain("\"entryType\":\"Pattern\"", "enums must travel as string names for the hub's JsonStringEnumConverter");
        json.Should().Contain("\"originAuthorPrincipal\":\"alice@spoke\"");
        json.Should().NotContain("tags", "null optionals are omitted");
        json.Should().NotContain("tenant", "the hub assigns the spoke's tenant from the token, never the payload (ADR-003/ADR-013)");
    }

    [Fact]
    public void SyncBatchResult_ParsesTheHubsBatchEntryResultShape()
    {
        // Mirror of the server's BatchEntryResult under its ConfigureHttpJsonOptions
        // (camelCase + JsonStringEnumConverter).
        const string body = """[{"index":0,"status":"Created","id":"11111111-1111-1111-1111-111111111111","error":null},{"index":1,"status":"Duplicate","id":"22222222-2222-2222-2222-222222222222","error":null},{"index":2,"status":"Rejected","id":null,"error":"nope"}]""";

        var verdicts = JsonSerializer.Deserialize(body, SyncJsonContext.Default.ListSyncBatchResult)!;

        verdicts.Should().HaveCount(3);
        verdicts[0].Status.Should().Be("Created");
        verdicts[1].Id.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        verdicts[2].Status.Should().Be("Rejected");
        verdicts[2].Error.Should().Be("nope");
    }

    [Fact]
    public void HubTokenResponse_ParsesSnakeCase()
    {
        const string body = """{"access_token":"tok-abc","token_type":"Bearer","expires_in":300}""";

        var token = JsonSerializer.Deserialize(body, SyncJsonContext.Default.HubTokenResponse)!;

        token.AccessToken.Should().Be("tok-abc");
        token.ExpiresIn.Should().Be(300);
    }
}
