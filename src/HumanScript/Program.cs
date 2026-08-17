using System.Diagnostics;
using HumanScript;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hsc <source.hs> [-o output] [-- <extra clang args>]");
    return 1;
}

bool isWindows = OperatingSystem.IsWindows();

string sourcePath = args[0];
string outputPath = Path.GetFileNameWithoutExtension(sourcePath) + (isWindows ? ".exe" : "");
var extraClangArgs = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) { outputPath = args[++i]; continue; }
    extraClangArgs.Add(args[i]);
}

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"error: file not found: {sourcePath}");
    return 1;
}

string source = File.ReadAllText(sourcePath);

string objPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".o");

try
{
    var tokens = new Lexer(source).Tokenize();
    var program = new Parser(tokens).Parse();

    new CodeGen().Generate(program, objPath);

    var psi = new ProcessStartInfo
    {
        FileName = isWindows ? "clang" : "clang-18",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };

    psi.ArgumentList.Add(objPath);
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add(outputPath);

    if (!isWindows)
    {
        psi.ArgumentList.Add("-fuse-ld=lld-18");
        psi.ArgumentList.Add("-static");
    }
    else
    {
        psi.ArgumentList.Add("-llegacy_stdio_definitions");
    }
    foreach (var a in extraClangArgs) psi.ArgumentList.Add(a);

    using var proc = Process.Start(psi)!;

    Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

    proc.WaitForExit();

    string stdout = stdoutTask.Result;
    string stderr = stderrTask.Result;

    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine("error: linking failed:");
        if (!string.IsNullOrWhiteSpace(stdout)) Console.Error.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        Console.Error.WriteLine($"(object file kept at: {objPath})");
        return 1;
    }

    File.Delete(objPath);
    Console.WriteLine($"Compiled '{sourcePath}' -> '{outputPath}'");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}