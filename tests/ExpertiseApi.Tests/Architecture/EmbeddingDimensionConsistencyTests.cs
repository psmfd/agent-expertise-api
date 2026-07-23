using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using ExpertiseApi.Models;
using ExpertiseApi.Services;

namespace ExpertiseApi.Tests.Architecture;

/// <summary>
/// Ties the two declarations of the vector dimension together (review finding,
/// 2026-07-23): <c>EmbeddingModelInfo.Dimensions</c> is documented as the single
/// source of truth, but the pgvector column width lives in a data-annotation
/// string on <c>ExpertiseEntry.Embedding</c> that nothing else enforces. A model
/// swap that bumps the constant without the attribute (or vice versa) compiles
/// cleanly and only fails at INSERT time in production — this test makes that
/// drift a CI failure instead.
/// </summary>
public class EmbeddingDimensionConsistencyTests
{
    [Fact]
    public void EmbeddingColumnType_MatchesEmbeddingModelInfoDimensions()
    {
        var column = typeof(ExpertiseEntry)
            .GetProperty(nameof(ExpertiseEntry.Embedding), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<ColumnAttribute>();

        column.Should().NotBeNull("ExpertiseEntry.Embedding must declare its pgvector column type");
        column!.TypeName.Should().Be($"vector({EmbeddingModelInfo.Dimensions})",
            "the column attribute and EmbeddingModelInfo.Dimensions must move together — " +
            "a mismatch surfaces as a runtime INSERT failure, not a compile error");
    }
}
