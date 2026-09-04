# H# Tutorials

Learn H# from zero to building native web servers. H# looks like C# and
manages memory like Rust: every value has one owner, and the compiler
proves your program frees exactly what it allocated. The result is a
single native executable, no VM, no garbage collector.

## Learning path

Work through these in order. Each one builds on the previous.

| # | Tutorial | You learn |
| --- | --- | --- |
| 1 | [Getting started](01-getting-started.md) | install, hello world, compile, run, the mem() habit |
| 2 | [Language basics](02-language-basics.md) | variables, types, strings, if/while/for, ternary, ?? |
| 3 | [Functions and generics](03-functions-and-generics.md) | parameters, move, type parameters, inference |
| 4 | [Ownership](04-ownership.md) | the one rule that makes H# H#: move, copy, borrow |
| 5 | [Collections](05-collections.md) | lists, maps, byte buffers |
| 6 | [Classes, structs, enums](06-classes-structs-enums.md) | user types, methods, initializers, the new keyword |
| 7 | [Nullables and errors](07-nullables-and-errors.md) | string?, ??, ?., try/catch, lastError |
| 8 | [Tasks and concurrency](08-tasks-and-concurrency.md) | Task.Run, await, Delay, WhenAll, lock, OnAccept loops |
| 9 | [Files and input](09-files-and-io.md) | files, arguments, environment, graceful exit |
| 10 | [Networking: TCP and UDP](10-networking-tcp-udp.md) | sockets, echo servers, datagrams |
| 11 | [HTTP servers and clients](11-http-web.md) | Listen, ListenRaw, headers, cookies, reverse proxying |
| 12 | [Tooling](12-tooling.md) | hsc flags, --check, the VS Code extension, build-dev.bat |

## Install

1. Build or download the H# installer (`installer/installer.iss`, built with
   Inno Setup) and run it. It puts `hsc` on your PATH and ships the runtime.
2. You also need clang (from LLVM) to link, and on Windows the MSVC or
   Windows SDK libraries that come with Visual Studio Build Tools.
3. Install the VS Code extension (`vsExtention/hsharp-language-*.vsix`) for
   completions, diagnostics and F5 build-and-run.

## The 30 second version

Save this as `hello.hs`:

```hsharp
var name = "world";
print($"hello {name}!");
print(mem());
```

Compile and run:

```
hsc hello.hs -o hello.exe
hello.exe
```

`mem()` prints the number of live heap allocations. It prints 0 here, and
in a well-formed H# program it always returns to 0: the compiler tracks
every value you create and inserts the frees for you. That habit, watching
mem() return to zero, is the fastest way to internalize the language.

## Sample programs

The `rt/` folder in the repository has complete demos: `demo-oop.hs`
(language tour), `demo-http.hs` and `demo-httpserver.hs` (web servers).
