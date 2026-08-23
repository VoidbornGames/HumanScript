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
    [InlineData("break;", "'break' outside of a loop")]
    [InlineData("while (true)\n{\n    print(\"x\");\n}\ncontinue;", "'continue' outside of a loop")]
    [InlineData("print(\"a\" < \"b\");", "strings only support '==' and '!='")]
    [InlineData("var p = \"x\";\nvar q = p;\nvar r = p;", "use of moved value 'p'")]
    [InlineData("void f(move string s)\n{\n    print(s);\n}\nf(\"a\");\nf(\"b\");", "must be an owned value")]
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
    public void CompilesToVerifiedObjectFile(string source)
    {
        var program = new Parser(new Lexer(source).Tokenize()).Parse();
        new Checker().Check(program);

        var objPath = Path.Combine(Path.GetTempPath(), "hs-test-" + Guid.NewGuid().ToString("N") + ".o");
        try
        {
            new CodeGen().Generate(program, objPath);
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
            new CodeGen().Generate(program, objPath, triple);
            Assert.True(File.Exists(objPath), $"no object emitted for {triple}");
            Assert.True(new FileInfo(objPath).Length > 0);
        }
        finally
        {
            if (File.Exists(objPath)) File.Delete(objPath);
        }
    }
}
