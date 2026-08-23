namespace HSharp;

using System.Runtime.InteropServices;

// where a value came from when an expression produced it
enum Prov { Static, Borrow, Var, Temp }

// a local variable's storage. Flag is the drop flag, left empty for plain numbers
sealed class VarSlot
{
    public string Name = "";
    public IntPtr Ptr, Flag;
    public Ty Ty = Ty.Int;
    public bool Owned, Borrow;
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

// turns a checked program into LLVM IR. the checker has already placed Drop
// statements at every point a variable dies, so this pass just follows them
public sealed class CodeGen
{
    // _b emits code, _ab parks in the function's entry block so allocas coming
    // from loop bodies get hoisted there instead of growing the stack every iteration
    private IntPtr _ctx, _module, _b, _ab;
    private IntPtr _i8, _i32, _i64, _double, _i8ptr, _void;
    private IntPtr _listTy;
    private IntPtr _entryBB, _curFn, _errFlag;

    // externs
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

    // sockets
    private IntPtr _tcpListenFn, _tcpListenTy;
    private IntPtr _tcpAcceptFn, _tcpAcceptTy;
    private IntPtr _tcpConnectFn, _tcpConnectTy;
    private IntPtr _tcpSendFn, _tcpSendTy;
    private IntPtr _tcpCloseFn, _tcpCloseTy;
    private IntPtr _udpOpenFn, _udpOpenTy;
    private IntPtr _udpSendToFn, _udpSendToTy;
    private IntPtr _tcpLineFn, _tcpLineTy;
    private IntPtr _udpRecvFn, _udpRecvTy;

    // prelude helpers
    private IntPtr _hsInc, _hsIncTy, _hsDec, _hsDecTy;
    private IntPtr _hsLive;

    // runtime library (rt.c) entry points
    private IntPtr _rtInitFn, _rtInitTy;
    private IntPtr _rtLiveIncFn, _rtLiveIncTy;
    private IntPtr _rtLiveDecFn, _rtLiveDecTy;
    private IntPtr _rtLiveGetFn, _rtLiveGetTy;
    private IntPtr _rtTaskNewFn, _rtTaskNewTy;
    private IntPtr _rtTaskSubmitFn, _rtTaskSubmitTy;
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

    // swapped wholesale when emitting a lambda trampoline, hence not readonly
    private List<Dictionary<string, VarSlot>> _scopes = new();
    private List<VarSlot> _fnSlots = new();

    // heap values created while emitting one statement; they die with it unless
    // someone takes them over
    private List<(IntPtr V, Ty Ty)> _temps = new();

    private IntPtr? _catchBB;
    private readonly Dictionary<string, (IntPtr fn, IntPtr ty, IntPtr entry, IntPtr body, FnDecl decl)> _fns = new();
    private FnDecl? _curDecl;

    // (break target, continue target) for the innermost loop, also swapped
    // during lambda emission
    private List<(IntPtr brk, IntPtr cont)> _loopExit = new();

    // one trampoline + env struct per lambda literal, emitted on first use.
    // reference keying: two identical-looking lambdas are different functions
    private sealed class LamInfo
    {
        public IntPtr Fn, EnvTy;
        public long EnvSize;
        public List<VarSlot> Captures = new();
        public Ty Ret = Ty.Void;
    }

    private readonly Dictionary<LamLit, LamInfo> _lambdas = new(ReferenceEqualityComparer.Instance);

    // set while emitting a lambda trampoline
    private Ty? _lamRetTy;
    private IntPtr _lamEnvParam;

    public void Generate(AstProgram program, string objPath) =>
        Generate(program, objPath, LLVM.PtrToStringAndFree(LLVM.LLVMGetDefaultTargetTriple()));

