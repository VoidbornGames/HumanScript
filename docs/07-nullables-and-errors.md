# 7. Nullables and errors

H# has no exceptions flying around by default and no null dereference
crashes. Absence is part of the type system, and runtime failures are
caught explicitly.

## Nullable types

Add `?` to a type to say the value might be absent:

```hsharp
string? maybe = env("HOME");     // env returns string?
int? nothing = null;
```

The compiler will not let you use a nullable value without proving it is
there:

```hsharp
var name = env("USER");
// print(len(name));              // error: possibly null
```

## Narrowing with a null check

An `if` against null narrows the type inside the branch:

```hsharp
if (name != null)
{
    print(len(name));            // name is a plain string here
}
```

## ?? the fallback operator

`??` supplies a default for the null case and is the idiomatic form:

```hsharp
var home = env("HOME") ?? "unknown";
var count = maybeCount ?? 0;
```

Chaining works, and the result type of `a ?? b` is the non-nullable
inner type.

## ?. the null-conditional operator

`?.` calls a method or reads a member only when the receiver is there,
and gives null otherwise:

```hsharp
string? header = req.Header("Authorization");
var token = header?.Trim();
```

Map reads are the everyday source of nullables:

```hsharp
var ages = map<string, int> { "ada": 36 };
var age = ages["ada"] ?? 0;      // reads are V?, so coalesce
```

## try and catch

Runtime failures, a missing file, division by zero, an out-of-bounds
index, do not crash the program. They jump to the nearest `catch`, and
`catch (e)` binds the error message as a string:

```hsharp
try
{
    var text = read("config.txt");
    print(text);
}
catch (e)
{
    print($"could not read config: {e}");
}
```

Everything owned on the try path is still freed correctly when the jump
happens; `mem()` stays at zero.

The parentheses are optional: `catch { ... }` works when you do not need
the message.

## lastError for built-ins

Some built-ins record a numeric error flag instead of failing loudly.
`lastError()` reads it:

```hsharp
var n = parseInt("not a number");
if (lastError() != 0)
{
    print("that was not a number");
}
```

## Putting it together

A command line tool that survives a missing file:

```hsharp
var all = args();
var path = len(all) > 0 ? all[0] : "config.txt";
var text = "";
try
{
    text = read(path);
}
catch (e)
{
    print($"no {path}, starting empty: {e}");
}

var hits = map<string, int> { };
var key = "runs";
hits[key] = (hits[key] ?? 0) + 1;
print($"loaded {len(text)} bytes, runs: {hits[key]}");
print(mem());
```

## Next

[Tasks and concurrency](08-tasks-and-concurrency.md): doing several
things at once, safely.
