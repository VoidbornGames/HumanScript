using HSharp.Syntax;

namespace HSharp.Analysis;

public sealed class RtMethod
{
    public string Name = "";
    public string[] Params = Array.Empty<string>();
    public string Ret = "void";
    public string Doc = "";

    public bool Special;

    public string Display = "";
}

public static class RuntimeApi
{
    private static readonly Dictionary<string, List<RtMethod>> _handles = new();
    private static readonly Dictionary<string, List<RtMethod>> _statics = new();
    private static readonly Dictionary<string, Ty> _tyCache = new();
    private static readonly List<RtMethod> _empty = new();

    private static void Add(Dictionary<string, List<RtMethod>> d, string kind, params RtMethod[] ms) =>
        d[kind] = new List<RtMethod>(ms);

    private static RtMethod M(string name, string ret, string doc = "", params string[] pars) =>
        new() { Name = name, Ret = ret, Params = pars, Doc = doc };

    static RuntimeApi()
    {
        Add(_handles, "listener",
            M("Accept", "Client", "Wait for one connection and return it."),
            M("AcceptTimeout", "Client?", "Wait up to ms milliseconds; null when nobody connected.", "int ms"),
            M("Close", "void", "Stop the listener and free its socket."),
            new RtMethod { Name = "OnAccept", Ret = "void", Special = true, Display = "void OnAccept((packet) => handler)", Doc = "Run the lambda on a pool thread per connection." });

        Add(_handles, "httpl",
            M("Accept", "HttpPacket", "Wait for one request and return it parsed."),
            M("AcceptTimeout", "HttpPacket?", "Wait up to ms milliseconds; null when no request came.", "int ms"),
            M("Close", "void", "Stop the http server and free its socket."),
            new RtMethod { Name = "OnAccept", Ret = "void", Special = true, Display = "void OnAccept((packet) => handler)", Doc = "Run the lambda on a pool thread per connection." });

        Add(_handles, "rawhttpl",
            M("Accept", "RawHttpPacket", "Wait for one request and return it unparsed."),
            M("AcceptTimeout", "RawHttpPacket?", "Wait up to ms milliseconds; null when no request came.", "int ms"),
            M("Close", "void", "Stop the http server and free its socket."),
            new RtMethod { Name = "OnAccept", Ret = "void", Special = true, Display = "void OnAccept((packet) => handler)", Doc = "Run the lambda on a pool thread per connection." });

        Add(_handles, "udp",
            M("SendTo", "int", "Send one datagram; returns bytes sent.", "string host", "int port", "string message"),
            M("Recv", "string", "Wait for one datagram."),
            M("Close", "void", "Free the socket."),
            new RtMethod { Name = "OnAccept", Ret = "void", Special = true, Display = "void OnAccept((packet) => handler)", Doc = "Run the lambda on a pool thread per datagram." });

        Add(_handles, "Client",
            M("Send", "int", "Send a string; returns bytes sent.", "string data"),
            M("Recv", "string", "Read until the peer closes or the buffer fills."),
            M("RecvTimeout", "string?", "Read with a deadline; null on timeout.", "int ms"),
            M("SendBytes", "int", "Send raw bytes; returns bytes sent.", "buffer data"),
            M("RecvBytes", "int", "Read into a buffer; returns bytes read.", "buffer into"),
            M("RecvAll", "buffer", "Read until the peer closes, up to maxSize bytes.", "int maxSize"),
            M("Close", "void", "Free the connection."));

        Add(_handles, "HttpPacket",
            M("Method", "string", "Request method, like GET or POST."),
            M("Path", "string", "Request path, like /index.html."),
            M("Header", "string?", "Read a request header; null when absent.", "string name"),
            M("Header", "void", "Store a response header for Respond to emit.", "string name", "string value"),
            new RtMethod { Name = "Cookies", Ret = "Cookies", Special = true, Doc = "Read and set cookies on this exchange." },
            M("Body", "string", "Request body."),
            M("Source", "string", "host:port the request came from."),
            M("Dest", "string", "host:port the request was sent to."),
            M("Respond", "int", "Send the response and free the packet.", "int status", "string body"),
            M("Forward", "int", "Proxy the request onward; returns the status.", "string host", "int port"),
            M("Close", "void", "Free the packet without responding."));

        Add(_handles, "Cookies",
            M("Get", "string?", "Read a request cookie; null when absent.", "string name"),
            M("Set", "void", "Queue a session cookie with defaults.", "string name", "string value"),
            M("Set", "void", "Queue a cookie with explicit options.", "string name", "string value", "CookieOptions options"));

        Add(_handles, "RawHttpPacket",
            M("Source", "string", "host:port the request came from."),
            M("Dest", "string", "host:port the request was sent to."),
            M("ToHttpPacket", "HttpPacket", "Parse the raw request into an HttpPacket."),
            M("Forward", "int", "Proxy the raw bytes onward; returns the status.", "string host", "int port"),
            M("Close", "void", "Free the packet without responding."));

        Add(_handles, "StringBuilder",
            new RtMethod { Name = "Add", Ret = "void", Special = true, Display = "void Add(string | int | float | buffer value)", Doc = "Append a value." },
            M("ToString", "string", "Materialize the built string."),
            M("Clear", "void", "Empty the builder, keeping the allocation."));

        Add(_handles, "string",
            M("Split", "list<string>", "Cut the string on every separator.", "string separator"),
            M("Contains", "bool", "True when sub appears anywhere.", "string sub"),
            M("StartsWith", "bool", "True when the string begins with prefix.", "string prefix"),
            M("IndexOf", "int", "First index of sub, or -1.", "string sub"),
            M("Substring", "string", "Copy length characters from start.", "int start", "int length"),
            M("Replace", "string", "Copy with every from replaced by to.", "string from", "string to"),
            M("Trim", "string", "Copy without leading and trailing whitespace."),
            M("ToLower", "string", "Lowercased copy."),
            M("ToUpper", "string", "Uppercased copy."),
            M("ToInt", "int", "Parse the whole string as an integer."));

        Add(_statics, "Task",
            new RtMethod { Name = "Run", Ret = "task<T>", Special = true, Display = "task<T> Task.Run((params) => body)", Doc = "Run a lambda as a background task; await collects the result." },
            new RtMethod { Name = "Delay", Ret = "task<void>", Params = new[] { "int ms" }, Display = "task<void> Task.Delay(int ms)", Doc = "A task that completes after ms milliseconds." },
            new RtMethod { Name = "WhenAll", Ret = "list<T>", Special = true, Display = "list<T> Task.WhenAll(list<task<T>> tasks)", Doc = "Await every task in the list, results in order." });

        Add(_statics, "Tcp",
            M("Listen", "listener", "Start a TCP server on port.", "int port"),
            M("Connect", "Client", "Connect to a TCP server.", "string host", "int port"));

        Add(_statics, "Udp",
            M("Open", "udp", "Open a UDP socket."),
            M("Listen", "udp", "Bind a UDP socket to port.", "int port"));

        Add(_statics, "Http",
            M("Listen", "httpl", "HTTP server handing out parsed HttpPackets.", "int port"),
            M("ListenRaw", "rawhttpl", "High-performance listener over raw packets.", "int port"),
            M("Get", "string?", "HTTP GET; null on failure. http:// only, no TLS.", "string url"),
            M("Post", "string?", "HTTP POST body; null on failure.", "string url", "string body"),
            M("Status", "int", "Status code of the last Http.Get/Post."));

        Add(_statics, "StringBuilder",
            M("New", "StringBuilder", "Create a string builder."));

        BuildDerivedViews();
    }

