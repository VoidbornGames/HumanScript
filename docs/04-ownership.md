# 4. Ownership

This is the tutorial that makes the rest of H# make sense.

## The one rule

Every heap value, a string, a list, an instance of a class, has exactly
**one owner** at any moment. When the owner goes away, the value is freed.
That is the entire memory model: no garbage collector, no reference
counting, no leaks and no double frees, because the compiler proves
ownership before your program ever runs.

The practical tool for watching this is `mem()`: the number of live heap
allocations. A correct program returns to 0.

```hsharp
var s = "owned by s";
print(mem());        // 1: s owns the string
// ... last use of s ...
print(mem());        // 0: the compiler freed it after its last use
```

The free is not "at the end of the scope", it is **after the last use**.
That is why the first `mem()` above can already print 0 if `s` is never
touched again.

## Move: transferring ownership

Assignment moves. After a move the source is gone:

```hsharp
var a = "payload";
var b = a;           // b owns it now
// print(a);         // error: use of moved value 'a'
print(b);
```

Functions take ownership with the `move` keyword on the parameter:

```hsharp
void consume(move string s)
{
    print($"consuming {s}");
}

consume($"literal");     // interpolated strings are real owned values
var m = "mine";
consume(m);              // ownership moves into consume
// print(m);             // error: use of moved value 'm'
```

You cannot accidentally pass something you still need: the compiler
rejects any later use of a moved value.

## copy: duplicating

Strings can be duplicated with `copy()`:

```hsharp
var original = "keep me";
var twin = copy(original);
print(original);     // still valid
print(twin);         // an independent duplicate
print(mem());        // 0, both were freed after their last uses
```

Passing a string without `move` borrows it, so the common pattern is:
borrow to read, `move` to hand over, `copy()` when both sides need it.

## Loops and ownership

Moving inside a loop would leave the second iteration holding a moved
value, so the compiler refuses it:

```hsharp
var msg = "hello";
while (true)
{
    // print(msg);      // error: cannot move 'msg' inside a loop
    break;
}
```

Values declared **inside** the loop body are fine: they are born and die
each iteration.

```hsharp
for (var i = 0; i < 3; i++)
{
    var temp = "x" + string(i);
    print(temp);       // fresh owner each round
}
print(mem());          // 0
```

## Tasks capture by move

A `Task.Run` lambda outlives the frame that starts it, so capturing an
owned variable moves it into the task:

```hsharp
var payload = "work item";
var t = Task.Run(() =>
{
    print(payload);    // moved into the task
});
await t;
// print(payload);     // error: the task owns it now
```

`OnAccept` handlers are different: they run for the lifetime of the
server, so top-level variables they touch are **shared by reference**
between the handler threads and main. Pair that with `lock`
(see [tasks and concurrency](08-tasks-and-concurrency.md)).

## Buffers of nothing: the mem() invariant

Every list, map, string and class instance participates. When a program
ends with `mem()` printing nonzero, something holds memory you did not
plan for, usually a value stored somewhere you forgot. The compiler
still frees it correctly at process exit; the invariant is a design
smell detector, not a safety net.

## What you get

Because ownership is proven at compile time:

- no use-after-free, guaranteed;
- no double free, guaranteed;
- no garbage collector pause, ever;
- `mem()` as a live dashboard of exactly what your program holds.

## Next

[collections](05-collections.md): lists, maps and buffers, all under the
same rules.
