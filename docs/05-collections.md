# 5. Collections

H# has three collection types: `list<T>`, `map<K, V>` and `buffer`. All of
them are owned values under the [ownership model](04-ownership.md).

## Lists

Create with a literal or empty, grow with `Add`:

```hsharp
var names = list<string> { "ada", "grace" };
var nums = list<int> { 1, 2, 3 };
var empty = list<int> { };

names.Add("linus");
print(len(names));        // 3
print(names[0]);          // ada
```

Read an element by index, remove by value, clear everything:

```hsharp
nums[0] = 10;
nums.Remove(2);
nums.Clear();
print(len(nums));         // 0
```

`foreach` walks a list and hands you a fresh element each round:

```hsharp
var total = 0;
foreach (var n in list<int> { 1, 2, 3 })
{
    total += n;
}
print(total);             // 6
```

Lists hold `int` or `string` elements. Lists cannot hold nullable values:
check for null first and store the unwrapped value. A `list<string>` can
also flatten back into one string:

```hsharp
print(names.Join(" & "));     // ada & grace & linus
```

Lists also carry the everyday operations:

```hsharp
names.Contains("ada");        // true
names.IndexOf("grace");       // 1, or -1 when absent
names.Sort();                 // in place, strings and numbers
names.Reverse();              // in place
```

## Maps

`map<K, V>` keys and values are `string` or `int`. A literal maps keys to
values:

```hsharp
var ages = map<string, int> { "ada": 36, "grace": 45 };
print(ages["ada"] ?? -1);        // 36
print(ages["missing"] ?? -1);    // -1: a missing key reads as null
```

Reading returns `V?`: the value, or null when the key is absent. Handle
it with `??` or a null check:

```hsharp
var age = ages["ada"] ?? 0;
if (ages["grace"] != null)
{
    print("found");
}
```

The whole contents come out as lists, so everything lists can do applies:

```hsharp
var keys = ages.Keys;         // list<string>
var vals = ages.Values;       // list<int>
keys.Sort();
print(ages.Contains("ada"));  // true, key lookup
print(ages.Count);
```

Write through the indexer, test and remove by key:

```hsharp
ages["linus"] = 28;
ages["ada"] = 37;
print(ages.Contains("ada"));   // true
ages.Remove("ada");
print(len(ages));              // 1
ages.Clear();
```

A classic counter, safe under [locks](08-tasks-and-concurrency.md):

```hsharp
var hits = map<string, int> { };
var key = "home";
var cur = hits[key] ?? 0;
hits[key] = cur + 1;
print(hits[key] ?? -1);        // 1
```

## Buffers

A `buffer` is raw bytes. Create one sized, or from a string:

```hsharp
var b = buffer(16);            // 16 zeroed bytes
var s = buffer("hello");       // the string's bytes
print(len(s));                 // 5
```

Read and write bytes by index (each slot is an int):

```hsharp
b[0] = 72;
b[1] = 73;
print(b[0]);                   // 72
print(string(b));              // the bytes back as a string
```

Buffers are how binary data crosses the network: see
[networking](10-networking-tcp-udp.md) for `SendBytes` / `RecvBytes` /
`RecvAll`.

## Choosing between them

| You have | You want | Use |
| --- | --- | --- |
| ordered items by position | index access, foreach | `list<T>` |
| lookup by key | count, contains, update | `map<K, V>` |
| raw bytes | sockets, encodings | `buffer` |

All three follow the [ownership model](04-ownership.md): one owner, moves
transfer, and `mem()` returns to zero when you are done with them.

## Next

[Classes, structs and enums](06-classes-structs-enums.md): your own types.
