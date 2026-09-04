# 6. Classes, structs and enums

## Classes: reference types with one owner

A class bundles fields and methods. Instances are heap objects with a
single owner, exactly like strings and lists:

```hsharp
class User
{
    string Name;
    int Salary;

    public string Whois()
    {
        return $"{Name}, with salary {Salary}$";
    }
}
```

Create one with an initializer, set every field:

```hsharp
var usr = User { Name: "Bob", Salary: 5000 };
print(usr.Whois());        // Bob, with salary 5000$
print(usr.Salary);         // 5000
usr.Salary = 6000;
```

Methods are declared with `public` when other files that import yours
should see them. Fields are private to the file that declares the class.

Inside a method, a bare field name means `this.field`:

```hsharp
class Counter
{
    int N;

    public void Bump()
    {
        N = N + 1;
    }

    public int Get()
    {
        return N;
    }
}
```

## Move semantics for classes

Assigning a class moves the reference; there is no hidden copy:

```hsharp
var a = Counter { N: 1 };
var b = a;             // moves: b is now the single owner
b.Bump();
// print(a.N);         // error: use of moved value 'a'
print(b.Get());        // 2
```

Passing a class to a function follows the same rules as strings: borrow
by default, `move` to transfer.

## Structs: value types

A struct copies on assignment. Every variable holds its own independent
copy, and a struct cannot contain owned class fields:

```hsharp
struct Point
{
    int X;
    int Y;

    public int Manhattan()
    {
        var ax = X;
        if (ax < 0) { ax = 0 - ax; }
        var ay = Y;
        if (ay < 0) { ay = 0 - ay; }
        return ax + ay;
    }
}

var p1 = Point { X: 3, Y: 4 };
var p2 = p1;          // an independent copy
p2.X = 100;
print(p1.X);          // 3, untouched
print(p1.Manhattan());    // 7
```

Use a struct for small plain values, a class for anything with an
identity or heavy contents.

## The new keyword

You create built-in types (like the HTTP `CookieOptions`) with the `new`
keyword, and it is required there:

```hsharp
var opts = new CookieOptions { Secure: true, HttpOnly: true };
```

For your own classes and structs `new` is optional sugar, both of these
compile to the same thing:

```hsharp
var u1 = User { Name: "a", Salary: 1 };
var u2 = new User { Name: "b", Salary: 2 };
```

Built-in initializers may leave fields out and take the defaults; your
own classes must set every field.

## Enums

Enums are named integer constants. Values auto-increment unless you set
them:

```hsharp
enum Mood { Grumpy, Happy = 5, Sleepy }

var m = Mood.Happy;
print(m);                 // 5

if (m == Mood.Happy)
{
    print("cheerful");
}
```

Enums are their own type: you cannot mix them with plain ints, which
keeps switch-like logic honest.

## Generic methods on classes

Methods can take type parameters, specialized per call site:

```hsharp
class Box
{
    public T Choose<T>(T a, T b)
    {
        return a;
    }
}

var box = Box { };
print(box.Choose<string>("first", "second"));
```

## Everything together

```hsharp
enum Level { Low, High }

class Task2
{
    string Title;
    Level Priority;

    public string Describe()
    {
        var tag = Priority == Level.High ? "!!!" : ".";
        return $"{Title} {tag}";
    }
}

var t = Task2 { Title: "ship", Priority: Level.High };
print(t.Describe());      // ship !!!
print(mem());             // 0
```

## Next

[Nullables and errors](07-nullables-and-errors.md): what happens when
things go missing or go wrong.
