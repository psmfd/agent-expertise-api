namespace ExpertiseApi.Tests.Evaluation;

/// <summary>
/// Marks a retrieval-evaluation test that runs the REAL pinned ONNX model (ADR-017)
/// against a real PostgreSQL container — deliberately excluded from normal
/// <c>dotnet test</c> runs (and CI) because it measures retrieval quality, not
/// correctness, and its metrics are for human comparison across revisions (#425).
///
/// Runs only when BOTH hold:
/// <list type="bullet">
///   <item><c>EXPERTISE_EVAL=1</c> is set in the environment, and</item>
///   <item>the ONNX model files exist at <c>src/ExpertiseApi/models/</c>
///     (fetch via <c>scripts/download-models.sh</c>).</item>
/// </list>
/// </summary>
public sealed class EvalFactAttribute : FactAttribute
{
    public EvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EXPERTISE_EVAL") != "1")
        {
            Skip = "Retrieval evaluation is opt-in: set EXPERTISE_EVAL=1 to run.";
            return;
        }

        if (ModelFiles.ModelPath is null)
        {
            Skip = "ONNX model files not found under src/ExpertiseApi/models/ — run scripts/download-models.sh first.";
        }
    }
}

/// <summary>
/// Locates the repo-root-relative ONNX model files from the test bin directory.
/// </summary>
internal static class ModelFiles
{
    /// <summary>Absolute path to model.onnx, or null when not present.</summary>
    public static string? ModelPath { get; }

    /// <summary>Absolute path to vocab.txt, or null when not present.</summary>
    public static string? VocabPath { get; }

    static ModelFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "ExpertiseApi.slnx")))
            dir = dir.Parent;

        if (dir is null)
            return;

        var model = Path.Join(dir.FullName, "src", "ExpertiseApi", "models", "model.onnx");
        var vocab = Path.Join(dir.FullName, "src", "ExpertiseApi", "models", "vocab.txt");
        if (File.Exists(model) && File.Exists(vocab))
        {
            ModelPath = model;
            VocabPath = vocab;
        }
    }
}
