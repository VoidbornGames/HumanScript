using HSharp.Checking;
using HSharp.CodeGen;
using HSharp.Lexing;
using HSharp.Parsing;
using HSharp.Syntax;
using Compiler = HSharp.CodeGen.CodeGen;
using HSharp;

namespace HSharp.Tests;

public class CheckerTests
{
    [Theory]
    [InlineData("var s = \"x\";\nvar t = s;\nprint(s);", "use of moved value 's'")]
    [InlineData("var s = \"x\";\nwhile (true)\n{\n    var t = s;\n}", "cannot move 's' inside a loop")]
    [InlineData("string f(string p)\n{\n    return p;\n}\nprint(f(\"a\"));", "cannot return borrowed value 'p'")]
    [InlineData("void g(move string s) { }\nvoid f(string p)\n{\n    g(p);\n}\ng(\"x\");", "cannot move borrowed value 'p'")]
    [InlineData("var x = 5;\nx = \"hello\";", "cannot assign a string value to 'x'")]
    [InlineData("var s = \"x\";\nvar c = true;\nif (c)\n{\n    var t = s;\n}\nprint(s);", "use of moved value 's'")]
    [InlineData("void g(move string s) { }\ng(\"literal\");", "must be an owned value")]
    [InlineData("print(nothing);", "undefined variable 'nothing'")]
    [InlineData("var x = 5;\nvar x = 6;", "is already declared in this scope")]
    [InlineData("int f(int a)\n{\n    return a;\n}\nf(1, 2);", "takes 1 argument(s)")]
    [InlineData("var l = list<string> { \"a\" };\nprint(l);", "cannot print a list")]
    [InlineData("var l = list<string> { \"a\" };\nl.Bogus();", "unknown list member 'Bogus'")]
    [InlineData("var l = list<float> { };", "not supported yet")]
    [InlineData("if (5)\n{\n    print(\"x\");\n}", "if condition must be a bool")]
    [InlineData("var l = list<string> { \"a\" };\nprint(l[\"k\"]);", "list index must be an int")]
    [InlineData("void f(string p)\n{\n    var t = p;\n}\nf(\"a\");", "cannot move out of borrowed parameter 'p', use copy(p)")]
    [InlineData("void f(string p)\n{\n    var t = \"\";\n    t = p;\n}\nf(\"a\");", "cannot move out of borrowed parameter 'p', use copy(p)")]
    [InlineData("break;", "'break' outside of a loop")]
    [InlineData("while (true)\n{\n    print(\"x\");\n}\ncontinue;", "'continue' outside of a loop")]
    [InlineData("print(\"a\" < \"b\");", "strings only support '==' and '!='")]
    [InlineData("var p = \"x\";\nvar q = p;\nvar r = p;", "use of moved value 'p'")]
    [InlineData("void f(move string s)\n{\n    print(s);\n}\nf(\"a\");\nf(\"b\");", "must be an owned value")]
    [InlineData("var b = true;\nvar i = int(b);", "cannot convert a bool value to int")]
    [InlineData("string? s = null;\nprint(s);", "cannot print a nullable value; check it against null first")]
    [InlineData("string? s = null;\nstring t = s;", "possibly null string?")]
    [InlineData("var s = null;", "cannot infer a type from 'null'; annotate the variable")]
    [InlineData("int? x = 5;\nprint(x + 1);", "requires numbers")]
    [InlineData("void? f()\n{\n}", "void cannot be made nullable")]
    [InlineData("list<int?> l = list<int?> { };", "lists cannot hold nullable values")]
    [InlineData("int? a = null;\nvar b = a ?? \"x\";", "cannot coalesce int? with a string value")]
    [InlineData("enum Color { Red }\nColor c = Color.Bogus;", "'Bogus' is not a member of enum Color")]
    [InlineData("enum Color { Red }\nvar c = Color.Red;\nvar i = c + 1;", "requires numbers")]
    [InlineData("enum Color { Red }\nvar c = Color.Red;\nc = 3;", "cannot assign a int value to 'c'")]
    [InlineData("Bogus x = 1;", "unknown type 'Bogus'")]
    [InlineData("struct Node { Node Next; }", "contains itself")]
    [InlineData("struct A { B B; }\nstruct B { A A; }", "contains itself")]
    [InlineData("class User { string Name; }\nvar u = User { Name: \"Bob\", Bogus: 1 };", "'User' has no field 'Bogus'")]
    [InlineData("class User { string Name; }\nvar u = User { };", "missing field 'Name'")]
    [InlineData("class User { string Name; }\nvar u = User { Name: \"Bob\" };\nvar v = u;\nprint(u.Name);", "use of moved value 'u'")]
    [InlineData("class User { string Name; }\nvar u = User { Name: \"Bob\" };\nu.Bogus();", "'User' has no method 'Bogus'")]
    [InlineData("class User { int Age; }\nvar u = User { Age: 20 };\nu.Age = \"x\";", "cannot assign a string value to field 'Age'")]
    [InlineData("class User { string Name; }\nvar u = User { Name: \"Bob\" };\nprint(u);", "cannot print a class or struct")]
    [InlineData("class Empty { }", "has no members")]
    [InlineData("T Get<T>()\n{\n    return 5;\n}\nvar x = Get();", "cannot infer 'T' for 'Get'")]
    [InlineData("T Get<T>(T v)\n{\n    return v;\n}\nvar x = Get<int>(\"s\");", "expected int, got string")]
    [InlineData("T Get<T>(int k)\n{\n    return 5;\n}\nvar x = Get(3);", "cannot infer 'T' for 'Get'")]
    [InlineData("T Get<T>(T v)\n{\n    return v;\n}\nvar x = Get<Bogus>(5);", "unknown type 'Bogus'")]
    public void RejectsInvalidPrograms(string source, string expected)
    {
        var ex = Assert.Throws<SourceError>(() => Check(source));
        Assert.Contains(expected, ex.Message);
    }

