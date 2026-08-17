# HumanScript

A programming language that reads like plain English and compiles straight to a native executable. No runtime, no VM — just LLVM doing the heavy lifting under the hood.

## What changed

The compiler was rebuilt from the ground up:

- **LLVM backend** — replaced the old Nim/C pipeline. Source now compiles directly to an object file via LLVM and links with `clang`, so there's no intermediate language to install or debug.
- **Indentation-based blocks** — no more `[`, `]`, or semicolons. Blocks are defined by indentation, closed with `end`.
- **New syntax** — variables use `remember x is 5`, output uses `say`/`show`, and file extension is `.hs`.
- **Real control flow** — `if`/`otherwise`, `while`, `repeat N times`, `for every item in list`, all nestable.
- **Functions** — declare with `to name param1 param2`, call directly by name.
- **Lists** — `add`/`remove`/`clear`, indexing with `list[0]`, `number of list` for length.
- **Error handling** — `try`/`catch` blocks.
- **File I/O** — `read`, `write into`, `delete`, `exists`.
- **Test suite** — compiler and codegen now have unit test coverage (xUnit).

## Requirements

- .NET 8 SDK (to build the compiler)
- LLVM 18 (`libLLVM-18` / `LLVM-C.dll`)
- `clang` (or `clang-18` on Linux/macOS) for linking

## Build

```bash
git clone https://github.com/your-username/HumanScript.git
cd HumanScript
dotnet build
```

## Usage

```bash
hsc program.hs -o program
./program
```

## Syntax at a glance

**Variables**
```
remember x is 10
set x to 20
increase x by 5
decrease x by 1
multiply x by 2
divide x by 2
```

**Output & input**
```
say "Hello, World!"
show x
ask "What's your name?" into name
```

**Conditionals**
```
if x is greater than 10 then
    say "big"
otherwise
    say "small"
end
```

**Loops**
```
repeat 3 times
    say "hi"
end

while x is less than 100
    increase x by 1
end

for every item in my_list
    say item
end
```

**Functions**
```
to greet name
    say name
end

greet "Alice"
```

**Lists**
```
remember fruits is a list
add "apple" to fruits
remove "apple" from fruits
say number of fruits
say fruits[0]
```

**Files**
```
write "log entry" into "log.txt"
read "log.txt" into content
if exists "log.txt" then
    delete "log.txt"
end
```

**Error handling**
```
try
    read "missing.txt" into data
catch
    say "caught it"
end
```

**Comments**
```
// this is a comment
```

## How it works

```
program.hs → Lexer → Parser → AST → LLVM IR → object file → clang link → native executable
```

## License
MIT