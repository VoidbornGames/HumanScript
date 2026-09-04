using System.Text;
using System.Text.Json;
using HSharp.Analysis;
using HSharp.Checking;
using HSharp.Lsp;
using HSharp.Syntax;
using static LspSession;

public class CompletionSessionTests
{
    [Fact]
    public void InitializerSuggestsFieldsOfTheType()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; public string Whois() { return Name; } }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import models.hs\nvar usr = User { Na\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 19)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Name", labels);
        Assert.Contains("Salary", labels);

        Assert.DoesNotContain("Whois", labels);
        Assert.DoesNotContain("var", labels);
        Assert.DoesNotContain("print", labels);
    }

    [Fact]
    public void InitializerValuePositionUsesFieldType()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "import models.hs\nvar txt = \"s\";\nvar num = 1;\nvar usr = User { Name: \n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 3, 22)
        });

        var ranked = Ranked(responses, 2);
        Assert.True(ranked.IndexOf("txt") < ranked.IndexOf("num"),
            $"expected the string local before the int one, got: {string.Join(", ", ranked)}");
    }

    [Fact]
    public void EditThenCompleteKeepsUpWithTyping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; public string Whois() { return Name; } }");
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        string v1 = "import models.hs\n\nvar usr = \n";

        string v2 = "import models.hs\n\nvar usr = User { Name: \"a\", Salary: 1 }\n";
        string v3 = "import models.hs\n\nvar usr = User { Name: \"a\", Salary: 1 }\nusr.\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, v1),
            Completion(2, uri, 2, 10),
            DidChange(uri, 2, v2),
            DidChange(uri, 3, v3),
            Completion(3, uri, 3, 4),
        });

        var first = Labels(responses, 2);
        Assert.Contains("User", first);

        var second = Labels(responses, 3);
        Assert.Contains("Name", second);
        Assert.Contains("Salary", second);
        Assert.Contains("Whois", second);
        Assert.DoesNotContain("usr", second);
    }

    [Fact]
    public void OnAcceptFlowSurvivesRealisticSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        string v1 = "var ls = Http.ListenRaw(80);\nls.OnAccept((\n";
        string v2 = "var ls = Http.ListenRaw(80);\nls.OnAccept((RawHttpPacket packet) =>\n{\n    packet.\n});\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, v1),
            Completion(2, uri, 1, 13),
            DidChange(uri, 2, v2),
            Completion(3, uri, 3, 11),
        });

        var lambdaTypes = Labels(responses, 2);
        Assert.Equal("RawHttpPacket", lambdaTypes[0]);
        Assert.DoesNotContain("ls", lambdaTypes);

        var members = Labels(responses, 3);
        Assert.Contains("Forward", members);
        Assert.Contains("ToHttpPacket", members);
        Assert.DoesNotContain("print", members);
    }

    [Fact]
    public void EncodedDriveUriResolvesImportsAndCompletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "models.hs"),
            "public class User { string Name; int Salary; public string Whois() { return Name; } }");
        string path = Path.Combine(dir, "app.hs");

        string rest = new Uri(path).AbsoluteUri["file:///".Length..];
        string uri = $"file:///{char.ToLower(rest[0])}%3A{rest[2..]}";

        string v1 = "import models.hs\n\nvar usr = User { Name: \"a\", Salary: 1 }\nusr.\n";
        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, v1),
            Completion(2, uri, 4, 4)
        });

        var members = Labels(responses, 2);
        Assert.Contains("Name", members);
        Assert.Contains("Whois", members);

        var messages = responses.Where(r => r.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics");
        Assert.All(messages, m => Assert.True(m.GetProperty("params").GetProperty("uri").GetString()!.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(messages, m => m.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Any(d => (d.GetProperty("message").GetString() ?? "").Contains("cannot find file")));
    }

    [Fact]
    public void HalfTypedImportsDoNotError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, "import mo\nprint(1);\n"),
            DidChange(uri, 2, "import models.h\nprint(1);\n"),
            DidChange(uri, 3, "import nope.hs\nprint(1);\n"),
        });

        var diags = responses.Where(r => r.TryGetProperty("method", out var m) && m.GetString() == "textDocument/publishDiagnostics")
            .SelectMany(r => r.GetProperty("params").GetProperty("diagnostics").EnumerateArray())
            .Select(d => d.GetProperty("message").GetString())
            .ToList();

        Assert.DoesNotContain(diags, d => d!.Contains("cannot find file 'mo'"));
        Assert.DoesNotContain(diags, d => d!.Contains("cannot find file 'models.h'"));
        Assert.Contains(diags, d => d!.Contains("nope.hs"));
    }

    [Fact]
    public void MethodChainCompletesAfterCall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var req = Http.Listen(80).Accept();\nreq.Source().\n";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 13)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Contains", labels);
        Assert.Contains("ToUpper", labels);
        Assert.DoesNotContain("Respond", labels);
    }

    [Fact]
    public void RawHttpPacketChainCompletesStringMethods()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "visits.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var ln = Http.ListenRaw(7100);\nln.OnAccept((RawHttpPacket packet) =>\n{\n    var sourceIP = packet.Source().\n";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 3, lines[3].Length)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Split", labels);
        Assert.Contains("Contains", labels);
        Assert.DoesNotContain("Respond", labels);
        Assert.DoesNotContain("ToHttpPacket", labels);
    }

    [Fact]
    public void HttpPacketChainCompletesInsideOnAccept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var ln = Http.Listen(8080);\nln.OnAccept((HttpPacket packet) =>\n{\n    var ip = packet.Source().\n";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 3, lines[3].Length)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Split", labels);
        Assert.Contains("ToLower", labels);
        Assert.DoesNotContain("Respond", labels);
    }

    [Fact]
    public void StaticClassMembersCompleteAfterDot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, "var ln = Http."),
            Completion(2, uri, 0, 15),
            DidChange(uri, 2, "var t = Task."),
            Completion(3, uri, 0, 13),
            DidChange(uri, 3, "var sb = StringBuilder."),
            Completion(4, uri, 0, 23)
        });

        var http = Labels(responses, 2);
        Assert.Contains("Listen", http);
        Assert.Contains("ListenRaw", http);
        Assert.Contains("Get", http);
        Assert.Contains("Post", http);
        Assert.Contains("Status", http);

        var task = Labels(responses, 3);
        Assert.Contains("Run", task);
        Assert.Contains("Delay", task);
        Assert.Contains("WhenAll", task);

        var sb = Labels(responses, 4);
        Assert.Contains("New", sb);
    }

    [Fact]
    public void EmptyOnAcceptBodySuggestsParamAndItsMembers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        string src = "var ln = Http.Listen(15600);\nln.OnAccept((HttpPacket packet) => {\n    ";
        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 2, 4),
            DidChange(uri, 2, src + "pac"),
            Completion(3, uri, 2, 7),
            DidChange(uri, 3, src + "packet."),
            Completion(4, uri, 2, 11)
        });

        var inBody = Labels(responses, 2);
        Assert.Contains("packet", inBody);

        var typing = Labels(responses, 3);
        Assert.Contains("packet", typing);

        var members = Labels(responses, 4);
        Assert.Contains("Respond", members);
        Assert.Contains("Cookies", members);
        Assert.Contains("Source", members);
    }

    [Fact]
    public void TypedDeclarationTypesSuggestWhileTyping()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;

        string src = "public class User { string Name; }\nvar a = 1;\nbo";
        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 2, 2),
            DidChange(uri, 2, "public class User { string Name; }\nvar a = 1;\nbool "),
            Completion(3, uri, 2, 5)
        });

        var typingType = Labels(responses, 2);
        Assert.Contains("bool", typingType);
        Assert.Contains("int", typingType);
        Assert.Contains("buffer", typingType);

        var afterType = Labels(responses, 3);
        Assert.DoesNotContain("if", afterType);
        Assert.DoesNotContain("bool", afterType);
    }

    [Fact]
    public void BuiltinCallChainCompletesAsList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "args().";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 0, 7)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Join", labels);
        Assert.Contains("Count", labels);
    }

    [Fact]
    public void PlainStringSuppressesSuggestions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var name = \"a\";\nvar s = \"hello wor";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 15)
        });

        Assert.Empty(Labels(responses, 2));
    }

    [Fact]
    public void InterpHoleSuggestsLocals()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var name = \"a\";\nvar msg = $\"hi {na";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 18)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("name", labels);
    }

    [Fact]
    public void CookiesFacadeCompletesGetAndSet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var req = Http.Listen(80).Accept();\nreq.Cookies.\n";
        var lines = src.Split("\n");

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 12)
        });

        var labels = Labels(responses, 2);
        Assert.Contains("Get", labels);
        Assert.Contains("Set", labels);
        Assert.DoesNotContain("Respond", labels);
        Assert.DoesNotContain("Forward", labels);
    }

    [Fact]
    public void PacketMembersIncludeCookies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var req = Http.Listen(80).Accept();\nreq.\n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 1, 4)
        });

        Assert.Contains("Cookies", Labels(responses, 2));
    }

    [Fact]
    public void AfterNewSuggestsTypesAndBuiltinFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hs-real-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "app.hs");
        string uri = new Uri(path).AbsoluteUri;
        string src = "var opts = new CookieOptions { \n";

        var responses = Run(dir, new List<object>
        {
            DidOpen(uri, src),
            Completion(2, uri, 0, 14),
            Completion(3, uri, 0, 30)
        });

        var afterNew = Labels(responses, 2);
        Assert.Contains("CookieOptions", afterNew);

        var fields = Labels(responses, 3);
        Assert.Contains("Secure", fields);
        Assert.Contains("HttpOnly", fields);
        Assert.Contains("MaxAge", fields);
        Assert.DoesNotContain("print", fields);
    }
}