    private static void Check(string source)
    {
        var program = new Parser(new Lexer(source).Tokenize()).Parse();
        new Checker().Check(program);
    }
}

public class CodegenTests
{
    [Theory]
    [InlineData("""
        var x = 10;
        print($"{x} is bigger than 5");
        """)]
    [InlineData("""
        var fruits = list<string> { "apple", "banana" };
        fruits.Add("cherry");
        fruits.Remove("banana");
        fruits[0] = "pear";
        print(fruits[0]);
        print(fruits.Count);
        foreach (var f in fruits)
        {
            print(f);
        }
        """)]
    [InlineData("""
        var nums = list<int> { 1, 2, 3 };
        var sum = 0;
        for (var i = 0; i < len(nums); i++)
        {
            sum += nums[i];
        }
        while (sum > 0)
        {
            sum -= 10;
        }
        print(sum);
        """)]
    [InlineData("""
        int fib(int n)
        {
            if (n < 2) { return n; }
            return fib(n - 1) + fib(n - 2);
        }
        print(fib(15));
        """)]
    [InlineData("""
        var s = "hello";
        var t = s;
        print(t);

        void swallow(move string v)
        {
            print(v);
        }
        var temp = "temp";
        swallow(temp);

        var a = "first";
        var b = "";
        if (true)
        {
            b = a;
        }
        print(b);
        """)]
    [InlineData("""
        try
        {
            var data = read("missing.txt");
        }
        catch
        {
            print("caught");
        }
        write("out.txt", "content");
        print(exists("out.txt"));
        delete("out.txt");
        """)]
    [InlineData("""
        var name = input("name: ");
        print($"hi {name}");
        print(1 + 2.5);
        print(mem());
        """)]
    [InlineData("""
        try
        {
            var x = 10 / 0;
            print("nope");
        }
        catch
        {
            print("caught");
        }
        print(10 % 3);
        """)]
    [InlineData("""
        var x = 10;
        var y = float(x);
        print(y);
        print(int(y));
        print(int("42"));
        print(float("2.5"));
        print(string(x));
        print(string(y));
        print(mem());
        """)]
    [InlineData("""
        string? s = null;
        if (s != null)
        {
            print(s);
        }
        s = "value";
        if (s != null)
        {
            print(s);
        }
        print(s ?? "fallback");
        var t = s ?? "other";
        print(mem());
        """)]
    [InlineData("""
        int? n = null;
        n = 5;
        if (n != null)
        {
            print(n + 1);
        }
        print(n ?? 0);
        if (n == null)
        {
            print("null");
        }
        else
        {
            print(n * 2);
        }
        print(mem());
        """)]
    [InlineData("""
        list<string>? l = null;
        int? c = l?.Count;
        if (c != null)
        {
            print(c);
        }
        l = list<string> { "a", "b" };
        c = l?.Count;
        print(c ?? -1);
        print(mem());
        """)]
    [InlineData("""
        enum Color { Red, Green = 5, Blue }

        Color favorite = Color.Green;
        if (favorite == Color.Green)
        {
            print("green");
        }
        print(favorite);
        print(Color.Blue);
        print($"{favorite}");

        string Name(Color c)
        {
            if (c == Color.Red) { return "red"; }
            if (c == Color.Green) { return "green"; }
            return "blue";
        }
        print(Name(Color.Blue));
        print(mem());
        """)]
    [InlineData("""
        struct Point { int X; int Y; }

        class User
        {
            string Name;
            int Age;

            string Greet()
            {
                return "hi " + Name;
            }

            void Birthday()
            {
                Age = Age + 1;
            }
        }

        var p = Point { X: 1, Y: 2 };
        p.X = 10;
        print(p.X + p.Y);

        var user = User { Name: "Bob", Age: 20 };
        user.Age = 21;
        print(user.Greet());
        user.Birthday();
        print(user.Age);
        print(mem());
        """)]
    [InlineData("""
        struct Box { string Label; }

        var a = Box { Label: "one" };
        var b = a;
        b.Label = "two";
        print(a.Label);
        print(b.Label);
        print(mem());
        """)]
    [InlineData("""
        class Item { string Name; }

        Item Make(string n)
        {
            return Item { Name: n };
        }

        var i = Make("x");
        print(i.Name);

        var j = i;
        print(j.Name);
        print(mem());
        """)]
    [InlineData("""
        class Bag { list<string> Items; }

        var b = Bag { Items: list<string> { "a" } };
        b.Items.Add("b");
        print(b.Items.Count);

        var c = b;
        c.Items.Add("c");
        print(c.Items.Count);
        print(mem());
        """)]
    [InlineData("""
        class User { string Name; }

        User? u = null;
        string? n = u?.Name;
        print(n ?? "none");
        u = User { Name: "Bob" };
        n = u?.Name;
        if (n != null)
        {
            print(n);
        }
        print(u?.Name ?? "anon");
        print(mem());
        """)]
    [InlineData("""
        struct Point { int X; int Y; }

        int Sum(Point p)
        {
            p.X = p.X + 1;
            return p.X + p.Y;
        }

        var pt = Point { X: 10, Y: 5 };
        print(Sum(pt));
        print(pt.X);
        print(mem());
        """)]
    [InlineData("""
        T GetData<T>(string key, T fallback)
        {
            if (len(key) > 0) { return fallback; }
            return fallback;
        }

        var a = GetData<int>("k", 42);
        var b = GetData<string>("k", "str");
        var c = GetData("k", 2.5);
        print(a);
        print(b);
        print(c);
        print(mem());
        """)]
    [InlineData("""
        class Picker
        {
            int Bias;
            list<string> Notes;

            T Pick<T>(T a, T b)
            {
                if (Bias > 0) { return a; }
                return b;
            }

            T AddNote<T>(T v)
            {
                Notes.Add(string(v));
                return v;
            }
        }

        var p = Picker { Bias: 1, Notes: list<string> { } };
        print(p.Pick<int>(7, 9));
        print(p.Pick("a", "b"));
        print(p.AddNote(5));
        print(p.Notes.Count);
        print(mem());
        """)]
    [InlineData("""
        T First<T>(list<T> items, T fallback)
        {
            if (items.Count > 0) { return items[0]; }
            return fallback;
        }

        var nums = list<int> { 10, 20 };
        var words = list<string> { "x" };
        print(First(nums, -1));
        print(First(words, "empty"));
        print(First(list<string> { }, "blank"));
        print(mem());
        """)]
    [InlineData("""
        string? page = Http.Get("http://example.com/");
        if (page != null)
        {
            print(len(page));
        }
        else
        {
            print(Http.Status());
        }
        print(page ?? "nothing");

        string? echoed = Http.Post("http://example.com/echo", "ping=1");
        if (echoed != null)
        {
            print(echoed);
        }
        print(mem());
        """)]
    [InlineData("""
        class Tracker
        {
            string Label;

            public string Report()
            {
                return Label + "!";
            }
        }

        var t = Tracker { Label: "hit" };
        string? r = null;
        print(r ?? t.Report());
        print(mem());
        """)]
    [InlineData("""
        var ln = Http.Listen(8080);
        var req = ln.Accept();
        print(req.Method());
        print(req.Path());
        string? host = req.Header("Host");
        print(host ?? "none");
        print(req.Body());
        print(req.Source());
        print(req.Dest());
        req.Respond(200, "ok");

        var rawLn = Http.ListenRaw(8081);
        var raw = rawLn.Accept();
        print(raw.Source());
        print(raw.Dest());
        var parsed = raw.ToHttpPacket();
        print(parsed.Path());
        parsed.Respond(404, "nope");
        raw.Close();
        print(mem());
        """)]
    public void CompilesToVerifiedObjectFile(string source)
    {
        var program = new Parser(new Lexer(source).Tokenize()).Parse();
        new Checker().Check(program);

        var objPath = Path.Combine(Path.GetTempPath(), "hs-test-" + Guid.NewGuid().ToString("N") + ".o");
        try
        {
            new Compiler().Generate(program, objPath);
            Assert.True(File.Exists(objPath), "object file was not emitted");
            Assert.True(new FileInfo(objPath).Length > 0);
        }
        finally
        {
            if (File.Exists(objPath)) File.Delete(objPath);
        }
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-apple-darwin")]
    [InlineData("aarch64-unknown-linux-gnu")]
    public void EmitsObjectsForEveryTarget(string triple)
    {
        var program = new Parser(new Lexer("print(\"hi\");").Tokenize()).Parse();
        new Checker().Check(program);

        var objPath = Path.Combine(Path.GetTempPath(), "hs-target-" + Guid.NewGuid().ToString("N") + ".o");
        try
        {
            new Compiler().Generate(program, objPath, triple);
            Assert.True(File.Exists(objPath), $"no object emitted for {triple}");
            Assert.True(new FileInfo(objPath).Length > 0);
        }
        finally
        {
            if (File.Exists(objPath)) File.Delete(objPath);
        }
    }
}

public class ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hs-imp-" + Guid.NewGuid().ToString("N"));

