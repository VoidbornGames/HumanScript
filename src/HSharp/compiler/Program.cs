using System.Diagnostics;
using HSharp;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hsc <source.hs> [-o output] [-platform win64|linux64|osx64|linux-arm64|osx-arm64] [-- <extra clang args>]");
    return 1;
}

bool isWindows = OperatingSystem.IsWindows();

string sourcePath = args[0];
var extraClangArgs = new List<string>();
string? platform = null;
string? givenOutput = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) { givenOutput = args[++i]; continue; }
    if (args[i] == "-platform" && i + 1 < args.Length) { platform = args[++i]; continue; }
    extraClangArgs.Add(args[i]);
}

// no -platform means build for whatever we're running on
string? triple = platform switch
{
    null => LLVM.PtrToStringAndFree(LLVM.LLVMGetDefaultTargetTriple()),
    "win64" => "x86_64-pc-windows-msvc",
    "linux64" => "x86_64-unknown-linux-gnu",
    "osx64" => "x86_64-apple-darwin",
    "linux-arm64" => "aarch64-unknown-linux-gnu",
    "osx-arm64" => "aarch64-apple-darwin",
    _ => null
};

if (triple == null)
{
    Console.Error.WriteLine($"error: unknown platform '{platform}' (win64, linux64, osx64, linux-arm64, osx-arm64)");
    return 1;
}

bool exeExt = triple.Contains("windows");
string outputPath = givenOutput ?? (Path.GetFileNameWithoutExtension(sourcePath) + (exeExt ? ".exe" : ""));

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
    new Checker().Check(program);

    new CodeGen().Generate(program, objPath, triple);

    var psi = new ProcessStartInfo
    {
        FileName = isWindows ? "clang" : "clang-18",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };

    psi.ArgumentList.Add("-target");
    psi.ArgumentList.Add(triple);
    psi.ArgumentList.Add(objPath);
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add(outputPath);

    // flags follow the target, not the machine we're sitting on
    if (exeExt)
    {
        psi.ArgumentList.Add("-llegacy_stdio_definitions");
    }
    else if (!triple.Contains("darwin"))
    {
        psi.ArgumentList.Add(isWindows ? "-fuse-ld=lld" : "-fuse-ld=lld-18");
        psi.ArgumentList.Add("-static");
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
catch (SourceError ex)
{
    Console.Error.WriteLine($"{sourcePath}({ex.Line},{ex.Col}): error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}