    public static IReadOnlyDictionary<string, List<RtMethod>> Handles => _handles;
    public static IReadOnlyDictionary<string, List<RtMethod>> Statics => _statics;

    private static string[] _staticClasses = Array.Empty<string>();
    public static string[] StaticClasses => _staticClasses;

    public static List<RtMethod> Lookup(string kind, string name) =>
        !_handles.TryGetValue(kind, out var ms) ? _empty : Filter(ms, name);

    public static List<RtMethod> LookupStatic(string cls, string name) =>
        !_statics.TryGetValue(cls, out var ms) ? _empty : Filter(ms, name);

    private static List<RtMethod> Filter(List<RtMethod> ms, string name)
    {
        var hit = ms.Where(x => x.Name == name).ToList();
        return hit.Count > 0 ? hit : _empty;
    }

    public static Ty TyOf(string name)
    {
        if (_tyCache.TryGetValue(name, out var t)) return t;
        if (name.EndsWith("?")) return _tyCache[name] = Ty.NullableOf(TyOf(name[..^1]));
        if (name.StartsWith("list<") && name.EndsWith(">"))
            return _tyCache[name] = Ty.List(TyOf(name[5..^1]));
        if (name.StartsWith("task<") && name.EndsWith(">"))
            return _tyCache[name] = Ty.Task(TyOf(name[5..^1]));
        t = name switch
        {
            "int" => Ty.Int,
            "float" => Ty.Float,
            "bool" => Ty.Bool,
            "string" => Ty.Str,
            "buffer" => Ty.Buffer,
            "void" => Ty.Void,
            _ => _handles.ContainsKey(name) ? Ty.Handle(name) : Ty.Named(name)
        };
        return _tyCache[name] = t;
    }

