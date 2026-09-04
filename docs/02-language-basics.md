# 2. Language basics

## Arithmetic and comparison

The usual operators, with `+` also joining strings:

```hsharp
print(7 / 2);          // 3, int division
print(7 / 2.0);        // 3.5
print(7 % 2);          // 1
print(2 + 3 * 4);      // 14

print(1 < 2);
print("a" == "a");
print("a" != "b");
```

Strings only support `==` and `!=` for direct comparison; for anything
deeper there are string methods, covered below.

Increment and compound assignment work on variables:

```hsharp
var n = 0;
n = n + 1;
n += 5;
n++;
```

## if and else

Braces are required. Conditions must be a real bool: `if (1)` is an error,
H# has no truthy integers.

```hsharp
var temperature = 31;

if (temperature > 30)
{
    print("hot");
}
else if (temperature > 20)
{
    print("nice");
}
else
{
    print("cold");
}
```

Combine conditions with `&&` and `||`, negate with `!`.

## while and for

```hsharp
var i = 0;
while (i < 3)
{
    print(i);
    i++;
}

for (var j = 0; j < 3; j++)
{
    print(j);
}
```

`break` leaves the loop, `continue` skips to the next round:

```hsharp
for (var k = 0; k < 10; k++)
{
    if (k == 3) { break; }
    if (k % 2 == 0) { continue; }
    print(k);          // 1
}
```

## foreach

`foreach` walks a `list<string>` or `list<int>` and hands you a fresh
element each round (see [collections](05-collections.md)):

```hsharp
var names = list<string> { "ada", "grace", "linus" };
foreach (var name in names)
{
    print(name);
}
```

## The ternary operator

`cond ? a : b` picks between two values. Both branches must have the same
type:

```hsharp
var age = 20;
var label = age >= 18 ? "adult" : "minor";
print(label);
```

## switch

`switch` picks one value from many cases. Every case compares the switch
value with `==`, and `default` catches everything left:

```hsharp
var code = 2;
var name = switch (code) { case 1: "one", case 2: "two", default: "many" };
print(name);
```

The switch value and all case values must be comparable with `==` (numbers,
strings, enums), and every branch must produce the same type. Keep the
switch expression itself free of side effects.

## ?? and ?.

These belong to nullable values and get a full tutorial of their own
([nullables and errors](07-nullables-and-errors.md)), but you will meet
them early. `??` supplies a fallback for a value that might be absent:

```hsharp
var home = env("HOME") ?? "unknown";
print(home);
```

`env(name)` returns `string?`: a string, or nothing. The type system
forces you to handle the nothing case before you can use the value.

## Conversions

Convert explicitly with the type-name-as-function form:

```hsharp
var raw = "42";
var n = int(raw);              // string to int
var f = float(7);              // int to float
var text = string(3.5);        // float to string
var data = buffer("bytes");    // string to byte buffer
```

## String methods

Strings carry their tools as methods, C# style:

```hsharp
var s = "Hello,World,H#";

var parts = s.Split(",");           // list<string>: "Hello", "World", "H#"
print(s.Contains("World"));         // true
print(s.StartsWith("Hell"));        // true
print(s.IndexOf("World"));          // 7, or -1 when absent
print(s.Substring(7, 5));           // World
print(s.Replace(",", " | "));
print(s.Trim());
print(s.ToLower());
print(s.ToUpper());

var n = "42".ToInt();               // parse straight into an int
```

Lists of strings join back into one string:

```hsharp
var names = list<string> { "ada", "grace" };
print(names.Join(" & "));           // ada & grace
```

## Comments

```hsharp
// a line comment

/* a block comment
   spanning lines */
```

## A small program

Everything so far in one piece:

```hsharp
var total = 0;
for (var i = 1; i <= 10; i++)
{
    total += i;
}

var parity = total % 2 == 0 ? "even" : "odd";
print($"sum 1..10 = {total}, which is {parity}");
print(mem());
```

## Next

[Functions and generics](03-functions-and-generics.md): packaging logic,
and types that adapt.