    public void Generate(AstProgram program, string objPath, string targetTriple)
    {
        _ctx = LLVM.LLVMContextCreate();
        _module = LLVM.LLVMModuleCreateWithNameInContext("hsharp", _ctx);
        _b = LLVM.LLVMCreateBuilderInContext(_ctx);
        _ab = LLVM.LLVMCreateBuilderInContext(_ctx);

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

        // two passes over user functions: register signatures first so call
        // order and mutual recursion work, bodies after main
        foreach (var s in program.Stmts)
            if (s is FnDecl f)
                CreateUserFn(f);

        EmitMain(program.Stmts.Where(s => s is not FnDecl).ToList());

        foreach (var s in program.Stmts)
            if (s is FnDecl f)
                EmitFnBody(f);

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

        // sockets
        Ext("rt_tcp_listen", _i64, new[] { _i32 }, false, out _tcpListenFn, out _tcpListenTy);
        Ext("rt_tcp_accept", _i64, new[] { _i64 }, false, out _tcpAcceptFn, out _tcpAcceptTy);
        Ext("rt_tcp_connect", _i64, new[] { _i8ptr, _i32 }, false, out _tcpConnectFn, out _tcpConnectTy);
        Ext("rt_tcp_send", _i64, new[] { _i64, _i8ptr, _i64 }, false, out _tcpSendFn, out _tcpSendTy);
        Ext("rt_tcp_close", _void, new[] { _i64 }, false, out _tcpCloseFn, out _tcpCloseTy);
        Ext("rt_udp_open", _i64, Array.Empty<IntPtr>(), false, out _udpOpenFn, out _udpOpenTy);
        Ext("rt_udp_sendto", _i64, new[] { _i64, _i8ptr, _i32, _i8ptr, _i64 }, false, out _udpSendToFn, out _udpSendToTy);
        Ext("rt_tcp_line", _i8ptr, new[] { _i64 }, false, out _tcpLineFn, out _tcpLineTy);
        Ext("rt_udp_recv", _i8ptr, new[] { _i64 }, false, out _udpRecvFn, out _udpRecvTy);
    }

    // declaration only, no body
    private void Ext(string name, IntPtr ret, IntPtr[] ps, bool varArg, out IntPtr fn, out IntPtr ty)
    {
        ty = LLVM.LLVMFunctionType(ret, ps, (uint)ps.Length, varArg);
        fn = LLVM.LLVMAddFunction(_module, name, ty);
    }

    // creates the function and points the code builder at its entry block
    private void Fn(string name, IntPtr ret, IntPtr[] ps, bool varArg, out IntPtr fn, out IntPtr ty)
    {
        Ext(name, ret, ps, varArg, out fn, out ty);
        _curFn = fn;
        LLVM.LLVMPositionBuilderAtEnd(_b, LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "entry"));
    }

    // the little runtime baked into every module: an allocation counter plus
    // list and string helpers, so statement emitters don't inline the same IR everywhere
    private void EmitPrelude()
    {
        Ext("rt_init", _void, Array.Empty<IntPtr>(), false, out _rtInitFn, out _rtInitTy);
        Ext("rt_live_inc", _void, Array.Empty<IntPtr>(), false, out _rtLiveIncFn, out _rtLiveIncTy);
        Ext("rt_live_dec", _void, Array.Empty<IntPtr>(), false, out _rtLiveDecFn, out _rtLiveDecTy);
        Ext("rt_live_get", _i64, Array.Empty<IntPtr>(), false, out _rtLiveGetFn, out _rtLiveGetTy);
        Ext("rt_task_new", _i8ptr, new[] { _i8ptr, _i8ptr }, false, out _rtTaskNewFn, out _rtTaskNewTy);
        Ext("rt_task_submit", _void, new[] { _i8ptr }, false, out _rtTaskSubmitFn, out _rtTaskSubmitTy);
        Ext("rt_task_join", _i8ptr, new[] { _i8ptr }, false, out _rtTaskJoinFn, out _rtTaskJoinTy);

        // the counter itself lives in the runtime now, atomic, so tasks
        // don't lie to mem()
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
        PreludeListDropStr();
        PreludeListDropPod();
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

        // the list owns its elements, so the old one dies here
        CallV(_freeTy, _freeFn, new[] { Int64ToPtr(old) });
        CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());

        Store(v, slot);
        RetVoid();

        At(fail);
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

