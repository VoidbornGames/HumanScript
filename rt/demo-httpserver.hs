var rawLn = Http.ListenRaw(18123);
var plainLn = Http.Listen(18124);

var rawTask = Task.Run(() =>
{
    var raw = rawLn.Accept();
    print("raw src: " + raw.Source());
    print("raw dst: " + raw.Dest());
    var req = raw.ToHttpPacket();
    print("method: " + req.Method());
    print("path: " + req.Path());
    var host = req.Header("Host");
    print("host: " + (host ?? "none"));
    print("body: " + req.Body());
    req.Respond(200, "raw served\n");
    return "raw done";
});

var plainTask = Task.Run(() =>
{
    var req = plainLn.Accept();
    print("plain method: " + req.Method());
    print("plain path: " + req.Path());
    print("plain src: " + req.Source());
    print("plain body: " + req.Body());
    req.Respond(404, "plain served\n");
    return "plain done";
});

var c1 = Tcp.Connect("127.0.0.1", 18123);
c1.Send("POST /login HTTP/1.0\r\nHost: hsharp.test\r\nContent-Length: 9\r\n\r\nuser=bob\n");
print("r1: " + c1.Recv());
c1.Close();

var c2 = Tcp.Connect("127.0.0.1", 18124);
c2.Send("GET /nope HTTP/1.0\r\nHost: other.test\r\n\r\n");
print("r2: " + c2.Recv());
c2.Close();

var a = await rawTask;
var b = await plainTask;
print(a + ", " + b);
print(mem());
