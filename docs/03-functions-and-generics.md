# 3. Functions and generics

## Declaring functions

Functions live at the top level of a file. Return type first, then the
name, then typed parameters:

```hsharp
int add(int a, int b)
{
    return a + b;
}

void greet(string who)
{
    print($"hello {who}");
}

greet("world");
print(add(2, 3));
```

A function with no `return` value is declared `void`. Returning is how a
function hands its result up; there are no out-parameters.

## Strings and ownership

Strings are owned values: exactly one variable is responsible for freeing
each one. When you pass a string to a function normally, the function
*borrow* it, it can read but not keep it:

```hsharp
int length(string s)
{
    return len(s);
}

var name = "ada";
print(length(name));    // fine: borrowed, name still yours
print(name);
```

If a function needs to *keep* the string (store it, return it), the
parameter is marked `move`, and ownership transfers to the callee:

```hsharp
string wrap(move string s)
{
    return "[" + s + "]";
}

print(wrap($"hi"));     // an interpolated string is a real value you own

var msg = "careful";
print(wrap(msg));       // moves msg's ownership into wrap
// print(msg);          // error: msg would be left dangling

var msg2 = "also mine";
print(wrap(copy(msg2)));  // copy() duplicates so msg2 stays yours
print(msg2);
```

One quirk to know: a plain string literal (`"text"`) is a compile-time
constant with no runtime owner, so a `move` parameter takes it as an
interpolated string (`$"text"`) or from a variable instead.

The rule in one line: borrowed to read, `move` to hand over, `copy()`
when you want both. [Ownership](04-ownership.md) covers the whole model.

## Type parameters

Generic functions take type parameters in angle brackets. The compiler
creates a specialized version for every type you use it with, so there is
no runtime boxing and no virtual dispatch:

```hsharp
T pick<T>(T a, T b)
{
    return a;
}

print(pick(1, 2));            // T inferred as int
print(pick("a", "b"));        // T inferred as string
print(pick<int>(3, 4));       // or state it explicitly
```

Inference comes from the arguments; when it cannot decide, name the type
argument yourself. A parameter typed exactly `T` pins that type argument
down:

```hsharp
T firstOf<T>(T a, T b)
{
    return a;
}

// this call is ambiguous, both arguments are int so T = int, fine:
print(firstOf<int>(7, 9));
```

Generic methods on classes work the same way
(see [classes](06-classes-structs-enums.md)).

## Returning values

The return type is checked on every path that returns. A `void` function
returns nothing and cannot be assigned:

```hsharp
string join(int a, int b)
{
    return string(a) + string(b);
}

var text = join(1, 2);
print(text);          // 12
```

## visibility: public

Declarations in a file are private to that file. Mark a function `public`
to let other files that import yours call it:

```hsharp
public string shout(string s)
{
    return s.Upper() + "!";
}
```

Another file does `import mylib.hs;` and calls `shout("hi")`. Everything
about imports, search paths and the import graph lives in the
[tooling tutorial](12-tooling.md).

## Reserved names

You cannot shadow the built-ins (`print`, `len`, `copy`, `mem`, ...) or
`main`. The compiler rejects those names at declaration time.

## Next

[Ownership](04-ownership.md): the model underneath `move` and `copy`, and
why mem() always returns to zero.
