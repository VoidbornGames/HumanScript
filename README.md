# H Sharp (H#)

H# is a small statically typed language with C# syntax and Rust-style
ownership. It compiles through LLVM to a native executable. No VM, no
garbage collector, no runtime to ship.

Every heap value has one owner. Assignment moves it, and using the old name
afterwards is a compile error. Borrowing is read-only, `move` parameters
take ownership, and the compiler frees each value right after its last use.
`mem()` reports live allocations and well-formed programs return to 0. This
is checked before the program runs, not at 3am.

The standard library targets small network programs: TCP and UDP sockets,
an HTTP client with TLS, two HTTP servers (parsed packets and a raw
packet-level listener for proxies), tasks on a thread pool, file IO, and
the usual collections. A concurrent HTTP handler is a lambda attached to a
listener; shared state is a map plus a `lock`.

Status: working preview (Gen 7). The ownership checker, codegen, runtime
and editor support are real and covered by the test suite, which compiles
programs to native code, runs them, and requires `mem()` to return to 0.
Not here yet: cooperative async (await blocks), a debugger, a package
manager, server-side TLS.

The [tutorials in docs/](docs/README.md) teach the language, twelve parts
from hello world to web servers. The examples are compiled against the
checker as part of `dotnet test`, so they cannot drift.

## Build

.NET 8 SDK, LLVM 18 (`LLVM-C.dll` / `libLLVM-18` on PATH) and clang for
linking.

```bash
git clone https://github.com/VoidbornGames/HSharp.git
cd HSharp
dotnet build
dotnet run --project src/HSharp/compiler -- program.hs -o program
./program
```

`-platform` picks win64, linux64, osx64, linux-arm64 or osx-arm64.
Cross-linking needs the target's C library for clang.

Errors carry positions: `program.hs(3,7): error: use of moved value 's'`.

## Tests

```bash
dotnet test
```

## Layout

`src/HSharp/compiler` is the compiler (Syntax, Lexing, Parsing, Checking,
CodeGen, Analysis, CLI), `src/HSharp/lsp` the language server used by the
VS Code extension in `vsExtention`, `src/HSharp.Tests` the tests, `rt/` the
C runtime.

## License

MIT

**Disclaimer**: AI assistant was used in C/C++ and LLVM development!