    // scan for a match, free the hit, shift the tail left. match() returns i1,
    // result is 1 when something was removed
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
        // elements, then backing array, then the header itself
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

        // failed opens still return a heap buffer so the caller can free it blindly
        At(fail);
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

    private IntPtr TyLLVM(Ty ty) => ty switch
    {
        _ when ty == Ty.Int || ty == Ty.Bool => _i32,
        _ when ty == Ty.Float => _double,
        _ when ty == Ty.Void => _void,
        _ => _i8ptr
    };

    private void CreateUserFn(FnDecl f)
    {
        var ps = f.Params.Select(p => TyLLVM(p.Type)).ToArray();
        var ty = LLVM.LLVMFunctionType(TyLLVM(f.Ret), ps, (uint)ps.Length, false);
        var fn = LLVM.LLVMAddFunction(_module, f.Name, ty);

        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "entry");
        var body = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "body");

        _fns[f.Name] = (fn, ty, entry, body, f);
    }

    // entry block holds the allocas, code starts in body. the br connecting them
    // is only emitted once the body is done, since allocas keep arriving meanwhile
    private void EmitMain(List<Stmt> stmts)
    {
        _curDecl = null;

        var ty = LLVM.LLVMFunctionType(_i32, Array.Empty<IntPtr>(), 0, false);
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

        // pool, winsock, counters: bring the runtime up before anything else
        CallV(_rtInitTy, _rtInitFn, Array.Empty<IntPtr>());

        EmitStmtList(stmts);
        if (!Terminated()) Ret(ConstI32(0));

        LLVM.LLVMPositionBuilderAtEnd(_ab, _entryBB);
        LLVM.LLVMBuildBr(_ab, body);
    }

    private void StoreAb(IntPtr v, IntPtr p) => LLVM.LLVMBuildStore(_ab, v, p);

    private void EmitFnBody(FnDecl f)
    {
        var (fn, _, entry, body, _) = _fns[f.Name];

        _curFn = fn;
        _curDecl = f;
        _entryBB = entry;

        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMPositionBuilderAtEnd(_b, body);

        _scopes.Clear();
        _fnSlots.Clear();
        _scopes.Add(new Dictionary<string, VarSlot>());

        _errFlag = Alloca(_i32, "errflag");
        StoreAb(ConstI32(0), _errFlag);

        for (int i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            var slot = NewSlot(p.Name, p.Type, p.Move, borrowParam: p.Type.Owned && !p.Move);

            Store(LLVM.LLVMGetParam(fn, (uint)i), slot.Ptr);
            if (slot.Flag != IntPtr.Zero) Store(ConstI32(1), slot.Flag);
        }

        EmitStmtList(f.Body);
        if (!Terminated()) EmitDefaultReturn(f.Ret);

        LLVM.LLVMPositionBuilderAtEnd(_ab, entry);
        LLVM.LLVMBuildBr(_ab, body);
    }

    // falling off the end returns a default value; the empty string/list are
    // heap allocated so the caller can free them like any other result
    private void EmitDefaultReturn(Ty ret)
    {
        FreeAllOwned(null);

        if (ret == Ty.Void) RetVoid();
        else if (ret == Ty.Int || ret == Ty.Bool) Ret(ConstI32(0));
        else if (ret == Ty.Float) Ret(ConstF(0.0));
        else if (ret == Ty.Str) Ret(Call(_strdupTy, _strdupFn, new[] { Str("") }));
        else Ret(Call(_listNewTy, _listNewFn, new[] { ConstI32(8) }));
    }

    private VarSlot NewSlot(string name, Ty ty, bool owned, bool borrowParam = false)
    {
        var slot = new VarSlot { Name = name, Ptr = Alloca(TyLLVM(ty), name), Ty = ty, Owned = owned, Borrow = borrowParam };

        if (owned) slot.Flag = Alloca(_i32, name + ".flag");

        _scopes[^1][name] = slot;
        if (owned) _fnSlots.Add(slot);

        return slot;
    }

    // allocas always go through the builder parked in the entry block
    private IntPtr Alloca(IntPtr ty, string name) => LLVM.LLVMBuildAlloca(_ab, ty, name);

    private VarSlot? FindSlot(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var s)) return s;
        return null;
    }

    private bool Terminated() => LLVM.LLVMGetBasicBlockTerminator(LLVM.LLVMGetInsertBlock(_b)) != IntPtr.Zero;

    // only branch if the block isn't already closed by a return
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
            case Drop dr: EmitDrop(dr); break;
        }
    }

    // every statement ends the same way: temps die, then the error flag
    // decides whether we jump to the catch we're inside of
    private void EndStatement()
    {
        if (Terminated()) return;

        FreeTemps();
        ErrorCheck();
    }

    private void FreeTemps()
    {
        foreach (var (v, ty) in _temps)
        {
            if (ty == Ty.Str)
            {
                CallV(_freeTy, _freeFn, new[] { v });
                CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
            }
            else if (ty.Elem != null) DropList(v, ty.Elem!);
        }

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

    // a value is being stored into a variable. a move just clears the source's
    // drop flag, a temp is handed over, anything else gets copied
    private IntPtr TakeOwnership(Val v)
    {
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

        return Call(_strdupTy, _strdupFn, new[] { v.V });
    }

    private IntPtr TempReg(IntPtr v, Ty ty)
    {
        _temps.Add((v, ty));
        return v;
    }

    // free only if the drop flag is still set, and clear it on the way out so
    // a second drop (early free plus scope end) stays a no-op
    private void EmitGuardedFree(VarSlot slot)
    {
        if (!slot.Owned || slot.Flag == IntPtr.Zero) return;

        var alive = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, Load(_i32, slot.Flag), ConstI32(0));
        var freeBB = Block(slot.Name + "_free");
        var done = Block(slot.Name + "_freed");

        CondBr(alive, freeBB, done);

        At(freeBB);
        var v = Load(_i8ptr, slot.Ptr);

        if (slot.Ty == Ty.Str)
        {
            CallV(_freeTy, _freeFn, new[] { v });
            CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
        }
        else DropList(v, slot.Ty.Elem!);

        Store(ConstI32(0), slot.Flag);
        Br(done);

        At(done);
    }

    // reverse declaration order, like stack unwinding
    private void FreeAllOwned(VarSlot? except)
    {
        for (int i = _fnSlots.Count - 1; i >= 0; i--)
        {
            var s = _fnSlots[i];
            if (s != except) EmitGuardedFree(s);
        }
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
        var v = EmitExpr(d.Init);
        var ty = d.Ann ?? v.Ty;
        if (ty != v.Ty && ty == Ty.Float && v.Ty == Ty.Int) v = new Val(SIToFP(v.V), ty, v.Prov, v.Src);

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
        if (a.Target is Ident id)
        {
            EmitAssignToVar(a, id);
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
        // discard, the fire-and-forget form: evaluate and drop
        if (id.Name == "_" && FindSlot(id.Name) == null)
        {
            EmitExpr(a.Value);
            return;
        }

        var slot = FindSlot(id.Name)!;

        if (a.Op != "=")
        {
            EmitCompoundAssign(a, slot);
            return;
        }

        var v = EmitExpr(a.Value);
        if (slot.Ty == Ty.Float && v.Ty == Ty.Int) v = new Val(SIToFP(v.V), slot.Ty, v.Prov, v.Src);

        if (!slot.Owned)
        {
            Store(v.V, slot.Ptr);
            return;
        }

        var owned = TakeOwnership(v);

        // s = s keeps the buffer, everything else frees the old value first
        if (v.Src == slot)
        {
            Store(owned, slot.Ptr);
            return;
        }

        EmitGuardedFree(slot);
        Store(owned, slot.Ptr);
        Store(ConstI32(1), slot.Flag);
    }

    private void EmitCompoundAssign(Assign a, VarSlot slot)
    {
        var cur = Load(TyLLVM(slot.Ty), slot.Ptr);
        var rhs = EmitExpr(a.Value);

        // s += x builds a fresh buffer, the old one dies
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

        var elem = list.Ty.Elem!;
        if (elem == Ty.Str)
        {
            // the list keeps its own copy
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

    // integer division by zero sets the error flag instead of crashing the
    // process; the result is 0 on that path and lands in catch
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
        // the loop variable lives in a scope that wraps the body
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

        // the loop variable gets its own copy, the list keeps the original
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

    // loop conditions run every iteration, so any temps they create have to be
    // freed right there in the cond block, not once after the loop
    private Val EmitLoopCond(Expr e)
    {
        int start = _temps.Count;
        var v = EmitExpr(e);

        var mine = _temps.Skip(start).ToList();
        _temps.RemoveRange(start, _temps.Count - start);

        foreach (var (tv, tty) in mine)
        {
            if (tty == Ty.Str)
            {
                CallV(_freeTy, _freeFn, new[] { tv });
                CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
            }
            else if (tty.Elem != null) DropList(tv, tty.Elem!);
        }

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

        // the error jump skips the try scope's own drops, so free its variables
        // here; already moved or freed ones get skipped by their flags
        foreach (var s in _fnSlots.Skip(slotBase))
            EmitGuardedFree(s);

        EmitStmtList(tc.Catch);
        BrIfLive(mergeBB);

        At(mergeBB);
    }

    // the returned value moves out with us, everything else this function owns dies here
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
        if (_curDecl!.Ret == Ty.Float && v.Ty == Ty.Int)
            v = new Val(SIToFP(v.V), Ty.Float, v.Prov, v.Src);

        var retVal = v.Ty.Owned ? TakeOwnership(v) : v.V;

        FreeTemps();
        FreeAllOwned(v.Prov == Prov.Var ? v.Src : null);
        Ret(retVal);
    }

    private IntPtr BoolOf(Val v) => ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, v.V, ConstI32(0));

    // numbers become heap strings so they can join concatenation and print;
    // bools pick one of two static strings instead, nothing to free there
    private IntPtr ToStringPtr(IntPtr v, Ty ty) => ty switch
    {
        _ when ty == Ty.Str => v,
        _ when ty == Ty.Int => TempReg(Call(_itoaTy, _itoaFn, new[] { v }), Ty.Str),
        _ when ty == Ty.Float => TempReg(Call(_ftoaTy, _ftoaFn, new[] { v }), Ty.Str),
        _ => Select(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, v, ConstI32(0)), Str("true"), Str("false"))
    };

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
                    var slot = FindSlot(id.Name) ?? throw new Exception($"undefined variable '{id.Name}'");
                    var prov = slot.Owned ? Prov.Var : Prov.Borrow;
                    return new(Load(TyLLVM(slot.Ty), slot.Ptr), slot.Ty, prov, slot);
                }

            case Un u: return EmitUnary(u);
            case Bin b: return EmitBinary(b);
            case Index ix: return EmitIndex(ix);
            case Call c: return EmitCall(c);
            case Method m: return EmitMethod(m);

            case Prop p:
                {
                    var t = EmitExpr(p.Target);
                    return new(Call(_listSizeTy, _listSizeFn, new[] { t.V }), Ty.Int);
                }

            case LamLit:
                throw new Exception("lambdas are only compiled through Task.Run");

            case AwaitExpr aw:
                return EmitAwait(aw);

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
        // these two must not evaluate the right side up front
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

    // evaluates && / || with branches so the right side only runs when it must.
    // its temporaries are freed on that path, since values defined in the rhs
    // block don't dominate the merge point
    private Val EmitShortCircuit(Bin b)
    {
        var l = EmitExpr(b.L);
        var lb = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, l.V, ConstI32(0));
        var preBB = LLVM.LLVMGetInsertBlock(_b);

        var rhsBB = Block("sc_rhs");
        var doneBB = Block("sc_done");

        // false && x skips x; true || x skips x
        if (b.Op == "&&") CondBr(lb, rhsBB, doneBB);
        else CondBr(lb, doneBB, rhsBB);

        At(rhsBB);
        int tempStart = _temps.Count;
        var r = EmitExpr(b.R);
        var rb = ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, r.V, ConstI32(0)), _i32);

        var rhsTemps = _temps.Skip(tempStart).ToList();
        _temps.RemoveRange(tempStart, _temps.Count - tempStart);
        foreach (var (tv, tty) in rhsTemps)
        {
            if (tty == Ty.Str)
            {
                CallV(_freeTy, _freeFn, new[] { tv });
                CallV(_hsDecTy, _hsDec, Array.Empty<IntPtr>());
            }
            else if (tty.Elem != null) DropList(tv, tty.Elem!);
        }

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

    // indexing a string list hands out a borrow, the list keeps ownership
    private Val EmitIndex(Index ix)
    {
        var list = EmitExpr(ix.Target);
        var idx = EmitExpr(ix.Idx);
        var raw = Call(_listGetTy, _listGetFn, new[] { list.V, idx.V, _errFlag });

        if (list.Ty.Elem == Ty.Str)
            return new(Int64ToPtr(raw), Ty.Str, Prov.Borrow);

        return new(Trunc(raw, _i32), Ty.Int);
    }

    private Val EmitListLit(ListLit ll)
    {
        var cap = Math.Max(ll.Items.Count, 8);
        var l = TempReg(Call(_listNewTy, _listNewFn, new[] { ConstI32(cap) }), Ty.List(ll.ElemTy));

        foreach (var item in ll.Items)
        {
            var v = EmitExpr(item);
            IntPtr asInt;
            if (ll.ElemTy == Ty.Str) asInt = PtrToInt64(Call(_strdupTy, _strdupFn, new[] { ToStringPtr(v.V, v.Ty) }));
            else asInt = v.Ty == Ty.Int ? ZExt(v.V, _i64) : SExt(v.V, _i64);
            CallV(_listAddTy, _listAddFn, new[] { l, asInt });
        }

        return new(l, Ty.List(ll.ElemTy), Prov.Temp);
    }

    private static bool IsStaticClass(string name) =>
        name is "Task" or "Tcp" or "Udp" or "Http";

    // long handle -> pointer, negative stays visible to the err check below
    private IntPtr HandlePtr(IntPtr v) => Int64ToPtr(v);

    // routes a negative rt return into the error flag, returns null instead
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
        Store(ConstI32(1), _errFlag);
        var failPtr = Null();
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("h"));
        LLVM.LLVMAddIncoming(phi, new[] { okPtr, failPtr }, new[] { okEnd, failEnd }, 2);
        return phi;
    }

    // an rt string: NULL becomes an error plus an owned empty string, so the
    // caller can free the result without caring which side it came from
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
        Store(ConstI32(1), _errFlag);
        var empty = Call(_strdupTy, _strdupFn, new[] { Str("") });
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i8ptr, T("ns"));
        LLVM.LLVMAddIncoming(phi, new[] { raw, empty }, new[] { okEnd, failEnd }, 2);
        return new(TempReg(phi, Ty.Str), Ty.Str, Prov.Temp);
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
            return new(GuardHandle(raw), Ty.Handle("client"));
        }

        if (cls == "Udp" && m.Name == "Open")
        {
            var raw = Call(_udpOpenTy, _udpOpenFn, Array.Empty<IntPtr>());
            return new(GuardHandle(raw), Ty.Handle("udp"));
        }

        throw new Exception($"'{cls}.{m.Name}' is not available yet");
    }

    private Val EmitHandleMethod(Val target, Method m)
    {
        var h64 = PtrToInt64(target.V);

        if (m.Name == "Accept")
        {
            var raw = Call(_tcpAcceptTy, _tcpAcceptFn, new[] { h64 });
            return new(GuardHandle(raw), Ty.Handle("client"));
        }

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

    // byte counts: negative turns into 0 plus the error flag
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
        Store(ConstI32(1), _errFlag);
        var failEnd = LLVM.LLVMGetInsertBlock(_b);
        Br(merge);

        At(merge);
        var phi = LLVM.LLVMBuildPhi(_b, _i32, T("nc"));
        LLVM.LLVMAddIncoming(phi, new[] { okVal, ConstI32(0) }, new[] { okEnd, failEnd }, 2);
        return phi;
    }

    private Val EmitTaskRun(Method m)
    {
        var lam = (LamLit)m.Args[0];

        if (!_lambdas.TryGetValue(lam, out var info))
            info = EmitLambda(lam);

        // fill the env with the current capture values; owned ones move in
        var env = Call(_mallocTy, _mallocFn, new[] { ConstI64(info.EnvSize) });

        foreach (var (slot, i) in info.Captures.Select((s, i) => (s, i)))
        {
            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, env, (uint)i, T("cap"));
            Store(Load(TyLLVM(slot.Ty), slot.Ptr), fieldPtr);
            if (slot.Owned) Store(ConstI32(0), slot.Flag);
        }

        var task = Call(_rtTaskNewTy, _rtTaskNewFn, new[] { info.Fn, env });
        CallV(_rtTaskSubmitTy, _rtTaskSubmitFn, new[] { task });

        return new(task, Ty.Task(info.Ret), Prov.Static);
    }

    // compiles the lambda into a trampoline: ptr(env) -> ptr(boxed result).
    // captured values are unpacked into ordinary local slots, so the whole
    // statement machinery, drops included, works unchanged inside
    private LamInfo EmitLambda(LamLit lam)
    {
        var info = new LamInfo { Ret = lam.RetTy ?? Ty.Void };
        info.Captures = CollectCaptures(lam);

        var fieldTys = info.Captures.Select(c => TyLLVM(c.Ty)).ToArray();
        info.EnvTy = LLVM.LLVMStructCreateNamed(_ctx, $"lam.env{_lambdas.Count}");
        if (fieldTys.Length > 0)
            LLVM.LLVMStructSetBody(info.EnvTy, fieldTys, (uint)fieldTys.Length, false);
        info.EnvSize = EnvSize(info.Captures.Select(c => c.Ty));

        // save the whole emission context, we're switching functions mid-flight
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
            var slot = NewSlot(cap.Name, cap.Ty, owned: cap.Ty.Owned);
            var fieldPtr = LLVM.LLVMBuildStructGEP2(_b, info.EnvTy, _lamEnvParam, (uint)i, T("cap"));
            Store(Load(TyLLVM(cap.Ty), fieldPtr), slot.Ptr);
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

        // back to whatever the caller was emitting
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
        _lamRetTy = null;
        _lamEnvParam = IntPtr.Zero;

        info.Fn = fn;
        _lambdas[lam] = info;
        return info;
    }

    // the identifiers a lambda body reads from the enclosing scopes, in first-use
    // order. names that don't resolve here are lambda locals and get skipped
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
            case ListLit ll: foreach (var i in ll.Items) CollectIdents(i, names); break;
            case AwaitExpr aw: CollectIdents(aw.Task, names); break;
        }
    }

    // LLVM struct layout math: sequential fields, each aligned to its own alignment
    private static long EnvSize(IEnumerable<Ty> fields)
    {
        long offset = 0, maxAlign = 1;

        foreach (var f in fields)
        {
            long size = f == Ty.Int || f == Ty.Bool ? 4 : 8;
            long align = size;
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

    // the return leaves with the task result; the env was only a carrier, so
    // it gets a raw free without touching the allocation counter
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

        var target = EmitExpr(m.Target);

        if (Ty.IsHandle(target.Ty))
            return EmitHandleMethod(target, m);

        var elem = target.Ty.Elem!;

        switch (m.Name)
        {
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
                    if (v.Ty == Ty.Str)
                        return new(Trunc(Call(_strlenTy, _strlenFn, new[] { v.V }), _i32), Ty.Int);
                    return new(Call(_listSizeTy, _listSizeFn, new[] { v.V }), Ty.Int);
                }

            case "copy":
                {
                    var v = EmitExpr(c.Args[0]);
                    return new(TempReg(Call(_strdupTy, _strdupFn, new[] { v.V }), Ty.Str), Ty.Str, Prov.Temp);
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

            case "contains":
                {
                    var s = EmitExpr(c.Args[0]);
                    var sub = EmitExpr(c.Args[1]);
                    var hit = Call(_strstrTy, _strstrFn, new[] { s.V, sub.V });
                    return new(ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, hit, Null()), _i32), Ty.Bool);
                }

            case "startsWith":
                {
                    var s = EmitExpr(c.Args[0]);
                    var pre = EmitExpr(c.Args[1]);
                    var len = Call(_strlenTy, _strlenFn, new[] { pre.V });
                    var cmp = Call(_strncmpTy, _strncmpFn, new[] { s.V, pre.V, len });
                    return new(ZExt(ICmp(LLVM.LLVMIntPredicate.LLVMIntEQ, cmp, ConstI32(0)), _i32), Ty.Bool);
                }

            case "indexOf":
                {
                    var s = EmitExpr(c.Args[0]);
                    var sub = EmitExpr(c.Args[1]);
                    var hit = Call(_strstrTy, _strstrFn, new[] { s.V, sub.V });
                    var found = ICmp(LLVM.LLVMIntPredicate.LLVMIntNE, hit, Null());
                    var diff = LLVM.LLVMBuildSub(_b, PtrToInt64(hit), PtrToInt64(s.V), T("idx"));
                    return new(Select(found, Trunc(diff, _i32), ConstI32(-1)), Ty.Int);
                }

            case "sub":
                {
                    var s = EmitExpr(c.Args[0]);
                    var start = EmitExpr(c.Args[1]);
                    var len = EmitExpr(c.Args[2]);
                    var buf = Call(_mallocTy, _mallocFn, new[] { Add(ZExt(len.V, _i64), ConstI64(1)) });
                    CallV(_hsIncTy, _hsInc, Array.Empty<IntPtr>());
                    var src = GepByte(s.V, SExt(start.V, _i64));
                    CallV(_memcpyTy, _memcpyFn, new[] { buf, src, ZExt(len.V, _i64) });
                    Store(ConstI8(0), GepByte(buf, ZExt(len.V, _i64)));
                    return new(TempReg(buf, Ty.Str), Ty.Str, Prov.Temp);
                }

            case "parseInt":
                {
                    var s = EmitExpr(c.Args[0]);
                    return new(Call(_atoiTy, _atoiFn, new[] { s.V }), Ty.Int);
                }
        }

        return EmitUserCall(c);
    }

    private Val EmitUserCall(Call c)
    {
        if (!_fns.TryGetValue(c.Name, out var fn))
            throw new Exception($"undefined function '{c.Name}'");

        var decl = fn.decl;
        var args = new List<IntPtr>();
        var moveSources = new List<VarSlot>();

        for (int i = 0; i < c.Args.Count; i++)
        {
            var v = EmitExpr(c.Args[i]);
            args.Add(v.V);
            if (decl.Params[i].Move && v.Prov == Prov.Var && v.Src != null)
                moveSources.Add(v.Src);
        }

        var name = decl.Ret == Ty.Void ? "" : T("res");
        var result = LLVM.LLVMBuildCall2(_b, fn.ty, fn.fn, args.ToArray(), (uint)args.Count, name);

        // moved-in arguments belong to the callee now
        foreach (var src in moveSources)
            if (src.Flag != IntPtr.Zero) Store(ConstI32(0), src.Flag);

        if (decl.Ret.Owned) return new(TempReg(result, decl.Ret), decl.Ret, Prov.Temp);
        return new(result, decl.Ret);
    }

    private void Print(Val v)
    {
        switch (v.Ty.Name)
        {
            case "string": CallV(_printfTy, _printfFn, new[] { Str("%s\n"), v.V }); break;
            case "int": CallV(_printfTy, _printfFn, new[] { Str("%d\n"), v.V }); break;
            case "float": CallV(_printfTy, _printfFn, new[] { Str("%g\n"), v.V }); break;
            case "bool": CallV(_printfTy, _printfFn, new[] { Str("%s\n"), ToStringPtr(v.V, v.Ty) }); break;
        }
    }

    private void EmitObjectFile(string objPath, string targetTriple)
    {
        // both families stay initialized so cross builds work from any host
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
