# H# for VS Code

VS Code extension for [H#](https://github.com/VoidbornGames/H-Sharp).
Bundles the H# language server, so completions, diagnostics, hover,
go-to-definition, rename, formatting, quick fixes and the build/run tasks
all run on the real compiler.

## Install

Double-click the `.vsix`, or:

```
code --install-extension hsharp-language-0.7.0.vsix
```

Reload VS Code afterwards; the old server keeps running until you do.

## `.hs` files

`.hs` is also Haskell. When VS Code asks, pick H#, or set it for good:

```json
"files.associations": { "*.hs": "hsharp" }
```

## Settings

Tasks and build/run need `hsc` on your PATH (the H# installer adds it).
Custom locations:

```json
"hsharp.compilerPath": "C:/tools/hsc.exe",
"hsharp.languageServerPath": "C:/tools"
```

The H# status bar item shows which language server version is connected.

## Code Runner

If you use the Code Runner extension (Ctrl+Alt+N), H# files build and run
with `hsc` automatically. Note it uses plain `hsc` from your PATH; F5 and
the editor run button use the bundled tools instead, which is safer after
an update.

