# 10. Networking: TCP and UDP

## A TCP echo server

`Tcp.Listen` binds a port and gives a `listener`. `OnAccept` installs a
handler that runs on its own thread for every connection:

```hsharp
var ln = Tcp.Listen(9000);
ln.OnAccept((Client c) =>
{
    var line = c.Recv();
    c.Send($"echo: {line}\n");
    c.Close();
});

print("listening on 9000");
while (!exiting())
{
    await Task.Delay(200);
}
```

`Client` is the connection handle: `Send` writes, `Recv` reads one line,
`Close` releases it.

## A TCP client

`Tcp.Connect` gives a `Client` to talk to a server:

```hsharp
var c = Tcp.Connect("127.0.0.1", 9000);
c.Send("hello\n");
print(c.Recv());        // echo: hello
c.Close();
```

## Timeouts

Blocking forever on a dead peer is a bug, so reads can take a
millisecond timeout and return null when it expires:

```hsharp
var c = Tcp.Connect("example.com", 80);
var reply = c.RecvTimeout(2000);
if (reply == null)
{
    print("no answer in 2s");
}
```

## Raw bytes

For binary protocols, `buffer` is the currency:

```hsharp
var c = Tcp.Connect("127.0.0.1", 9000);
var outBuf = buffer("binary payload");
c.SendBytes(outBuf);

var inBuf = buffer(4096);
var got = c.RecvBytes(inBuf);        // bytes read
print(got);
```

`RecvAll(maxSize)` reads until the connection closes and hands back one
buffer holding everything, which is what HTTP-shaped request/response
flows want:

```hsharp
var all = c.RecvAll(65536);
print(len(all));
```

## UDP

UDP is connectionless. `Udp.Open` makes a socket for sending,
`Udp.Listen` binds one for receiving:

```hsharp
var sock = Udp.Open();
sock.SendTo("127.0.0.1", 9001, "ping");
sock.Close();
```

The receiver gets a handler per datagram, and the payload arrives as a
string parameter:

```hsharp
var udp = Udp.Listen(9001);
udp.OnAccept((string msg) =>
{
    print($"got: {msg}");
});

while (!exiting())
{
    await Task.Delay(200);
}
```

## Concurrent handlers, shared state

`OnAccept` handlers run on their own threads, so state shared with main
is guarded by `lock`:

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

A `map` shared the same way counts per-key traffic safely.

## Stopping cleanly

`Close` on the listener ends its accept loop. Combine with `exiting()`
for a server that shuts down on Ctrl+C:

```hsharp
var ln = Tcp.Listen(9000);
ln.OnAccept((Client c) => { c.Close(); });

while (!exiting())
{
    await Task.Delay(200);
}
ln.Close();
print("server stopped");
```

## Next

[HTTP servers and clients](11-http-web.md): the reason these sockets
exist.
