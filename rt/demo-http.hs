// a tiny HTTP server and client, both in H#.
// run: hsc demo-http.hs && ./demo-http

var ln = Tcp.Listen(18902);
print("http server on 18902");

_ = Task.Run(() =>
{
    for (var i = 0; i < 2; i++)
    {
        var c = ln.Accept();

        // request line: METHOD /path HTTP/1.1
        var req = c.Recv();
        var sp = indexOf(req, " ");
        var method = sub(req, 0, sp);
        var rest = sub(req, sp + 1, len(req) - sp - 1);
        var sp2 = indexOf(rest, " ");
        var path = sub(rest, 0, sp2);

        // drain headers until the empty line
        for (;;)
        {
            var h = c.Recv();
            if (len(h) == 0)
            {
                break;
            }
        }

        var body = "";
        if (path == "/hello")
        {
            body = "hello from h#";
        }
        else
        {
            body = $"you asked for {path}";
        }

        var head = "HTTP/1.1 200 OK\r\n";
        head += "Content-Type: text/plain\r\n";
        head += $"Content-Length: {len(body)}\r\n";
        head += "Connection: close\r\n\r\n";
        c.Send(head + body);
        c.Close();
    }
});

// the client side, plain HTTP/1.1 over Tcp
var sock = Tcp.Connect("127.0.0.1", 18902);
var get = "GET /hello HTTP/1.1\r\n";
get += "Host: localhost\r\n";
get += "Connection: close\r\n\r\n";
sock.Send(get);

// status line, headers, then the body by content-length
var status = sock.Recv();
print(status);

var clen = 0;
for (;;)
{
    var h = sock.Recv();
    if (len(h) == 0)
    {
        break;
    }
    if (startsWith(h, "Content-Length:"))
    {
        clen = parseInt(sub(h, 16, len(h) - 16));
    }
}

// body arrives in raw bytes, not lines: pull it with one more recv per chunk
var got = "";
for (;;)
    {
    var chunk = sock.Recv();
    got = got + chunk;
    if (len(got) >= clen)
    {
        break;
    }
}
print(got);
sock.Close();
print(mem());
