using System.Runtime.InteropServices;
using HSharp.Analysis;
using HSharp.Syntax;
using Index = HSharp.Syntax.Index;

namespace HSharp.CodeGen;

enum Prov { Static, Borrow, Var, Temp }

sealed class VarSlot
{
    public string Name = "";
    public IntPtr Ptr, Flag;
    public Ty Ty = Ty.Int;
    public bool Owned, Borrow;

    public bool ByRef;
}

readonly struct Val
{
    public readonly IntPtr V;
    public readonly Ty Ty;
    public readonly Prov Prov;
    public readonly VarSlot? Src;

    public Val(IntPtr v, Ty ty, Prov prov = Prov.Static, VarSlot? src = null)
    {
        V = v;
        Ty = ty;
        Prov = prov;
        Src = src;
    }
}

// lowers the checked AST to LLVM IR. every heap value carries a drop flag
// and a live-allocation counter so a well-formed program returns mem() to 0
public sealed class CodeGen
{

    private IntPtr _ctx, _module, _b, _ab;
    private IntPtr _i1, _i8, _i32, _i64, _double, _i8ptr, _void;
    private IntPtr _listTy;

    private readonly Dictionary<string, IntPtr> _optTys = new();

    private readonly Dictionary<string, IntPtr> _userTys = new();
    private readonly Dictionary<string, TypeDecl> _typeDecls = new();
    private readonly Dictionary<string, IntPtr> _cloneFns = new();
    private readonly Dictionary<string, IntPtr> _cloneTys = new();
    private readonly Dictionary<string, IntPtr> _udropFns = new();
    private readonly Dictionary<string, IntPtr> _udropTys = new();

    private IntPtr _listCopyStrFn, _listCopyStrTy;
    private IntPtr _listCopyPodFn, _listCopyPodTy;
    private IntPtr _entryBB, _curFn, _errFlag;

    private IntPtr _printfFn, _printfTy;
    private IntPtr _mallocFn, _mallocTy;
    private IntPtr _reallocFn, _reallocTy;
    private IntPtr _freeFn, _freeTy;
    private IntPtr _strlenFn, _strlenTy;
    private IntPtr _strcmpFn, _strcmpTy;
    private IntPtr _sprintfFn, _sprintfTy;
    private IntPtr _memcpyFn, _memcpyTy;
    private IntPtr _fopenFn, _fopenTy;
    private IntPtr _fcloseFn, _fcloseTy;
    private IntPtr _fputsFn, _fputsTy;
    private IntPtr _freadFn, _freadTy;
    private IntPtr _fseekFn, _fseekTy;
    private IntPtr _ftellFn, _ftellTy;
    private IntPtr _rewindFn, _rewindTy;
    private IntPtr _removeFn, _removeTy;
    private IntPtr _getcharFn, _getcharTy;
    private IntPtr _strcatFn, _strcatTy;
    private IntPtr _atoiFn, _atoiTy;
    private IntPtr _strncmpFn, _strncmpTy;
    private IntPtr _strstrFn, _strstrTy;
    private IntPtr _atofFn, _atofTy;

    private IntPtr _tcpListenFn, _tcpListenTy;
    private IntPtr _tcpAcceptFn, _tcpAcceptTy;
    private IntPtr _tcpConnectFn, _tcpConnectTy;
    private IntPtr _tcpSendFn, _tcpSendTy;
    private IntPtr _tcpCloseFn, _tcpCloseTy;
    private IntPtr _udpOpenFn, _udpOpenTy;
    private IntPtr _udpListenFn, _udpListenTy;
    private IntPtr _udpSendToFn, _udpSendToTy;
    private IntPtr _tcpLineFn, _tcpLineTy;
    private IntPtr _udpRecvFn, _udpRecvTy;
    private IntPtr _httpGetFn, _httpGetTy;
    private IntPtr _httpPostFn, _httpPostTy;
    private IntPtr _httpStatusFn, _httpStatusTy;
    private IntPtr _httpAcceptFn, _httpAcceptTy;
    private IntPtr _httpToPacketFn, _httpToPacketTy;
    private IntPtr _httpMethodFn, _httpMethodTy;
    private IntPtr _httpPathFn, _httpPathTy;
    private IntPtr _httpHeaderFn, _httpHeaderTy;
    private IntPtr _httpSetHeaderFn, _httpSetHeaderTy;
    private IntPtr _httpCookiesFn, _httpCookiesTy;
    private IntPtr _httpCookieGetFn, _httpCookieGetTy;
    private IntPtr _httpCookieSetFn, _httpCookieSetTy;
    private IntPtr _httpCookieSetDefFn, _httpCookieSetDefTy;
    private IntPtr _httpBodyFn, _httpBodyTy;
    private IntPtr _httpSourceFn, _httpSourceTy;
    private IntPtr _httpDestFn, _httpDestTy;
    private IntPtr _httpRespondFn, _httpRespondTy;
    private IntPtr _httpReqCloseFn, _httpReqCloseTy;
    private IntPtr _httpAcceptToFn, _httpAcceptToTy;
    private IntPtr _httpForwardFn, _httpForwardTy;
    private IntPtr _clockFn, _clockTy;
    private IntPtr _lineTimeoutFn, _lineTimeoutTy;
    private IntPtr _setArgsFn, _setArgsTy;
    private IntPtr _argsCountFn, _argsCountTy;
    private IntPtr _argsGetFn, _argsGetTy;
    private IntPtr _envFn, _envTy;
    private IntPtr _splitFn, _splitTy;
    private IntPtr _joinFn, _joinTy;
    private IntPtr _replaceFn, _replaceTy;
    private IntPtr _trimFn, _trimTy;
    private IntPtr _caseFoldFn, _caseFoldTy;

    private IntPtr _bufNewFn, _bufNewTy;
    private IntPtr _bufLenFn, _bufLenTy;
    private IntPtr _bufGetFn, _bufGetTy;
    private IntPtr _bufSetFn, _bufSetTy;
    private IntPtr _bufDropFn, _bufDropTy;
    private IntPtr _bufFromStrFn, _bufFromStrTy;
    private IntPtr _bufToStrFn, _bufToStrTy;
    private IntPtr _recvBytesFn, _recvBytesTy;
    private IntPtr _recvAllFn, _recvAllTy;
    private IntPtr _sendBytesFn, _sendBytesTy;
    private IntPtr _sbNewFn, _sbNewTy;
    private IntPtr _sbAddStrFn, _sbAddStrTy;
    private IntPtr _sbAddIntFn, _sbAddIntTy;
    private IntPtr _sbAddFloatFn, _sbAddFloatTy;
    private IntPtr _sbAddBufFn, _sbAddBufTy;
    private IntPtr _sbStrFn, _sbStrTy;
    private IntPtr _sbDropFn, _sbDropTy;
    private IntPtr _exitingFn, _exitingTy;
    private IntPtr _lockAcqFn, _lockAcqTy;
    private IntPtr _lockRelFn, _lockRelTy;
    private IntPtr _taskDelayFn, _taskDelayTy;
    private IntPtr _spawnFn, _spawnTy;
    private IntPtr _errorSetFn, _errorSetTy;
    private IntPtr _errorGetFn, _errorGetTy;
    private IntPtr _mapNewFn, _mapNewTy;
    private IntPtr _mapInsertFn, _mapInsertTy;
    private IntPtr _mapGetFn, _mapGetTy;
    private IntPtr _mapContainsFn, _mapContainsTy;
    private IntPtr _mapRemoveFn, _mapRemoveTy;
    private IntPtr _mapCountFn, _mapCountTy;
    private IntPtr _mapClearFn, _mapClearTy;
    private IntPtr _mapDropFn, _mapDropTy;
    private IntPtr _mapItemsFn, _mapItemsTy;

    private IntPtr _unixtimeFn, _unixtimeTy;
    private IntPtr _fmttimeFn, _fmttimeTy;
    private IntPtr _fmtFloatFn, _fmtFloatTy;

    private IntPtr _hsInc, _hsIncTy, _hsDec, _hsDecTy;
    private IntPtr _hsLive;

    private IntPtr _rtInitFn, _rtInitTy;
    private IntPtr _rtLiveIncFn, _rtLiveIncTy;
    private IntPtr _rtLiveDecFn, _rtLiveDecTy;
    private IntPtr _rtLiveGetFn, _rtLiveGetTy;
    private IntPtr _rtTaskNewFn, _rtTaskNewTy;
    private IntPtr _rtTaskSubmitFn, _rtTaskSubmitTy;
    private IntPtr _rtTaskForgetFn, _rtTaskForgetTy;
    private IntPtr _rtTaskJoinFn, _rtTaskJoinTy;
    private IntPtr _listNewFn, _listNewTy;
    private IntPtr _listAddFn, _listAddTy;
    private IntPtr _listGetFn, _listGetTy;
    private IntPtr _listSetStrFn, _listSetStrTy;
    private IntPtr _listSetIntFn, _listSetIntTy;
    private IntPtr _listDropStrFn, _listDropStrTy;
    private IntPtr _listDropPodFn, _listDropPodTy;
    private IntPtr _listClearStrFn, _listClearStrTy;
    private IntPtr _listClearPodFn, _listClearPodTy;
    private IntPtr _listRemoveStrFn, _listRemoveStrTy;
    private IntPtr _listRemoveIntFn, _listRemoveIntTy;
    private IntPtr _listIndexFn, _listIndexTy;
    private IntPtr _listSortFn, _listSortTy;
    private IntPtr _listReverseFn, _listReverseTy;
    private IntPtr _listSizeFn, _listSizeTy;
    private IntPtr _strdupFn, _strdupTy;
    private IntPtr _concatFn, _concatTy;
    private IntPtr _itoaFn, _itoaTy;
    private IntPtr _ftoaFn, _ftoaTy;
    private IntPtr _readFn, _readTy;
    private IntPtr _writeFn, _writeTy;
    private IntPtr _existsFn, _existsTy;
    private IntPtr _inputFn, _inputTy;

    private int _tmp;

    private string T(string p) => $"{p}{_tmp++}";

    private List<Dictionary<string, VarSlot>> _scopes = new();
    private List<VarSlot> _fnSlots = new();

    private List<(IntPtr V, Ty Ty)> _temps = new();

    private IntPtr? _catchBB;
    private readonly Dictionary<string, (IntPtr fn, IntPtr ty, IntPtr entry, IntPtr body, FnDecl decl, TypeDecl? owner)> _fns = new();
    private FnDecl? _curDecl;

    private List<(IntPtr brk, IntPtr cont)> _loopExit = new();

    private TypeDecl? _curType;

    private Dictionary<string, Ty>? _subst;

    private readonly Dictionary<string, Dictionary<string, Ty>> _fnSubsts = new();

    private Ty CTy(Ty ty)
    {
        if (_subst == null || _subst.Count == 0) return ty;
        if (ty.Nullable)
        {
            var inner = CTy(ty.Elem!);
            return inner == ty.Elem ? ty : Ty.NullableOf(inner);
        }
        if (ty.Elem != null && !Ty.IsTask(ty))
        {
            var e2 = CTy(ty.Elem!);
            return e2 == ty.Elem ? ty : Ty.List(e2);
        }
        if (Ty.IsUser(ty) && ty.Kind == UserKind.None && _subst!.TryGetValue(ty.Name, out var c))
            return c;
        return ty;
    }

    private static string Mangle(string name, List<Ty> tys) =>
        tys.Count == 0 ? name : $"{name}.{string.Join(".", tys.Select(t => t.Name))}";

    private static Dictionary<string, Ty>? SubstOf(FnDecl decl, List<Ty>? inst)
    {
        if (inst == null || inst.Count == 0) return null;
        var s = new Dictionary<string, Ty>();
        for (int i = 0; i < inst.Count; i++) s[decl.TPs[i]] = inst[i];
        return s;
    }

    private static Ty Apply(Ty ty, Dictionary<string, Ty>? s)
    {
        if (s == null) return ty;
        if (ty.Nullable)
        {
            var inner = Apply(ty.Elem!, s);
            return inner == ty.Elem ? ty : Ty.NullableOf(inner);
        }
        if (ty.Elem != null && !Ty.IsTask(ty))
        {
            var e2 = Apply(ty.Elem!, s);
            return e2 == ty.Elem ? ty : Ty.List(e2);
        }
        if (Ty.IsUser(ty) && ty.Kind == UserKind.None && s.TryGetValue(ty.Name, out var c))
            return c;
        return ty;
    }

    private sealed class LamInfo
    {
        public IntPtr Fn, EnvTy;
        public long EnvSize;
        public List<VarSlot> Captures = new();
        public List<bool> ByRefs = new();
        public Ty Ret = Ty.Void;
        public Param? Param;
    }

    private readonly Dictionary<LamLit, LamInfo> _lambdas = new(ReferenceEqualityComparer.Instance);

    private Ty? _lamRetTy;
    private IntPtr _lamEnvParam;

    private HashSet<string> _sharedTopNames = new();

    private bool _sharedCaptures;

    public void Generate(AstProgram program, string objPath) =>
        Generate(program, objPath, LLVM.PtrToStringAndFree(LLVM.LLVMGetDefaultTargetTriple()));

