# 1. Getting started

## What you need

- The `hsc` compiler on your PATH (the H# installer does this).
- clang, from LLVM 18 or newer, to link the final executable.
- Windows: Visual Studio Build Tools (for the MSVC libraries clang links
  against). Linux: a normal gcc toolchain environment.

Check everything:

```
hsc
clang --version
```

## Hello world

`hello.hs`:

```hsharp
var name = "world";
print($"hello {name}!");
```

Compile. `-o` chooses the output file:

```
hsc hello.hs -o hello.exe
```

Run it:

```
hello.exe
```

Output:

```
hello world!
```

## Variables

`var` declares a variable and the compiler infers its type from the
initializer. An initializer is required; there is no uninitialized
"later" in H#.

```hsharp
var age = 30;              // int
var pi = 3.14;             // float
var ok = true;             // bool
var name = "Ada";          // string
```

You can also write the type yourself:

```hsharp
int width = 640;
float ratio = 0.5;
string title = "H#";
```

## print and strings

`print` writes one value plus a newline. It accepts ints, floats, bools
and strings:

```hsharp
print(42);
print(1.5);
print(true);
print("done");
```

Strings glue together with `+`, and numbers convert automatically when
joined to a string:

```hsharp
var count = 3;
print("count: " + count);        // count: 3
```

For anything more than gluing, use an interpolated string: start it with
`$`, put expressions inside `{ }`, and escape literal braces by doubling
them:

```hsharp
var user = "ada";
print($"hi {user}, you have {2 + 2} messages");
print($"literal braces look {{like this}}");
```

## The mem() habit

`mem()` returns how many heap allocations are alive right now. A
well-formed H# program starts at 0 and ends at 0, because the compiler
inserts every free for you. Get in the habit of printing it at the end of
`main` while you learn:

```hsharp
var s = "temporary";
print(s);
print(mem());     // 0: s was already freed after its last use
```

If you ever see a nonzero number, [the ownership tutorial](04-ownership.md)
explains what the compiler saw.

## Compile modes

```
hsc app.hs --check          type-check only, no output file
hsc app.hs -o app.exe       name the output
hsc app.hs -I libs          extra folder to search for imports
```

The compiler prints errors as `file(line,col): error: message` and returns
a nonzero exit code, so it fits plain scripts and CI.

## Next

[Language basics](02-language-basics.md): conditions, loops, and the rest
of the expression toolbox.
