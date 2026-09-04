using HSharp.Checking;
using HSharp.CodeGen;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;
using Compiler = HSharp.CodeGen.CodeGen;
using HSharp;

namespace HSharp.Tests;

public class EndToEndTests
{
    private static readonly bool RunTests = Linker.ClangAvailable();

    private static string BuildAndRun(string source)
    {
        Assert.True(RunTests, "clang not available, e2e tests skipped");

        string dir = CompileTo(source);
        try
        {
            return RunBuilt(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CompileTo(string source)
    {
        var program = Imports.Load(WriteSource(source));
        new Checker().Check(program);

        string dir = Path.Combine(Path.GetTempPath(), "hs-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        new Compiler().Generate(program, Path.Combine(dir, "prog.o"), Linker.DefaultTriple());
        return dir;
    }

    private static string RunBuilt(string dir)
    {
        Assert.True(Linker.BuildExecutable(dir), "linking failed");

        var (exitCode, stdout) = Linker.RunCaptured(dir, 20000);
        Assert.True(exitCode == 0, $"exit {exitCode}\nstdout:\n{stdout}");
        return stdout;
    }

    private static string WriteSource(string source)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-e2e-src");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".hs");
        File.WriteAllText(path, source);
        return path;
    }

    private static void AssertLines(string stdout, params string[] expected)
    {
        var lines = stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length == expected.Length,
            $"expected {expected.Length} lines, got {lines.Length}:\n{stdout}");
        for (int i = 0; i < expected.Length; i++)
            Assert.True(lines[i] == expected[i],
                $"line {i + 1}: expected '{expected[i]}', got '{lines[i]}'\nfull output:\n{stdout}");
    }

    [Fact]
    public void NullableComparisonsAndDefaults()
    {
        var outp = BuildAndRun("""
            int? maybe = 8;
            print(maybe == null);
            print(maybe != null);
            int? empty = null;
            print(empty == null);
            print(empty ?? -1);
            print(maybe ?? -1);
            float? f = 2.5;
            print(f == null);
            print(f ?? 0.5);
            string? s = "x";
            print(s == null);
            print(s ?? "no");
            print(mem());
            """);
        AssertLines(outp,
            "false", "true", "true", "-1", "8",
            "false", "2.5", "false", "x", "0");
    }

    [Fact]
    public void NullableStructMemberAccess()
    {
        var outp = BuildAndRun("""
            struct Point { int X; int Y; }
            Point? p = Point { X: 3, Y: 4 };
            print(p?.X ?? -1);
            Point? q = null;
            print(q?.X ?? -1);
            if (p != null)
            {
                print(p.X + p.Y);
            }
            print(mem());
            """);
        AssertLines(outp, "3", "-1", "7", "0");
    }

    [Fact]
    public void OwnershipMovesAndDrops()
    {
        var outp = BuildAndRun("""
            var a = "first";
            var b = a;
            print(b);
            var l = list<string> { "x" };
            l.Add("y");
            print(l.Count);
            print(mem());
            """);
        AssertLines(outp, "first", "2", "0");
    }

    [Fact]
    public void ClassesStructsAndGenerics()
    {
        var outp = BuildAndRun("""
            enum Mood { Grumpy, Happy = 5 }

            struct Point
            {
                int X;
                int Y;
                int Sum() { return X + Y; }
            }

            class User
            {
                string Name;
                Mood Mood;

                public string Describe() { return Name + "=" + string(int(Mood)); }
                public T Pick<T>(T a, T b) { if (Mood == Mood.Happy) { return a; } return b; }
            }

            var p = Point { X: 10, Y: 5 };
            print(p.Sum());

            var u = User { Name: "ada", Mood: Mood.Happy };
            print(u.Describe());
            print(u.Pick<string>("yes", "no"));
            print(u.Pick(1, 2));
            print(mem());
            """);
        AssertLines(outp, "15", "ada=5", "yes", "1", "0");
    }

    [Fact]
    public void CoalesceWithCallDoesNotCorrupt()
    {
        var outp = BuildAndRun("""
            class Tracker
            {
                string Label;
                public string Report() { return Label + "!"; }
            }

            var t = Tracker { Label: "hit" };
            string? r = null;
            print(r ?? t.Report());
            print(mem());
            """);
        AssertLines(outp, "hit!", "0");
    }

    [Fact]
    public void ConversionsAndEnums()
    {
        var outp = BuildAndRun("""
            var x = 10;
            print(float(x) / 4.0);
            print(int("42") + int(2.9));
            print(string(7));
            enum Color { Red, Green = 5, Blue }
            print(int(Color.Blue));
            print(mem());
            """);
        AssertLines(outp, "2.5", "44", "7", "6", "0");
    }

    [Fact]
    public void TcpEchoRoundTrip()
    {
        var outp = BuildAndRun("""
            var ln = Tcp.Listen(18801);
            var srv = Task.Run(() =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var c = ln.Accept();
                    c.Send("echo:" + c.Recv());
                    c.Close();
                }
                return "ok";
            });

            for (var i = 0; i < 3; i++)
            {
                var c = Tcp.Connect("127.0.0.1", 18801);
                c.Send("ping" + string(i) + "\n");
                print(c.Recv());
                c.Close();
            }
            var done = await srv;
            print(done);
            print(mem());
            """);
        AssertLines(outp, "echo:ping0", "echo:ping1", "echo:ping2", "ok", "0");
    }

    [Fact]
    public void HttpServerRawConvertAndRespond()
    {
        var outp = BuildAndRun("""
            var ln = Http.ListenRaw(18802);
            var srv = Task.Run(() =>
            {
                var raw = ln.Accept();
                var req = raw.ToHttpPacket();
                var host = req.Header("Host");
                req.Respond(200, "saw " + req.Method() + " " + req.Path() + " " + (host ?? "?") + "\n");
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18802);
            c.Send("POST /login HTTP/1.0\r\nHost: unit.test\r\nContent-Length: 6\r\n\r\nhello\n");
            print(c.Recv());
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        AssertLines(outp, "HTTP/1.0 200 OK", "ok", "0");
    }

    [Fact]
    public void RespondSendsHtmlContentType()
    {
        var outp = BuildAndRun("""
            var ln = Http.ListenRaw(18819);
            var srv = Task.Run(() =>
            {
                var raw = ln.Accept();
                var req = raw.ToHttpPacket();
                req.Respond(404, "<small>Not Found!</small>");
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18819);
            c.Send("GET /missing HTTP/1.0\r\nHost: unit.test\r\n\r\n");
            print(c.Recv());
            print(c.Recv());
            print(c.Recv());
            print(c.Recv());
            print(c.Recv());
            print(c.Recv());
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        Assert.Contains("HTTP/1.0 404 Not Found", outp);
        Assert.Contains("Content-Type: text/html; charset=utf-8", outp);
        Assert.Contains("<small>Not Found!</small>", outp);
    }

    [Fact]
    public void RespondHonorsCustomHeaders()
    {
        var outp = BuildAndRun("""
            var ln = Http.ListenRaw(18820);
            var srv = Task.Run(() =>
            {
                var raw = ln.Accept();
                var req = raw.ToHttpPacket();
                req.Header("Content-Type", "application/json");
                req.Header("X-Flag", "on");
                req.Respond(200, "{\"a\":1}");
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18820);
            c.Send("GET /api HTTP/1.0\r\nHost: unit.test\r\n\r\n");
            for (var i = 0; i < 8; i++)
            {
                print(c.Recv());
            }
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        Assert.DoesNotContain("text/html", outp);
        Assert.Contains("Content-Type: application/json", outp);
        Assert.Contains("X-Flag: on", outp);
        Assert.Contains("{\"a\":1}", outp);
    }

    [Fact]
    public void CookiesSetGetWithOptions()
    {
        var outp = BuildAndRun("""
            var ln = Http.Listen(18821);
            var srv = Task.Run(() =>
            {
                var req = ln.Accept();
                var who = req.Cookies.Get("session") ?? "anon";
                var opts = new CookieOptions { Secure: true, HttpOnly: true, SameSite: SameSite.Lax, MaxAge: 3600 };
                req.Cookies.Set("seen", "yes", opts);
                req.Cookies.Set("temp", "1");
                req.Respond(200, "who=" + who);
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18821);
            c.Send("GET / HTTP/1.0\r\nHost: unit.test\r\nCookie: session=abc123\r\n\r\n");
            for (var i = 0; i < 11; i++)
            {
                print(c.Recv());
            }
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        Assert.Contains("who=abc123", outp);
        Assert.Contains("Set-Cookie: seen=yes; Max-Age=3600; Secure; HttpOnly; SameSite=Lax", outp);
        Assert.Contains("Set-Cookie: temp=1; Path=/; SameSite=Lax", outp);
        var tail = outp.Replace("\r", "").TrimEnd();
        Assert.EndsWith("ok\n0", tail);
    }

    [Fact]
    public void TaskRunAndAwait()
    {
        var outp = BuildAndRun("""
            var t = Task.Run(() =>
            {
                return "worked";
            });
            print(await t);
            print(mem());
            """);
        AssertLines(outp, "worked", "0");
    }

    [Fact]
    public void ClockMsAdvances()
    {
        var outp = BuildAndRun("""
            var start = clock_ms();
            var total = 0;
            for (var i = 0; i < 1000000; i++)
            {
                total += 1;
            }
            var elapsed = clock_ms() - start;
            print(total == 1000000);
            print(elapsed >= 0);
            print(mem());
            """);
        AssertLines(outp, "true", "true", "0");
    }

    [Fact]
    public void StringToolkitSplitJoinReplaceTrimCase()
    {
        var outp = BuildAndRun("""
            var parts = "a,b,c".Split(",");
            print(parts.Count);
            print(parts[0] + "|" + parts[2]);
            print(parts.Join("+"));
            print("a-b-a".Replace("a", "z"));
            print("[" + "  pad  ".Trim() + "]");
            print("mIxEd".ToUpper());
            print("MiXeD".ToLower());
            print(mem());
            """);
        AssertLines(outp, "3", "a|c", "a+b+c", "z-b-z", "[pad]", "MIXED", "mixed", "0");
    }

    [Fact]
    public void EnvReturnsNullForMissingName()
    {
        var outp = BuildAndRun("""
            string? v = env("HSHARP_DEFINITELY_NOT_SET_9q7");
            print(v == null);
            print(v ?? "absent");
            print(mem());
            """);
        AssertLines(outp, "true", "absent", "0");
    }

    [Fact]
    public void RecvTimeoutReturnsNullWhenQuiet()
    {
        var outp = BuildAndRun("""
            var ln = Tcp.Listen(18803);
            var srv = Task.Run(() =>
            {
                var c = ln.Accept();
                string? line = c.RecvTimeout(150);
                print(line == null);
                c.Close();
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18803);
            var done = await srv;
            print(done);
            c.Close();
            print(mem());
            """);
        AssertLines(outp, "true", "ok", "0");
    }

    [Fact]
    public void AcceptTimeoutReturnsNullWhenQuiet()
    {
        var outp = BuildAndRun("""
            var ln = Http.ListenRaw(18804);
            var t0 = clock_ms();
            var none = ln.AcceptTimeout(120);
            var elapsed = clock_ms() - t0;
            print(none == null);
            print(elapsed >= 100);
            print(mem());
            """);
        AssertLines(outp, "true", "true", "0");
    }

    [Fact]
    public void ForwardRelaysToUpstream()
    {
        var outp = BuildAndRun("""
            var up = Http.Listen(18805);
            var srv = Task.Run(() =>
            {
                var req = up.Accept();
                req.Respond(200, "from upstream\n");
                return "up done";
            });

            var gate = Task.Run(() =>
            {
                var ln = Http.ListenRaw(18806);
                var raw = ln.Accept();
                raw.Forward("127.0.0.1", 18805);
                return "gate done";
            });

            var c = Tcp.Connect("127.0.0.1", 18806);
            c.Send("GET /x HTTP/1.0\r\nHost: g\r\n\r\n");
            print(c.Recv());
            c.Close();

            var a = await gate;
            var b = await srv;
            print(a);
            print(b);
            print(mem());
            """);
        AssertLines(outp, "HTTP/1.0 200 OK", "gate done", "up done", "0");
    }

    [Fact]
    public void ArgsListProgramArguments()
    {
        var outp = BuildAndRun("""
            print(args().Count);
            print(args()[0]);
            print(args().Join(","));
            print(mem());
            """);
        var lines = outp.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length == 4, $"expected 4 lines:\n{outp}");
        Assert.Equal("1", lines[0]);
        Assert.EndsWith("prog.exe", lines[1].Replace("\\", "/"));
        Assert.True(lines[2].EndsWith("prog.exe"), $"unexpected args line: {lines[2]}");
        Assert.Equal("0", lines[3]);
    }

    [Fact]
    public void TernaryAndExiting()
    {
        var outp = BuildAndRun("""
            var pick = 3;
            print(pick > 0 ? "positive" : "other");
            print((pick > 0 ? 7 : 9) + 1);
            int? n = null;
            print((pick > 0 ? 100 : 200) + (n ?? 1));
            print(exiting());
            print(mem());
            """);
        AssertLines(outp, "positive", "8", "101", "false", "0");
    }

    [Fact]
    public void BuffersRoundTripOverSockets()
    {
        var outp = BuildAndRun("""
            var b = buffer(16);
            b[0] = 200;
            b[1] = 7;
            b[2] = 0;
            print(len(b));
            print(b[0] + b[1] + b[2]);

            var fromStr = buffer("xyz");
            print(len(fromStr));
            print(string(fromStr));

            var ln = Tcp.Listen(18811);
            var srv = Task.Run(() =>
            {
                var c = ln.Accept();
                var rb = buffer(64);
                var got = c.RecvBytes(rb);
                print("got " + string(got) + " bytes");
                rb[0] = 89;
                c.SendBytes(rb);
                c.Close();
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18811);
            c.SendBytes(b);
            var resp = buffer(64);
            c.RecvBytes(resp);
            print(resp[0]);
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        AssertLines(outp, "16", "207", "3", "xyz", "got 16 bytes", "89", "ok", "0");
    }

    [Fact]
    public void RecvAllReadsToEof()
    {
        var outp = BuildAndRun("""
            var ln = Tcp.Listen(18812);
            var srv = Task.Run(() =>
            {
                var c = ln.Accept();
                c.Send("part1-part2-part3");
                c.Close();
                return "ok";
            });

            var c = Tcp.Connect("127.0.0.1", 18812);
            var data = c.RecvAll(1024);
            print(len(data));
            print(string(data));
            c.Close();
            var done = await srv;
            print(done);
            print(mem());
            """);
        AssertLines(outp, "17", "part1-part2-part3", "ok", "0");
    }

    [Fact]
    public void StringBuilderAmortizes()
    {
        var outp = BuildAndRun("""
            var sb = StringBuilder.New();
            for (var i = 0; i < 100; i++)
            {
                sb.Add("ab");
            }
            var s = sb.ToString();
            print(len(s));
            sb.Add("!");
            var longer = sb.ToString();
            print(len(longer));
            print(longer.Contains("abab!"));
            print(mem());
            """);
        AssertLines(outp, "200", "201", "true", "0");
    }

    [Fact]
    public void TaskDelayAndWhenAll()
    {
        var outp = BuildAndRun("""
            var t0 = clock_ms();
            var d = Task.Delay(80);
            var ta = Task.Run(() => { return 10; });
            var tb = Task.Run(() => { return 20; });
            var results = Task.WhenAll(list<task<int>> { ta, tb });
            await d;
            var elapsed = clock_ms() - t0;
            print(results[0] + results[1]);
            print(elapsed >= 75);
            print(mem());
            """);
        AssertLines(outp, "30", "true", "0");
    }

    [Fact]
    public void ConcurrentOnAcceptServerWithSharedCounter()
    {
        var outp = BuildAndRun("""
            var counter = 0;
            var ln = Tcp.Listen(18813);
            ln.OnAccept((Client c) =>
            {
                var line = c.Recv();
                lock (counter)
                {
                    counter = counter + 1;
                }
                c.Send("hi " + line + "\n");
                c.Close();
            });

            var total = 0;
            for (var i = 0; i < 5; i++)
            {
                var c = Tcp.Connect("127.0.0.1", 18813);
                c.Send("c" + string(i) + "\n");
                var resp = c.Recv();
                c.Close();
                if (resp.Contains("hi c")) { total = total + 1; }
            }
            print("echoed: " + string(total));
            print("counter: " + string(counter));
            // wait for the last handler threads to drop their allocations
            var guard = 0;
            while (mem() > 0 && guard < 500)
            {
                guard = guard + 1;
                await Task.Delay(5);
            }
            print(mem());
            """);
        AssertLines(outp, "echoed: 5", "counter: 5", "0");
    }

    [Fact]
    public void ConcurrentHttpOnAcceptServer()
    {
        var outp = BuildAndRun("""
            var hits = 0;
            var ln = Http.ListenRaw(18814);
            ln.OnAccept((RawHttpPacket raw) =>
            {
                var req = raw.ToHttpPacket();
                lock (hits)
                {
                    hits = hits + 1;
                }
                req.Respond(200, "served " + req.Path() + "\n");
            });

            for (var i = 0; i < 3; i++)
            {
                var c = Tcp.Connect("127.0.0.1", 18814);
                c.Send("GET /p" + string(i) + " HTTP/1.0\r\nHost: t\r\n\r\n");
                var resp = c.RecvAll(4096);
                if (string(resp).Contains("served /p" + string(i))) { }
                c.Close();
            }
            print("hits: " + string(hits));

            // responses arrive before the handler threads finish freeing
            // their strings, so give teardown a bounded window to drain
            var start = clock_ms();
            while (mem() > 0 && clock_ms() - start < 10000)
            {
                await Task.Delay(10);
            }
            print(mem());
            """);
        AssertLines(outp, "hits: 3", "0");
    }

    [Fact]
    public void ImportSearchPathResolvesLibraries()
    {
        string libDir = Path.Combine(Path.GetTempPath(), "hs-e2e-libs");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "pathlib.hs"), "public string LibName() { return \"pathlib\"; }");

        try
        {
            Imports.ConfigureSearchPaths(new[] { libDir });
            var outp = BuildAndRun("""
                import pathlib.hs;

                print(LibName());
                print(mem());
                """);
            AssertLines(outp, "pathlib", "0");
        }
        finally
        {
            Imports.ConfigureSearchPaths(Array.Empty<string>());
            Directory.Delete(libDir, recursive: true);
        }
    }

    [Fact]
    public void BareTaskStatementsAndDiscards()
    {
        var outp = BuildAndRun("""
            await Task.Delay(30);
            Task.Run(() => { print("bare ran"); });
            _ = Task.Run(() => { return "discarded"; });
            await Task.Delay(60);
            print(mem());
            """);
        AssertLines(outp, "bare ran", "0");
    }

    [Fact]
    public void UdpOnAcceptHandlesDatagrams()
    {
        var outp = BuildAndRun("""
            var got = 0;
            var udp = Udp.Listen(18815);
            udp.OnAccept((string msg) =>
            {
                print("dg: " + msg);
                lock (got)
                {
                    got = got + 1;
                }
            });

            var s = Udp.Open();
            s.SendTo("127.0.0.1", 18815, "one");
            s.SendTo("127.0.0.1", 18815, "two");
            var start = clock_ms();
            while ((got < 2 || mem() > 0) && clock_ms() - start < 10000)
            {
                await Task.Delay(10);
            }
            print("got: " + string(got));
            print(mem());
            """);
        var lines = outp.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length == 4, $"expected 4 lines:\n{outp}");
        Assert.Equal("got: 2", lines[2]);
        Assert.Equal("0", lines[3]);
    }

    [Fact]
    public void OnAcceptBodiesCallFunctions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-e2e-fnlib");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "fnlib.hs"),
                "public string Tag(string s)\n{\n    return \"[\" + s + \"]\";\n}\n");

            Imports.ConfigureSearchPaths(new[] { dir });
            var outp = BuildAndRun("""
                import fnlib.hs;

                string Local(string s)
                {
                    return "(" + s + ")";
                }

                var ln = Tcp.Listen(18816);
                ln.OnAccept((Client c) =>
                {
                    var line = c.Recv();
                    c.Send(Tag(line) + Local(line) + "\n");
                    c.Close();
                });

                var c = Tcp.Connect("127.0.0.1", 18816);
                c.Send("x\n");
                print(c.Recv());
                c.Close();
                var start = clock_ms();
                while (mem() > 0 && clock_ms() - start < 10000)
                {
                    await Task.Delay(10);
                }
                print(mem());
                """);
            AssertLines(outp, "[x](x)", "0");
        }
        finally
        {
            Imports.ConfigureSearchPaths(Array.Empty<string>());
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MapsInsertLookupRemove()
    {
        var outp = BuildAndRun("""
            var m = map<string, int> { "alpha": 1, "beta": 2 };
            print(m["alpha"] ?? -1);
            print(m["missing"] ?? -1);
            m["gamma"] = 3;
            print(m["gamma"] ?? -1);
            m["alpha"] = 10;
            print(m["alpha"] ?? -1);
            print(len(m));
            print(m.Contains("beta"));
            print(m.Contains("nope"));
            m.Remove("beta");
            print(m.Contains("beta"));
            print(m.Count);
            m.Clear();
            print(len(m));
            print(mem());
            """);
        AssertLines(outp, "1", "-1", "3", "10", "3", "true", "false", "false", "2", "0", "0");
    }

    [Fact]
    public void StringValueMapsDeepCopy()
    {
        var outp = BuildAndRun("""
            var key = "k";
            var m = map<string, string> { };
            m[key] = "v1";
            key = "other";
            print(key);
            print(m.Contains("k"));
            print(m["k"] ?? "none");
            print(m["other"] ?? "none");
            print(mem());
            """);
        AssertLines(outp, "other", "true", "v1", "none", "0");
    }

    [Fact]
    public void SharedMapAcrossHandlerThreads()
    {
        var outp = BuildAndRun("""
            var hits = map<string, int> { };
            var ln = Tcp.Listen(18817);
            ln.OnAccept((Client c) =>
            {
                var line = c.Recv();
                lock (hits)
                {
                    var cur = hits[line] ?? 0;
                    hits[line] = cur + 1;
                }
                c.Send("ok\n");
                c.Close();
            });

            for (var i = 0; i < 4; i++)
            {
                var c = Tcp.Connect("127.0.0.1", 18817);
                c.Send("route" + string(i % 2) + "\n");
                var r = c.Recv();
                c.Close();
            }
            await Task.Delay(150);
            print(hits["route0"] ?? -1);
            print(hits["route1"] ?? -1);
            print(len(hits));
            ln.Close();
            """);

        AssertLines(outp, "2", "2", "2");
    }

    [Fact]
    public void CatchReceivesErrorMessages()
    {
        var outp = BuildAndRun("""
            try
            {
                var x = 10 / 0;
            }
            catch (e1)
            {
                print(e1);
            }
            try
            {
                var l = list<string> { };
                print(l[3]);
            }
            catch (e2)
            {
                print(e2);
            }
            try
            {
                var b = buffer(4);
                print(b[8]);
            }
            catch
            {
                print("buffer caught");
            }
            print(mem());
            """);

        AssertLines(outp, "division by zero", "list index out of bounds", "0", "buffer caught", "0");
    }

    [Fact]
    public void ListenerCloseStopsTheAcceptLoop()
    {
        var outp = BuildAndRun("""
            var ln = Tcp.Listen(18818);
            ln.OnAccept((Client c) =>
            {
                c.Close();
            });
            ln.Close();
            await Task.Delay(50);
            print("clean exit");
            print(exiting());
            print(mem());
            """);
        AssertLines(outp, "clean exit", "false", "0");
    }

    [Fact]
    public void ListOperationsContainSortReverseIndexOf()
    {
        var outp = BuildAndRun("""
            var names = list<string> { };
            names.Add("zoe");
            names.Add("amy");
            names.Add("bob");
            print(names.Contains("amy"));
            print(names.Contains("max"));
            print(names.IndexOf("bob"));
            names.Sort();
            print(names[0]);
            print(names[2]);
            names.Reverse();
            print(names[0]);
            var nums = list<int> { };
            nums.Add(30);
            nums.Add(10);
            nums.Add(20);
            nums.Sort();
            print(nums[0]);
            print(nums[2]);
            print(mem());
            """);
        AssertLines(outp, "true", "false", "2", "amy", "zoe", "zoe", "10", "30", "0");
    }

    [Fact]
    public void MapKeysAndValuesReturnLists()
    {
        var outp = BuildAndRun("""
            var ages = map<string, int> { "ana": 31, "bo": 9 };
            print(ages.Count);
            var names = ages.Keys;
            names.Sort();
            print(names[0]);
            print(names[1]);
            print(ages.Values.Contains(31));
            print(ages.Contains("bo"));
            print(mem());
            """);
        AssertLines(outp, "2", "ana", "bo", "true", "true", "0");
    }

    [Fact]
    public void TimeAndFormatBuiltins()
    {
        var outp = BuildAndRun("""
            var t = unixtime();
            print(t > 1700000000);
            print(format(3.14159, 2));
            print(format(7, 0));
            var day = fmttime(t, "%Y");
            print(len(day) == 4);
            print(mem());
            """);
        AssertLines(outp, "true", "3.14", "7", "true", "0");
    }

    [Fact]
    public void SwitchExpressionOnIntAndString()
    {
        var outp = BuildAndRun("""
            var code = 2;
            var name = switch (code) { case 1: "one", case 2: "two", default: "many" };
            print(name);
            print(switch (code) { case 1: 10, case 2: 20, default: 0 });
            var cmd = "stop";
            print(switch (cmd) { case "start": 1, case "stop": 2, default: 0 });
            print(switch (code) { case 2: "matched", default: "no" });
            print(switch (99) { case 1: "a", default: "fallback" });
            print(mem());
            """);
        AssertLines(outp, "two", "20", "2", "matched", "fallback", "0");
    }
}