    public void Generate(AstProgram program, string objPath, string targetTriple)
    {
        _ctx = LLVM.LLVMContextCreate();
        _module = LLVM.LLVMModuleCreateWithNameInContext("hsharp", _ctx);
        _b = LLVM.LLVMCreateBuilderInContext(_ctx);
        _ab = LLVM.LLVMCreateBuilderInContext(_ctx);

        _i1 = LLVM.LLVMInt1TypeInContext(_ctx);
        _i8 = LLVM.LLVMInt8TypeInContext(_ctx);
        _i32 = LLVM.LLVMInt32TypeInContext(_ctx);
        _i64 = LLVM.LLVMInt64TypeInContext(_ctx);
        _double = LLVM.LLVMDoubleTypeInContext(_ctx);
        _void = LLVM.LLVMVoidTypeInContext(_ctx);
        _i8ptr = LLVM.LLVMPointerType(_i8, 0);

        _listTy = LLVM.LLVMStructCreateNamed(_ctx, "hs.list");
        LLVM.LLVMStructSetBody(_listTy, new[] { _i8ptr, _i32, _i32 }, 3, false);

        DeclareExterns();
        EmitPrelude();
        RegisterTypes(program);

        foreach (var s in program.Stmts)
        {
            if (s is FnDecl f)
            {
                foreach (var tys in InstancesOf(program, f))
                    CreateUserFn(f, Mangle(f.Name, tys), null, tys);
            }
            else if (s is TypeDecl td)
                foreach (var m in td.Methods)
                    foreach (var tys in InstancesOf(program, m))
                        CreateUserFn(m, Mangle($"{td.Name}.{m.Name}", tys), td, tys);
        }

        EmitMain(program.Stmts.Where(s => s is not (FnDecl or TypeDecl or EnumDecl)).ToList(), SharedTopLevelNames(program));

        foreach (var s in program.Stmts)
        {
            if (s is FnDecl f)
            {
                foreach (var tys in InstancesOf(program, f))
                    EmitFnBody(f, Mangle(f.Name, tys));
            }
            else if (s is TypeDecl td)
                foreach (var m in td.Methods)
                    foreach (var tys in InstancesOf(program, m))
                        EmitFnBody(m, Mangle($"{td.Name}.{m.Name}", tys));
        }

        if (LLVM.LLVMVerifyModule(_module, 2, out var err) != 0)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hs-broken.ir"),
                LLVM.PtrToStringAndFree(LLVM.LLVMPrintModuleToString(_module)));
            throw new Exception("generated IR failed verification: " + LLVM.PtrToStringAndFree(err));
        }

        if (Environment.GetEnvironmentVariable("HS_DUMP_IR") == "1")
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "hs-dump.ir"),
                LLVM.PtrToStringAndFree(LLVM.LLVMPrintModuleToString(_module)));

        EmitObjectFile(objPath, targetTriple);
    }

    private void DeclareExterns()
    {
        Ext("printf", _i32, new[] { _i8ptr }, true, out _printfFn, out _printfTy);
        Ext("malloc", _i8ptr, new[] { _i64 }, false, out _mallocFn, out _mallocTy);
        Ext("realloc", _i8ptr, new[] { _i8ptr, _i64 }, false, out _reallocFn, out _reallocTy);
        Ext("free", _void, new[] { _i8ptr }, false, out _freeFn, out _freeTy);
        Ext("strlen", _i64, new[] { _i8ptr }, false, out _strlenFn, out _strlenTy);
        Ext("strcmp", _i32, new[] { _i8ptr, _i8ptr }, false, out _strcmpFn, out _strcmpTy);
        Ext("sprintf", _i32, new[] { _i8ptr, _i8ptr }, true, out _sprintfFn, out _sprintfTy);
        Ext("memcpy", _i8ptr, new[] { _i8ptr, _i8ptr, _i64 }, false, out _memcpyFn, out _memcpyTy);
        Ext("strcat", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _strcatFn, out _strcatTy);
        Ext("fopen", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _fopenFn, out _fopenTy);
        Ext("fclose", _i32, new[] { _i8ptr }, false, out _fcloseFn, out _fcloseTy);
        Ext("fputs", _i32, new[] { _i8ptr, _i8ptr }, false, out _fputsFn, out _fputsTy);
        Ext("fread", _i64, new[] { _i8ptr, _i64, _i64, _i8ptr }, false, out _freadFn, out _freadTy);
        Ext("fseek", _i32, new[] { _i8ptr, _i64, _i32 }, false, out _fseekFn, out _fseekTy);
        Ext("ftell", _i64, new[] { _i8ptr }, false, out _ftellFn, out _ftellTy);
        Ext("rewind", _void, new[] { _i8ptr }, false, out _rewindFn, out _rewindTy);
        Ext("remove", _i32, new[] { _i8ptr }, false, out _removeFn, out _removeTy);
        Ext("getchar", _i32, Array.Empty<IntPtr>(), false, out _getcharFn, out _getcharTy);
        Ext("atoi", _i32, new[] { _i8ptr }, false, out _atoiFn, out _atoiTy);
        Ext("strncmp", _i32, new[] { _i8ptr, _i8ptr, _i64 }, false, out _strncmpFn, out _strncmpTy);
        Ext("strstr", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _strstrFn, out _strstrTy);
        Ext("atof", _double, new[] { _i8ptr }, false, out _atofFn, out _atofTy);

        Ext("rt_tcp_listen", _i64, new[] { _i32 }, false, out _tcpListenFn, out _tcpListenTy);
        Ext("rt_tcp_accept", _i64, new[] { _i64 }, false, out _tcpAcceptFn, out _tcpAcceptTy);
        Ext("rt_tcp_connect", _i64, new[] { _i8ptr, _i32 }, false, out _tcpConnectFn, out _tcpConnectTy);
        Ext("rt_tcp_send", _i64, new[] { _i64, _i8ptr, _i64 }, false, out _tcpSendFn, out _tcpSendTy);
        Ext("rt_tcp_close", _void, new[] { _i64 }, false, out _tcpCloseFn, out _tcpCloseTy);
        Ext("rt_udp_open", _i64, Array.Empty<IntPtr>(), false, out _udpOpenFn, out _udpOpenTy);
        Ext("rt_udp_listen", _i64, new[] { _i32 }, false, out _udpListenFn, out _udpListenTy);
        Ext("rt_udp_sendto", _i64, new[] { _i64, _i8ptr, _i32, _i8ptr, _i64 }, false, out _udpSendToFn, out _udpSendToTy);
        Ext("rt_tcp_line", _i8ptr, new[] { _i64 }, false, out _tcpLineFn, out _tcpLineTy);
        Ext("rt_udp_recv", _i8ptr, new[] { _i64 }, false, out _udpRecvFn, out _udpRecvTy);

        Ext("rt_http_get", _i8ptr, new[] { _i8ptr }, false, out _httpGetFn, out _httpGetTy);
        Ext("rt_http_post", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _httpPostFn, out _httpPostTy);
        Ext("rt_http_last_status", _i32, Array.Empty<IntPtr>(), false, out _httpStatusFn, out _httpStatusTy);

        Ext("rt_http_accept", _i64, new[] { _i64, _i32 }, false, out _httpAcceptFn, out _httpAcceptTy);
        Ext("rt_http_to_packet", _i64, new[] { _i64 }, false, out _httpToPacketFn, out _httpToPacketTy);
        Ext("rt_http_method", _i8ptr, new[] { _i64 }, false, out _httpMethodFn, out _httpMethodTy);
        Ext("rt_http_path", _i8ptr, new[] { _i64 }, false, out _httpPathFn, out _httpPathTy);
        Ext("rt_http_header", _i8ptr, new[] { _i64, _i8ptr }, false, out _httpHeaderFn, out _httpHeaderTy);
        Ext("rt_http_set_header", _i64, new[] { _i64, _i8ptr, _i8ptr }, false, out _httpSetHeaderFn, out _httpSetHeaderTy);
        Ext("rt_http_cookies", _i64, new[] { _i64 }, false, out _httpCookiesFn, out _httpCookiesTy);
        Ext("rt_http_cookie_get", _i8ptr, new[] { _i64, _i8ptr }, false, out _httpCookieGetFn, out _httpCookieGetTy);
        Ext("rt_http_cookie_set", _i64, new[] { _i64, _i8ptr, _i8ptr, _i8, _i8, _i32, _i8ptr, _i8ptr, _i32 }, false, out _httpCookieSetFn, out _httpCookieSetTy);
        Ext("rt_http_cookie_setdef", _i64, new[] { _i64, _i8ptr, _i8ptr }, false, out _httpCookieSetDefFn, out _httpCookieSetDefTy);
        Ext("rt_http_body", _i8ptr, new[] { _i64 }, false, out _httpBodyFn, out _httpBodyTy);
        Ext("rt_http_source", _i8ptr, new[] { _i64 }, false, out _httpSourceFn, out _httpSourceTy);
        Ext("rt_http_dest", _i8ptr, new[] { _i64 }, false, out _httpDestFn, out _httpDestTy);
        Ext("rt_http_respond", _i64, new[] { _i64, _i32, _i8ptr }, false, out _httpRespondFn, out _httpRespondTy);
        Ext("rt_http_req_close", _void, new[] { _i64 }, false, out _httpReqCloseFn, out _httpReqCloseTy);
        Ext("rt_http_accept_timeout", _i64, new[] { _i64, _i32, _i32 }, false, out _httpAcceptToFn, out _httpAcceptToTy);
        Ext("rt_http_forward", _i64, new[] { _i64, _i8ptr, _i32 }, false, out _httpForwardFn, out _httpForwardTy);

        Ext("rt_clock_ms", _i64, Array.Empty<IntPtr>(), false, out _clockFn, out _clockTy);
        Ext("rt_unixtime", _i64, Array.Empty<IntPtr>(), false, out _unixtimeFn, out _unixtimeTy);
        Ext("rt_fmttime", _i8ptr, new[] { _i64, _i8ptr }, false, out _fmttimeFn, out _fmttimeTy);
        Ext("rt_format_float", _i8ptr, new[] { _double, _i32 }, false, out _fmtFloatFn, out _fmtFloatTy);
        Ext("rt_map_items", _i8ptr, new[] { _i64, _i32, _i32 }, false, out _mapItemsFn, out _mapItemsTy);
        Ext("rt_tcp_line_timeout", _i8ptr, new[] { _i64, _i32 }, false, out _lineTimeoutFn, out _lineTimeoutTy);
        Ext("rt_set_args", _void, new[] { _i32, _i8ptr }, false, out _setArgsFn, out _setArgsTy);
        Ext("rt_args_count", _i64, Array.Empty<IntPtr>(), false, out _argsCountFn, out _argsCountTy);
        Ext("rt_args_get", _i8ptr, new[] { _i64 }, false, out _argsGetFn, out _argsGetTy);
        Ext("rt_env", _i8ptr, new[] { _i8ptr }, false, out _envFn, out _envTy);
        Ext("rt_split", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _splitFn, out _splitTy);
        Ext("rt_join", _i8ptr, new[] { _i64, _i8ptr }, false, out _joinFn, out _joinTy);
        Ext("rt_replace", _i8ptr, new[] { _i8ptr, _i8ptr, _i8ptr }, false, out _replaceFn, out _replaceTy);
        Ext("rt_trim", _i8ptr, new[] { _i8ptr }, false, out _trimFn, out _trimTy);
        Ext("rt_case_fold", _i8ptr, new[] { _i8ptr, _i32 }, false, out _caseFoldFn, out _caseFoldTy);

        Ext("rt_buf_new", _i8ptr, new[] { _i64 }, false, out _bufNewFn, out _bufNewTy);
        Ext("rt_buf_len", _i64, new[] { _i64 }, false, out _bufLenFn, out _bufLenTy);
        Ext("rt_buf_get", _i64, new[] { _i64, _i64, _i8ptr }, false, out _bufGetFn, out _bufGetTy);
        Ext("rt_buf_set", _void, new[] { _i64, _i64, _i64, _i8ptr }, false, out _bufSetFn, out _bufSetTy);
        Ext("rt_buf_drop", _void, new[] { _i64 }, false, out _bufDropFn, out _bufDropTy);
        Ext("rt_buf_from_str", _i8ptr, new[] { _i8ptr }, false, out _bufFromStrFn, out _bufFromStrTy);
        Ext("rt_buf_to_str", _i8ptr, new[] { _i64 }, false, out _bufToStrFn, out _bufToStrTy);
        Ext("rt_recv_bytes", _i64, new[] { _i64, _i64 }, false, out _recvBytesFn, out _recvBytesTy);
        Ext("rt_recv_all", _i8ptr, new[] { _i64, _i64 }, false, out _recvAllFn, out _recvAllTy);
        Ext("rt_send_bytes", _i64, new[] { _i64, _i64 }, false, out _sendBytesFn, out _sendBytesTy);

        Ext("rt_sb_new", _i8ptr, Array.Empty<IntPtr>(), false, out _sbNewFn, out _sbNewTy);
        Ext("rt_sb_add_str", _void, new[] { _i8ptr, _i8ptr }, false, out _sbAddStrFn, out _sbAddStrTy);
        Ext("rt_sb_add_int", _void, new[] { _i8ptr, _i64 }, false, out _sbAddIntFn, out _sbAddIntTy);
        Ext("rt_sb_add_float", _void, new[] { _i8ptr, _double }, false, out _sbAddFloatFn, out _sbAddFloatTy);
        Ext("rt_sb_add_buf", _void, new[] { _i8ptr, _i64 }, false, out _sbAddBufFn, out _sbAddBufTy);
        Ext("rt_sb_str", _i8ptr, new[] { _i8ptr }, false, out _sbStrFn, out _sbStrTy);
        Ext("rt_sb_drop", _void, new[] { _i8ptr }, false, out _sbDropFn, out _sbDropTy);

        Ext("rt_exiting", _i32, Array.Empty<IntPtr>(), false, out _exitingFn, out _exitingTy);
        Ext("rt_lock_acquire", _void, new[] { _i8ptr }, false, out _lockAcqFn, out _lockAcqTy);
        Ext("rt_lock_release", _void, new[] { _i8ptr }, false, out _lockRelFn, out _lockRelTy);
        Ext("rt_task_delay", _i8ptr, new[] { _i32 }, false, out _taskDelayFn, out _taskDelayTy);

        Ext("rt_spawn", _void, new[] { _i8ptr, _i8ptr }, false, out _spawnFn, out _spawnTy);
        Ext("rt_error_set", _void, new[] { _i8ptr }, false, out _errorSetFn, out _errorSetTy);
        Ext("rt_error_get", _i8ptr, Array.Empty<IntPtr>(), false, out _errorGetFn, out _errorGetTy);

        Ext("rt_map_new", _i8ptr, new[] { _i32, _i32 }, false, out _mapNewFn, out _mapNewTy);
        Ext("rt_map_insert", _void, new[] { _i64, _i64, _i64 }, false, out _mapInsertFn, out _mapInsertTy);
        Ext("rt_map_get", _i64, new[] { _i64, _i64, _i8ptr }, false, out _mapGetFn, out _mapGetTy);
        Ext("rt_map_contains", _i32, new[] { _i64, _i64 }, false, out _mapContainsFn, out _mapContainsTy);
        Ext("rt_map_remove", _void, new[] { _i64, _i64 }, false, out _mapRemoveFn, out _mapRemoveTy);
        Ext("rt_map_count", _i64, new[] { _i64 }, false, out _mapCountFn, out _mapCountTy);
        Ext("rt_map_clear", _void, new[] { _i64 }, false, out _mapClearFn, out _mapClearTy);
        Ext("rt_map_drop", _void, new[] { _i64 }, false, out _mapDropFn, out _mapDropTy);
    }

    private void Ext(string name, IntPtr ret, IntPtr[] ps, bool varArg, out IntPtr fn, out IntPtr ty)
    {
        ty = LLVM.LLVMFunctionType(ret, ps, (uint)ps.Length, varArg);
        fn = LLVM.LLVMAddFunction(_module, name, ty);
    }

    private void Fn(string name, IntPtr ret, IntPtr[] ps, bool varArg, out IntPtr fn, out IntPtr ty)
    {
        Ext(name, ret, ps, varArg, out fn, out ty);
        _curFn = fn;
        LLVM.LLVMPositionBuilderAtEnd(_b, LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "entry"));
    }

    private void EmitPrelude()
    {
        Ext("rt_init", _void, Array.Empty<IntPtr>(), false, out _rtInitFn, out _rtInitTy);
        Ext("rt_live_inc", _void, Array.Empty<IntPtr>(), false, out _rtLiveIncFn, out _rtLiveIncTy);
        Ext("rt_live_dec", _void, Array.Empty<IntPtr>(), false, out _rtLiveDecFn, out _rtLiveDecTy);
        Ext("rt_live_get", _i64, Array.Empty<IntPtr>(), false, out _rtLiveGetFn, out _rtLiveGetTy);
        Ext("rt_task_new", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _rtTaskNewFn, out _rtTaskNewTy);
        Ext("rt_task_submit", _void, new[] { _i8ptr }, false, out _rtTaskSubmitFn, out _rtTaskSubmitTy);
        Ext("rt_task_forget", _void, new[] { _i8ptr, _i32 }, false, out _rtTaskForgetFn, out _rtTaskForgetTy);
        Ext("rt_task_join", _i8ptr, new[] { _i8ptr }, false, out _rtTaskJoinFn, out _rtTaskJoinTy);

        Fn("hs_inc", _void, Array.Empty<IntPtr>(), false, out _hsInc, out _hsIncTy);
        {
            CallV(_rtLiveIncTy, _rtLiveIncFn, Array.Empty<IntPtr>());
            RetVoid();
        }

        Fn("hs_dec", _void, Array.Empty<IntPtr>(), false, out _hsDec, out _hsDecTy);
        {
            CallV(_rtLiveDecTy, _rtLiveDecFn, Array.Empty<IntPtr>());
            RetVoid();
        }
        _hsLive = IntPtr.Zero;

        PreludeStrdup();
        PreludeConcat();
        PreludeItoa();
        PreludeFtoa();
        PreludeListNew();
        PreludeListSize();
        PreludeListAdd();
        PreludeListGet();
        PreludeListSetStr();
        PreludeListSetInt();
        PreludeListClearStr();
        PreludeListClearPod();
        PreludeListRemoveStr();
        PreludeListRemoveInt();
        PreludeListIndex();
        PreludeListSort();
        PreludeListReverse();
        PreludeListDropStr();
        PreludeListDropPod();
        PreludeListCopyStr();
        PreludeListCopyPod();
        PreludeRead();
        PreludeWrite();
        PreludeExists();
        PreludeInput();
    }

    private IntPtr Block(string name) => LLVM.LLVMAppendBasicBlockInContext(_ctx, _curFn, name);
    private void At(IntPtr bb) => LLVM.LLVMPositionBuilderAtEnd(_b, bb);

    private IntPtr ConstI64(long v) => LLVM.LLVMConstInt(_i64, unchecked((ulong)v), true);
    private IntPtr ConstI32(int v) => LLVM.LLVMConstInt(_i32, unchecked((uint)v), true);
    private IntPtr ConstI8(int v) => LLVM.LLVMConstInt(_i8, unchecked((uint)v), true);
    private IntPtr ConstBool(bool v) => LLVM.LLVMConstInt(_i32, v ? 1u : 0u, false);
    private IntPtr ConstF(double v) => LLVM.LLVMConstReal(_double, v);
    private IntPtr Str(string s) => LLVM.LLVMBuildGlobalStringPtr(_b, s, T("s"));
    private IntPtr Null() => LLVM.LLVMConstPointerNull(_i8ptr);

    private IntPtr Add(IntPtr a, IntPtr b) => LLVM.LLVMBuildAdd(_b, a, b, T("add"));
    private IntPtr Sub(IntPtr a, IntPtr b) => LLVM.LLVMBuildSub(_b, a, b, T("sub"));
    private IntPtr Mul(IntPtr a, IntPtr b) => LLVM.LLVMBuildMul(_b, a, b, T("mul"));
    private IntPtr Call(IntPtr ty, IntPtr fn, IntPtr[] args) => LLVM.LLVMBuildCall2(_b, ty, fn, args, (uint)args.Length, T("call"));
    private void CallV(IntPtr ty, IntPtr fn, IntPtr[] args) => LLVM.LLVMBuildCall2(_b, ty, fn, args, (uint)args.Length, "");
    private IntPtr Load(IntPtr ty, IntPtr p) => LLVM.LLVMBuildLoad2(_b, ty, p, T("ld"));
    private void Store(IntPtr v, IntPtr p) => LLVM.LLVMBuildStore(_b, v, p);
    private IntPtr ICmp(LLVM.LLVMIntPredicate p, IntPtr a, IntPtr b) => LLVM.LLVMBuildICmp(_b, p, a, b, T("icmp"));
    private IntPtr FCmp(LLVM.LLVMRealPredicate p, IntPtr a, IntPtr b) => LLVM.LLVMBuildFCmp(_b, p, a, b, T("fcmp"));
    private IntPtr ZExt(IntPtr v, IntPtr ty) => LLVM.LLVMBuildZExt(_b, v, ty, T("zext"));
    private IntPtr Trunc(IntPtr v, IntPtr ty) => LLVM.LLVMBuildTrunc(_b, v, ty, T("trunc"));
    private IntPtr SExt(IntPtr v, IntPtr ty) => LLVM.LLVMBuildSExt(_b, v, ty, T("sext"));
    private IntPtr SIToFP(IntPtr v) => LLVM.LLVMBuildSIToFP(_b, v, _double, T("tof"));
    private IntPtr Select(IntPtr c, IntPtr a, IntPtr b) => LLVM.LLVMBuildSelect(_b, c, a, b, T("sel"));
    private IntPtr PtrToInt64(IntPtr p) => LLVM.LLVMBuildPtrToInt(_b, p, _i64, T("p2i"));
    private IntPtr Int64ToPtr(IntPtr v) => LLVM.LLVMBuildIntToPtr(_b, v, _i8ptr, T("i2p"));
    private IntPtr GepElem(IntPtr data, IntPtr idx64) => LLVM.LLVMBuildInBoundsGEP2(_b, _i64, data, new[] { idx64 }, 1, T("gep"));
    private IntPtr GepByte(IntPtr p, IntPtr idx64) => LLVM.LLVMBuildInBoundsGEP2(_b, _i8, p, new[] { idx64 }, 1, T("gep"));
    private IntPtr Field(IntPtr l, uint idx) => LLVM.LLVMBuildStructGEP2(_b, _listTy, l, idx, T("fld"));

    private IntPtr ListData(IntPtr l) => Load(_i8ptr, Field(l, 0));
    private IntPtr ListSize(IntPtr l) => Load(_i32, Field(l, 1));
    private IntPtr ListCap(IntPtr l) => Load(_i32, Field(l, 2));

    private IntPtr Br(IntPtr bb) => LLVM.LLVMBuildBr(_b, bb);
    private IntPtr CondBr(IntPtr c, IntPtr t, IntPtr f) => LLVM.LLVMBuildCondBr(_b, c, t, f);
    private IntPtr Ret(IntPtr v) => LLVM.LLVMBuildRet(_b, v);
    private void RetVoid() => LLVM.LLVMBuildRetVoid(_b);

    private void PreludeStrdup()
    {
        Fn("hs_strdup", _i8ptr, new[] { _i8ptr }, false, out _strdupFn, out _strdupTy);

        var s = LLVM.LLVMGetParam(_curFn, 0);

        var len = Call(_strlenTy, _strlenFn, new[] { s });
        var n = Add(len, ConstI64(1));
        var buf = Call(_mallocTy, _mallocFn, new[] { n });

        CallV(_memcpyTy, _memcpyFn, new[] { buf, s, n });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(buf);
    }

    private void PreludeConcat()
    {
        Fn("hs_concat", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _concatFn, out _concatTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var r = LLVM.LLVMGetParam(_curFn, 1);

        var ll = Call(_strlenTy, _strlenFn, new[] { l });
        var rl = Call(_strlenTy, _strlenFn, new[] { r });
        var total = Add(Add(ll, rl), ConstI64(1));
        var buf = Call(_mallocTy, _mallocFn, new[] { total });

        CallV(_memcpyTy, _memcpyFn, new[] { buf, l, Add(ll, ConstI64(1)) });
        CallV(_memcpyTy, _memcpyFn, new[] { GepByte(buf, ll), r, Add(rl, ConstI64(1)) });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(buf);
    }

    private void PreludeItoa()
    {
        Fn("hs_itoa", _i8ptr, new[] { _i32 }, false, out _itoaFn, out _itoaTy);

        var v = LLVM.LLVMGetParam(_curFn, 0);
        var buf = Call(_mallocTy, _mallocFn, new[] { ConstI64(16) });

        CallV(_sprintfTy, _sprintfFn, new[] { buf, Str("%d"), v });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(buf);
    }

    private void PreludeFtoa()
    {
        Fn("hs_ftoa", _i8ptr, new[] { _double }, false, out _ftoaFn, out _ftoaTy);

        var v = LLVM.LLVMGetParam(_curFn, 0);
        var buf = Call(_mallocTy, _mallocFn, new[] { ConstI64(32) });

        CallV(_sprintfTy, _sprintfFn, new[] { buf, Str("%g"), v });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(buf);
    }

    private void PreludeListNew()
    {
        Fn("hs_list_new", _i8ptr, new[] { _i32 }, false, out _listNewFn, out _listNewTy);

        var want = LLVM.LLVMGetParam(_curFn, 0);

        var small = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, want, ConstI32(1));
        var cap = Select(small, ConstI32(8), want);
        var l = Call(_mallocTy, _mallocFn, new[] { ConstI64(32) });
        var bytes = Mul(ZExt(cap, _i64), ConstI64(8));
        var data = Call(_mallocTy, _mallocFn, new[] { bytes });

        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Store(data, Field(l, 0));
        Store(ConstI32(0), Field(l, 1));
        Store(cap, Field(l, 2));

        Ret(l);
    }

    private void PreludeListSize()
    {
        Fn("hs_list_size", _i32, new[] { _i8ptr }, false, out _listSizeFn, out _listSizeTy);
        Ret(ListSize(LLVM.LLVMGetParam(_curFn, 0)));
    }

    private void PreludeListAdd()
    {
        Fn("hs_list_add", _void, new[] { _i8ptr, _i64 }, false, out _listAddFn, out _listAddTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var v = LLVM.LLVMGetParam(_curFn, 1);

        var sizeF = Field(l, 1);
        var size = Load(_i32, sizeF);
        var cap = ListCap(l);
        var need = ICmp(LLVM.LLVMIntPredicate.LLVMIntSGE, size, cap);

        var grow = Block("grow");
        var add = Block("add");

        CondBr(need, grow, add);

        At(grow);
        var ncap = Mul(cap, ConstI32(2));
        var bytes = Mul(ZExt(ncap, _i64), ConstI64(8));
        var nd = Call(_reallocTy, _reallocFn, new[] { ListData(l), bytes });

        Store(ncap, Field(l, 2));
        Store(nd, Field(l, 0));
        Br(add);

        At(add);
        var data = ListData(l);

        Store(v, GepElem(data, ZExt(size, _i64)));
        Store(Add(size, ConstI32(1)), sizeF);

        RetVoid();
    }

    private void PreludeListGet()
    {
        Fn("hs_list_get", _i64, new[] { _i8ptr, _i32, _i8ptr }, false, out _listGetFn, out _listGetTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var i = LLVM.LLVMGetParam(_curFn, 1);
        var err = LLVM.LLVMGetParam(_curFn, 2);

        var size = ListSize(l);
        var neg = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, ConstI32(0));
        var big = ICmp(LLVM.LLVMIntPredicate.LLVMIntSGE, i, size);
        var oob = LLVM.LLVMBuildOr(_b, neg, big, T("oob"));

        var ok = Block("ok");
        var fail = Block("fail");

        CondBr(oob, fail, ok);

        At(ok);
        var data = ListData(l);
        var v = Load(_i64, GepElem(data, ZExt(i, _i64)));

        Ret(v);

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("list index out of bounds") });
        Store(ConstI32(1), err);
        Ret(ConstI64(0));
    }

    private void PreludeListSetStr()
    {
        Fn("hs_list_set_str", _void, new[] { _i8ptr, _i32, _i64, _i8ptr }, false, out _listSetStrFn, out _listSetStrTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var i = LLVM.LLVMGetParam(_curFn, 1);
        var v = LLVM.LLVMGetParam(_curFn, 2);
        var err = LLVM.LLVMGetParam(_curFn, 3);

        BoundsCheck(l, i, err, out var ok, out var fail);

        At(ok);
        var slot = GepElem(ListData(l), ZExt(i, _i64));
        var old = Load(_i64, slot);

        CallV(_freeTy, _freeFn, new[] { Int64ToPtr(old) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());

        Store(v, slot);
        RetVoid();

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("list index out of bounds") });
        RetVoid();
    }

    private void PreludeListSetInt()
    {
        Fn("hs_list_set_int", _void, new[] { _i8ptr, _i32, _i64, _i8ptr }, false, out _listSetIntFn, out _listSetIntTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var i = LLVM.LLVMGetParam(_curFn, 1);
        var v = LLVM.LLVMGetParam(_curFn, 2);
        var err = LLVM.LLVMGetParam(_curFn, 3);

        BoundsCheck(l, i, err, out var ok, out var fail);

        At(ok);
        Store(v, GepElem(ListData(l), ZExt(i, _i64)));
        RetVoid();

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("list index out of bounds") });
        RetVoid();
    }

    private void BoundsCheck(IntPtr l, IntPtr i, IntPtr err, out IntPtr ok, out IntPtr fail)
    {
        var size = ListSize(l);
        var neg = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, ConstI32(0));
        var big = ICmp(LLVM.LLVMIntPredicate.LLVMIntSGE, i, size);
        var oob = LLVM.LLVMBuildOr(_b, neg, big, T("oob"));

        ok = Block("ok");
        fail = Block("fail");

        CondBr(oob, fail, ok);
    }

    private void PreludeListClearStr()
    {
        Fn("hs_list_clear_str", _void, new[] { _i8ptr }, false, out _listClearStrFn, out _listClearStrTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        Store(ConstI32(0), iPtr);

        var loop = Block("loop");
        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var size = ListSize(l);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);

        var body = Block("body");
        var done = Block("done");

        CondBr(more, body, done);

        At(body);
        var slot = GepElem(ListData(l), ZExt(i, _i64));

        CallV(_freeTy, _freeFn, new[] { Int64ToPtr(Load(_i64, slot)) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loop);

        At(done);
        Store(ConstI32(0), Field(l, 1));
        RetVoid();
    }

    private void PreludeListClearPod()
    {
        Fn("hs_list_clear_pod", _void, new[] { _i8ptr }, false, out _listClearPodFn, out _listClearPodTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        Store(ConstI32(0), Field(l, 1));
        RetVoid();
    }

    private void PreludeListRemoveStr()
    {
        Fn("hs_list_remove_str", _i32, new[] { _i8ptr, _i8ptr, _i8ptr }, false, out _listRemoveStrFn, out _listRemoveStrTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var val = LLVM.LLVMGetParam(_curFn, 1);

        EmitListRemove(l,
            i =>
            {
                var slot = GepElem(ListData(l), ZExt(i, _i64));
                CallV(_freeTy, _freeFn, new[] { Int64ToPtr(Load(_i64, slot)) });
                CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
            },
            elem => ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ,
                Call(_strcmpTy, _strcmpFn, new[] { Int64ToPtr(elem), val }), ConstI32(0)));
    }

    private void PreludeListRemoveInt()
    {
        Fn("hs_list_remove_int", _i32, new[] { _i8ptr, _i64, _i8ptr }, false, out _listRemoveIntFn, out _listRemoveIntTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var val = LLVM.LLVMGetParam(_curFn, 1);

        EmitListRemove(l, _ => { }, elem => ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, elem, val));
    }

    // first index of a matching element, -1 when absent; strElem picks
    // strcmp over raw i64 equality
    private void PreludeListIndex()
    {
        Fn("hs_list_index", _i64, new[] { _i8ptr, _i64, _i32 }, false, out _listIndexFn, out _listIndexTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var val = LLVM.LLVMGetParam(_curFn, 1);
        var strElem = LLVM.LLVMGetParam(_curFn, 2);

        var iPtr = LLVM.LLVMBuildAlloca(_b, _i64, "i");
        Store(ConstI64(0), iPtr);

        var cond = Block("cond");
        Br(cond);

        At(cond);
        var i = Load(_i64, iPtr);
        var size = ZExt(ListSize(l), _i64);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);
        var body = Block("body");
        var miss = Block("miss");
        var hit = Block("hit");
        var step = Block("step");
        CondBr(more, body, miss);

        At(body);
        var slot = GepElem(ListData(l), i);
        var elem = Load(_i64, slot);
        var strPath = Block("str_path");
        var eqPath = Block("eq_path");
        var isStr = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, strElem, ConstI32(0));
        CondBr(isStr, strPath, eqPath);

        At(strPath);
        var c = Call(_strcmpTy, _strcmpFn, new[] { Int64ToPtr(elem), Int64ToPtr(val) });
        var strEq = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, c, ConstI32(0));
        CondBr(strEq, hit, step);

        At(eqPath);
        var eq = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, elem, val);
        CondBr(eq, hit, step);

        At(hit);
        Ret(i);

        At(step);
        Store(Add(i, ConstI64(1)), iPtr);
        Br(cond);

        At(miss);
        Ret(ConstI64(-1));
    }

    // qsort with one of two comparators picked at runtime; the comparators
    // deref the 8-byte element slots themselves
    private void PreludeListSort()
    {
        Fn("hs_list_sort", _void, new[] { _i8ptr, _i32 }, false, out _listSortFn, out _listSortTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var strElem = LLVM.LLVMGetParam(_curFn, 1);

        var entryBB = LLVM.LLVMGetInsertBlock(_b);
        var savedFn = _curFn;

        var cmpTy = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i8ptr }, 2, false);
        var cmpPtrTy = LLVM.LLVMPointerType(cmpTy, 0);
        var cmpStr = LLVM.LLVMAddFunction(_module, "hs_list_cmp_str", cmpTy);
        LLVM.LLVMPositionBuilderAtEnd(_b, LLVM.LLVMAppendBasicBlockInContext(_ctx, cmpStr, "entry"));
        var sa = Load(_i8ptr, LLVM.LLVMGetParam(cmpStr, 0));
        var sb = Load(_i8ptr, LLVM.LLVMGetParam(cmpStr, 1));
        Ret(Call(_strcmpTy, _strcmpFn, new[] { sa, sb }));

        var cmpPod = LLVM.LLVMAddFunction(_module, "hs_list_cmp_pod", cmpTy);
        LLVM.LLVMPositionBuilderAtEnd(_b, LLVM.LLVMAppendBasicBlockInContext(_ctx, cmpPod, "entry"));
        var pa = Load(_i64, LLVM.LLVMGetParam(cmpPod, 0));
        var pb = Load(_i64, LLVM.LLVMGetParam(cmpPod, 1));
        var lt = ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, pa, pb), _i32);
        var gt = ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntSGT, pa, pb), _i32);
        Ret(LLVM.LLVMBuildSub(_b, gt, lt, T("cmp")));

        _curFn = savedFn;
        LLVM.LLVMPositionBuilderAtEnd(_b, entryBB);

        Ext("qsort", _void, new[] { _i8ptr, _i64, _i64, cmpPtrTy }, false, out var qsortFn, out var qsortTy);
        var isStr = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, strElem, ConstI32(0));
        var cmp = LLVM.LLVMBuildSelect(_b, isStr, cmpStr, cmpPod, T("cmp"));
        CallV(qsortTy, qsortFn, new[] { ListData(l), ZExt(ListSize(l), _i64), ConstI64(8), cmp });
        RetVoid();
    }

    private void PreludeListReverse()
    {
        Fn("hs_list_reverse", _void, new[] { _i8ptr }, false, out _listReverseFn, out _listReverseTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var iPtr = LLVM.LLVMBuildAlloca(_b, _i64, "i");
        var jPtr = LLVM.LLVMBuildAlloca(_b, _i64, "j");
        Store(ConstI64(0), iPtr);
        Store(LLVM.LLVMBuildSub(_b, ZExt(ListSize(l), _i64), ConstI64(1), T("last")), jPtr);

        var cond = Block("cond");
        Br(cond);

        At(cond);
        var i = Load(_i64, iPtr);
        var j = Load(_i64, jPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, j);
        var swap = Block("swap");
        var done = Block("done");
        CondBr(more, swap, done);

        At(swap);
        var aSlot = GepElem(ListData(l), i);
        var bSlot = GepElem(ListData(l), j);
        var av = Load(_i64, aSlot);
        var bv = Load(_i64, bSlot);
        Store(bv, aSlot);
        Store(av, bSlot);
        Store(Add(i, ConstI64(1)), iPtr);
        Store(LLVM.LLVMBuildSub(_b, j, ConstI64(1), T("dec")), jPtr);
        Br(cond);

        At(done);
        RetVoid();
    }

    private void EmitListRemove(IntPtr l, Action<IntPtr> freeElem, Func<IntPtr, IntPtr> match)
    {
        var sizeF = Field(l, 1);
        var size = Load(_i32, sizeF);

        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        var jPtr = LLVM.LLVMBuildAlloca(_b, _i32, "j");
        Store(ConstI32(0), iPtr);

        var loop = Block("scan");
        var hit = Block("hit");
        var next = Block("next");
        var shiftCond = Block("shift_cond");
        var shiftBody = Block("shift_body");
        var shrink = Block("shrink");
        var miss = Block("miss");

        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);
        CondBr(more, hit, miss);

        At(hit);
        var slot = GepElem(ListData(l), ZExt(i, _i64));
        CondBr(match(Load(_i64, slot)), shrink, next);

        At(shrink);
        freeElem(i);
        Store(Add(i, ConstI32(1)), jPtr);
        Br(shiftCond);

        At(shiftCond);
        var j = Load(_i32, jPtr);
        var moreShift = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, j, size);
        var done = Block("done");
        CondBr(moreShift, shiftBody, done);

        At(shiftBody);
        var dst = GepElem(ListData(l), ZExt(Sub(j, ConstI32(1)), _i64));
        var src = GepElem(ListData(l), ZExt(j, _i64));
        Store(Load(_i64, src), dst);
        Store(Add(j, ConstI32(1)), jPtr);
        Br(shiftCond);

        At(done);
        Store(Sub(size, ConstI32(1)), sizeF);
        Ret(ConstI32(1));

        At(next);
        Store(Add(Load(_i32, iPtr), ConstI32(1)), iPtr);
        Br(loop);

        At(miss);
        Ret(ConstI32(0));
    }

    private void PreludeListDropStr()
    {
        Fn("hs_list_drop_str", _void, new[] { _i8ptr }, false, out _listDropStrFn, out _listDropStrTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        Store(ConstI32(0), iPtr);

        var loop = Block("loop");
        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, ListSize(l));

        var body = Block("body");
        var done = Block("done");

        CondBr(more, body, done);

        At(body);
        var slot = GepElem(ListData(l), ZExt(i, _i64));

        CallV(_freeTy, _freeFn, new[] { Int64ToPtr(Load(_i64, slot)) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loop);

        At(done);

        CallV(_freeTy, _freeFn, new[] { ListData(l) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        CallV(_freeTy, _freeFn, new[] { l });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        RetVoid();
    }

    private void PreludeListDropPod()
    {
        Fn("hs_list_drop_pod", _void, new[] { _i8ptr }, false, out _listDropPodFn, out _listDropPodTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);

        CallV(_freeTy, _freeFn, new[] { ListData(l) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        CallV(_freeTy, _freeFn, new[] { l });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());

        RetVoid();
    }

    private void PreludeListCopyStr()
    {
        Fn("hs_list_copy_str", _i8ptr, new[] { _i8ptr }, false, out _listCopyStrFn, out _listCopyStrTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var size = ListSize(l);
        var nl = Call(_listNewTy, _listNewFn, new[] { size });

        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        var errLocal = LLVM.LLVMBuildAlloca(_b, _i32, "err");
        Store(ConstI32(0), iPtr);
        Store(ConstI32(0), errLocal);

        var loop = Block("loop");
        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);

        var body = Block("body");
        var done = Block("done");

        CondBr(more, body, done);

        At(body);
        var raw = Call(_listGetTy, _listGetFn, new[] { l, i, errLocal });
        var dup = Call(_strdupTy, _strdupFn, new[] { Int64ToPtr(raw) });
        CallV(_listAddTy, _listAddFn, new[] { nl, PtrToInt64(dup) });
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loop);

        At(done);
        Ret(nl);
    }

    private void PreludeListCopyPod()
    {
        Fn("hs_list_copy_pod", _i8ptr, new[] { _i8ptr }, false, out _listCopyPodFn, out _listCopyPodTy);

        var l = LLVM.LLVMGetParam(_curFn, 0);
        var size = ListSize(l);
        var nl = Call(_listNewTy, _listNewFn, new[] { size });

        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        var errLocal = LLVM.LLVMBuildAlloca(_b, _i32, "err");
        Store(ConstI32(0), iPtr);
        Store(ConstI32(0), errLocal);

        var loop = Block("loop");
        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);

        var body = Block("body");
        var done = Block("done");

        CondBr(more, body, done);

        At(body);
        var raw = Call(_listGetTy, _listGetFn, new[] { l, i, errLocal });
        CallV(_listAddTy, _listAddFn, new[] { nl, raw });
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loop);

        At(done);
        Ret(nl);
    }

    private void PreludeRead()
    {
        Fn("hs_read", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _readFn, out _readTy);

        var path = LLVM.LLVMGetParam(_curFn, 0);
        var err = LLVM.LLVMGetParam(_curFn, 1);

        var f = Call(_fopenTy, _fopenFn, new[] { path, Str("rb") });
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, f, Null());

        var ok = Block("ok");
        var fail = Block("fail");

        CondBr(isNull, fail, ok);

        At(ok);
        CallV(_fseekTy, _fseekFn, new[] { f, ConstI64(0), ConstI32(2) });
        var size = Call(_ftellTy, _ftellFn, new[] { f });
        CallV(_rewindTy, _rewindFn, new[] { f });

        var buf = Call(_mallocTy, _mallocFn, new[] { Add(size, ConstI64(1)) });
        CallV(_freadTy, _freadFn, new[] { buf, ConstI64(1), size, f });
        Store(ConstI8(0), GepByte(buf, size));
        CallV(_fcloseTy, _fcloseFn, new[] { f });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(buf);

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("could not read file") });
        Store(ConstI32(1), err);
        var empty = Call(_mallocTy, _mallocFn, new[] { ConstI64(1) });
        Store(ConstI8(0), empty);
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Ret(empty);
    }

    private void PreludeWrite()
    {
        Fn("hs_write", _void, new[] { _i8ptr, _i8ptr, _i8ptr }, false, out _writeFn, out _writeTy);

        var path = LLVM.LLVMGetParam(_curFn, 0);
        var content = LLVM.LLVMGetParam(_curFn, 1);
        var err = LLVM.LLVMGetParam(_curFn, 2);

        var f = Call(_fopenTy, _fopenFn, new[] { path, Str("w") });
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, f, Null());

        var ok = Block("ok");
        var fail = Block("fail");

        CondBr(isNull, fail, ok);

        At(ok);
        CallV(_fputsTy, _fputsFn, new[] { content, f });
        CallV(_fcloseTy, _fcloseFn, new[] { f });
        RetVoid();

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("could not write file") });
        Store(ConstI32(1), err);
        RetVoid();
    }

    private void PreludeExists()
    {
        Fn("hs_exists", _i32, new[] { _i8ptr }, false, out _existsFn, out _existsTy);

        var path = LLVM.LLVMGetParam(_curFn, 0);
        var f = Call(_fopenTy, _fopenFn, new[] { path, Str("r") });
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, f, Null());

        var ok = Block("ok");
        var fail = Block("fail");

        CondBr(isNull, fail, ok);

        At(ok);
        CallV(_fcloseTy, _fcloseFn, new[] { f });
        Ret(ConstI32(1));

        At(fail);
        Ret(ConstI32(0));
    }

    private void PreludeInput()
    {
        Fn("hs_input", _i8ptr, new[] { _i8ptr }, false, out _inputFn, out _inputTy);

        var prompt = LLVM.LLVMGetParam(_curFn, 0);

        CallV(_printfTy, _printfFn, new[] { Str("%s"), prompt });

        var buf = Call(_mallocTy, _mallocFn, new[] { ConstI64(1024) });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        var iPtr = LLVM.LLVMBuildAlloca(_b, _i32, "i");
        Store(ConstI32(0), iPtr);

        var loop = Block("loop");
        var store = Block("store");
        var fin = Block("fin");

        Br(loop);

        At(loop);
        var i = Load(_i32, iPtr);
        var ch = Call(_getcharTy, _getcharFn, Array.Empty<IntPtr>());

        var eof = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, ch, ConstI32(-1));
        var nl = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, ch, ConstI32(10));
        var end = LLVM.LLVMBuildOr(_b, eof, nl, T("end"));
        var full = ICmp(LLVM.LLVMIntPredicate.LLVMIntSGE, i, ConstI32(1023));
        var keepGoing = LLVM.LLVMBuildAnd(_b,
            LLVM.LLVMBuildNot(_b, end, T("notend")),
            LLVM.LLVMBuildNot(_b, full, T("notfull")),
            T("keep"));

        CondBr(keepGoing, store, fin);

        At(store);
        Store(Trunc(ch, _i8), GepByte(buf, SExt(i, _i64)));
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loop);

        At(fin);
        Store(ConstI8(0), GepByte(buf, SExt(Load(_i32, iPtr), _i64)));
        Ret(buf);
    }

    private IntPtr TyLLVM(Ty ty) => TyLLVMCore(CTy(ty));

    private IntPtr TyLLVMCore(Ty ty) => ty switch
    {
        _ when ty == Ty.Int || ty == Ty.Bool || ty.Kind == UserKind.Enum => _i32,
        _ when ty == Ty.Float => _double,
        _ when ty == Ty.Void => _void,
        _ when ty.Nullable && !ty.Elem!.IsPtrKind => OptTy(ty.Elem),
        _ when ty.Kind == UserKind.Struct => _userTys[ty.Name],
        _ => _i8ptr
    };

    private IntPtr FieldLLVM(Ty ty) => ty switch
    {
        _ when ty == Ty.Int || ty == Ty.Bool || ty.Kind == UserKind.Enum => _i32,
        _ when ty == Ty.Float => _double,
        _ when ty.Kind == UserKind.Struct => _userTys[ty.Name],
        _ when ty.Nullable && !ty.Elem!.IsPtrKind => OptTy(ty.Elem),
        _ => _i8ptr
    };

    private IntPtr StructGEP(TypeDecl td, IntPtr obj, uint idx) =>
        LLVM.LLVMBuildStructGEP2(_b, _userTys[td.Name], obj, idx, T("fld"));

    private static long AlignTo(long off, long a) => (off + a - 1) / a * a;

    private long SizeOfTy(Ty ty)
    {
        if (ty == Ty.Int || ty == Ty.Bool || ty.Kind == UserKind.Enum) return 4;
        if (ty == Ty.Float) return 8;
        if (ty.Nullable) return AlignTo(SizeOfTy(ty.Elem!) + 1, Math.Max(AlignOfTy(ty.Elem!), 1));

        if (ty.Kind is UserKind.Struct or UserKind.Class) return ObjSize(ty);
        if (ty.IsPtrKind) return 8;
        return 8;
    }

    private long ObjSize(Ty ty)
    {
        long off = 0, maxA = 1;
        foreach (var f in _typeDecls[ty.Name].Fields)
        {
            off = AlignTo(off, AlignOfTy(f.Type)) + SizeOfTy(f.Type);
            maxA = Math.Max(maxA, AlignOfTy(f.Type));
        }
        return AlignTo(off, maxA);
    }

    private long AlignOfTy(Ty ty)
    {
        if (ty == Ty.Int || ty == Ty.Bool || ty.Kind == UserKind.Enum) return 4;
        if (ty == Ty.Float || ty.IsPtrKind) return 8;
        if (ty.Nullable) return AlignOfTy(ty.Elem!);
        if (ty.Kind == UserKind.Struct)
        {
            long a = 1;
            foreach (var f in _typeDecls[ty.Name].Fields)
                a = Math.Max(a, AlignOfTy(f.Type));
            return a;
        }
        return 8;
    }

    private void RegisterTypes(AstProgram program)
    {
        foreach (var s in program.Stmts)
            if (s is TypeDecl td)
            {
                _typeDecls[td.Name] = td;
                _userTys[td.Name] = LLVM.LLVMStructCreateNamed(_ctx, $"hs.{td.Name}");
            }

        foreach (var td in _typeDecls.Values)
        {
            var fields = td.Fields.Select(f => FieldLLVM(f.Type)).ToArray();
            LLVM.LLVMStructSetBody(_userTys[td.Name], fields, (uint)fields.Length, false);
        }

        foreach (var td in _typeDecls.Values)
            if (td.Kind == UserKind.Class)
            {
                Ext($"hs.{td.Name}.clone", _i8ptr, new[] { _i8ptr }, false, out var cf, out var ct);
                _cloneFns[td.Name] = cf;
                _cloneTys[td.Name] = ct;
                Ext($"hs.{td.Name}.drop", _void, new[] { _i8ptr }, false, out var df, out var dt);
                _udropFns[td.Name] = df;
                _udropTys[td.Name] = dt;
            }

        foreach (var td in _typeDecls.Values)
            if (td.Kind == UserKind.Class)
            {
                EmitClassClone(td);
                EmitClassDrop(td);
            }
    }

    private void EmitClassClone(TypeDecl td)
    {
        _curFn = _cloneFns[td.Name];
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, _curFn, "entry");
        LLVM.LLVMPositionBuilderAtEnd(_b, entry);

        var self = LLVM.LLVMGetParam(_curFn, 0);
        var nu = Call(_mallocTy, _mallocFn, new[] { ConstI64(SizeOfTy(Ty.Named(td.Name))) });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        for (int i = 0; i < td.Fields.Count; i++)
        {
            var ft = td.Fields[i].Type;
            var v = Load(FieldLLVM(ft), StructGEP(td, self, (uint)i));
            Store(ft.Owned ? CopyFieldValue(v, ft) : v, StructGEP(td, nu, (uint)i));
        }

        Ret(nu);
    }

    private void EmitClassDrop(TypeDecl td)
    {
        _curFn = _udropFns[td.Name];
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, _curFn, "entry");
        LLVM.LLVMPositionBuilderAtEnd(_b, entry);

        var self = LLVM.LLVMGetParam(_curFn, 0);
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, self, Null());

        var bodyBB = Block("d");
        var doneBB = Block("d_end");
        CondBr(isNull, doneBB, bodyBB);

        At(bodyBB);
        EmitFreeFields(td, self);
        CallV(_freeTy, _freeFn, new[] { self });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        Br(doneBB);

        At(doneBB);
        RetVoid();
    }

    private void EmitFreeFields(TypeDecl td, IntPtr obj)
    {
        for (int i = 0; i < td.Fields.Count; i++)
        {
            var ft = td.Fields[i].Type;
            if (!ft.Owned) continue;
            var v = Load(FieldLLVM(ft), StructGEP(td, obj, (uint)i));

            if (ft.IsPtrKind)
            {
                var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, v, Null());
                var freeBB = Block("ffield");
                var doneBB = Block("ffield_done");
                CondBr(isNull, doneBB, freeBB);

                At(freeBB);
                FreeOwnedVal(v, ft);
                Br(doneBB);

                At(doneBB);
            }
            else
            {
                FreeOwnedVal(v, ft);
            }
        }
    }

    private IntPtr CopyFieldValue(IntPtr v, Ty ty)
    {
        if (ty == Ty.Str) return Call(_strdupTy, _strdupFn, new[] { v });
        if (ty.Nullable && ty.Elem == Ty.Str) return NullSafeStrdup(v);
        if (ty.Kind == UserKind.Class) return Call(_cloneTys[ty.Name], _cloneFns[ty.Name], new[] { v });
        if (ty.Elem != null && !Ty.IsTask(ty))
            return Call(ty.Elem == Ty.Str ? _listCopyStrTy : _listCopyPodTy,
                ty.Elem == Ty.Str ? _listCopyStrFn : _listCopyPodFn, new[] { v });
        if (ty.Kind == UserKind.Struct) return DeepCopyVal(v, ty);
        return v;
    }

    private IntPtr DeepCopyVal(IntPtr val, Ty ty)
    {
        var td = _typeDecls[ty.Name];
        var res = val;
        for (int i = 0; i < td.Fields.Count; i++)
        {
            var ft = td.Fields[i].Type;
            if (!ft.Owned) continue;
            res = LLVM.LLVMBuildInsertValue(_b, res, CopyFieldValue(LLVM.LLVMBuildExtractValue(_b, val, (uint)i, T("f")), ft), (uint)i, T("fc"));
        }
        return res;
    }

    private IntPtr ToAddr(Val v)
    {
        if (v.Ty.Kind == UserKind.Class) return v.V;
        if (v.Ty.Kind == UserKind.Struct)
        {
            var scratch = Alloca(_userTys[v.Ty.Name], "mat");
            Store(v.V, scratch);
            return scratch;
        }
        return IntPtr.Zero;
    }

    private IntPtr OptTy(Ty inner)
    {
        if (_optTys.TryGetValue(inner.Name, out var t)) return t;
        t = LLVM.LLVMStructCreateNamed(_ctx, $"hs.opt.{inner.Name}");
        LLVM.LLVMStructSetBody(t, new[] { TyLLVM(inner), _i1 }, 2, false);
        _optTys[inner.Name] = t;
        return t;
    }

    private IntPtr ConstI1(bool v) => LLVM.LLVMConstInt(_i1, v ? 1u : 0u, false);

    private IntPtr DefaultOf(Ty ty)
    {
        if (ty.Nullable && !ty.Elem!.IsPtrKind)
        {
            var agg = LLVM.LLVMGetUndef(OptTy(ty.Elem));
            agg = LLVM.LLVMBuildInsertValue(_b, agg, ty.Elem == Ty.Float ? ConstF(0) : ConstI32(0), 0, T("none"));
            agg = LLVM.LLVMBuildInsertValue(_b, agg, ConstI1(false), 1, T("none"));
            return agg;
        }
        return Null();
    }

    private Val Wrap(Val v, Ty opt)
    {
        if (opt.Elem!.IsPtrKind) return new(v.V, opt, v.Prov, v.Src);
        var agg = LLVM.LLVMGetUndef(OptTy(opt.Elem));
        agg = LLVM.LLVMBuildInsertValue(_b, agg, v.V, 0, T("wrap"));
        agg = LLVM.LLVMBuildInsertValue(_b, agg, ConstI1(true), 1, T("wrap"));
        return new(agg, opt, v.Prov, v.Src);
    }

    private IntPtr IsNullVal(Val v)
    {
        if (v.Ty.Elem!.IsPtrKind)
            return ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, v.V, Null());
        return LLVM.LLVMBuildNot(_b, LLVM.LLVMBuildExtractValue(_b, v.V, 1, T("has")), T("isnull"));
    }

    private IntPtr UnwrapV(Val v) =>
        v.Ty.Elem!.IsPtrKind ? v.V : LLVM.LLVMBuildExtractValue(_b, v.V, 0, T("unw"));

    private Val Coerce(Val v, Ty target)
    {
        if (v.Ty == Ty.Null && target.Nullable) return new(DefaultOf(target), target, Prov.Static);
        if (target.Nullable && v.Ty == target.Elem) return Wrap(v, target);
        if (target == Ty.Float && v.Ty == Ty.Int) return new(SIToFP(v.V), target, v.Prov, v.Src);
        return v;
    }

    private IntPtr NullSafeStrdup(IntPtr p)
    {
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, p, Null());
        var pre = LLVM.LLVMGetInsertBlock(_b);

        var copyBB = Block("nsdup");
        var doneBB = Block("nsdup_done");

        CondBr(isNull, doneBB, copyBB);

        At(copyBB);
        var dup = Call(_strdupTy, _strdupFn, new[] { p });
        var copyEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("ns"));
        LLVM.LLVMAddIncoming(phi, new[] { Null(), dup }, new[] { pre, copyEnd }, 2);
        return phi;
    }

    private void FreeOwnedVal(IntPtr v, Ty ty)
    {
        if (ty.Nullable)
        {
            if (!ty.Elem!.IsPtrKind) return;

            var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, v, Null());
            var freeBB = Block("gfree");
            var doneBB = Block("gfree_done");

            CondBr(isNull, doneBB, freeBB);

            At(freeBB);
            FreeOwnedVal(v, ty.Elem);
            Br(doneBB);

            At(doneBB);
            return;
        }

        if (ty.Name == "StringBuilder")
        {
            CallV(_sbDropTy, _sbDropFn, new[] { v });
            return;
        }

        if (Ty.IsHandle(ty)) return;

        if (ty == Ty.Str)
        {
            CallV(_freeTy, _freeFn, new[] { v });
            CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
            return;
        }

        if (ty == Ty.Buffer)
        {
            CallV(_bufDropTy, _bufDropFn, new[] { PtrToInt64(v) });
            return;
        }

        if (ty.Kind == UserKind.Class)
        {
            CallV(_udropTys[ty.Name], _udropFns[ty.Name], new[] { v });
            return;
        }

        if (ty.Kind == UserKind.Struct)
        {
            var scratch = Alloca(_userTys[ty.Name], "sdrop");
            Store(v, scratch);
            EmitFreeFields(_typeDecls[ty.Name], scratch);
            return;
        }

        if (Ty.IsMap(ty))
        {
            CallV(_mapDropTy, _mapDropFn, new[] { PtrToInt64(v) });
            return;
        }

        DropList(v, ty.Elem!);
    }

    private static List<List<Ty>> InstancesOf(AstProgram program, FnDecl f)
    {
        if (f.TPs.Count == 0) return new List<List<Ty>> { new List<Ty>() };
        return program.Instantiations.TryGetValue(f, out var list) ? list : new List<List<Ty>>();
    }

    private void CreateUserFn(FnDecl f, string name, TypeDecl? owner, List<Ty>? tys)
    {
        var savedSubst = _subst;
        if (tys?.Count > 0)
        {
            var subst = new Dictionary<string, Ty>();
            for (int i = 0; i < tys.Count; i++) subst[f.TPs[i]] = tys[i];
            _fnSubsts[name] = subst;
            _subst = subst;
        }

        var ps = new List<IntPtr>();
        if (owner != null)
            ps.Add(owner.Kind == UserKind.Class ? _i8ptr : LLVM.LLVMPointerType(_userTys[owner.Name], 0));
        ps.AddRange(f.Params.Select(p => TyLLVM(p.Type)));

        var ty = LLVM.LLVMFunctionType(TyLLVM(f.Ret), ps.ToArray(), (uint)ps.Count, false);
        var fn = LLVM.LLVMAddFunction(_module, name, ty);

        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "entry");
        var body = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "body");

        _fns[name] = (fn, ty, entry, body, f, owner);
        _subst = savedSubst;
    }

    private static HashSet<string> SharedTopLevelNames(AstProgram program)
    {
        var topNames = program.Stmts.OfType<VarDecl>().Select(d => d.Name).ToHashSet();
        var lambdaIdents = new List<string>();

        foreach (var s in program.Stmts)
            foreach (var lam in FindLambdas(s))
                CollectIdents(lam.Body, lambdaIdents);

        var shared = new HashSet<string>();
        foreach (var n in lambdaIdents)
            if (topNames.Contains(n)) shared.Add(n);
        return shared;
    }

    private static IEnumerable<LamLit> FindLambdas(Stmt s)
    {
        var found = new List<LamLit>();
        FindLambdas(s, found);
        return found;
    }

    private static void FindLambdas(Stmt s, List<LamLit> found)
    {
        switch (s)
        {
            case VarDecl d: FindLambdas(d.Init, found); break;
            case Assign a: FindLambdas(a.Target, found); FindLambdas(a.Value, found); break;
            case IncDec i: FindLambdas(i.Target, found); break;
            case ExprStmt e: FindLambdas(e.E, found); break;
            case If f:
                FindLambdas(f.Cond, found);
                foreach (var st in f.Then) FindLambdas(st, found);
                if (f.Else != null) foreach (var st in f.Else) FindLambdas(st, found);
                break;
            case While w: FindLambdas(w.Cond, found); foreach (var st in w.Body) FindLambdas(st, found); break;
            case For f:
                if (f.Init != null) FindLambdas(f.Init, found);
                if (f.Cond != null) FindLambdas(f.Cond, found);
                foreach (var st in f.Body) FindLambdas(st, found);
                if (f.Step != null) FindLambdas(f.Step, found);
                break;
            case Foreach fe: FindLambdas(fe.Iter, found); foreach (var st in fe.Body) FindLambdas(st, found); break;
            case Return r: if (r.Value != null) FindLambdas(r.Value, found); break;
            case TryCatch tc:
                foreach (var st in tc.Try) FindLambdas(st, found);
                foreach (var st in tc.Catch) FindLambdas(st, found);
                break;
            case BlockStmt b: foreach (var st in b.Body) FindLambdas(st, found); break;
            case Lock lk: FindLambdas(lk.Target, found); foreach (var st in lk.Body) FindLambdas(st, found); break;
        }
    }

    private static void FindLambdas(Expr e, List<LamLit> found)
    {
        switch (e)
        {
            case LamLit l:
                found.Add(l);
                foreach (var st in l.Body) FindLambdas(st, found);
                break;
            case InterpLit it: foreach (var p in it.Parts) FindLambdas(p, found); break;
            case Un u: FindLambdas(u.E, found); break;
            case Bin b: FindLambdas(b.L, found); FindLambdas(b.R, found); break;
            case Cond cd: FindLambdas(cd.CondExpr, found); FindLambdas(cd.Then, found); FindLambdas(cd.Else, found); break;
            case Coalesce co: FindLambdas(co.L, found); FindLambdas(co.R, found); break;
            case Index ix: FindLambdas(ix.Target, found); FindLambdas(ix.Idx, found); break;
            case Call c: foreach (var a in c.Args) FindLambdas(a, found); break;
            case Method m:
                FindLambdas(m.Target, found);
                foreach (var a in m.Args) FindLambdas(a, found);
                break;
            case Prop p: FindLambdas(p.Target, found); break;
            case NewLit nl: foreach (var fi in nl.Fields) FindLambdas(fi.Value, found); break;
            case MapLit ml: foreach (var p in ml.Pairs) { FindLambdas(p.Key, found); FindLambdas(p.Value, found); } break;
            case Cast ca: FindLambdas(ca.Value, found); break;
            case ListLit ll: foreach (var i in ll.Items) FindLambdas(i, found); break;
            case AwaitExpr aw: FindLambdas(aw.Task, found); break;
        }
    }

    private void EmitMain(List<Stmt> stmts, HashSet<string> sharedNames)
    {
        _sharedTopNames = sharedNames;
        _curDecl = null;

        var ty = LLVM.LLVMFunctionType(_i32, new[] { _i32, _i8ptr }, 2, false);
        _curFn = LLVM.LLVMAddFunction(_module, "main", ty);
        _entryBB = LLVM.LLVMAppendBasicBlockInContext(_ctx, _curFn, "entry");
        var body = LLVM.LLVMAppendBasicBlockInContext(_ctx, _curFn, "body");

        LLVM.LLVMPositionBuilderAtEnd(_ab, _entryBB);
        LLVM.LLVMPositionBuilderAtEnd(_b, body);

        _scopes.Clear();
        _fnSlots.Clear();
        _scopes.Add(new Dictionary<string, VarSlot>());

        _errFlag = Alloca(_i32, "errflag");
        StoreAb(ConstI32(0), _errFlag);

        CallV(_setArgsTy, _setArgsFn, new[] { LLVM.LLVMGetParam(_curFn, 0), LLVM.LLVMGetParam(_curFn, 1) });
        CallV(_rtInitTy, _rtInitFn, Array.Empty<IntPtr>());

        EmitStmtList(stmts);
        if (!Terminated()) Ret(ConstI32(0));

        LLVM.LLVMPositionBuilderAtEnd(_ab, _entryBB);
        LLVM.LLVMBuildBr(_ab, body);
    }

    private void StoreAb(IntPtr v, IntPtr p) => LLVM.LLVMBuildStore(_ab, v, p);

    private void EmitFnBody(FnDecl f, string name)
    {
        var (fn, _, entry, body, _, owner) = _fns[name];

        _curFn = fn;
        _curDecl = f;
        _curType = owner;
        _entryBB = entry;

        var savedSubst = _subst;
        _subst = _fnSubsts.TryGetValue(name, out var subst) ? subst : null;

        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMPositionBuilderAtEnd(_b, body);

        _scopes.Clear();
        _fnSlots.Clear();
        _scopes.Add(new Dictionary<string, VarSlot>());

        _errFlag = Alloca(_i32, "errflag");
        StoreAb(ConstI32(0), _errFlag);

        int pi = 0;

        if (owner != null)
        {
            var thisTy = Ty.Named(owner.Name);
            if (owner.Kind == UserKind.Class)
            {
                var slot = NewSlot("this", thisTy, owned: false, borrowParam: true);
                Store(LLVM.LLVMGetParam(fn, 0), slot.Ptr);
            }
            else
            {
                _scopes[0]["this"] = new VarSlot { Name = "this", Ptr = LLVM.LLVMGetParam(fn, 0), Ty = thisTy, Borrow = true };
            }
            pi = 1;
        }

        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            var pty = CTy(p.Type);
            bool isStruct = pty.Kind == UserKind.Struct;
            var slot = NewSlot(p.Name, pty, p.Move || (isStruct && pty.Owned), borrowParam: pty.Owned && !p.Move && !isStruct);

            Store(LLVM.LLVMGetParam(fn, (uint)(pi + i)), slot.Ptr);
            if (slot.Flag != IntPtr.Zero) Store(ConstI32(1), slot.Flag);
        }

        EmitStmtList(f.Body);
        if (!Terminated()) EmitDefaultReturn(CTy(f.Ret));

        _curType = null;
        _subst = savedSubst;
        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMBuildBr(_ab, body);
    }

    private IntPtr DefaultStructVal(Ty ty)
    {
        var td = _typeDecls[ty.Name];
        var scratch = Alloca(_userTys[ty.Name], "dflt");

        for (int i = 0; i < td.Fields.Count; i++)
        {
            var ft = td.Fields[i].Type;
            IntPtr dv = ft switch
            {
                _ when ft == Ty.Float => ConstF(0.0),
                _ when ft == Ty.Str => Call(_strdupTy, _strdupFn, new[] { Str("") }),
                _ when ft.Elem != null => Call(_listNewTy, _listNewFn, new[] { ConstI32(8) }),
                _ when ft.Nullable => DefaultOf(ft),
                _ when ft.Kind == UserKind.Struct => DefaultStructVal(ft),
                _ when ft.Kind == UserKind.Class => Null(),
                _ => ConstI32(0)
            };
            Store(dv, StructGEP(td, scratch, (uint)i));
        }

        return Load(_userTys[ty.Name], scratch);
    }

    private void EmitDefaultReturn(Ty ret)
    {
        FreeAllOwned(null);

        if (ret == Ty.Void) RetVoid();
        else if (ret == Ty.Int || ret == Ty.Bool || ret.Kind == UserKind.Enum) Ret(ConstI32(0));
        else if (ret == Ty.Float) Ret(ConstF(0.0));
        else if (ret == Ty.Str) Ret(Call(_strdupTy, _strdupFn, new[] { Str("") }));
        else if (ret.Kind == UserKind.Class) Ret(Null());
        else if (Ty.IsMap(ret)) Ret(Call(_mapNewTy, _mapNewFn, new[]
        {
            ConstI32(ret.KeyTy == Ty.Str ? 1 : 0),
            ConstI32(ret.Elem == Ty.Str ? 1 : 0)
        }));
        else if (ret.Kind == UserKind.Struct) Ret(DefaultStructVal(ret));
        else if (ret.Nullable && ret.Elem!.IsPtrKind) Ret(Null());
        else if (ret.Nullable) Ret(DefaultOf(ret));
        else Ret(Call(_listNewTy, _listNewFn, new[] { ConstI32(8) }));
    }

    private VarSlot NewSlot(string name, Ty ty, bool owned, bool borrowParam = false)
    {
        var slot = new VarSlot { Name = name, Ty = ty, Owned = owned, Borrow = borrowParam };

        if (_sharedTopNames.Contains(name) && _curDecl == null && _lamRetTy == null)
        {
            var g = LLVM.LLVMAddGlobal(_module, TyLLVM(ty), "hs." + name);
            LLVM.LLVMSetInitializer(g, ty == Ty.Float ? ConstF(0) : ty.IsPtrKind ? Null() : ConstI32(0));
            slot.Ptr = g;
        }
        else
        {
            slot.Ptr = Alloca(TyLLVM(ty), name);
        }

        if (owned) slot.Flag = Alloca(_i32, name + ".flag");

        _scopes[^1][name] = slot;
        if (owned) _fnSlots.Add(slot);

        return slot;
    }

    private IntPtr Alloca(IntPtr ty, string name) => LLVM.LLVMBuildAlloca(_ab, ty, name);

    private VarSlot? FindSlot(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null;
    }

    private int SlotDepth(VarSlot slot)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsValue(slot)) return i;
        return -1;
    }

    private bool Terminated() => LLVM.LLVMGetBasicBlockTerminator(LLVM.LLVMGetInsertBlock(_b)) != IntPtr.Zero;

    private void BrIfLive(IntPtr bb)
    {
        if (!Terminated()) Br(bb);
    }

    private void EmitStmtList(List<Stmt> stmts)
    {
        _scopes.Add(new Dictionary<string, VarSlot>());

        foreach (var s in stmts)
        {
            if (Terminated()) break;
            EmitStmt(s);
        }

        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void EmitStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDecl d: EmitVarDecl(d); EndStatement(); break;
            case Assign a: EmitAssign(a); EndStatement(); break;
            case IncDec inc: EmitIncDec(inc); EndStatement(); break;
            case ExprStmt e: EmitExpr(e.E); EndStatement(); break;
            case If s: EmitIf(s); EndStatement(); break;
            case While w: EmitWhile(w); EndStatement(); break;
            case For f: EmitFor(f); EndStatement(); break;
            case Foreach fe: EmitForeach(fe); EndStatement(); break;
            case Return r: EmitReturn(r); break;
            case Break: Br(_loopExit[^1].brk); break;
            case Continue: Br(_loopExit[^1].cont); break;
            case TryCatch tc: EmitTryCatch(tc); EndStatement(); break;
            case BlockStmt b: EmitStmtList(b.Body); break;
            case Lock lk: EmitLock(lk); EndStatement(); break;
            case Drop dr: EmitDrop(dr); break;
        }
    }

    private void EndStatement()
    {
        if (Terminated()) return;

        FreeTemps();
        ErrorCheck();
    }

    private void FreeTemps()
    {
        foreach (var (v, ty) in _temps)
            FreeOwnedVal(v, ty);

        _temps.Clear();
    }

    private void DropList(IntPtr l, Ty elem)
    {
        var fn = elem == Ty.Str ? _listDropStrFn : _listDropPodFn;
        var ty = elem == Ty.Str ? _listDropStrTy : _listDropPodTy;
        CallV(ty, fn, new[] { l });
    }

    private void ErrorCheck()
    {
        if (_catchBB is not IntPtr catchBB) return;

        var flag = Load(_i32, _errFlag);
        var has = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, flag, ConstI32(0));
        var cont = Block("try_cont");

        CondBr(has, catchBB, cont);

        At(cont);
        Store(ConstI32(0), _errFlag);
    }

    private IntPtr TakeOwnership(Val v)
    {
        if (v.Ty.Kind == UserKind.Struct)
            return DeepCopyVal(v.V, v.Ty);

        if (v.Prov == Prov.Var && v.Src != null)
        {
            if (v.Src.Flag != IntPtr.Zero) Store(ConstI32(0), v.Src.Flag);
            return v.V;
        }

        if (v.Prov == Prov.Temp)
        {
            _temps.RemoveAll(t => t.V == v.V);
            return v.V;
        }

        if (v.Ty.Nullable && v.Ty.Elem!.IsPtrKind)
        {
            if (v.Ty.Elem == Ty.Str) return NullSafeStrdup(v.V);
            if (v.Ty.Elem.Kind == UserKind.Class) return NullSafeClone(v.V, v.Ty.Elem);
            return v.V;
        }

        if (v.Ty.Kind == UserKind.Class)
            return Call(_cloneTys[v.Ty.Name], _cloneFns[v.Ty.Name], new[] { v.V });

        return Call(_strdupTy, _strdupFn, new[] { v.V });
    }

    private IntPtr NullSafeClone(IntPtr p, Ty classTy)
    {
        var isNull = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, p, Null());
        var pre = LLVM.LLVMGetInsertBlock(_b);

        var copyBB = Block("nclone");
        var doneBB = Block("nclone_done");

        CondBr(isNull, doneBB, copyBB);

        At(copyBB);
        var dup = Call(_cloneTys[classTy.Name], _cloneFns[classTy.Name], new[] { p });
        var copyEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("nc"));
        LLVM.LLVMAddIncoming(phi, new[] { Null(), dup }, new[] { pre, copyEnd }, 2);
        return phi;
    }

    private IntPtr TempReg(IntPtr v, Ty ty)
    {
        _temps.Add((v, ty));
        return v;
    }

    private void EmitGuardedFree(VarSlot slot)
    {
        if (!slot.Owned || slot.Flag == IntPtr.Zero) return;

        var alive = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, Load(_i32, slot.Flag), ConstI32(0));
        var freeBB = Block(slot.Name + "_free");
        var done = Block(slot.Name + "_freed");

        CondBr(alive, freeBB, done);

        At(freeBB);
        if (slot.Ty.Kind == UserKind.Class)
        {
            CallV(_udropTys[slot.Ty.Name], _udropFns[slot.Ty.Name], new[] { Load(_i8ptr, slot.Ptr) });
        }
        else if (slot.Ty.Kind == UserKind.Struct)
        {

            EmitFreeFields(_typeDecls[slot.Ty.Name], slot.Ptr);
        }
        else
        {
            FreeOwnedVal(Load(_i8ptr, slot.Ptr), slot.Ty);
        }
        Store(ConstI32(0), slot.Flag);
        Br(done);

        At(done);
    }

    private void FreeAllOwned(VarSlot? except)
    {
        for (int i = _fnSlots.Count - 1; i >= 0; i--)
        {
            var s = _fnSlots[i];
            if (s != except) EmitGuardedFree(s);
        }
    }

    private void EmitLock(Lock lk)
    {
        var slot = FindSlot(((Ident)lk.Target).Name)!;
        var key = slot.ByRef ? Load(_i8ptr, slot.Ptr) : slot.Ptr;

        CallV(_lockAcqTy, _lockAcqFn, new[] { key });
        EmitStmtList(lk.Body);
        if (!Terminated())
            CallV(_lockRelTy, _lockRelFn, new[] { key });
    }

    private void EmitDrop(Drop d)
    {
        foreach (var name in d.Names)
        {
            var slot = FindSlot(name);
            if (slot != null) EmitGuardedFree(slot);
        }
    }

    private void EmitVarDecl(VarDecl d)
    {
        var ann = d.Ann == null ? null : CTy(d.Ann);
        var v = d.Init is NullLit && ann != null
            ? new Val(DefaultOf(ann), ann, Prov.Static)
            : EmitExpr(d.Init);
        var ty = ann ?? v.Ty;
        v = Coerce(v, ty);

        if (ty.Owned)
        {
            var slot = NewSlot(d.Name, ty, owned: true);
            var owned = TakeOwnership(v);

            Store(owned, slot.Ptr);
            Store(ConstI32(1), slot.Flag);
        }
        else
        {
            var slot = NewSlot(d.Name, ty, owned: false);
            Store(v.V, slot.Ptr);
        }
    }

    private void EmitAssign(Assign a)
    {
        if (a.Target is Ident tid && tid.ThisField)
        {
            EmitAssignToField(a, tid);
            return;
        }

        if (a.Target is Ident id)
        {
            EmitAssignToVar(a, id);
            return;
        }

        if (a.Target is Prop pr && pr.FieldIndex >= 0)
        {
            EmitAssignToField(a, pr);
            return;
        }

        if (a.Target is Index ix)
        {
            EmitAssignToIndex(a, ix);
            return;
        }

        throw new Exception("invalid assignment target");
    }

    private void EmitAssignToVar(Assign a, Ident id)
    {

        if (id.Name == "_" && FindSlot(id.Name) == null)
        {

            if (a.Value is Method dm && dm.Target is Ident dn && dn.Name == "Task" && dm.Name == "Run")
                EmitTaskRun(dm, forget: true);
            else
                EmitExpr(a.Value);
            return;
        }

        var slot = FindSlot(id.Name)!;

        if (slot.ByRef)
        {
            var p = Load(_i8ptr, slot.Ptr);
            EmitStoreThroughRef(a, p, slot);
            return;
        }

        if (a.Op != "=")
        {
            EmitCompoundAssign(a, slot);
            return;
        }

        var v = a.Value is NullLit
            ? new Val(DefaultOf(slot.Ty), slot.Ty, Prov.Static)
            : EmitExpr(a.Value);
        v = Coerce(v, slot.Ty);

        if (!slot.Owned)
        {
            Store(v.V, slot.Ptr);
            return;
        }

        var owned = TakeOwnership(v);

        if (v.Src == slot)
        {
            Store(owned, slot.Ptr);
            return;
        }

        EmitGuardedFree(slot);
        Store(owned, slot.Ptr);
        Store(ConstI32(1), slot.Flag);
    }

    private void EmitStoreThroughRef(Assign a, IntPtr p, VarSlot slot)
    {
        if (a.Op == "=")
        {
            var v = a.Value is NullLit
                ? new Val(DefaultOf(slot.Ty), slot.Ty, Prov.Static)
                : EmitExpr(a.Value);
            Store(Coerce(v, slot.Ty).V, p);
            return;
        }

        var cur = Load(TyLLVM(slot.Ty), p);
        var rhs = EmitExpr(a.Value);
        Store(Arith(a.Op[0], slot.Ty, cur, rhs.V), p);
    }

    private void EmitCompoundAssign(Assign a, VarSlot slot)
    {
        var cur = Load(TyLLVM(slot.Ty), slot.Ptr);
        var rhs = EmitExpr(a.Value);

        if (slot.Ty == Ty.Str)
        {
            var l = ToStringPtr(cur, slot.Ty);
            var r = ToStringPtr(rhs.V, rhs.Ty);
            var combined = TempReg(Call(_concatTy, _concatFn, new[] { l, r }), Ty.Str);
            var owned = TakeOwnership(new Val(combined, Ty.Str, Prov.Temp));

            EmitGuardedFree(slot);
            Store(owned, slot.Ptr);
            Store(ConstI32(1), slot.Flag);
            return;
        }

        var res = Arith(a.Op[0], slot.Ty, cur, rhs.V);
        Store(res, slot.Ptr);
    }

    private void EmitAssignToIndex(Assign a, Index ix)
    {
        var list = EmitExpr(ix.Target);
        var idx = EmitExpr(ix.Idx);
        var val = EmitExpr(a.Value);

        if (Ty.IsMap(list.Ty))
        {

            var k = BoxMapKeyOrVal(list.Ty.KeyTy!, idx);
            var v = BoxMapKeyOrVal(list.Ty.Elem!, val);
            CallV(_mapInsertTy, _mapInsertFn, new[] { PtrToInt64(list.V), k, v });
            return;
        }

        if (list.Ty == Ty.Buffer)
        {
            CallV(_bufSetTy, _bufSetFn, new[] { PtrToInt64(list.V), SExt(idx.V, _i64), SExt(val.V, _i64), _errFlag });
            return;
        }

        var elem = list.Ty.Elem!;
        if (elem == Ty.Str)
        {

            var dup = Call(_strdupTy, _strdupFn, new[] { ToStringPtr(val.V, val.Ty) });
            CallV(_listSetStrTy, _listSetStrFn, new[] { list.V, idx.V, PtrToInt64(dup), _errFlag });
        }
        else
        {
            var widened = val.Ty == Ty.Int ? ZExt(val.V, _i64) : SExt(val.V, _i64);
            CallV(_listSetIntTy, _listSetIntFn, new[] { list.V, idx.V, widened, _errFlag });
        }
    }

    private void EmitIncDec(IncDec inc)
    {
        var delta = ConstI32(inc.Inc ? 1 : -1);

        if (inc.Target is Ident id)
        {
            var slot = FindSlot(id.Name)!;
            Store(Add(Load(_i32, slot.Ptr), delta), slot.Ptr);
            return;
        }

        if (inc.Target is Prop or Ident { ThisField: true })
        {
            var (addr, fty) = AddrOf(inc.Target);
            if (addr != IntPtr.Zero)
            {
                Store(Add(Load(_i32, addr), delta), addr);
                return;
            }
        }

        var ix = (Index)inc.Target;
        var list = EmitExpr(ix.Target);
        var idx = EmitExpr(ix.Idx);

        var raw = Call(_listGetTy, _listGetFn, new[] { list.V, idx.V, _errFlag });
        var bumped = Add(Trunc(raw, _i32), delta);

        CallV(_listSetIntTy, _listSetIntFn, new[] { list.V, idx.V, ZExt(bumped, _i64), _errFlag });
    }

    private IntPtr Arith(char op, Ty ty, IntPtr l, IntPtr r)
    {
        if (ty == Ty.Float)
        {
            return op switch
            {
                '+' => LLVM.LLVMBuildFAdd(_b, l, r, T("fadd")),
                '-' => LLVM.LLVMBuildFSub(_b, l, r, T("fsub")),
                '*' => LLVM.LLVMBuildFMul(_b, l, r, T("fmul")),
                '/' => LLVM.LLVMBuildFDiv(_b, l, r, T("fdiv")),
                _ => LLVM.LLVMBuildFRem(_b, l, r, T("frem"))
            };
        }

        return op switch
        {
            '+' => Add(l, r),
            '-' => Sub(l, r),
            '*' => Mul(l, r),
            '/' => GuardedDivRem(true, l, r),
            _ => GuardedDivRem(false, l, r)
        };
    }

    private IntPtr GuardedDivRem(bool div, IntPtr l, IntPtr r)
    {
        var zero = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, r, ConstI32(0));

        var divBB = Block("dz_div");
        var failBB = Block("dz_fail");
        var merge = Block("dz_merge");

        CondBr(zero, failBB, divBB);

        At(divBB);
        var res = div
            ? LLVM.LLVMBuildSDiv(_b, l, r, T("sdiv"))
            : LLVM.LLVMBuildSRem(_b, l, r, T("srem"));
        var divEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(failBB);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("division by zero") });
        Store(ConstI32(1), _errFlag);
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i32, T("dz"));
        LLVM.LLVMAddIncoming(phi, new[] { res, ConstI32(0) }, new[] { divEnd, failEnd }, 2);
        return phi;
    }

    private void EmitIf(If s)
    {
        var cond = BoolOf(EmitExpr(s.Cond));

        var thenBB = Block("if_then");
        var elseBB = Block("if_else");
        var mergeBB = Block("if_merge");

        CondBr(cond, thenBB, elseBB);

        At(thenBB);
        EmitStmtList(s.Then);
        BrIfLive(mergeBB);

        At(elseBB);
        if (s.Else != null) EmitStmtList(s.Else);
        BrIfLive(mergeBB);

        At(mergeBB);
    }

    private void EmitWhile(While w)
    {
        var condBB = Block("while_cond");
        var bodyBB = Block("while_body");
        var afterBB = Block("while_after");

        Br(condBB);

        At(condBB);
        var cond = BoolOf(EmitLoopCond(w.Cond));
        CondBr(cond, bodyBB, afterBB);

        At(bodyBB);
        _loopExit.Add((afterBB, condBB));
        EmitStmtList(w.Body);
        _loopExit.RemoveAt(_loopExit.Count - 1);
        BrIfLive(condBB);

        At(afterBB);
    }

    private void EmitFor(For f)
    {

        _scopes.Add(new Dictionary<string, VarSlot>());
        if (f.Init != null) EmitStmt(f.Init);

        var condBB = Block("for_cond");
        var bodyBB = Block("for_body");
        var stepBB = Block("for_step");
        var afterBB = Block("for_after");

        Br(condBB);

        At(condBB);
        if (f.Cond != null)
        {
            var cond = BoolOf(EmitLoopCond(f.Cond));
            CondBr(cond, bodyBB, afterBB);
        }
        else Br(bodyBB);

        At(bodyBB);
        _scopes.Add(new Dictionary<string, VarSlot>());
        _loopExit.Add((afterBB, stepBB));

        foreach (var st in f.Body)
        {
            if (Terminated()) break;
            EmitStmt(st);
        }

        _loopExit.RemoveAt(_loopExit.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        BrIfLive(stepBB);

        At(stepBB);
        if (f.Step != null) EmitStmt(f.Step);
        BrIfLive(condBB);

        At(afterBB);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void EmitForeach(Foreach fe)
    {
        var iter = EmitExpr(fe.Iter);
        var elem = iter.Ty.Elem!;
        var idxPtr = Alloca(_i32, "feidx");
        Store(ConstI32(0), idxPtr);

        var condBB = Block("fe_cond");
        var bodyBB = Block("fe_body");
        var nextBB = Block("fe_next");
        var afterBB = Block("fe_after");

        Br(condBB);

        At(condBB);
        var idx = Load(_i32, idxPtr);
        var size = Call(_listSizeTy, _listSizeFn, new[] { iter.V });
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, idx, size);
        CondBr(more, bodyBB, afterBB);

        At(bodyBB);
        _scopes.Add(new Dictionary<string, VarSlot>());
        _loopExit.Add((afterBB, nextBB));

        var slot = NewSlot(fe.Var, elem, owned: elem.Owned);
        var raw = Call(_listGetTy, _listGetFn, new[] { iter.V, idx, _errFlag });

        if (elem == Ty.Str)
        {
            Store(Call(_strdupTy, _strdupFn, new[] { Int64ToPtr(raw) }), slot.Ptr);
            Store(ConstI32(1), slot.Flag);
        }
        else Store(Trunc(raw, _i32), slot.Ptr);

        foreach (var st in fe.Body)
        {
            if (Terminated()) break;
            EmitStmt(st);
        }

        _loopExit.RemoveAt(_loopExit.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        BrIfLive(nextBB);

        At(nextBB);
        Store(Add(Load(_i32, idxPtr), ConstI32(1)), idxPtr);
        BrIfLive(condBB);

        At(afterBB);
    }

    private Val EmitLoopCond(Expr e)
    {
        int start = _temps.Count;
        var v = EmitExpr(e);

        var mine = _temps.Skip(start).ToList();
        _temps.RemoveRange(start, _temps.Count - start);

        foreach (var (tv, tty) in mine)
            FreeOwnedVal(tv, tty);

        return v;
    }

    private void EmitTryCatch(TryCatch tc)
    {
        var catchBB = Block("catch");
        var mergeBB = Block("try_merge");
        var prev = _catchBB;

        _catchBB = catchBB;
        Store(ConstI32(0), _errFlag);

        int slotBase = _fnSlots.Count;

        EmitStmtList(tc.Try);
        BrIfLive(mergeBB);

        _catchBB = prev;
        At(catchBB);
        Store(ConstI32(0), _errFlag);

        foreach (var s in _fnSlots.Skip(slotBase))
            EmitGuardedFree(s);

        if (tc.ErrName != null)
        {
            var slot = NewSlot(tc.ErrName, Ty.Str, owned: true);
            var msg = Call(_errorGetTy, _errorGetFn, Array.Empty<IntPtr>());
            Store(Call(_strdupTy, _strdupFn, new[] { msg }), slot.Ptr);
            Store(ConstI32(1), slot.Flag);
        }

        EmitStmtList(tc.Catch);
        BrIfLive(mergeBB);

        At(mergeBB);
    }

    private void EmitReturn(Return r)
    {
        if (_lamRetTy != null)
        {
            EmitLambdaReturn(r);
            return;
        }

        if (r.Value == null)
        {
            FreeTemps();
            FreeAllOwned(null);
            RetVoid();
            return;
        }

        var v = EmitExpr(r.Value);
        v = Coerce(v, CTy(_curDecl!.Ret));

        var retVal = v.Ty.Owned ? TakeOwnership(v) : v.V;

        FreeTemps();

        FreeAllOwned(v.Ty.Kind == UserKind.Struct ? null : (v.Prov == Prov.Var ? v.Src : null));
        Ret(retVal);
    }

    private IntPtr BoolOf(Val v) => ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, v.V, ConstI32(0));

    private IntPtr NullSafeString(IntPtr p) =>
        Select(ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, p, Null()), Str(""), p);

    private IntPtr ToStringPtr(IntPtr v, Ty ty) => ty switch
    {
        _ when ty == Ty.Str => NullSafeString(v),
        _ when ty == Ty.Int || ty.Kind == UserKind.Enum => TempReg(Call(_itoaTy, _itoaFn, new[] { v }), Ty.Str),
        _ when ty == Ty.Float => TempReg(Call(_ftoaTy, _ftoaFn, new[] { v }), Ty.Str),
        _ => Select(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, v, ConstI32(0)), Str("true"), Str("false"))
    };

    private void Print(Val v)
    {
        if (v.Ty.Kind == UserKind.Enum)
        {
            CallV(_printfTy, _printfFn, new[] { Str("%d\n"), v.V });
            return;
        }

        switch (v.Ty.Name)
        {

            case "string": CallV(_printfTy, _printfFn, new[] { Str("%s\n"), NullSafeString(v.V) }); break;
            case "int": CallV(_printfTy, _printfFn, new[] { Str("%d\n"), v.V }); break;
            case "float": CallV(_printfTy, _printfFn, new[] { Str("%g\n"), v.V }); break;
            case "bool": CallV(_printfTy, _printfFn, new[] { Str("%s\n"), ToStringPtr(v.V, v.Ty) }); break;
        }
    }

    private Val EmitExpr(Expr e)
    {
        switch (e)
        {
            case IntLit n: return new(LLVM.LLVMConstInt(_i32, unchecked((uint)n.Value), true), Ty.Int);
            case FloatLit f: return new(ConstF(f.Value), Ty.Float);
            case BoolLit b: return new(ConstBool(b.Value), Ty.Bool);
            case StrLit s: return new(Str(s.Value), Ty.Str, Prov.Static);
            case InterpLit it: return EmitInterp(it);

            case Ident id:
                {

                    if (id.ThisField)
                    {
                        var (addr, fty) = AddrOf(id);
                        return LoadField(addr, fty!);
                    }

                    var slot = FindSlot(id.Name) ?? throw new Exception($"undefined variable '{id.Name}'");

                    if (id.Unwrap)
                    {
                        var inner = slot.Ty.Elem!;
                        if (inner.IsPtrKind)
                            return new(Load(_i8ptr, slot.Ptr), inner, Prov.Borrow);
                        var f0 = LLVM.LLVMBuildStructGEP2(_b, OptTy(inner), slot.Ptr, 0, T("unw"));
                        return new(Load(TyLLVM(inner), f0), inner, Prov.Borrow);
                    }

                    var prov = slot.Owned ? Prov.Var : Prov.Borrow;

                    if (slot.ByRef)
                    {
                        var p = Load(_i8ptr, slot.Ptr);
                        return new(Load(TyLLVM(slot.Ty), p), slot.Ty, Prov.Borrow);
                    }

                    return new(Load(TyLLVM(slot.Ty), slot.Ptr), slot.Ty, prov, slot);
                }

            case Un u: return EmitUnary(u);
            case Bin b: return EmitBinary(b);
            case Index ix: return EmitIndex(ix);
            case Call c: return EmitCall(c);
            case Method m: return EmitMethod(m);
            case NullLit: return new(Null(), Ty.Null, Prov.Static);
            case Coalesce co: return EmitCoalesce(co);
            case Cond cd: return EmitCond(cd);

            case Prop p:
                {
                    if (p.EnumValue is int ev)
                        return new(ConstI32(ev), p.ResultTy!);

                    if (p.CookiesFacade)
                    {
                        var ct = EmitExpr(p.Target);
                        var ck = Call(_httpCookiesTy, _httpCookiesFn, new[] { PtrToInt64(ct.V) });
                        var kt = Ty.Handle("Cookies");
                        return new(TempReg(ck, kt), kt, Prov.Temp);
                    }

                    if (p.FieldIndex >= 0)
                    {
                        if (p.NullCond)
                            return EmitNullCond(p.Target, p.ResultTy!, t => FieldFromBase(t, p));
                        var (addr, fty) = AddrOf(p);
                        if (addr != IntPtr.Zero)
                            return LoadField(addr, fty!);

                        var t = EmitExpr(p.Target);
                        var ftd = _typeDecls[t.Ty.Name];
                        var ft = ftd.Fields[p.FieldIndex].Type;
                        var fv = LLVM.LLVMBuildExtractValue(_b, t.V, (uint)p.FieldIndex, T("fld"));
                        return ft.Owned && ft.IsPtrKind
                            ? new(fv, ft, Prov.Borrow)
                            : new(fv, ft);
                    }

                    if (p.NullCond)
                        return EmitNullCond(p.Target, p.ResultTy!, t =>
                            Ty.IsMap(t.Ty)
                                ? new(Call(_mapCountTy, _mapCountFn, new[] { PtrToInt64(t.V) }), Ty.Int)
                                : new(Call(_listSizeTy, _listSizeFn, new[] { t.V }), Ty.Int));
                    var t2 = EmitExpr(p.Target);
                    if (Ty.IsMap(t2.Ty))
                    {
                        if (p.Name == "Keys" || p.Name == "Values")
                        {
                            bool wantValues = p.Name == "Values";
                            var kindTy = wantValues ? t2.Ty.Elem! : t2.Ty.KeyTy!;
                            var list = Call(_mapItemsTy, _mapItemsFn,
                                new[] { PtrToInt64(t2.V), ConstI32(wantValues ? 1 : 0), ConstI32(kindTy == Ty.Str ? 1 : 0) });
                            return new(TempReg(list, Ty.List(kindTy)), Ty.List(kindTy), Prov.Temp);
                        }
                        return new(Call(_mapCountTy, _mapCountFn, new[] { PtrToInt64(t2.V) }), Ty.Int);
                    }
                    return new(Call(_listSizeTy, _listSizeFn, new[] { t2.V }), Ty.Int);
                }

            case NewLit nl:
                return EmitNewLit(nl);
            case MapLit ml:
                return EmitMapLit(ml);

            case LamLit:
                throw new Exception("lambdas are only compiled through Task.Run");

            case AwaitExpr aw:
                return EmitAwait(aw);

            case Cast c:
                return EmitCast(c);

            case ListLit ll: return EmitListLit(ll);
            default: throw new Exception("unsupported expression");
        }
    }

    private Val EmitInterp(InterpLit it)
    {
        var parts = new List<IntPtr>();
        foreach (var p in it.Parts)
        {
            var v = EmitExpr(p);
            parts.Add(ToStringPtr(v.V, v.Ty));
        }

        var total = ConstI64(1);
        foreach (var p in parts)
            total = Add(total, Call(_strlenTy, _strlenFn, new[] { p }));

        var buf = Call(_mallocTy, _mallocFn, new[] { total });
        CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

        Store(ConstI8(0), buf);
        foreach (var p in parts)
            CallV(_strcatTy, _strcatFn, new[] { buf, p });

        return new(TempReg(buf, Ty.Str), Ty.Str, Prov.Temp);
    }

    private Val EmitUnary(Un u)
    {
        var v = EmitExpr(u.E);

        if (u.Op == "!")
            return new(ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, v.V, ConstI32(0)), _i32), Ty.Bool);

        if (v.Ty == Ty.Float)
            return new(LLVM.LLVMBuildFSub(_b, ConstF(0.0), v.V, T("negf")), Ty.Float);

        return new(Sub(ConstI32(0), v.V), Ty.Int);
    }

    private Val EmitBinary(Bin b)
    {

        if (b.Op == "&&" || b.Op == "||")
            return EmitShortCircuit(b);

        var l = EmitExpr(b.L);
        var r = EmitExpr(b.R);

        switch (b.Op)
        {
            case "==": case "!=": case "<": case "<=": case ">": case ">=":
                return EmitCompare(b.Op, l, r);

            case "+":
                if (l.Ty == Ty.Str || r.Ty == Ty.Str)
                {
                    var ls = ToStringPtr(l.V, l.Ty);
                    var rs = ToStringPtr(r.V, r.Ty);
                    return new(TempReg(Call(_concatTy, _concatFn, new[] { ls, rs }), Ty.Str), Ty.Str, Prov.Temp);
                }
                goto Arith;

            case "-": case "*": case "/": case "%":
                if (l.Ty == Ty.Str || r.Ty == Ty.Str) throw new Exception($"'{b.Op}' is not supported on strings");
                goto Arith;

            Arith:
                if (l.Ty == Ty.Float || r.Ty == Ty.Float)
                {
                    var lf = l.Ty == Ty.Float ? l.V : SIToFP(l.V);
                    var rf = r.Ty == Ty.Float ? r.V : SIToFP(r.V);
                    var res = b.Op switch
                    {
                        "+" => LLVM.LLVMBuildFAdd(_b, lf, rf, T("fadd")),
                        "-" => LLVM.LLVMBuildFSub(_b, lf, rf, T("fsub")),
                        "*" => LLVM.LLVMBuildFMul(_b, lf, rf, T("fmul")),
                        "/" => LLVM.LLVMBuildFDiv(_b, lf, rf, T("fdiv")),
                        _ => LLVM.LLVMBuildFRem(_b, lf, rf, T("frem"))
                    };
                    return new(res, Ty.Float);
                }

                {
                    var res = Arith(b.Op[0], Ty.Int, l.V, r.V);
                    return new(res, Ty.Int);
                }

            default:
                throw new Exception("unknown operator " + b.Op);
        }
    }

    private Val EmitShortCircuit(Bin b)
    {
        var l = EmitExpr(b.L);
        var lb = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, l.V, ConstI32(0));
        var preBB = LLVM.LLVMGetInsertBlock(_b);

        var rhsBB = Block("sc_rhs");
        var doneBB = Block("sc_done");

        if (b.Op == "&&") CondBr(lb, rhsBB, doneBB);
        else CondBr(lb, doneBB, rhsBB);

        At(rhsBB);
        int tempStart = _temps.Count;
        var r = EmitExpr(b.R);
        var rb = ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, r.V, ConstI32(0)), _i32);

        var rhsTemps = _temps.Skip(tempStart).ToList();
        _temps.RemoveRange(tempStart, _temps.Count - tempStart);
        foreach (var (tv, tty) in rhsTemps)
            FreeOwnedVal(tv, tty);

        var rhsEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        var phi = LLVM.LLVMBuildPhi(_b, _i32, T("sc"));
        var skipped = ConstBool(b.Op == "&&" ? false : true);
        LLVM.LLVMAddIncoming(phi, new[] { skipped, rb }, new[] { preBB, rhsEnd }, 2);
        return new(phi, Ty.Bool);
    }

    private Val EmitCompare(string op, Val l, Val r)
    {
        if (l.Ty == Ty.Null || r.Ty == Ty.Null)
        {
            var nv = l.Ty == Ty.Null ? r : l;
            var isNull = IsNullVal(nv);
            if (op == "!=") isNull = LLVM.LLVMBuildNot(_b, isNull, T("notnull"));
            return new(ZExt(isNull, _i32), Ty.Bool);
        }

        if (l.Ty == Ty.Str && r.Ty == Ty.Str)
        {
            var cmp = Call(_strcmpTy, _strcmpFn, new[] { l.V, r.V });
            var pred = op == "==" ? LLVM.LLVMIntPredicate.LLVMIntEQ : LLVM.LLVMIntPredicate.LLVMIntNE;
            return new(ZExt(ICmp(pred, cmp, ConstI32(0)), _i32), Ty.Bool);
        }

        if (l.Ty == Ty.Float || r.Ty == Ty.Float)
        {
            var lf = l.Ty == Ty.Float ? l.V : SIToFP(l.V);
            var rf = r.Ty == Ty.Float ? r.V : SIToFP(r.V);
            var pred = op switch
            {
                "==" => LLVM.LLVMRealPredicate.LLVMRealOEQ,
                "!=" => LLVM.LLVMRealPredicate.LLVMRealONE,
                "<" => LLVM.LLVMRealPredicate.LLVMRealOLT,
                "<=" => LLVM.LLVMRealPredicate.LLVMRealOLE,
                ">" => LLVM.LLVMRealPredicate.LLVMRealOGT,
                _ => LLVM.LLVMRealPredicate.LLVMRealOGE
            };
            return new(ZExt(FCmp(pred, lf, rf), _i32), Ty.Bool);
        }

        var ip = op switch
        {
            "==" => LLVM.LLVMIntPredicate.LLVMIntEQ,
            "!=" => LLVM.LLVMIntPredicate.LLVMIntNE,
            "<" => LLVM.LLVMIntPredicate.LLVMIntSLT,
            "<=" => LLVM.LLVMIntPredicate.LLVMIntSLE,
            ">" => LLVM.LLVMIntPredicate.LLVMIntSGT,
            _ => LLVM.LLVMIntPredicate.LLVMIntSGE
        };
        return new(ZExt(ICmp(ip, l.V, r.V), _i32), Ty.Bool);
    }

    private Val EmitIndex(Index ix)
    {
        var list = EmitExpr(ix.Target);
        var idx = EmitExpr(ix.Idx);

        if (list.Ty == Ty.Buffer)
        {
            var raw = Call(_bufGetTy, _bufGetFn, new[] { PtrToInt64(list.V), SExt(idx.V, _i64), _errFlag });
            return new(Trunc(raw, _i32), Ty.Int);
        }

        if (Ty.IsMap(list.Ty))
        {

            var key = BoxMapKeyLookup(list.Ty.KeyTy!, idx);
            var found = Alloca(_i32, "found");
            Store(ConstI32(0), found);
            var val = Call(_mapGetTy, _mapGetFn, new[] { PtrToInt64(list.V), key, found });
            var has = Load(_i32, found);
            var vt = list.Ty.Elem!;
            var resTy = Ty.NullableOf(vt);

            if (vt == Ty.Str)
            {
                var ptr = Select(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, has, ConstI32(0)), Int64ToPtr(val), Null());
                return new(ptr, resTy, Prov.Borrow);
            }

            var agg = LLVM.LLVMGetUndef(OptTy(vt));
            agg = LLVM.LLVMBuildInsertValue(_b, agg, Trunc(val, _i32), 0, T("mv"));
            agg = LLVM.LLVMBuildInsertValue(_b, agg, Trunc(has, _i1), 1, T("mv"));
            return new(agg, resTy);
        }

        var lraw = Call(_listGetTy, _listGetFn, new[] { list.V, idx.V, _errFlag });

        if (list.Ty.Elem == Ty.Str)
            return new(Int64ToPtr(lraw), Ty.Str, Prov.Borrow);

        return new(Trunc(lraw, _i32), Ty.Int);
    }

    private Val EmitListLit(ListLit ll)
    {
        var elemTy = CTy(ll.ElemTy);
        var cap = Math.Max(ll.Items.Count, 8);
        var l = TempReg(Call(_listNewTy, _listNewFn, new[] { ConstI32(cap) }), Ty.List(elemTy));

        foreach (var item in ll.Items)
        {
            var v = EmitExpr(item);
            CallV(_listAddTy, _listAddFn, new[] { l, BoxListElem(v, elemTy) });
        }

        return new(l, Ty.List(elemTy), Prov.Temp);
    }

    private IntPtr BoxMapKeyOrVal(Ty ty, Val v)
    {
        if (ty == Ty.Str)
            return PtrToInt64(Call(_strdupTy, _strdupFn, new[] { NullSafeString(v.V) }));
        return v.Ty == Ty.Int ? ZExt(v.V, _i64) : SExt(v.V, _i64);
    }

    private IntPtr BoxMapKeyLookup(Ty ty, Val v)
    {
        if (ty == Ty.Str)
            return PtrToInt64(NullSafeString(ToStringPtr(v.V, v.Ty)));
        return v.Ty == Ty.Int ? ZExt(v.V, _i64) : SExt(v.V, _i64);
    }

    private Val EmitMapLit(MapLit ml)
    {
        var mt = Ty.Map(ml.KeyTy, ml.ValTy);
        var m = TempReg(Call(_mapNewTy, _mapNewFn, new[]
        {
            ConstI32(ml.KeyTy == Ty.Str ? 1 : 0),
            ConstI32(ml.ValTy == Ty.Str ? 1 : 0)
        }), mt);

        foreach (var p in ml.Pairs)
        {
            var k = BoxMapKeyOrVal(ml.KeyTy, EmitExpr(p.Key));
            var v = BoxMapKeyOrVal(ml.ValTy, EmitExpr(p.Value));
            CallV(_mapInsertTy, _mapInsertFn, new[] { PtrToInt64(m), k, v });
        }

        return new(m, mt, Prov.Temp);
    }

    private IntPtr BoxListElem(Val v, Ty elemTy)
    {
        if (elemTy == Ty.Str) return PtrToInt64(Call(_strdupTy, _strdupFn, new[] { ToStringPtr(v.V, v.Ty) }));
        if (elemTy.IsPtrKind) return PtrToInt64(v.V);
        return v.Ty == Ty.Int ? ZExt(v.V, _i64) : SExt(v.V, _i64);
    }

    private static bool IsStaticClass(string name) =>
        name is "Task" or "Tcp" or "Udp" or "Http" or "StringBuilder";

    private IntPtr HandlePtr(IntPtr v) => Int64ToPtr(v);

    private IntPtr GuardHandle(IntPtr raw)
    {
        var bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, raw, ConstI64(0));

        var ok = Block("net_ok");
        var fail = Block("net_fail");
        var merge = Block("net_merge");

        CondBr(bad, fail, ok);

        At(ok);
        var okPtr = Int64ToPtr(raw);
        var okEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("network call failed") });
        Store(ConstI32(1), _errFlag);
        var failPtr = Null();
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("h"));
        LLVM.LLVMAddIncoming(phi, new[] { okPtr, failPtr }, new[] { okEnd, failEnd }, 2);
        return phi;
    }

    private Val GuardNetString(IntPtr raw)
    {
        var bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, raw, Null());

        var ok = Block("net_ok");
        var fail = Block("net_fail");
        var merge = Block("net_merge");

        CondBr(bad, fail, ok);

        At(ok);
        var okEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("network receive failed") });
        Store(ConstI32(1), _errFlag);
        var empty = Call(_strdupTy, _strdupFn, new[] { Str("") });
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("ns"));
        LLVM.LLVMAddIncoming(phi, new[] { raw, empty }, new[] { okEnd, failEnd }, 2);
        return new(TempReg(phi, Ty.Str), Ty.Str, Prov.Temp);
    }

    private Val EmitWhenAll(Method m)
    {
        var list = EmitExpr(m.Args[0]);
        var retTy = list.Ty.Elem!.Elem!;
        var res = TempReg(Call(_listNewTy, _listNewFn, new[] { ConstI32(8) }), Ty.List(retTy));

        var iPtr = Alloca(_i32, "wai");
        Store(ConstI32(0), iPtr);

        var loopBB = Block("wa_loop");
        var doneBB = Block("wa_done");
        Br(loopBB);

        At(loopBB);
        var i = Load(_i32, iPtr);
        var size = Call(_listSizeTy, _listSizeFn, new[] { list.V });
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, size);
        var bodyBB = Block("wa_body");
        CondBr(more, bodyBB, doneBB);

        At(bodyBB);
        var raw = Call(_listGetTy, _listGetFn, new[] { list.V, i, _errFlag });
        var joined = Call(_rtTaskJoinTy, _rtTaskJoinFn, new[] { Int64ToPtr(raw) });

        IntPtr asInt;
        if (retTy.IsPtrKind)
        {
            asInt = PtrToInt64(joined);
        }
        else if (retTy == Ty.Float)
        {
            var f = Load(_double, joined);
            asInt = LLVM.LLVMBuildFPToSI(_b, f, _i64, T("f2i"));
        }
        else
        {
            asInt = ZExt(Trunc(PtrToInt64(joined), _i32), _i64);
        }
        CallV(_listAddTy, _listAddFn, new[] { res, asInt });
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loopBB);

        At(doneBB);
        return new(res, Ty.List(retTy), Prov.Temp);
    }

    private Val EmitNetStatic(string cls, Method m)
    {
        if (cls == "Tcp" && m.Name == "Listen")
        {
            var port = EmitExpr(m.Args[0]);
            var raw = Call(_tcpListenTy, _tcpListenFn, new[] { port.V });
            return new(GuardHandle(raw), Ty.Handle("listener"));
        }

        if (cls == "Tcp" && m.Name == "Connect")
        {
            var host = EmitExpr(m.Args[0]);
            var port = EmitExpr(m.Args[1]);
            var raw = Call(_tcpConnectTy, _tcpConnectFn, new[] { host.V, port.V });
            return new(GuardHandle(raw), Ty.Handle("Client"));
        }

        if (cls == "Udp" && m.Name == "Open")
        {
            var raw = Call(_udpOpenTy, _udpOpenFn, Array.Empty<IntPtr>());
            return new(GuardHandle(raw), Ty.Handle("udp"));
        }

        if (cls == "Udp" && m.Name == "Listen")
        {
            var port = EmitExpr(m.Args[0]);
            var raw = Call(_udpListenTy, _udpListenFn, new[] { port.V });
            return new(GuardHandle(raw), Ty.Handle("udp"));
        }

        if (cls == "Http" && m.Name == "Get")
        {
            var url = EmitExpr(m.Args[0]);
            var raw = Call(_httpGetTy, _httpGetFn, new[] { url.V });
            var resTy = Ty.NullableOf(Ty.Str);
            return new(TempReg(raw, resTy), resTy, Prov.Temp);
        }

        if (cls == "Http" && m.Name == "Post")
        {
            var url = EmitExpr(m.Args[0]);
            var body = EmitExpr(m.Args[1]);
            var raw = Call(_httpPostTy, _httpPostFn, new[] { url.V, body.V });
            var resTy = Ty.NullableOf(Ty.Str);
            return new(TempReg(raw, resTy), resTy, Prov.Temp);
        }

        if (cls == "Task" && m.Name == "Delay")
        {
            var ms = EmitExpr(m.Args[0]);
            return new(Call(_taskDelayTy, _taskDelayFn, new[] { ms.V }), Ty.Task(Ty.Void), Prov.Static);
        }

        if (cls == "Task" && m.Name == "WhenAll")
            return EmitWhenAll(m);

        if (cls == "StringBuilder" && m.Name == "New")
            return new(TempReg(Call(_sbNewTy, _sbNewFn, Array.Empty<IntPtr>()), Ty.Handle("StringBuilder")), Ty.Handle("StringBuilder"), Prov.Temp);

        if (cls == "Http" && m.Name == "Status")
            return new(Call(_httpStatusTy, _httpStatusFn, Array.Empty<IntPtr>()), Ty.Int);

        if (cls == "Http" && m.Name is "Listen" or "ListenRaw")
        {
            var port = EmitExpr(m.Args[0]);
            var raw = Call(_tcpListenTy, _tcpListenFn, new[] { port.V });
            return new(GuardHandle(raw), Ty.Handle(m.Name == "Listen" ? "httpl" : "rawhttpl"));
        }

        throw new Exception($"'{cls}.{m.Name}' is not available yet");
    }

    private Val EmitHandleMethod(Val target, Method m)
    {
        var h64 = PtrToInt64(target.V);

        if (m.Name == "Accept" && target.Ty.Name == "listener")
        {
            var raw = Call(_tcpAcceptTy, _tcpAcceptFn, new[] { h64 });
            return new(GuardHandle(raw), Ty.Handle("Client"));
        }

        if (m.Name == "Close" && target.Ty.Name is "listener" or "httpl" or "rawhttpl" or "udp")
        {
            CallV(_tcpCloseTy, _tcpCloseFn, new[] { h64 });
            return new(IntPtr.Zero, Ty.Void);
        }

        if (m.Name == "Accept" && target.Ty.Name is "httpl" or "rawhttpl")
        {
            bool raw = target.Ty.Name == "rawhttpl";
            var res = Call(_httpAcceptTy, _httpAcceptFn, new[] { h64, ConstI32(raw ? 1 : 0) });
            return new(GuardHandle(res), Ty.Handle(raw ? "RawHttpPacket" : "HttpPacket"));
        }

        if (m.Name == "AcceptTimeout" && target.Ty.Name is "httpl" or "rawhttpl")
        {
            bool raw = target.Ty.Name == "rawhttpl";
            var ms = EmitExpr(m.Args[0]);
            var res = Call(_httpAcceptToTy, _httpAcceptToFn, new[] { h64, ConstI32(raw ? 1 : 0), ms.V });
            return new(GuardHandle(res), Ty.NullableOf(Ty.Handle(raw ? "RawHttpPacket" : "HttpPacket")));
        }

        if (m.Name == "RecvTimeout" && target.Ty.Name == "Client")
        {
            var ms = EmitExpr(m.Args[0]);
            return GuardedTimeoutString(Call(_lineTimeoutTy, _lineTimeoutFn, new[] { h64, ms.V }));
        }

        if (m.Name == "SendBytes" && target.Ty.Name == "Client")
        {
            var b = EmitExpr(m.Args[0]);
            return new(GuardNetCount(Call(_sendBytesTy, _sendBytesFn, new[] { h64, PtrToInt64(b.V) })), Ty.Int);
        }

        if (m.Name == "RecvBytes" && target.Ty.Name == "Client")
        {
            var b = EmitExpr(m.Args[0]);
            return new(Trunc(Call(_recvBytesTy, _recvBytesFn, new[] { h64, PtrToInt64(b.V) }), _i32), Ty.Int);
        }

        if (m.Name == "RecvAll" && target.Ty.Name == "Client")
        {
            var max = EmitExpr(m.Args[0]);
            var raw = Call(_recvAllTy, _recvAllFn, new[] { h64, SExt(max.V, _i64) });
            return new(TempReg(raw, Ty.Buffer), Ty.Buffer, Prov.Temp);
        }

        if (target.Ty.Name == "StringBuilder")
            return EmitBuilderMethod(target, m);

        if (Ty.IsMap(target.Ty))
            return EmitMapMethod(target, m);

        if (m.Name == "OnAccept" && target.Ty.Name is "listener" or "httpl" or "rawhttpl" or "udp")
            return EmitOnAccept(h64, m);

        if (target.Ty.Name == "HttpPacket")
            return EmitPacketMethod(h64, m);

        if (target.Ty.Name == "Cookies")
            return EmitCookiesMethod(h64, m);

        if (target.Ty.Name == "RawHttpPacket")
            return EmitRawPacketMethod(h64, m);

        if (m.Name == "Send")
        {
            var v = EmitExpr(m.Args[0]);
            var len = Call(_strlenTy, _strlenFn, new[] { v.V });
            var raw = Call(_tcpSendTy, _tcpSendFn, new[] { h64, v.V, len });
            return new(GuardNetCount(raw), Ty.Int);
        }

        if (m.Name == "Recv")
        {
            var fn = target.Ty.Name == "udp" ? (_udpRecvFn, _udpRecvTy) : (_tcpLineFn, _tcpLineTy);
            var raw = Call(fn.Item2, fn.Item1, new[] { h64 });
            return GuardNetString(raw);
        }

        if (m.Name == "SendTo")
        {
            var host = EmitExpr(m.Args[0]);
            var port = EmitExpr(m.Args[1]);
            var msg = EmitExpr(m.Args[2]);
            var len = Call(_strlenTy, _strlenFn, new[] { msg.V });
            var raw = Call(_udpSendToTy, _udpSendToFn, new[] { h64, host.V, port.V, msg.V, len });
            return new(GuardNetCount(raw), Ty.Int);
        }

        if (m.Name == "Close")
        {
            CallV(_tcpCloseTy, _tcpCloseFn, new[] { h64 });
            return new(IntPtr.Zero, Ty.Void);
        }

        throw new Exception($"'{m.Name}' is not available on a {target.Ty.Name}");
    }

    private Val EmitMapMethod(Val target, Method m)
    {
        var mh = PtrToInt64(target.V);
        switch (m.Name)
        {
            case "Contains":
                {
                    var k = BoxMapKeyLookup(target.Ty.KeyTy!, EmitExpr(m.Args[0]));
                    return new(Call(_mapContainsTy, _mapContainsFn, new[] { mh, k }), Ty.Bool);
                }
            case "Remove":
                {
                    var k = BoxMapKeyLookup(target.Ty.KeyTy!, EmitExpr(m.Args[0]));
                    CallV(_mapRemoveTy, _mapRemoveFn, new[] { mh, k });
                    return new(IntPtr.Zero, Ty.Void);
                }
            case "Clear":
                CallV(_mapClearTy, _mapClearFn, new[] { mh });
                return new(IntPtr.Zero, Ty.Void);

            case "Keys":
            case "Values":
                {
                    bool wantValues = m.Name == "Values";
                    var kindTy = wantValues ? target.Ty.Elem! : target.Ty.KeyTy!;
                    var kindStr = ConstI32(kindTy == Ty.Str ? 1 : 0);
                    var list = Call(_mapItemsTy, _mapItemsFn, new[] { mh, ConstI32(wantValues ? 1 : 0), kindStr });
                    return new(TempReg(list, Ty.List(kindTy)), Ty.List(kindTy), Prov.Temp);
                }
        }
        throw new Exception($"'{m.Name}' is not available on a map");
    }

    private Val EmitBuilderMethod(Val target, Method m)
    {
        switch (m.Name)
        {
            case "Add":
                {
                    var v = EmitExpr(m.Args[0]);
                    if (v.Ty == Ty.Str)
                        CallV(_sbAddStrTy, _sbAddStrFn, new[] { target.V, v.V });
                    else if (v.Ty == Ty.Int)
                        CallV(_sbAddIntTy, _sbAddIntFn, new[] { target.V, SExt(v.V, _i64) });
                    else if (v.Ty == Ty.Float)
                        CallV(_sbAddFloatTy, _sbAddFloatFn, new[] { target.V, v.V });
                    else
                        CallV(_sbAddBufTy, _sbAddBufFn, new[] { target.V, PtrToInt64(v.V) });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "ToString":
                return new(TempReg(Call(_sbStrTy, _sbStrFn, new[] { target.V }), Ty.Str), Ty.Str, Prov.Temp);

            case "Clear":
                return new(IntPtr.Zero, Ty.Void);
        }

        throw new Exception($"'{m.Name}' is not available on a builder");
    }

    private Val EmitOnAccept(IntPtr listener, Method m)
    {
        var lam = (LamLit)m.Args[0];
        var kind = EmitExpr(m.Target).Ty.Name;
        bool raw = kind == "rawhttpl";
        bool tcp = kind == "listener";

        var savedShared = _sharedCaptures;
        _sharedCaptures = true;
        if (!_lambdas.TryGetValue(lam, out var info))
            info = EmitLambda(lam);
        _sharedCaptures = savedShared;

        var savedFn = _curFn; var savedDecl = _curDecl; var savedType = _curType;
        var savedEntry = _entryBB; var savedCodeBB = LLVM.LLVMGetInsertBlock(_b); var savedAbBB = LLVM.LLVMGetInsertBlock(_ab);
        var savedScopes = _scopes; var savedSlots = _fnSlots; var savedTemps = _temps;
        var savedErr = _errFlag; var savedCatch = _catchBB; var savedLoops = _loopExit;
        var savedLamRetTy = _lamRetTy; var savedLamEnv = _lamEnvParam;

        var loopTy = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr }, 1, false);
        var loopFn = LLVM.LLVMAddFunction(_module, $"hs.oaloop{_lambdas.Count}", loopTy);
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, loopFn, "entry");
        var body = LLVM.LLVMAppendBasicBlockInContext(_ctx, loopFn, "body");

        _curFn = loopFn; _curDecl = null; _curType = null; _entryBB = entry;
        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMPositionBuilderAtEnd(_b, body);

        _scopes = new List<Dictionary<string, VarSlot>> { new() };
        _fnSlots = new List<VarSlot>();
        _temps = new List<(IntPtr, Ty)>();
        _catchBB = null;
        _loopExit = new List<(IntPtr, IntPtr)>();
        _errFlag = Alloca(_i32, "errflag");
        StoreAb(ConstI32(0), _errFlag);
        _lamRetTy = null;
        _lamEnvParam = IntPtr.Zero;

        var loopEnv = LLVM.LLVMGetParam(loopFn, 0);

        var loopBB = Block("loop");
        Br(loopBB);

        At(loopBB);

        var lh = PtrToInt64(Load(_i8ptr, loopEnv));

        IntPtr packetPtr;
        IntPtr bad;

        if (kind == "udp")
        {

            var msg = Call(_udpRecvTy, _udpRecvFn, new[] { lh });
            bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, msg, Null());
            packetPtr = msg;
        }
        else
        {
            IntPtr res = tcp
                ? Call(_tcpAcceptTy, _tcpAcceptFn, new[] { lh })
                : Call(_httpAcceptTy, _httpAcceptFn, new[] { lh, ConstI32(raw ? 1 : 0) });
            bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, res, ConstI64(0));
            packetPtr = Int64ToPtr(res);
        }

        var stopBB = Block("stop");
        var runBB = Block("run");
        CondBr(bad, stopBB, runBB);

        At(runBB);
        var lamEnv = Call(_mallocTy, _mallocFn, new[] { ConstI64(info.EnvSize) });
        for (int i = 0; i < info.Captures.Count; i++)
        {
            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, lamEnv, (uint)i, T("cap"));
            if (info.ByRefs[i])
            {

                Store(info.Captures[i].Ptr, fieldPtr);
            }
            else
            {
                var val = Load(TyLLVM(info.Captures[i].Ty), info.Captures[i].Ptr);
                if (info.Captures[i].Ty.Kind == UserKind.Struct && info.Captures[i].Ty.Owned)
                    val = DeepCopyVal(val, info.Captures[i].Ty);
                Store(val, fieldPtr);
            }
        }

        var paramPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, lamEnv, (uint)info.Captures.Count, T("prm"));
        Store(packetPtr, paramPtr);

        var task = Call(_rtTaskNewTy, _rtTaskNewFn, new[] { info.Fn, lamEnv });
        CallV(_rtTaskSubmitTy, _rtTaskSubmitFn, new[] { task });
        Br(loopBB);

        At(stopBB);
        CallV(_freeTy, _freeFn, new[] { loopEnv });
        Ret(Null());

        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMBuildBr(_ab, body);

        _curFn = savedFn; _curDecl = savedDecl; _curType = savedType;
        _entryBB = savedEntry;
        At(savedCodeBB);
        LLVM.LLVMPositionBuilderAtEnd(_ab, savedAbBB);
        _scopes = savedScopes; _fnSlots = savedSlots; _temps = savedTemps;
        _errFlag = savedErr; _catchBB = savedCatch; _loopExit = savedLoops;
        _lamRetTy = savedLamRetTy; _lamEnvParam = savedLamEnv;

        var envPtr = Call(_mallocTy, _mallocFn, new[] { ConstI64(8) });
        Store(listener, envPtr);

        CallV(_spawnTy, _spawnFn, new[] { loopFn, envPtr });

        return new(IntPtr.Zero, Ty.Void);
    }

    private Val EmitCookiesMethod(IntPtr h64, Method m)
    {
        if (m.Name == "Get")
        {
            var name = EmitExpr(m.Args[0]);
            var v = Call(_httpCookieGetTy, _httpCookieGetFn, new[] { h64, name.V });
            var ht = Ty.NullableOf(Ty.Str);
            return new(TempReg(v, ht), ht, Prov.Temp);
        }

        var n = EmitExpr(m.Args[0]);
        var val = EmitExpr(m.Args[1]);
        IntPtr raw;
        if (m.Args.Count == 3)
        {
            var opt = EmitExpr(m.Args[2]);
            raw = Call(_httpCookieSetTy, _httpCookieSetFn, new[]
            {
                h64, n.V, val.V,
                CookieField(opt.V, "Secure", _i8),
                CookieField(opt.V, "HttpOnly", _i8),
                CookieField(opt.V, "SameSite", _i32),
                CookieField(opt.V, "Path", _i8ptr),
                CookieField(opt.V, "Domain", _i8ptr),
                CookieField(opt.V, "MaxAge", _i32)
            });
        }
        else
        {
            raw = Call(_httpCookieSetDefTy, _httpCookieSetDefFn, new[] { h64, n.V, val.V });
        }
        return new(GuardNetCount(raw), Ty.Void);
    }

    private IntPtr CookieField(IntPtr optPtr, string name, IntPtr llty)
    {
        var td = _typeDecls["CookieOptions"];
        int idx = td.Fields.FindIndex(f => f.Name == name);
        var gep = StructGEP(td, optPtr, (uint)idx);
        return LLVM.LLVMBuildLoad2(_b, llty, gep, T(name));
    }

    private Val EmitPacketMethod(IntPtr h64, Method m)    {
        switch (m.Name)
        {
            case "Method":
                return new(TempReg(Call(_httpMethodTy, _httpMethodFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Path":
                return new(TempReg(Call(_httpPathTy, _httpPathFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Header":
                {
                    var name = EmitExpr(m.Args[0]);
                    if (m.Args.Count == 2)
                    {
                        var val = EmitExpr(m.Args[1]);
                        var set = Call(_httpSetHeaderTy, _httpSetHeaderFn, new[] { h64, name.V, val.V });
                        return new(GuardNetCount(set), Ty.Int);
                    }
                    var v = Call(_httpHeaderTy, _httpHeaderFn, new[] { h64, name.V });
                    var ht = Ty.NullableOf(Ty.Str);
                    return new(TempReg(v, ht), ht, Prov.Temp);
                }

            case "Body":
                return new(TempReg(Call(_httpBodyTy, _httpBodyFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Source":
                return new(TempReg(Call(_httpSourceTy, _httpSourceFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Dest":
                return new(TempReg(Call(_httpDestTy, _httpDestFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Respond":
                {
                    var status = EmitExpr(m.Args[0]);
                    var body = EmitExpr(m.Args[1]);
                    var raw = Call(_httpRespondTy, _httpRespondFn, new[] { h64, status.V, body.V });
                    return new(GuardNetCount(raw), Ty.Int);
                }

            case "Forward":
                {
                    var host = EmitExpr(m.Args[0]);
                    var port = EmitExpr(m.Args[1]);
                    var raw = Call(_httpForwardTy, _httpForwardFn, new[] { h64, host.V, port.V });
                    return new(GuardNetCount(raw), Ty.Int);
                }

            case "Close":
                CallV(_httpReqCloseTy, _httpReqCloseFn, new[] { h64 });
                return new(IntPtr.Zero, Ty.Void);
        }

        throw new Exception($"'{m.Name}' is not available on an http packet");
    }

    private Val EmitRawPacketMethod(IntPtr h64, Method m)
    {
        switch (m.Name)
        {
            case "Source":
                return new(TempReg(Call(_httpSourceTy, _httpSourceFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "Dest":
                return new(TempReg(Call(_httpDestTy, _httpDestFn, new[] { h64 }), Ty.Str), Ty.Str, Prov.Temp);

            case "ToHttpPacket":
                {
                    var raw = Call(_httpToPacketTy, _httpToPacketFn, new[] { h64 });
                    return new(GuardHandle(raw), Ty.Handle("HttpPacket"));
                }

            case "Forward":
                {
                    var host = EmitExpr(m.Args[0]);
                    var port = EmitExpr(m.Args[1]);
                    var raw = Call(_httpForwardTy, _httpForwardFn, new[] { h64, host.V, port.V });
                    return new(GuardNetCount(raw), Ty.Int);
                }

            case "Close":
                CallV(_httpReqCloseTy, _httpReqCloseFn, new[] { h64 });
                return new(IntPtr.Zero, Ty.Void);
        }

        throw new Exception($"'{m.Name}' is not available on a raw http packet");
    }

    private IntPtr GuardNetCount(IntPtr raw)
    {
        var bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, raw, ConstI64(0));

        var ok = Block("net_ok");
        var fail = Block("net_fail");
        var merge = Block("net_merge");

        CondBr(bad, fail, ok);

        At(ok);
        var okVal = Trunc(raw, _i32);
        var okEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(fail);
        CallV(_errorSetTy, _errorSetFn, new[] { Str("network send failed") });
        Store(ConstI32(1), _errFlag);
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i32, T("nc"));
        LLVM.LLVMAddIncoming(phi, new[] { okVal, ConstI32(0) }, new[] { okEnd, failEnd }, 2);
        return phi;
    }

    private Val EmitTaskRun(Method m, bool forget = false)
    {
        var lam = (LamLit)m.Args[0];

        if (!_lambdas.TryGetValue(lam, out var info))
            info = EmitLambda(lam);

        var env = Call(_mallocTy, _mallocFn, new[] { ConstI64(info.EnvSize) });

        foreach (var (slot, i) in info.Captures.Select((s, i) => (s, i)))
        {
            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, env, (uint)i, T("cap"));
            if (info.ByRefs[i])
            {
                Store(slot.Ptr, fieldPtr);
                continue;
            }
            var val = Load(TyLLVM(slot.Ty), slot.Ptr);
            if (slot.Ty.Kind == UserKind.Struct && slot.Ty.Owned) val = DeepCopyVal(val, slot.Ty);
            Store(val, fieldPtr);
            if (slot.Owned && slot.Ty.Kind != UserKind.Struct) Store(ConstI32(0), slot.Flag);
        }

        var task = Call(_rtTaskNewTy, _rtTaskNewFn, new[] { info.Fn, env });
        if (forget)
        {

            bool freeResult = info.Ret.Owned;
            CallV(_rtTaskForgetTy, _rtTaskForgetFn, new[] { task, ConstI32(freeResult ? 1 : 0) });
        }
        else
            CallV(_rtTaskSubmitTy, _rtTaskSubmitFn, new[] { task });

        return new(task, Ty.Task(info.Ret), Prov.Static);
    }

    private LamInfo EmitLambda(LamLit lam)
    {
        var info = new LamInfo { Ret = lam.RetTy ?? Ty.Void };
        info.Captures = CollectCaptures(lam);
        info.ByRefs = info.Captures.Select(CaptureByRef).ToList();
        if (lam.Params.Count == 1) info.Param = lam.Params[0];

        var fieldTys = new List<IntPtr>();
        var fieldValTys = new List<Ty>();
        for (int i = 0; i < info.Captures.Count; i++)
        {
            fieldTys.Add(info.ByRefs[i] ? _i8ptr : TyLLVM(info.Captures[i].Ty));
            fieldValTys.Add(info.ByRefs[i] ? Ty.Str  : info.Captures[i].Ty);
        }
        if (info.Param != null)
        {
            fieldTys.Add(TyLLVM(info.Param.Type));
            fieldValTys.Add(info.Param.Type);
        }
        info.EnvTy = LLVM.LLVMStructCreateNamed(_ctx, $"lam.env{_lambdas.Count}");
        if (fieldTys.Count > 0)
            LLVM.LLVMStructSetBody(info.EnvTy, fieldTys.ToArray(), (uint)fieldTys.Count, false);
        info.EnvSize = EnvSize(fieldValTys);

        var savedFn = _curFn;
        var savedDecl = _curDecl;
        var savedEntry = _entryBB;
        var savedCodeBB = LLVM.LLVMGetInsertBlock(_b);
        var savedAbBB = LLVM.LLVMGetInsertBlock(_ab);
        var savedScopes = _scopes;
        var savedSlots = _fnSlots;
        var savedTemps = _temps;
        var savedErr = _errFlag;
        var savedCatch = _catchBB;
        var savedLoopExit = _loopExit;
        var savedLamRetTy = _lamRetTy;
        var savedLamEnv = _lamEnvParam;

        var fnTy = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr }, 1, false);
        var fn = LLVM.LLVMAddFunction(_module, $"hs_lam{_lambdas.Count}", fnTy);
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "entry");
        var body = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "body");

        _curFn = fn;
        _curDecl = null;
        _entryBB = entry;
        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMPositionBuilderAtEnd(_b, body);

        _scopes = new List<Dictionary<string, VarSlot>> { new() };
        _fnSlots = new List<VarSlot>();
        _temps = new List<(IntPtr, Ty)>();
        _catchBB = null;
        _loopExit = new List<(IntPtr, IntPtr)>();
        _errFlag = Alloca(_i32, "errflag");
        StoreAb(ConstI32(0), _errFlag);

        _lamRetTy = info.Ret;
        _lamEnvParam = LLVM.LLVMGetParam(fn, 0);

        for (int i = 0; i < info.Captures.Count; i++)
        {
            var cap = info.Captures[i];

            var slot = new VarSlot { Name = cap.Name, Ty = cap.Ty, Owned = cap.Ty.Owned && !info.ByRefs[i], ByRef = info.ByRefs[i] };
            slot.Ptr = Alloca(_i8ptr, cap.Name + ".ref");
            _scopes[^1][cap.Name] = slot;

            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, _lamEnvParam, (uint)i, T("cap"));
            Store(Load(_i8ptr, fieldPtr), slot.Ptr);
        }

        if (info.Param != null)
        {
            var p = info.Param;
            var slot = NewSlot(p.Name, p.Type, owned: p.Type.Owned);
            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, _lamEnvParam, (uint)info.Captures.Count, T("prm"));
            Store(Load(TyLLVM(p.Type), fieldPtr), slot.Ptr);
            if (slot.Owned) Store(ConstI32(1), slot.Flag);
        }

        EmitStmtList(lam.Body);

        if (!Terminated())
        {
            FreeTemps();
            FreeAllOwned(null);
            CallV(_freeTy, _freeFn, new[] { _lamEnvParam });
            Ret(Null());
        }

        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMBuildBr(_ab, body);

        _curFn = savedFn;
        _curDecl = savedDecl;
        _entryBB = savedEntry;
        At(savedCodeBB);
        LLVM.LLVMPositionBuilderAtEnd(_ab, savedAbBB);
        _scopes = savedScopes;
        _fnSlots = savedSlots;
        _temps = savedTemps;
        _errFlag = savedErr;
        _catchBB = savedCatch;
        _loopExit = savedLoopExit;
        _lamRetTy = savedLamRetTy;
        _lamEnvParam = savedLamEnv;

        info.Fn = fn;
        _lambdas[lam] = info;
        return info;
    }

    private List<VarSlot> CollectCaptures(LamLit lam)
    {
        var names = new List<string>();
        CollectIdents(lam.Body, names);
        var seen = new HashSet<string>();
        var caps = new List<VarSlot>();

        foreach (var n in names)
        {
            if (seen.Contains(n)) continue;
            var slot = FindSlot(n);
            if (slot == null) continue;
            seen.Add(n);
            caps.Add(slot);
        }

        return caps;
    }

    private bool CaptureByRef(VarSlot slot) =>
        _curDecl == null && _lamRetTy == null && SlotDepth(slot) <= 1
        && (!slot.Owned || _sharedCaptures);

    private static void CollectIdents(List<Stmt> stmts, List<string> names)
    {
        foreach (var s in stmts) CollectIdents(s, names);
    }

    private static void CollectIdents(Stmt s, List<string> names)
    {
        switch (s)
        {
            case VarDecl d: CollectIdents(d.Init, names); break;
            case Assign a: CollectIdents(a.Target, names); CollectIdents(a.Value, names); break;
            case IncDec i: CollectIdents(i.Target, names); break;
            case ExprStmt e: CollectIdents(e.E, names); break;
            case If f:
                CollectIdents(f.Cond, names); CollectIdents(f.Then, names);
                if (f.Else != null) CollectIdents(f.Else, names);
                break;
            case While w: CollectIdents(w.Cond, names); CollectIdents(w.Body, names); break;
            case For f:
                if (f.Init != null) CollectIdents(f.Init, names);
                if (f.Cond != null) CollectIdents(f.Cond, names);
                CollectIdents(f.Body, names);
                if (f.Step != null) CollectIdents(f.Step, names);
                break;
            case Foreach fe: CollectIdents(fe.Iter, names); CollectIdents(fe.Body, names); break;
            case Return r: if (r.Value != null) CollectIdents(r.Value, names); break;
            case TryCatch tc: CollectIdents(tc.Try, names); CollectIdents(tc.Catch, names); break;
            case BlockStmt b: CollectIdents(b.Body, names); break;
            case Lock lk: CollectIdents(lk.Target, names); CollectIdents(lk.Body, names); break;
        }
    }

    private static void CollectIdents(Expr e, List<string> names)
    {
        switch (e)
        {
            case Ident id: names.Add(id.Name); break;
            case InterpLit it: foreach (var p in it.Parts) CollectIdents(p, names); break;
            case Un u: CollectIdents(u.E, names); break;
            case Bin b: CollectIdents(b.L, names); CollectIdents(b.R, names); break;
            case Index ix: CollectIdents(ix.Target, names); CollectIdents(ix.Idx, names); break;
            case Call c: foreach (var a in c.Args) CollectIdents(a, names); break;
            case Method m:
                CollectIdents(m.Target, names);
                foreach (var a in m.Args) CollectIdents(a, names);
                break;
            case Prop p: CollectIdents(p.Target, names); break;
            case NewLit nl: foreach (var fi in nl.Fields) CollectIdents(fi.Value, names); break;
            case MapLit ml: foreach (var p in ml.Pairs) { CollectIdents(p.Key, names); CollectIdents(p.Value, names); } break;
            case Cast c: CollectIdents(c.Value, names); break;
            case Coalesce co: CollectIdents(co.L, names); CollectIdents(co.R, names); break;
            case Cond cd: CollectIdents(cd.CondExpr, names); CollectIdents(cd.Then, names); CollectIdents(cd.Else, names); break;
            case ListLit ll: foreach (var i in ll.Items) CollectIdents(i, names); break;
            case AwaitExpr aw: CollectIdents(aw.Task, names); break;
            case LamLit l:

                var inner = new List<string>();
                CollectIdents(l.Body, inner);
                var ps = l.Params.Select(p => p.Name).ToHashSet();
                foreach (var nm in inner)
                    if (!ps.Contains(nm)) names.Add(nm);
                break;
        }
    }

    private long EnvSize(IEnumerable<Ty> fields)
    {
        long offset = 0, maxAlign = 1;

        foreach (var f in fields)
        {

            long size = f.Kind == UserKind.Class ? 8 : SizeOfTy(f);
            long align = f.Kind == UserKind.Class ? 8 : AlignOfTy(f);
            offset = (offset + align - 1) / align * align;
            offset += size;
            if (align > maxAlign) maxAlign = align;
        }

        return (offset + maxAlign - 1) / maxAlign * maxAlign;
    }

    private Val EmitAwait(AwaitExpr aw)
    {
        var t = EmitExpr(aw.Task);
        var raw = Call(_rtTaskJoinTy, _rtTaskJoinFn, new[] { t.V });
        var ret = t.Ty.Elem!;

        if (ret == Ty.Int || ret == Ty.Bool)
            return new(Trunc(PtrToInt64(raw), _i32), ret);
        if (ret == Ty.Float)
        {
            var v = Load(_double, raw);
            CallV(_freeTy, _freeFn, new[] { raw });
            return new(v, Ty.Float);
        }
        if (ret == Ty.Str || ret.Elem != null)
            return new(TempReg(raw, ret), ret, Prov.Temp);

        return new(IntPtr.Zero, Ty.Void);
    }

    private Val EmitCast(Cast c)
    {
        var v = EmitExpr(c.Value);

        if (c.Type == Ty.Buffer && v.Ty == Ty.Str)
            return new(TempReg(Call(_bufFromStrTy, _bufFromStrFn, new[] { v.V }), Ty.Buffer), Ty.Buffer, Prov.Temp);

        if (c.Type == Ty.Buffer && v.Ty == Ty.Int)
            return new(TempReg(Call(_bufNewTy, _bufNewFn, new[] { SExt(v.V, _i64) }), Ty.Buffer), Ty.Buffer, Prov.Temp);

        if (c.Type == Ty.Str && v.Ty == Ty.Buffer)
            return new(TempReg(Call(_bufToStrTy, _bufToStrFn, new[] { PtrToInt64(v.V) }), Ty.Str), Ty.Str, Prov.Temp);

        if (c.Type == Ty.Int)
        {
            if (v.Ty == Ty.Float)
                return new(LLVM.LLVMBuildFPToSI(_b, v.V, _i32, T("f2i")), Ty.Int);
            if (v.Ty == Ty.Str)
                return new(Call(_atoiTy, _atoiFn, new[] { v.V }), Ty.Int);
            return new(v.V, Ty.Int);
        }

        if (c.Type == Ty.Float)
        {
            if (v.Ty == Ty.Int)
                return new(SIToFP(v.V), Ty.Float);
            if (v.Ty == Ty.Str)
                return new(Call(_atofTy, _atofFn, new[] { v.V }), Ty.Float);
            return v;
        }

        if (v.Ty == Ty.Str)
            return new(TempReg(Call(_strdupTy, _strdupFn, new[] { v.V }), Ty.Str), Ty.Str, Prov.Temp);
        if (v.Ty == Ty.Float)
            return new(TempReg(Call(_ftoaTy, _ftoaFn, new[] { v.V }), Ty.Str), Ty.Str, Prov.Temp);
        return new(TempReg(Call(_itoaTy, _itoaFn, new[] { v.V }), Ty.Str), Ty.Str, Prov.Temp);
    }

    private IntPtr BoxForTask(Val v, Ty ret)
    {
        if (ret == Ty.Int || ret == Ty.Bool)
            return Int64ToPtr(ZExt(v.V, _i64));
        if (ret == Ty.Float)
        {
            var buf = Call(_mallocTy, _mallocFn, new[] { ConstI64(8) });
            Store(v.V, buf);
            return buf;
        }
        if (ret.Owned) return TakeOwnership(v);
        return Null();
    }

    private void EmitLambdaReturn(Return r)
    {
        IntPtr boxed;

        if (r.Value == null)
        {
            boxed = Null();
        }
        else
        {
            var v = EmitExpr(r.Value);
            boxed = BoxForTask(v, _lamRetTy!);
        }

        FreeTemps();
        FreeAllOwned(r.Value is Ident id ? FindSlot(id.Name) : null);
        CallV(_freeTy, _freeFn, new[] { _lamEnvParam });
        Ret(boxed);
    }

    private Val EmitMethod(Method m)
    {
        if (m.Target is Ident t2 && t2.Name == "Task" && m.Name == "Run")
            return EmitTaskRun(m);
        if (m.Target is Ident ns && IsStaticClass(ns.Name))
            return EmitNetStatic(ns.Name, m);

        if (m.NullCond)
            return EmitNullCond(m.Target, m.ResultTy!, t =>
                t.Ty.Kind is UserKind.Class or UserKind.Struct
                    ? EmitUserMethodCall(t, m)
                    : EmitListMethod(t, m));

        var target = EmitExpr(m.Target);

        if (Ty.IsMap(target.Ty))
            return EmitMapMethod(target, m);

        if (target.Ty == Ty.Str)
            return EmitStringMethod(target, m);

        if (target.Ty.Kind is UserKind.Class or UserKind.Struct)
            return EmitUserMethodCall(target, m);

        if (Ty.IsHandle(target.Ty))
            return EmitHandleMethod(target, m);

        return EmitListMethod(target, m);
    }

    private Val EmitNullCond(Expr target, Ty resTy, Func<Val, Val> emitMember)
    {
        var t = EmitExpr(target);

        if (!t.Ty.Nullable)
            return Coerce(emitMember(t), resTy);

        var isNull = IsNullVal(t);

        var innerV = t.Ty.Elem!.IsPtrKind
            ? t.V
            : LLVM.LLVMBuildExtractValue(_b, t.V, 0, T("unw"));

        var memberBB = Block("nc_some");
        var doneBB = Block("nc_done");

        IntPtr nullVal = IntPtr.Zero, nullEnd = IntPtr.Zero;

        if (resTy == Ty.Void)
        {
            CondBr(isNull, doneBB, memberBB);
        }
        else
        {
            var nullBB = Block("nc_none");
            CondBr(isNull, nullBB, memberBB);

            At(nullBB);
            nullVal = DefaultOf(resTy);
            nullEnd = LLVM.LLVMGetInsertBlock(_b);
            Br(doneBB);
        }

        At(memberBB);
        int tempStart = _temps.Count;
        var member = Coerce(emitMember(new(innerV, t.Ty.Elem!, t.Prov, t.Src)), resTy);

        var branchTemps = _temps.Skip(tempStart).ToList();
        _temps.RemoveRange(tempStart, _temps.Count - tempStart);
        bool adopted = branchTemps.Any(x => x.V == member.V);

        foreach (var (tv, tty) in branchTemps)
            if (!adopted || tv != member.V)
                FreeOwnedVal(tv, tty);

        var memberEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        if (resTy == Ty.Void) return new(IntPtr.Zero, Ty.Void);

        var phi = LLVM.LLVMBuildPhi(_b, TyLLVM(resTy), T("nc"));
        LLVM.LLVMAddIncoming(phi, new[] { member.V, nullVal }, new[] { memberEnd, nullEnd }, 2);

        if (adopted && resTy.Owned)
            _temps.Add((phi, resTy));

        return new(phi, resTy);
    }

    private Val EmitCond(Cond cd)
    {
        var cond = BoolOf(EmitExpr(cd.CondExpr));
        var resTy = cd.Ty!;

        var thenBB = Block("cd_then");
        var elseBB = Block("cd_else");
        var doneBB = Block("cd_done");

        CondBr(cond, thenBB, elseBB);

        IntPtr thenVal = IntPtr.Zero, thenEnd = IntPtr.Zero;
        IntPtr elseVal = IntPtr.Zero, elseEnd = IntPtr.Zero;

        At(thenBB);
        int tStart = _temps.Count;
        var tv = Coerce(EmitExpr(cd.Then), resTy);
        AdoptBranchTemps(tStart, tv, resTy);
        thenVal = tv.V;
        thenEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(elseBB);
        int eStart = _temps.Count;
        var ev = Coerce(EmitExpr(cd.Else), resTy);
        AdoptBranchTemps(eStart, ev, resTy);
        elseVal = ev.V;
        elseEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        var phi = LLVM.LLVMBuildPhi(_b, TyLLVM(resTy), T("cd"));
        LLVM.LLVMAddIncoming(phi, new[] { thenVal, elseVal }, new[] { thenEnd, elseEnd }, 2);
        return new(phi, resTy);
    }

    private void AdoptBranchTemps(int start, Val val, Ty resTy)
    {
        var mine = _temps.Skip(start).ToList();
        _temps.RemoveRange(start, _temps.Count - start);
        bool adopted = mine.Any(x => x.V == val.V);
        foreach (var (tv, tty) in mine)
            if (!adopted || tv != val.V)
                FreeOwnedVal(tv, tty);
        if (adopted)
            _temps.Add((val.V, resTy));
    }

    private Val EmitCoalesce(Coalesce co)
    {
        var resTy = co.Ty!;
        var l = EmitExpr(co.L);
        var isNull = IsNullVal(l);

        var left = resTy.Nullable ? l.V : UnwrapV(l);

        var rhsBB = Block("co_rhs");
        var doneBB = Block("co_done");
        var preBB = LLVM.LLVMGetInsertBlock(_b);

        CondBr(isNull, rhsBB, doneBB);

        At(rhsBB);
        int tempStart = _temps.Count;
        var r = Coerce(EmitExpr(co.R), resTy);

        var rhsTemps = _temps.Skip(tempStart).ToList();
        _temps.RemoveRange(tempStart, _temps.Count - tempStart);
        bool adopted = rhsTemps.Any(x => x.V == r.V);
        foreach (var (tv, tty) in rhsTemps)
            if (!adopted || tv != r.V)
                FreeOwnedVal(tv, tty);

        var rhsEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(doneBB);

        At(doneBB);
        var phi = LLVM.LLVMBuildPhi(_b, TyLLVM(resTy), T("co"));
        LLVM.LLVMAddIncoming(phi, new[] { left, r.V }, new[] { preBB, rhsEnd }, 2);

        if (adopted && resTy.Owned)
            _temps.Add((phi, resTy));

        return new(phi, resTy, Prov.Borrow);
    }

    private (IntPtr addr, Ty? ty) AddrOf(Expr e)
    {
        switch (e)
        {
            case Ident id when id.ThisField:
                {
                    var ts = FindSlot("this")!;
                    var td = _typeDecls[ts.Ty.Name];
                    var obj = ts.Ty.Kind == UserKind.Class ? Load(_i8ptr, ts.Ptr) : ts.Ptr;
                    var idx = (uint)id.ThisIndex;
                    return (StructGEP(td, obj, idx), td.Fields[id.ThisIndex].Type);
                }

            case Ident id:
                {
                    var slot = FindSlot(id.Name);
                    if (slot == null) return (IntPtr.Zero, null);

                    var ty = slot.Ty.Nullable ? slot.Ty.Elem! : slot.Ty;
                    var ptr = slot.Ptr;

                    if (slot.Ty.Nullable && !slot.Ty.Elem!.IsPtrKind && ty.Kind == UserKind.Struct)
                        ptr = LLVM.LLVMBuildStructGEP2(_b, OptTy(ty), slot.Ptr, 0, T("unwa"));

                    return (ptr, ty);
                }

            case Prop p when p.FieldIndex >= 0:
                {
                    var (pa, pt) = AddrOf(p.Target);
                    if (pa == IntPtr.Zero || pt == null) return (IntPtr.Zero, null);
                    if (pt.Kind is not (UserKind.Class or UserKind.Struct)) return (IntPtr.Zero, null);

                    var td = _typeDecls[pt.Name];
                    var obj = pt.Kind == UserKind.Class ? Load(_i8ptr, pa) : pa;
                    return (StructGEP(td, obj, (uint)p.FieldIndex), td.Fields[p.FieldIndex].Type);
                }
        }

        return (IntPtr.Zero, null);
    }

    private Val LoadField(IntPtr addr, Ty ty)
    {
        if (ty.Owned && ty.IsPtrKind) return new(Load(_i8ptr, addr), ty, Prov.Borrow);
        return new(Load(TyLLVM(ty), addr), ty);
    }

    private Val FieldFromBase(Val bases, Prop p)
    {
        var td = _typeDecls[bases.Ty.Name];
        var addr = StructGEP(td, ToAddr(bases), (uint)p.FieldIndex);
        return LoadField(addr, td.Fields[p.FieldIndex].Type);
    }

    private Val EmitNewLit(NewLit nl)
    {
        var td = nl.Decl!;
        var ty = Ty.Named(td.Name);
        var vals = new Dictionary<string, Val>();

        foreach (var fi in nl.Fields)
            vals[fi.Name] = EmitExpr(fi.Value);

        IntPtr storage;
        if (td.Kind == UserKind.Class)
        {
            storage = Call(_mallocTy, _mallocFn, new[] { ConstI64(SizeOfTy(ty)) });
            CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());

            if (td.BuiltIn)
                for (int i = 0; i < td.Fields.Count; i++)
                    Store(DefaultOf(td.Fields[i].Type), StructGEP(td, storage, (uint)i));
        }
        else
        {
            storage = Alloca(_userTys[td.Name], "new");
        }

        for (int i = 0; i < td.Fields.Count; i++)
        {
            var f = td.Fields[i];
            if (!vals.TryGetValue(f.Name, out var v)) continue;
            var slotPtr = StructGEP(td, storage, (uint)i);
            Store(f.Type.Owned ? TakeOwnership(v) : Coerce(v, f.Type).V, slotPtr);
        }

        if (td.Kind == UserKind.Class)
            return new(TempReg(storage, ty), ty, Prov.Temp);

        var loaded = Load(_userTys[td.Name], storage);
        return ty.Owned
            ? new(TempReg(loaded, ty), ty, Prov.Temp)
            : new(loaded, ty);
    }

    private void EmitAssignToField(Assign a, Expr target)
    {
        var (addr, fty) = AddrOf(target);
        if (addr == IntPtr.Zero || fty == null)
            throw new Exception("invalid field assignment target");

        var v = a.Value is NullLit
            ? new Val(DefaultOf(fty), fty, Prov.Static)
            : EmitExpr(a.Value);
        v = Coerce(v, fty);

        if (a.Op != "=")
        {
            var cur = Load(TyLLVM(fty), addr);
            Store(Arith(a.Op[0], fty, cur, v.V), addr);
            return;
        }

        if (fty.Owned)
        {
            var owned = TakeOwnership(v);
            FreeOwnedVal(Load(TyLLVM(fty), addr), fty);
            Store(owned, addr);
            return;
        }

        Store(v.V, addr);
    }

    private Val EmitUserMethodCall(Val target, Method m)
    {
        var baseKey = $"{target.Ty.Name}.{m.Name}";
        var key = m.Instantiation != null ? Mangle(baseKey, m.Instantiation) : baseKey;
        if (!_fns.TryGetValue(key, out var fi))
            throw new Exception($"undefined method '{baseKey}'");

        var retTy = Apply(fi.decl.Ret, SubstOf(fi.decl, m.Instantiation));
        var args = new List<IntPtr> { target.Ty.Kind == UserKind.Class ? target.V : ToAddr(target) };
        var moveSources = new List<VarSlot>();

        for (int i = 0; i < m.Args.Count; i++)
        {
            var pt = Apply(fi.decl.Params[i].Type, SubstOf(fi.decl, m.Instantiation));
            var v = Coerce(EmitExpr(m.Args[i]), pt);
            if (v.Ty.Kind == UserKind.Struct) v = new(DeepCopyVal(v.V, v.Ty), v.Ty);
            args.Add(v.V);
            if (fi.decl.Params[i].Move && v.Prov == Prov.Var && v.Src != null && v.Ty.Kind != UserKind.Struct)
                moveSources.Add(v.Src);
        }

        var name = retTy == Ty.Void ? "" : T("res");
        var result = LLVM.LLVMBuildCall2(_b, fi.ty, fi.fn, args.ToArray(), (uint)args.Count, name);

        foreach (var src in moveSources)
            if (src.Flag != IntPtr.Zero) Store(ConstI32(0), src.Flag);

        if (retTy.Owned) return new(TempReg(result, retTy), retTy, Prov.Temp);
        return new(result, retTy);
    }

    private Val EmitStringMethod(Val target, Method m)
    {
        if (m.NullCond)
            return EmitNullCond(m.Target, m.ResultTy!, t => EmitStringMethodOn(t, m));
        var recv = EmitExpr(m.Target);
        return EmitStringMethodOn(recv, m);
    }

    private Val EmitStringMethodOn(Val s, Method m)
    {
        switch (m.Name)
        {
            case "Contains":
                {
                    var sub = EmitExpr(m.Args[0]);
                    var hit = Call(_strstrTy, _strstrFn, new[] { s.V, sub.V });
                    return new(ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, hit, Null()), _i32), Ty.Bool);
                }

            case "StartsWith":
                {
                    var pre = EmitExpr(m.Args[0]);
                    var len = Call(_strlenTy, _strlenFn, new[] { pre.V });
                    var cmp = Call(_strncmpTy, _strncmpFn, new[] { s.V, pre.V, len });
                    return new(ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, cmp, ConstI32(0)), _i32), Ty.Bool);
                }

            case "IndexOf":
                {
                    var sub = EmitExpr(m.Args[0]);
                    var hit = Call(_strstrTy, _strstrFn, new[] { s.V, sub.V });
                    var found = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, hit, Null());
                    var diff = LLVM.LLVMBuildSub(_b, PtrToInt64(hit), PtrToInt64(s.V), T("idx"));
                    return new(Select(found, Trunc(diff, _i32), ConstI32(-1)), Ty.Int);
                }

            case "Substring":
                {
                    var start = EmitExpr(m.Args[0]);
                    var len = EmitExpr(m.Args[1]);
                    var buf = Call(_mallocTy, _mallocFn, new[] { Add(ZExt(len.V, _i64), ConstI64(1)) });
                    CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());
                    var src = GepByte(s.V, SExt(start.V, _i64));
                    CallV(_memcpyTy, _memcpyFn, new[] { buf, src, ZExt(len.V, _i64) });
                    Store(ConstI8(0), GepByte(buf, ZExt(len.V, _i64)));
                    return new(TempReg(buf, Ty.Str), Ty.Str, Prov.Temp);
                }

            case "ToInt":
                return new(Call(_atoiTy, _atoiFn, new[] { s.V }), Ty.Int);

            case "Split":
                {
                    var sep = EmitExpr(m.Args[0]);
                    var raw = Call(_splitTy, _splitFn, new[] { s.V, sep.V });
                    var lt = Ty.List(Ty.Str);
                    return new(TempReg(raw, lt), lt, Prov.Temp);
                }

            case "Replace":
                {
                    var from = EmitExpr(m.Args[0]);
                    var to = EmitExpr(m.Args[1]);
                    return new(TempReg(Call(_replaceTy, _replaceFn, new[] { s.V, from.V, to.V }), Ty.Str), Ty.Str, Prov.Temp);
                }

            case "Trim":
                return new(TempReg(Call(_trimTy, _trimFn, new[] { s.V }), Ty.Str), Ty.Str, Prov.Temp);

            case "ToLower":
            case "ToUpper":
                {
                    var up = ConstI32(m.Name == "ToUpper" ? 1 : 0);
                    return new(TempReg(Call(_caseFoldTy, _caseFoldFn, new[] { s.V, up }), Ty.Str), Ty.Str, Prov.Temp);
                }

            default:
                throw new Exception($"unsupported string method {m.Name}");
        }
    }

    private Val EmitListMethod(Val target, Method m)
    {
        var elem = target.Ty.Elem!;

        switch (m.Name)
        {
            case "Join":
                {
                    var sep = EmitExpr(m.Args[0]);
                    return new(TempReg(Call(_joinTy, _joinFn, new[] { PtrToInt64(target.V), sep.V }), Ty.Str), Ty.Str, Prov.Temp);
                }
            case "Add":
                {
                    var v = EmitExpr(m.Args[0]);
                    IntPtr asInt;
                    if (elem == Ty.Str) asInt = PtrToInt64(Call(_strdupTy, _strdupFn, new[] { ToStringPtr(v.V, v.Ty) }));
                    else asInt = v.Ty == Ty.Int ? ZExt(v.V, _i64) : SExt(v.V, _i64);
                    CallV(_listAddTy, _listAddFn, new[] { target.V, asInt });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "Remove":
                {
                    var v = EmitExpr(m.Args[0]);
                    if (elem == Ty.Str)
                        CallV(_listRemoveStrTy, _listRemoveStrFn, new[] { target.V, ToStringPtr(v.V, v.Ty), _errFlag });
                    else
                        CallV(_listRemoveIntTy, _listRemoveIntFn, new[] { target.V, ZExt(v.V, _i64), _errFlag });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "Clear":
                {
                    if (elem == Ty.Str) CallV(_listClearStrTy, _listClearStrFn, new[] { target.V });
                    else CallV(_listClearPodTy, _listClearPodFn, new[] { target.V });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "Contains":
            case "IndexOf":
                {
                    var v = EmitExpr(m.Args[0]);
                    var boxed = elem == Ty.Str ? PtrToInt64(ToStringPtr(v.V, v.Ty)) : ZExt(v.V, _i64);
                    var idx = Call(_listIndexTy, _listIndexFn, new[] { target.V, boxed, ConstI32(elem == Ty.Str ? 1 : 0) });
                    if (m.Name == "IndexOf")
                        return new(Trunc(idx, _i32), Ty.Int);
                    var hit = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, idx, ConstI64(-1));
                    return new(ZExt(hit, _i32), Ty.Bool);
                }

            case "Sort":
                CallV(_listSortTy, _listSortFn, new[] { target.V, ConstI32(elem == Ty.Str ? 1 : 0) });
                return new(IntPtr.Zero, Ty.Void);

            case "Reverse":
                CallV(_listReverseTy, _listReverseFn, new[] { target.V });
                return new(IntPtr.Zero, Ty.Void);

            default:
                throw new Exception("unknown list member " + m.Name);
        }
    }

    private Val EmitCall(Call c)
    {
        switch (c.Name)
        {
            case "print":
                {
                    var v = EmitExpr(c.Args[0]);
                    Print(v);
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "input":
                {
                    var p = EmitExpr(c.Args[0]);
                    return new(TempReg(Call(_inputTy, _inputFn, new[] { p.V }), Ty.Str), Ty.Str, Prov.Temp);
                }

            case "len":
                {
                    var v = EmitExpr(c.Args[0]);
                    if (v.Ty == Ty.Buffer)
                        return new(Trunc(Call(_bufLenTy, _bufLenFn, new[] { PtrToInt64(v.V) }), _i32), Ty.Int);
                    if (v.Ty == Ty.Str)
                        return new(Trunc(Call(_strlenTy, _strlenFn, new[] { v.V }), _i32), Ty.Int);
                    if (Ty.IsMap(v.Ty))
                        return new(Trunc(Call(_mapCountTy, _mapCountFn, new[] { PtrToInt64(v.V) }), _i32), Ty.Int);
                    return new(Call(_listSizeTy, _listSizeFn, new[] { v.V }), Ty.Int);
                }

            case "buffer":
                {
                    var a = EmitExpr(c.Args[0]);
                    var raw = a.Ty == Ty.Str
                        ? Call(_bufFromStrTy, _bufFromStrFn, new[] { a.V })
                        : Call(_bufNewTy, _bufNewFn, new[] { SExt(a.V, _i64) });
                    return new(TempReg(raw, Ty.Buffer), Ty.Buffer, Prov.Temp);
                }

            case "exiting":
                return new(Call(_exitingTy, _exitingFn, Array.Empty<IntPtr>()), Ty.Bool);

            case "copy":
                {
                    var v = EmitExpr(c.Args[0]);
                    var dup = v.Ty.Nullable ? NullSafeStrdup(v.V) : Call(_strdupTy, _strdupFn, new[] { v.V });
                    return new(TempReg(dup, v.Ty), v.Ty, Prov.Temp);
                }

            case "read":
                {
                    var p = EmitExpr(c.Args[0]);
                    return new(TempReg(Call(_readTy, _readFn, new[] { p.V, _errFlag }), Ty.Str), Ty.Str, Prov.Temp);
                }

            case "write":
                {
                    var path = EmitExpr(c.Args[0]);
                    var content = EmitExpr(c.Args[1]);
                    CallV(_writeTy, _writeFn, new[] { path.V, content.V, _errFlag });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "exists":
                {
                    var p = EmitExpr(c.Args[0]);
                    return new(Call(_existsTy, _existsFn, new[] { p.V }), Ty.Bool);
                }

            case "delete":
                {
                    var p = EmitExpr(c.Args[0]);
                    CallV(_removeTy, _removeFn, new[] { p.V });
                    return new(IntPtr.Zero, Ty.Void);
                }

            case "mem":
                return new(Trunc(Call(_rtLiveGetTy, _rtLiveGetFn, Array.Empty<IntPtr>()), _i32), Ty.Int);

            case "clock_ms":
                return new(Trunc(Call(_clockTy, _clockFn, Array.Empty<IntPtr>()), _i32), Ty.Int);

            case "unixtime":
                return new(Trunc(Call(_unixtimeTy, _unixtimeFn, Array.Empty<IntPtr>()), _i32), Ty.Int);

            case "fmttime":
                {
                    var unix = EmitExpr(c.Args[0]);
                    var fmt = EmitExpr(c.Args[1]);
                    var s = Call(_fmttimeTy, _fmttimeFn, new[] { SExt(unix.V, _i64), fmt.V });
                    return new(TempReg(s, Ty.Str), Ty.Str, Prov.Temp);
                }

            case "format":
                {
                    var value = EmitExpr(c.Args[0]);
                    var decimals = EmitExpr(c.Args[1]);
                    var dv = value.Ty == Ty.Float ? value.V : SIToFP(value.V);
                    var s = Call(_fmtFloatTy, _fmtFloatFn, new[] { dv, ZExt(decimals.V, _i32) });
                    return new(TempReg(s, Ty.Str), Ty.Str, Prov.Temp);
                }

            case "lastError":
                return new(Load(_i32, _errFlag), Ty.Int);

            case "env":
                {
                    var name = EmitExpr(c.Args[0]);
                    var raw = Call(_envTy, _envFn, new[] { name.V });

                    return new(raw, Ty.NullableOf(Ty.Str), Prov.Static);
                }

            case "args":
                return EmitArgs();
        }

        return EmitUserCall(c);
    }

    private Val EmitArgs()
    {
        var lt = Ty.List(Ty.Str);
        var count = Trunc(Call(_argsCountTy, _argsCountFn, Array.Empty<IntPtr>()), _i32);
        var l = TempReg(Call(_listNewTy, _listNewFn, new[] { count }), lt);

        var iPtr = Alloca(_i32, "argi");
        Store(ConstI32(0), iPtr);

        var loopBB = Block("args_loop");
        var doneBB = Block("args_done");
        Br(loopBB);

        At(loopBB);
        var i = Load(_i32, iPtr);
        var more = ICmp(LLVM.LLVMIntPredicate.LLVMIntSLT, i, count);
        var bodyBB = Block("args_body");
        CondBr(more, bodyBB, doneBB);

        At(bodyBB);
        var raw = Call(_argsGetTy, _argsGetFn, new[] { SExt(i, _i64) });
        var dup = Call(_strdupTy, _strdupFn, new[] { raw });
        CallV(_listAddTy, _listAddFn, new[] { l, PtrToInt64(dup) });
        Store(Add(i, ConstI32(1)), iPtr);
        Br(loopBB);

        At(doneBB);
        return new(l, lt, Prov.Temp);
    }

    private Val GuardedTimeoutString(IntPtr raw)
    {
        var bad = ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, raw, Int64ToPtr(ConstI64(-1)));

        var okBB = Block("to_ok");
        var failBB = Block("to_fail");
        var mergeBB = Block("to_merge");

        CondBr(bad, failBB, okBB);

        At(okBB);
        var okEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(mergeBB);

        At(failBB);
        Store(ConstI32(1), _errFlag);
        var empty = Call(_strdupTy, _strdupFn, new[] { Str("") });
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(mergeBB);

        At(mergeBB);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("to"));
        LLVM.LLVMAddIncoming(phi, new[] { raw, empty }, new[] { okEnd, failEnd }, 2);

        var ty = Ty.NullableOf(Ty.Str);
        return new(TempReg(phi, ty), ty, Prov.Temp);
    }

    private Val EmitUserCall(Call c)
    {
        var key = c.Instantiation != null ? Mangle(c.Name, c.Instantiation) : c.Name;
        if (!_fns.TryGetValue(key, out var fn))
            throw new Exception($"undefined function '{c.Name}'");

        var decl = fn.decl;
        var subst = SubstOf(decl, c.Instantiation);
        var retTy = Apply(decl.Ret, subst);
        var args = new List<IntPtr>();
        var moveSources = new List<VarSlot>();

        for (int i = 0; i < c.Args.Count; i++)
        {
            var pt = Apply(decl.Params[i].Type, subst);
            var v = Coerce(EmitExpr(c.Args[i]), pt);
            if (v.Ty.Kind == UserKind.Struct) v = new(DeepCopyVal(v.V, v.Ty), v.Ty);
            args.Add(v.V);
            if (decl.Params[i].Move && v.Prov == Prov.Var && v.Src != null && v.Ty.Kind != UserKind.Struct)
                moveSources.Add(v.Src);
        }

        var name = retTy == Ty.Void ? "" : T("res");
        var result = LLVM.LLVMBuildCall2(_b, fn.ty, fn.fn, args.ToArray(), (uint)args.Count, name);

        foreach (var src in moveSources)
            if (src.Flag != IntPtr.Zero) Store(ConstI32(0), src.Flag);

        if (retTy.Owned) return new(TempReg(result, retTy), retTy, Prov.Temp);
        return new(result, retTy);
    }

    private void EmitObjectFile(string objPath, string targetTriple)
    {

        LLVM.LLVMInitializeX86TargetInfo();
        LLVM.LLVMInitializeX86Target();
        LLVM.LLVMInitializeX86TargetMC();
        LLVM.LLVMInitializeX86AsmPrinter();
        LLVM.LLVMInitializeAArch64TargetInfo();
        LLVM.LLVMInitializeAArch64Target();
        LLVM.LLVMInitializeAArch64TargetMC();
        LLVM.LLVMInitializeAArch64AsmPrinter();

        var triple = targetTriple;

        if (LLVM.LLVMGetTargetFromTriple(triple, out var target, out var err) != 0)
            throw new Exception("failed to get LLVM target: " + LLVM.PtrToStringAndFree(err));

        var machine = LLVM.LLVMCreateTargetMachine(target, triple, "generic", "",
            LLVM.LLVMCodeGenOptLevel.Default, LLVM.LLVMRelocMode.PIC, LLVM.LLVMCodeModel.Default);

        var passes = LLVM.LLVMCreatePassBuilderOptions();
        LLVM.LLVMPassBuilderOptionsSetVerifyEach(passes, true);
        var optErr = LLVM.LLVMRunPasses(_module, "default<O2>", machine, passes);
        LLVM.LLVMDisposePassBuilderOptions(passes);

        if (optErr != IntPtr.Zero)
        {
            var msg = LLVM.LLVMGetErrorMessage(optErr);
            var text = Marshal.PtrToStringAnsi(msg) ?? "unknown";
            LLVM.LLVMDisposeErrorMessage(msg);
            throw new Exception("optimization failed: " + text);
        }

        if (LLVM.LLVMTargetMachineEmitToFile(machine, _module, objPath, LLVM.LLVMCodeGenFileType.ObjectFile, out var emitErr) != 0)
            throw new Exception("failed to emit object file: " + LLVM.PtrToStringAndFree(emitErr));
    }
}

