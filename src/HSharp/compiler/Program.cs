using System.Diagnostics;
using System.Text.RegularExpressions;
using HSharp;
using HSharp.Checking;
using HSharp.CodeGen;
using HSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hsc <source.hs> [-o output] [-platform win64|linux64|osx64|linux-arm64|osx-arm64] [-- <extra clang args>]");
    return 1;
}

bool isWindows = OperatingSystem.IsWindows();

string sourcePath = args[0];
var extraClangArgs = new List<string>();
var importDirs = new List<string>();
string? platform = null;
string? givenOutput = null;
bool checkOnly = false;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) { givenOutput = args[++i]; continue; }
    if (args[i] == "-platform" && i + 1 < args.Length) { platform = args[++i]; continue; }
    if (args[i] == "-I" && i + 1 < args.Length) { importDirs.Add(args[++i]); continue; }
    if (args[i] == "--check") { checkOnly = true; continue; }
    extraClangArgs.Add(args[i]);
}

Imports.ConfigureSearchPaths(importDirs);

var safeArg = new Regex("^[A-Za-z0-9_+=.,:\\/@%\" -]+$");
foreach (var a in extraClangArgs)
{
    if (!safeArg.IsMatch(a))
    {
        Console.Error.WriteLine($"error: unsupported linker argument '{a}'");
        return 1;
    }
}

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

try { outputPath = Path.GetFullPath(outputPath); }
catch (Exception)
{
    Console.Error.WriteLine($"error: invalid output path '{outputPath}'");
    return 1;
}

if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"error: file not found: {sourcePath}");
    return 1;
}

string objPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".o");

try
{
    var program = Imports.Load(sourcePath);
    new Checker().Check(program);

    if (checkOnly)
    {
        Console.Out.WriteLine("OK");
        return 0;
    }

    new CodeGen().Generate(program, objPath, triple);

    var rtObj = Linker.RuntimeObject(triple);
    if (rtObj == null)
    {
        Console.Error.WriteLine("error: failed to build the runtime (is rt.c next to the compiler?)");
        return 1;
    }

    if (!Linker.Link(objPath, outputPath, triple, extraClangArgs))
    {
        Console.Error.WriteLine("error: linking failed");
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
catch (Exception ex) when (Environment.GetEnvironmentVariable("HS_DEBUG") == "1")
{
    Console.Error.WriteLine(ex);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