    public static string ParamTy(string param)
    {
        int sp = param.IndexOf(' ');
        return sp < 0 ? param : param[..sp];
    }

    public static readonly Dictionary<string, string[]> StaticMembers = new();
    public static readonly Dictionary<string, (string Sig, string Doc)[]> StaticSignatures = new();
    public static readonly Dictionary<string, string[]> HandleMembers = new();
    public static readonly Dictionary<string, string[]> HandleSignatures = new();
    public static readonly Dictionary<string, string[]> StringMembers = new();

    public static bool IsStaticClass(string name) => StaticMembers.ContainsKey(name);

    public static readonly string[] ListMembers = { "Add", "Remove", "Clear", "Count", "Contains", "IndexOf", "Sort", "Reverse", "Join" };

    public static readonly Dictionary<string, string> ListSignatures = new()
    {
        ["Add"] = "void Add(value)",
        ["Remove"] = "void Remove(value)",
        ["Clear"] = "void Clear()",
        ["Count"] = "int Count",
        ["Contains"] = "bool Contains(value)",
        ["IndexOf"] = "int IndexOf(value)",
        ["Sort"] = "void Sort()",
        ["Reverse"] = "void Reverse()",
        ["Join"] = "string Join(string separator)   // list<string> only"
    };

    private static void BuildDerivedViews()
    {
        foreach (var (cls, ms) in _statics)
        {
            _staticClasses = _statics.Keys.ToArray();
            StaticMembers[cls] = ms.Select(x => x.Name).ToArray();
            foreach (var x in ms)
                StaticSignatures[$"{cls}.{x.Name}"] = new[] { (SigOf(x, cls), x.Doc) };
        }

        foreach (var (kind, ms) in _handles)
        {
            HandleMembers[kind] = ms.Select(x => x.Name).ToArray();
            foreach (var x in ms)
            {
                string sig = SigOf(x, null);
                HandleSignatures[$"{kind}.{x.Name}"] = ms.Where(y => y.Name == x.Name)
                    .Select(y => SigOf(y, null)).ToArray();
                if (kind == "string") StringMembers[x.Name] = new[] { sig };
            }
        }
    }

    private static string SigOf(RtMethod m, string? cls)
    {
        if (m.Display.Length > 0) return m.Display;
        string name = cls == null ? m.Name : $"{cls}.{m.Name}";
        return $"{m.Ret} {name}({string.Join(", ", m.Params)})";
    }
}

