# 9. Files and input

## Reading and writing files

`read` slurps a whole file; `write` replaces it. Both take strings:

```hsharp
write("notes.txt", "first line\nsecond line\n");
var text = read("notes.txt");
print(len(text));      // 28, byte count
```

A missing file does not crash the program: it raises a catchable error.

```hsharp
string? contents = null;
try
{
    contents = read("config.txt");
}
catch (e)
{
    print($"no config ({e}), using defaults");
}

var body = contents ?? "";
print($"loaded {len(body)} bytes");
```

## existence and deletion

```hsharp
if (exists("data.txt"))
{
    print("found it");
    delete("data.txt");
    print("gone");
}
```

## Command line arguments

`args()` returns the program's arguments as a `list<string>`; element
zero is the first argument, not the program name:

```hsharp
// greet.hs
var who = "world";
var all = args();
if (len(all) > 0)
{
    who = all[0];
}
print($"hello {who}");
```

```
hsc greet.hs -o greet.exe
greet.exe ada        -> hello ada
```

## Environment variables

`env(name)` returns `string?`, null when the variable is unset. Coalesce
a default:

```hsharp
var port = env("PORT") ?? "8080";
print($"serving on {port}");
var n = int(port);
```

## Building a tiny counter app

Everything from this tutorial in one program:

```hsharp
var file = "counter.txt";
var current = 0;
if (exists(file))
{
    current = int(read(file));
}

current += 1;
write(file, string(current));
print($"this program has run {current} times");
print(mem());
```

## Prompting the user

`input` writes a prompt and reads a line from stdin:

```hsharp
var name = input("your name: ");
print($"hi {name}");
```

## Graceful exit

Long-running programs should notice Ctrl+C. `exiting()` becomes true
after the signal; poll it:

```hsharp
while (!exiting())
{
    await Task.Delay(200);
}
write("state.txt", "saved on exit");
print("bye");
```

## Next

[Networking: TCP and UDP](10-networking-tcp-udp.md): real sockets.
