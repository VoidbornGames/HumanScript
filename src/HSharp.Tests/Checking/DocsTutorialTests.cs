using HSharp;
using HSharp.Checking;
using HSharp.Parsing;
using HSharp.Syntax;

public class DocsTutorialTests
{
    private static string RepoRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static void CheckCompiles(string name, string source)
    {
        string path = Path.Combine(Path.GetTempPath(), "docs-" + name + ".hs");
        File.WriteAllText(path, source);
        Imports.ConfigureSearchPaths(Array.Empty<string>());
        var program = Imports.LoadTolerant(path, source);
        var checker = new Checker();
        checker.Check(program);
        Assert.True(checker.Errors.Count == 0,
            $"{name}.hs should compile, got:\n{string.Join("\n", checker.Errors.Select(e => e.Message))}");
    }

    [Fact]
    public void BasicsTutorialProgram() => CheckCompiles("basics", """
        var total = 0;
        for (var i = 1; i <= 10; i++)
        {
            total += i;
        }

        var parity = total % 2 == 0 ? "even" : "odd";
        print($"sum 1..10 = {total}, which is {parity}");
        print(mem());
        """);

    [Fact]
    public void OwnershipTutorialProgram() => CheckCompiles("ownership", """
        void consume(move string s)
        {
            print($"consuming {s}");
        }

        consume($"literal");
        var m = "mine";
        consume(m);
        print(copy("keep"));
        for (var i = 0; i < 3; i++)
        {
            var temp = "x" + string(i);
            print(temp);
        }

        var payload = "work item";
        var t = Task.Run(() =>
        {
            print(payload);
        });
        await t;
        print(mem());
        """);

    [Fact]
    public void CollectionsTutorialProgram() => CheckCompiles("collections", """
        var names = list<string> { "ada", "grace" };
        names.Add("linus");
        print(len(names));
        print(names[0]);
        var nums = list<int> { 1, 2, 3 };
        nums[0] = 10;
        nums.Remove(2);
        nums.Clear();

        var hits = map<string, int> { };
        var key = "home";
        var cur = hits[key] ?? 0;
        hits[key] = cur + 1;
        print(hits[key] ?? -1);
        print(hits.Contains("home"));
        hits.Remove("home");

        var b = buffer(16);
        b[0] = 72;
        print(string(b));
        print(mem());
        """);

    [Fact]
    public void ClassesTutorialProgram() => CheckCompiles("classes", """
        enum Level { Low, High }

        class Task2
        {
            string Title;
            Level Priority;

            public string Describe()
            {
                var tag = Priority == Level.High ? "!!!" : ".";
                return $"{Title} {tag}";
            }
        }

        var t = new Task2 { Title: "ship", Priority: Level.High };
        print(t.Describe());

        var opts = new CookieOptions { Secure: true, HttpOnly: true, SameSite: SameSite.Lax, MaxAge: 3600 };
        print(opts.Secure);
        print(mem());
        """);

    [Fact]
    public void HttpTutorialProgram() => CheckCompiles("http", """
        var pages = map<string, string>
        {
            "/": "<h1>home</h1>",
            "/about": "<h1>about</h1>"
        };

        var visits = 0;
        var ln = Http.Listen(8080);
        ln.OnAccept((HttpPacket req) =>
        {
            lock (visits)
            {
                visits = visits + 1;
            }
            var sid = req.Cookies.Get("session") ?? "guest";
            var opts = new CookieOptions { Secure: true, HttpOnly: true, SameSite: SameSite.Lax, MaxAge: 3600 };
            req.Cookies.Set("session", sid, opts);
            req.Header("X-Visits", string(visits));
            var page = pages[req.Path()] ?? "<h1>404</h1>";
            req.Respond(200, page);
        });

        var raw = Http.ListenRaw(8081);
        raw.OnAccept((RawHttpPacket r) =>
        {
            var req = r.ToHttpPacket();
            if (req.Path() == "/health")
            {
                req.Respond(200, "ok");
            }
            else
            {
                r.Forward("127.0.0.1", 9000);
            }
        });

        while (!exiting())
        {
            await Task.Delay(200);
        }
        """);

    [Fact]
    public void NetworkingTutorialProgram() => CheckCompiles("networking", """
        var hits = 0;
        var ln = Tcp.Listen(9002);
        ln.OnAccept((Client c) =>
        {
            var line = c.Recv();
            lock (hits)
            {
                hits = hits + 1;
            }
            c.Send($"you are number {hits}\n");
            c.Close();
        });

        var udp = Udp.Listen(9003);
        udp.OnAccept((string msg) =>
        {
            print($"got: {msg}");
        });

        var c = Tcp.Connect("127.0.0.1", 9002);
        var reply = c.RecvTimeout(2000);
        if (reply == null)
        {
            print("no answer in 2s");
        }
        var all = c.RecvAll(65536);
        print(len(all));

        while (!exiting())
        {
            await Task.Delay(200);
        }
        """);

    [Fact]
    public void FilesTutorialProgram() => CheckCompiles("files", """
        var all = args();
        var who = "world";
        if (len(all) > 0)
        {
            who = all[0];
        }
        print($"hello {who}");

        var file = "counter.txt";
        var current = 0;
        if (exists(file))
        {
            current = int(read(file));
        }
        current += 1;
        write(file, string(current));
        print($"this program has run {current} times");

        var home = env("HOME") ?? "unknown";
        print(home);

        while (!exiting())
        {
            await Task.Delay(200);
        }
        """);

    [Fact]
    public void ConcurrencyTutorialProgram() => CheckCompiles("concurrency", """
        var tasks = list<task<int>> { };
        tasks.Add(Task.Run(() => { return 1; }));
        tasks.Add(Task.Run(() => { return 2; }));

        var results = Task.WhenAll(tasks);
        foreach (var r in results)
        {
            print(r);
        }

        var hits = 0;
        var ln = Tcp.Listen(9100);
        ln.OnAccept((Client c) =>
        {
            lock (hits)
            {
                hits = hits + 1;
            }
            c.Send($"n={hits}\n");
            c.Close();
        });
        print(mem());
        """);
}

