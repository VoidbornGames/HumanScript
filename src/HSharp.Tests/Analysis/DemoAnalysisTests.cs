using HSharp.Analysis;
using HSharp.Syntax;

public class DemoAnalysisTests
{
    [Fact]
    public void AnalyzesDemoOop()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rt", "demo-oop.hs"));
        string text = File.ReadAllText(path);
        var doc = Workspace.Analyze(path, text);
        Assert.False(doc.Diags.Any(), string.Join("; ", doc.Diags.Select(d => d.Message)));
    }
}

