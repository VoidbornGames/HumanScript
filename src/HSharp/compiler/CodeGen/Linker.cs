using System.Diagnostics;

namespace HSharp.CodeGen;

public static class Linker
{
    public static string Clang => OperatingSystem.IsWindows() ? "clang" : "clang-18";

    public static bool ClangAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = Clang, ArgumentList = { "--version" }, RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? RuntimeObject(string triple)
    {
        string rtSrc = Path.Combine(AppContext.BaseDirectory, "rt.c");
        if (!File.Exists(rtSrc))
        {

            var repo = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rt", "rt.c");
            rtSrc = Path.GetFullPath(repo);
            if (!File.Exists(rtSrc)) return null;
        }

        string rtDir = Path.Combine(Path.GetTempPath(), "hsharp-rt");
        string stamp = File.GetLastWriteTimeUtc(rtSrc).Ticks.ToString();
        string rtObj = Path.Combine(rtDir, triple.Replace('/', '_') + "-" + stamp + ".o");

        if (File.Exists(rtObj)) return rtObj;

        Directory.CreateDirectory(rtDir);
        var psi = new ProcessStartInfo
        {
            FileName = Clang,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(rtSrc);
        psi.ArgumentList.Add("-target");
        psi.ArgumentList.Add(triple);
        psi.ArgumentList.Add("-D_WINSOCK_DEPRECATED_NO_WARNINGS");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(rtObj);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0 ? rtObj : null;
    }

    public static string DefaultTriple() =>
        LLVM.PtrToStringAndFree(LLVM.LLVMGetDefaultTargetTriple()).Trim();

    public static bool BuildExecutable(string dir) =>
        Link(Path.Combine(dir, "prog.o"), Path.Combine(dir, "prog.exe"), DefaultTriple());

    public static (int exitCode, string stdout) RunCaptured(string dir, int timeoutMs)
    {
        var saved = Environment.CurrentDirectory;
        Environment.CurrentDirectory = dir;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "prog.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                proc.Kill();
                proc.WaitForExit();
                return (-1, stdout);
            }
            return (proc.ExitCode, stdout);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    public static bool Link(string objPath, string outputPath, string triple, IEnumerable<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Clang,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-target");
        psi.ArgumentList.Add(triple);
        var rtObj = RuntimeObject(triple);
        if (rtObj != null) psi.ArgumentList.Add(rtObj);
        psi.ArgumentList.Add(objPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);

        if (triple.Contains("windows"))
        {
            psi.ArgumentList.Add("-llegacy_stdio_definitions");
            psi.ArgumentList.Add("-lws2_32");
            psi.ArgumentList.Add("-lwinhttp");
        }
        else if (!triple.Contains("darwin"))
        {
            psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "-fuse-ld=lld" : "-fuse-ld=lld-18");
            psi.ArgumentList.Add("-static");
        }
        foreach (var a in extraArgs ?? Array.Empty<string>()) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
}

