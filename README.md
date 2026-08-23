# H Sharp (H#)

A small statically-typed language that looks like C# and manages memory like Rust, compiling to a native executable through LLVM. No runtime, no VM, no garbage collector.

## The memory model

This is the point of the language. Values are freed exactly when they stop being used, checked at compile time:

- `int`, `float`, `bool` are copied by value.
- `string` and `list<T>` have exactly one owner. Assigning from a variable **moves** it; using it afterwards is a compile error. Use `copy(s)` when you need both.
- A value is freed right after its **last use**, not just at scope exit. Deterministic, zero runtime cost, no collector.
- Conditional moves are handled with hidden drop flags (the same trick pre-2016 Rust used), so frees stay correct on both branches of an `if`.
- Moving a variable declared outside a loop inside that loop is a compile error, which kills the classic use-after-free case before it exists.
- Lists own their elements. Insertion copies, remove/clear/overwrite free.
- Function parameters are borrowed by default (read-only, can't escape). `move` parameters take ownership. Return values are owned by the caller.
- `mem()` returns the number of live heap allocations. A well-formed program returns to 0; every test checks this.

## Syntax

```csharp
var x = 10;
var name = input("name? ");

if (x > 5)
{
    print($"{x} is bigger than 5");
}
else if (x == 5)
{
    print("exactly 5");
}
else
{
    print("small");
}

var fruits = list<string> { "apple", "banana" };
fruits.Add("cherry");
fruits.Remove("banana");
print(fruits[0]);
print(fruits.Count);

foreach (var f in fruits)
{
    print(f);
}

for (var i = 0; i < 10; i++)
{
    print(i);
}

int add(int a, int b)
{
    return a + b;
}
print(add(2, 3));

var s = "hello";
var t = s;          // moves: s is gone now
print(t);           // fine
// print(s);        // error: use of moved value 's'

void log(move string line)
{
    print(line);    // takes ownership, frees it on return
}

try
{
    var data = read("config.txt");
    print(data);
}
catch
{
    print("config missing");
}

write("out.txt", "saved");
print(exists("out.txt"));
delete("out.txt");
print(mem());       // 0, nothing leaked
```

Types: `int`, `float`, `bool`, `string`, `list<int>` / `list<string>`. Locals use `var` with inference; functions declare their return type and parameter types. `&&`, `||`, `!`, `==`, `!=`, `<`, `<=`, `>`, `>=`, `+ - * / %`, `+=` and friends, `//` comments, `$"...{expr}..."` interpolation.

Runtime errors (missing files, out-of-bounds indexing, division by zero, failed network calls) route to the nearest `catch`: errors are detected at statement boundaries, and any values already created in the failing statement are cleaned up before the catch runs.

## Concurrency

```csharp
var payload = "data";
var t = Task.Run(() =>
{
    // runs on the thread pool; payload moved in, plain numbers copied
    print(payload);
    return 40 + 2;
});

_ = Task.Run(() => { print("fire and forget"); });   // discard form

var answer = await t;     // collects the result, 42
```

Lambdas live where a task is expected. Owned values (string, list) capture by **move**: after the capture the outer name is gone, exactly like assignment. Numbers capture by value. Borrowed values can't be captured, `copy()` them first. `await` blocks the caller until the task finishes and hands over its owned result.

## Networking

```csharp
var ln = Tcp.Listen(8080);
var client = ln.Accept();          // or: Tcp.Connect("host", 8080)
client.Send("hello\n");            // Recv() reads one line
var line = client.Recv();
client.Close();

var udp = Udp.Open();
udp.SendTo("127.0.0.1", 9000, "ping");
var msg = udp.Recv();
```

String toolkit for parsing: `contains`, `startsWith`, `indexOf`, `sub(s, start, len)`, `parseInt`. HTTP isn't a builtin yet, it's just the language: `rt/demo-http.hs` is a complete HTTP/1.1 server + client written in H# using exactly these pieces. `await` is blocking for now; cooperative async (state machines) is the next milestone.

## Build

Requires .NET 8 SDK, LLVM 18 (`LLVM-C.dll` / `libLLVM-18` on your PATH), and `clang` for linking.

```bash
git clone https://github.com/VoidbornGames/HSharp.git
cd HSharp
dotnet build
```

## Use

```bash
dotnet run --project src/HSharp/compiler -- program.hs -o program
./program
```

Without `-platform` the build targets whatever OS you're running on. To pick a target explicitly:

```bash
dotnet run --project src/HSharp/compiler -- program.hs -platform win64
dotnet run --project src/HSharp/compiler -- program.hs -platform linux64
dotnet run --project src/HSharp/compiler -- program.hs -platform osx64
```

(plus `linux-arm64` and `osx-arm64`). Cross-linking needs the target's C library available to clang; pass e.g. `-- --sysroot=/path/to/sysroot` for that.

Errors point at the line and column: `program.hs(3,7): error: use of moved value 's'`.

Set `HS_DUMP_IR=1` to dump the generated LLVM IR to `%TEMP%/hs-dump.ir` when debugging the compiler.

## How it works

```
program.hs -> Lexer -> Parser -> Checker -> CodeGen -> LLVM IR -> object file -> clang -> native executable
```

The checker does static typing plus the ownership analysis: it tracks every move and every last use, then annotates the AST with drop points. Codegen emits LLVM IR through the C API, including a small IR-level runtime (allocation counter, list helpers, string helpers) built into each module, so there is no external runtime library. All allocas are hoisted to the function entry block so loops don't grow the stack.

## Tests

```bash
dotnet test
```

Compile-error coverage (moves, borrows, types) runs the checker in-process; codegen tests run the full pipeline to a verified object file.

## License
MIT

`Disclaimer`: AI was used in C/C++ and LLVM development!
