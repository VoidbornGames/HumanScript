# 11. HTTP servers and clients

## Your first web server

`Http.Listen` binds a port and `OnAccept` hands you a parsed
`HttpPacket` per request:

```hsharp
var ln = Http.Listen(8080);
ln.OnAccept((HttpPacket req) =>
{
    req.Respond(200, $"<h1>hello from {req.Path()}</h1>");
});

while (!exiting())
{
    await Task.Delay(200);
}
```

Run it, then open `http://localhost:8080/hello` in a browser. `Respond`
sends the status and body; bodies are served as `text/html; charset=utf-8`
so markup renders.

## Reading the request

The packet carries everything the client sent:

```hsharp
ln.OnAccept((HttpPacket req) =>
{
    var method = req.Method();         // "GET"
    var path = req.Path();             // "/about"
    var body = req.Body();             // request body as a string
    var agent = req.Header("User-Agent") ?? "unknown";

    req.Respond(200, $"you did a {method} on {path}");
});
```

`Header(name)` reads a request header and returns `string?`:

```hsharp
var host = req.Header("Host") ?? "?";
```

## Response headers

Set response headers with the two-argument form before `Respond`. A
custom `Content-Type` replaces the default:

```hsharp
ln.OnAccept((HttpPacket req) =>
{
    req.Header("X-Powered-By", "H#");
    req.Header("Content-Type", "application/json");
    req.Respond(200, "{\"ok\":true}");
});
```

## Cookies

`req.Cookies` is the cookie facade: `Get` reads the request's Cookie
header, `Set` appends a `Set-Cookie` response header. Options are a
built-in class created with `new`:

```hsharp
ln.OnAccept((HttpPacket req) =>
{
    var sid = req.Cookies.Get("session") ?? "guest";

    var opts = new CookieOptions
    {
        Secure: true,
        HttpOnly: true,
        SameSite: SameSite.Lax,
        MaxAge: 3600
    };
    req.Cookies.Set("session", sid, opts);
    req.Cookies.Set("seen", "yes");     // defaults: Path=/, SameSite=Lax

    req.Respond(200, $"welcome back, {sid}");
});
```

`CookieOptions` fields: `Secure`, `HttpOnly`, `SameSite` (`SameSite.None`,
`SameSite.Lax`, `SameSite.Strict`), `Path`, `Domain`, `MaxAge` (seconds;
below zero means a session cookie). Every `Set` emits its own
`Set-Cookie` header.

## Routing

A tiny router is just a map of paths to bodies:

```hsharp
var pages = map<string, string>
{
    "/": "<h1>home</h1>",
    "/about": "<h1>about</h1>"
};

var ln = Http.Listen(8080);
ln.OnAccept((HttpPacket req) =>
{
    var page = pages[req.Path()] ?? "<h1>404</h1>";
    req.Respond(200, page);
});
```

## HTTP client

`Http.Get` and `Http.Post` talk to other servers. They return `string?`,
null on failure, and `Http.Status` reports the last status code:

```hsharp
var body = Http.Get("http://example.com/");
if (body != null)
{
    print($"got {len(body)} bytes, status {Http.Status()}");
}
else
{
    print($"request failed, status {Http.Status()}");
}

var reply = Http.Post("http://example.com/form", "a=1&b=2");
print(reply ?? "post failed");
```

## High performance: ListenRaw

`Http.ListenRaw` skips request parsing. You get a `RawHttpPacket`, the
raw bytes of the whole exchange, and choose what to do:

```hsharp
var ln = Http.ListenRaw(8081);
ln.OnAccept((RawHttpPacket raw) =>
{
    var req = raw.ToHttpPacket();          // parse only when needed
    if (req.Path() == "/health")
    {
        req.Respond(200, "ok");
    }
    else
    {
        raw.Forward("127.0.0.1", 9000);    // or pass it through untouched
    }
});
```

`Forward(host, port)` ships the raw request to a real backend and pipes
the answer back, which is the core of a reverse proxy or firewall in a
dozen lines. `raw.Source()` and `raw.Dest()` expose the endpoints.

## A complete mini site

```hsharp
var visits = 0;
var ln = Http.Listen(8080);
ln.OnAccept((HttpPacket req) =>
{
    lock (visits)
    {
        visits = visits + 1;
    }
    req.Header("X-Visits", string(visits));
    req.Respond(200, $"<p>visit number {visits}</p>");
});

while (!exiting())
{
    await Task.Delay(200);
}
```

Concurrency, parsing, cookies, headers and memory management are all
handled; this is the level H# web code lives at.

## Next

[Tooling](12-tooling.md): the compiler flags, the editor, and the
development workflow.