    private string Write(string name, string source)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private void Compile(string entry)
    {
        var program = Imports.Load(entry);
        new Checker().Check(program);

        var objPath = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".o");
        new Compiler().Generate(program, objPath);
        Assert.True(File.Exists(objPath) && new FileInfo(objPath).Length > 0);
    }

    [Fact]
    public void MergesImportedDeclarations()
    {
        Write("superlib.hs", """
            public string Shout(string s)
            {
                return s + "!";
            }

            public int Secret()
            {
                return 41;
            }

            public enum Level { Low, High }

            public class Greeter
            {
                string Name;

                public string Hello()
                {
                    return "hello " + Name;
                }
            }

            T Echo<T>(T v)
            {
                return v;
            }
            """);
        var entry = Write("main.hs", """
            import superlib.hs;

            print(Shout("hey"));
            print(Level.High);
            var g = Greeter { Name: "bob" };
            print(g.Hello());
            print(mem());
            """);

        Compile(entry);
    }

    [Fact]
    public void NonPublicItemsStayHidden()
    {
        Write("hidden.hs", "int Secret() { return 1; }");
        var entry = Write("main.hs", "import hidden.hs;\nprint(Secret());");

        var ex = Assert.Throws<SourceError>(() =>
        {
            var program = Imports.Load(entry);
            new Checker().Check(program);
        });
        Assert.Contains("'Secret' is not public", ex.Message);
    }

    [Fact]
    public void NonPublicTypesStayHidden()
    {
        Write("hidden.hs", "class Inner { int X; }");
        var entry = Write("main.hs", "import hidden.hs;\nvar i = Inner { X: 1 };");

        var ex = Assert.Throws<SourceError>(() =>
        {
            var program = Imports.Load(entry);
            new Checker().Check(program);
        });
        Assert.Contains("type 'Inner' is not public", ex.Message);
    }

    [Fact]
    public void ImportedFilesRejectStatements()
    {
        Write("lib.hs", "print(\"nope\");");
        var entry = Write("main.hs", "import lib.hs;");

        var ex = Assert.Throws<SourceError>(() => Imports.Load(entry));
        Assert.Contains("can only contain declarations", ex.Message);
    }

    [Fact]
    public void CyclicImportsLoadOnce()
    {
        Write("a.hs", """
            import b.hs;

            public int A()
            {
                return 1;
            }
            """);
        Write("b.hs", """
            import a.hs;

            public int B()
            {
                return 2;
            }
            """);
        var entry = Write("main.hs", """
            import a.hs;
            import b.hs;
            print(A() + B());
            """);

        Compile(entry);
    }

    [Fact]
    public void MissingImportFailsCleanly()
    {
        var entry = Write("main.hs", "import nothere.hs;");
        var ex = Assert.Throws<SourceError>(() => Imports.Load(entry));
        Assert.Contains("cannot find file", ex.Message);
    }

    [Fact]
    public void DiamondWithBackEdgeLoadsEachFileOnce()
    {

        Write("c.hs", "import a.hs;\n\npublic int C() { return A() + 100; }");
        Write("a.hs", "import c.hs;\n\npublic int A() { return 1; }");
        Write("b.hs", "import c.hs;\n\npublic int B() { return C() + 1000; }");
        var entry = Write("main.hs", """
            import a.hs;
            import b.hs;
            import c.hs;

            print(A() + B() + C());
            """);

        Compile(entry);
    }

    [Fact]
    public void PublicMethodsMayUsePrivateHelpers()
    {
        Write("lib.hs", """
            int Volume() { return 3; }

            public string Shout(string s)
            {
                return s + "!" + string(Volume());
            }

            public T Amplify<T>(T v)
            {
                if (Volume() > 0) { return v; }
                return v;
            }
            """);
        var entry = Write("main.hs", """
            import lib.hs;

            print(Shout("hey"));
            print(Amplify<int>(7));
            print(Amplify("inferred"));
            """);

        Compile(entry);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

