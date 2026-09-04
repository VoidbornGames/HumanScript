# 12. Tooling

## The compiler: hsc

```
hsc app.hs                       compile to app.exe (or app on linux)
hsc app.hs -o out/tool.exe       choose the output path
hsc app.hs --check               type-check only, print OK, exit 0
hsc app.hs -I libs               add a folder to search for imports
hsc app.hs -- <extra clang args> pass flags through to the linker
```

Errors print as `file(line,col): error: message` with a nonzero exit
code, so editors and scripts can parse them.

The compiler embeds LLVM for code generation and compiles the C runtime
(`rt.c`) that ships next to it, then clang links everything into one
native executable. No DLLs of ours, no VM.

## Imports and search paths

`import lib.hs;` loads a library. The compiler looks:

1. next to the importing file,
2. in every `-I` folder,
3. in every folder in the `HSHARP_PATH` environment variable
   (`;` separated on Windows, `:` on Linux).

Dotted names map to folders: `import lib.util.hs;` can resolve to
`lib/util.hs`. Imported files may contain declarations only, types,
enums and functions; the entry file owns the top-level statements that
become `main`. Only `public` declarations cross the file boundary.

Import cycles among libraries are fine; nothing may import the entry
file.

## The VS Code extension

Install `vsExtention/hsharp-language-*.vsix`. It bundles the language
server and gives you:

- compiler-accurate diagnostics as you type, multi-error;
- context-aware completions (members, types after `new`, cookie and
  header calls, all from the real checker);
- hover types, go-to-definition, find references, rename;
- quick fixes: typo suggestions, make-public, auto-import;
- signature help, inlay hints, formatting, folding;
- **F5** or the editor play button: save, build, run;
- Ctrl+Shift+B: build. Tasks live under Terminal > Run Task.

Settings: `hsharp.compilerPath` and `hsharp.languageServerPath` for
custom binary locations, `hsharp.trace.server` for protocol logging.
If completions ever stall: Command Palette -> `H#: Restart the language
server`, and check the `H# Language Server` output channel.

## The development loop: build-dev.bat

In the repository root, `build-dev.bat` is the whole workflow:

```
build-dev.bat                 build, test, publish, package, install, check installer
build-dev.bat --no-test       skip the test suite
build-dev.bat --no-install    package the vsix but do not install it
build-dev.bat --installer     also rebuild the Inno Setup installer
build-dev.bat --fast          build + publish only
```

It publishes single-file `hsc` and `hsharp-lsp` binaries, bundles the
server into the extension, installs the extension into VS Code, and
verifies the installer inputs are current (it tells you when to run it
with `--installer`).

## The installer

`installer/installer.iss` (Inno Setup) installs `hsc`, `rt.c`,
`LLVM-C.dll` and `hsharp-lsp.exe` into `Program Files\HSharp`, adds that
folder to your PATH, and warns if clang is missing. After installing,
open a new terminal so PATH updates take effect.

## Where things live

```
src/HSharp/compiler     lexer, parser, checker, codegen, the hsc CLI
src/HSharp/lsp          the language server (hsharp-lsp)
src/HSharp.Tests        the test suite: dotnet test
rt/rt.c                 the C runtime every H# program links against
vsExtention/            the VS Code extension
installer/              the Inno Setup script
docs/                   these tutorials
```

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `clang not found` at link time | install LLVM 18+, and VS Build Tools on Windows |
| completions stopped | restart the language server command, or reload the window |
| popup `textDocument/... failed` | check the H# Language Server output channel; update the extension |
| `hsc` not recognized | reinstall via the installer, or open a new terminal |
| tests flaky in CI | they are network tests; run with `--filter` to isolate, or retry |

## Where to go from here

Read the demos in `rt/`: `demo-oop.hs` tours the type system,
`demo-httpserver.hs` builds a real web server. Then go write something
and keep `mem()` in your back pocket.
