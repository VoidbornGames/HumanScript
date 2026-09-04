# 8. Tasks and concurrency

H# has real threads, not asyncCOLOR-washed continuations: `Task.Run`
starts a lambda on a worker thread immediately, and `await` blocks the
current flow until the result is there. The syntax feels like C#, the
model is simple to reason about.

## Task.Run and await

```hsharp
var t = Task.Run(() =>
{
    var sum = 0;
    for (var i = 1; i <= 100; i++) { sum += i; }
    return sum;
});

print("doing other work...");
var result = await t;
print(result);        // 5050
```

The lambda's return type becomes the task's type: `task<int>` here.
A lambda with no return gives `task<void>`, which awaits to nothing.

Capturing variables moves them into the task (see
[ownership](04-ownership.md)); the task becomes the owner:

```hsharp
var payload = "data";
var t2 = Task.Run(() =>
{
    print($"task got: {payload}");
});
await t2;
```

## Fire and forget

A task nobody awaits is fine; use the discard target or just call it as
a statement, and give the program a moment if it needs to finish:

```hsharp
Task.Run(() => { print("background"); });
_ = Task.Run(() => { return "nobody reads this"; });

await Task.Delay(50);
print(mem());
```

`Task.Delay(ms)` is a task that completes later, which is also how you
pause:

```hsharp
print("start");
await Task.Delay(500);
print("half a second later");
```

## WhenAll

`Task.WhenAll` takes a list of tasks with results and waits for them
all, returning the results in order:

```hsharp
var tasks = list<task<int>> { };
tasks.Add(Task.Run(() => { return 1; }));
tasks.Add(Task.Run(() => { return 2; }));

var results = Task.WhenAll(tasks);
foreach (var r in results)
{
    print(r);          // 1, then 2
}
```

## lock

Two threads touching the same top-level variable need mutual exclusion.
`lock (variable) { ... }` takes the lock for the statement block. It
keys on the variable itself; do not return, break or continue out of a
lock body.

The classic use is an `OnAccept` server whose handlers share state with
main (handlers see top-level variables by reference):

```hsharp
var hits = 0;
var ln = Tcp.Listen(9002);
ln.OnAccept((Client c) =>
{
    var line = c.Recv();
    lock (hits)
    {
        hits = hits + 1;
    }
    c.Send($"you are number {hits}\n");
    c.Close();
});
```

Note that a `Task.Run` lambda captures plain numbers *by copy*: each
task would get its own `counter`, so the shared-counter pattern belongs
to `OnAccept` handlers, which share by reference.

## OnAccept: dedicated server loops

The network listeners have a special method, `OnAccept`, that installs a
handler and runs a dedicated thread per connection
(see [networking](10-networking-tcp-udp.md)):

```hsharp
var ln = Tcp.Listen(8080);
ln.OnAccept((Client c) =>
{
    var line = c.Recv();
    c.Send($"echo: {line}\n");
    c.Close();
});

while (true)
{
    await Task.Delay(1000);
}
```

Top-level variables the handler touches are shared between the handler
threads and main; guard them with `lock`, exactly like the counter
above.

## Graceful shutdown

`exiting()` turns true after Ctrl+C or SIGTERM. Servers poll it in their
main loop:

```hsharp
while (!exiting())
{
    await Task.Delay(200);
}
print("shutting down cleanly");
```

## Next

[Files and input](09-files-and-io.md): reading, writing, arguments and
the environment.
