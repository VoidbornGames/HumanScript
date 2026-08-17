using System.Diagnostics;
using HumanScript;

namespace HumanScript.Tests;

public class ParserAndCodegenTests
{
    [Fact]
    public void ParsesListIndexExpression()
    {
        var lexer = new Lexer(
            """
            remember fruits is a list
            add "apple" to fruits
            add "banana" to fruits
            remember first is fruits[0]
            """);

        var parser = new Parser(lexer.Tokenize());
        var program = parser.Parse();

        Assert.NotEmpty(program.Statements);
        var define = Assert.IsType<DefineStmt>(program.Statements[3]);
        Assert.IsType<ListIndexExpr>(define.Value);
    }

    [Fact]
    public void ParsesListIndexInAssignment()
    {
        var lexer = new Lexer("remember fruits is a list\nadd \"apple\" to fruits\nremember first is fruits[0]");
        var parser = new Parser(lexer.Tokenize());
        var program = parser.Parse();

        Assert.IsType<DefineStmt>(program.Statements[2]);
    }

    [Fact]
    public void ParsesIndexedAssignment()
    {
        var lexer = new Lexer("set fruits[0] to \"pear\"");
        var parser = new Parser(lexer.Tokenize());
        var program = parser.Parse();

        var setStmt = Assert.IsType<SetStmt>(program.Statements[0]);
        var indexExpr = Assert.IsType<ListIndexExpr>(setStmt.Target);
        var targetName = Assert.IsType<IdentExpr>(indexExpr.Target);
        Assert.Equal("fruits", targetName.Name);
    }

    [Fact]
    public void ParsesNumberOfAsListLengthExpression()
    {
        var lexer = new Lexer("remember count is number of fruits");
        var parser = new Parser(lexer.Tokenize());
        var program = parser.Parse();

        var define = Assert.IsType<DefineStmt>(program.Statements[0]);
        Assert.IsType<ListLengthExpr>(define.Value);
    }

    [Fact]
    public void RemoveFromListUpdatesSizeAndContents()
    {
        var output = CompileAndRun(
            """
            remember fruits is a list
            add "apple" to fruits
            add "banana" to fruits
            remove "apple" from fruits
            say number of fruits
            say fruits[0]
            """);

        Assert.Contains("1", output);
        Assert.Contains("banana", output);
    }

    [Fact]
    public void TryCatchBranchesImmediatelyAfterError()
    {
        var output = CompileAndRun(
            """
            try
                read "missing-file-for-test.txt" into content
                say "should not run"
            catch
                say "caught"
            end
            """);

        Assert.Equal("caught" + Environment.NewLine, output);
    }

    [Fact]
    public void ExecutesCoreExpressionsAndConditionals()
    {
        var output = CompileAndRun(
            """
            remember x is 10
            remember y is 3
            increase x by 2
            decrease y by 1
            remember total is x + y
            say total
            if x is greater than y then
                say "gt"
            otherwise
                say "not-gt"
            end
            remember flag is not false
            say flag
            """);

        Assert.Contains("14", output);
        Assert.Contains("gt", output);
        Assert.Contains("yes", output);
    }

    [Fact]
    public void ExecutesStringAndListFeatures()
    {
        var output = CompileAndRun("""
            remember greeting is "Hello, World!"
                        say length of greeting
            if greeting contains "World" then
                say "contains"
            end
            if greeting starts with "Hello" then
                say "starts"
            end
            if greeting ends with "World!" then
                say "ends"
            end
            remember fruits is a list
            add "apple" to fruits
            add "banana" to fruits
            say number of fruits
            remember first is fruits[0]
            say first
            set fruits[0] to "pear"
            say fruits[0]
            remove "pear" from fruits
            say number of fruits
            clear fruits
            say length of fruits
            """);

        Assert.Contains("13", output);
        Assert.Contains("contains", output);
        Assert.Contains("starts", output);
        Assert.Contains("ends", output);
        Assert.Contains("pear", output);
        Assert.Contains("0", output);
    }

    [Fact]
    public void ExecutesLoopsFunctionsAndNestedConditionals()
    {
        var output = CompileAndRun(
            """
            to greet name
                say name
            end
            greet "Alice"

            remember i is 0
            repeat 2 times
                increase i by 1
                say i
            end

            remember j is 0
            while j is less than 2
                say "loop"
                increase j by 1
            end

            remember fruits is a list
            add "apple" to fruits
            for every fruit in fruits
                say fruit
            end

            if 1 is less than 2 then
                if 2 is greater than 1 then
                    say "nested"
                end
            end
            """);

        Assert.Contains("Alice", output);
        Assert.Contains("1", output);
        Assert.Contains("loop", output);
        Assert.Contains("apple", output);
        Assert.Contains("nested", output);
    }

    [Fact]
    public void ExecutesFileIOAndTryCatch()
    {
        var output = CompileAndRun(
            """
            write "hello" into "testfile.txt"
            read "testfile.txt" into content
            say content
            if exists "testfile.txt" then
                delete "testfile.txt"
            end
            try
                read "missing-file.txt" into data
                say "should not run"
            catch
                say "caught"
            end
            """);

        Assert.Contains("hello", output);
        Assert.Contains("caught", output);
    }

    private static string CompileAndRun(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "humanscript-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourcePath = Path.Combine(tempDir, "program.hs");
            var outputPath = Path.Combine(tempDir, "program" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            File.WriteAllText(sourcePath, source);

            var compilerPath = typeof(Lexer).Assembly.Location;
            var compilePsi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            compilePsi.ArgumentList.Add(compilerPath);
            compilePsi.ArgumentList.Add(sourcePath);
            compilePsi.ArgumentList.Add("-o");
            compilePsi.ArgumentList.Add(outputPath);

            using var compileProc = Process.Start(compilePsi)!;
            var compileStdout = compileProc.StandardOutput.ReadToEnd();
            var compileStderr = compileProc.StandardError.ReadToEnd();
            compileProc.WaitForExit();
            Assert.True(compileProc.ExitCode == 0, $"compiler failed: {compileStdout}{compileStderr}");

            var runPsi = new ProcessStartInfo
            {
                FileName = outputPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var runProc = Process.Start(runPsi)!;
            var runStdout = runProc.StandardOutput.ReadToEnd();
            var runStderr = runProc.StandardError.ReadToEnd();
            runProc.WaitForExit();
            Assert.True(runProc.ExitCode == 0, $"program failed: {runStdout}{runStderr}");
            return runStdout;
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}