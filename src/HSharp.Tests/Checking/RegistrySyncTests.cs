using HSharp;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Syntax;

public class RegistrySyncTests
{
    private static string Arg(string ty) => ty switch
    {
        "int" => "1",
        "float" => "1.0",
        "bool" => "true",
        "string" => "\"s\"",
        "buffer" => "buffer(8)",
        "CookieOptions" => "new CookieOptions { }",
        _ => throw new InvalidOperationException($"registry sync test needs a sample arg for type '{ty}'")
    };

    private static readonly Dictionary<string, (string Recv, string Decl)> Receivers = new()
    {
        ["listener"] = ("x", "var x = Tcp.Listen(1);"),
        ["httpl"] = ("x", "var x = Http.Listen(1);"),
        ["rawhttpl"] = ("x", "var x = Http.ListenRaw(1);"),
        ["udp"] = ("x", "var x = Udp.Listen(1);"),
        ["Client"] = ("x", "var x = Tcp.Connect(\"h\", 1);"),
        ["HttpPacket"] = ("x", "var x = Http.Listen(1).Accept();"),
        ["RawHttpPacket"] = ("x", "var x = Http.ListenRaw(1).Accept();"),
        ["Cookies"] = ("x.Cookies", "var x = Http.Listen(1).Accept();"),
        ["StringBuilder"] = ("x", "var x = StringBuilder.New();"),
        ["string"] = ("x", "var x = \"s\";"),
    };

    private static readonly Dictionary<string, string> OnAcceptParam = new()
    {
        ["listener"] = "Client", ["httpl"] = "HttpPacket", ["rawhttpl"] = "RawHttpPacket", ["udp"] = "string"
    };

    [Fact]
    public void EveryInstanceMethodChecksClean()
    {
        foreach (var (kind, ms) in RuntimeApi.Handles)
        {
            var (recv, decl) = Receivers[kind];
            var body = new List<string> { decl };
            int n = 0;
            foreach (var m in ms)
            {
                string res = $"var r{n++}";
                if (m.Special && m.Name == "OnAccept")
                {
                    var pt = OnAcceptParam[kind];
                    body.Add($"{recv}.OnAccept(({pt} p{n}) => {{ }});");
                }
                else if (m.Special && m.Name == "Add")
                {
                    body.Add($"{recv}.Add(\"s\");");
                }
                else if (m.Special && m.Ret == "Cookies")
                {
                    body.Add($"{res} = {recv};");
                }
                else
                {
                    var args = string.Join(", ", m.Params.Select(p => Arg(RuntimeApi.ParamTy(p))));
                    body.Add(m.Ret == "void"
                        ? $"{recv}.{m.Name}({args});"
                        : $"{res} = {recv}.{m.Name}({args});");
                }
            }
            CheckClean(string.Join("\n", body), $"{kind}: {string.Join(", ", ms.Select(x => x.Name))}");
        }
    }

    [Fact]
    public void EveryStaticMethodChecksClean()
    {
        var body = new List<string>
        {
            "var r0 = Tcp.Listen(1);",
            "var r1 = Tcp.Connect(\"h\", 1);",
            "var r2 = Udp.Open();",
            "var r3 = Udp.Listen(1);",
            "var r4 = Http.Listen(1);",
            "var r5 = Http.ListenRaw(1);",
            "var r6 = Http.Get(\"http://h\");",
            "var r7 = Http.Post(\"http://h\", \"b\");",
            "var r8 = Http.Status();",
            "var r9 = StringBuilder.New();",
            "var r10 = Task.Run(() => { return 1; });",
            "await Task.Delay(1);",
            "var ts = list<task<int>> { };",
            "ts.Add(Task.Run(() => { return 2; }));",
            "var r11 = Task.WhenAll(ts);",
        };
        CheckClean(string.Join("\n", body), "statics");
    }

    private static void CheckClean(string src, string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hs-reg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "sync.hs");
        File.WriteAllText(path, src);

        var program = Imports.Load(path);
        try
        {
            new Checker().Check(program);
        }
        catch (SourceError ex)
        {
            Assert.Fail($"registry entry rejected by the checker [{label}]: {ex.Message}");
        }
    }
}

