namespace HumanScript;

public class CodeGen
{
    private IntPtr _ctx, _module, _builder;
    private IntPtr _i32, _i64, _i8, _i8ptr, _void, _double;

    private IntPtr _printfFn, _printfType;
    private IntPtr _sprintfFn, _sprintfType;
    private IntPtr _snprintfFn, _snprintfType;
    private IntPtr _fopenFn, _fopenType;
    private IntPtr _fseekFn, _fseekType;
    private IntPtr _ftellFn, _ftellType;
    private IntPtr _rewindFn, _rewindType;
    private IntPtr _mallocFn, _mallocType;
    private IntPtr _reallocFn, _reallocType;
    private IntPtr _freeFn, _freeType;
    private IntPtr _freadFn, _freadType;
    private IntPtr _fcloseFn, _fcloseType;
    private IntPtr _fputsFn, _fputsType;
    private IntPtr _fgetsFn, _fgetsType;
    private IntPtr _strstrFn, _strstrType;
    private IntPtr _strlenFn, _strlenType;
    private IntPtr _strcpyFn, _strcpyType;
    private IntPtr _strcatFn, _strcatType;
    private IntPtr _strcmpFn, _strcmpType;
    private IntPtr _strncmpFn, _strncmpType;
    private IntPtr _strncpyFn, _strncpyType;
    private IntPtr _getcharFn, _getcharType;
    private IntPtr _removeFn, _removeType;
    private IntPtr _exitFn, _exitType;
    private IntPtr _memsetFn, _memsetType;
    private IntPtr _memcpyFn, _memcpyType;
    private IntPtr _atoiFn, _atoiType;
    private IntPtr _atofFn, _atofType;

    private IntPtr _mainFn;
    private IntPtr _errorFlagPtr;
    private IntPtr? _currentTryCatchCatchBlock;
    private int _tmpCounter;
    private string Tmp(string prefix) => $"{prefix}{_tmpCounter++}";

    private Dictionary<string, IntPtr> _variables;
    private Dictionary<string, string> _varTypes;

    private readonly Dictionary<string, (IntPtr func, IntPtr funcType, List<string> paramNames)> _functions = new();
    private IntPtr? _currentFunction = null;

    private readonly Dictionary<string, (IntPtr array, IntPtr capacity, IntPtr size, string elemType)> _lists = new();

    private bool _hasReturned;

    public void Generate(AstProgram program, string objOutputPath)
    {
        _ctx = LLVM.LLVMContextCreate();
        _module = LLVM.LLVMModuleCreateWithNameInContext("humanscript_module", _ctx);
        _builder = LLVM.LLVMCreateBuilderInContext(_ctx);

        _i32 = LLVM.LLVMInt32TypeInContext(_ctx);
        _i64 = LLVM.LLVMInt64TypeInContext(_ctx);
        _i8 = LLVM.LLVMInt8TypeInContext(_ctx);
        _i8ptr = LLVM.LLVMPointerType(_i8, 0);
        _void = LLVM.LLVMVoidTypeInContext(_ctx);
        _double = LLVM.LLVMDoubleTypeInContext(_ctx);

        _variables = new Dictionary<string, IntPtr>();
        _varTypes = new Dictionary<string, string>();

        DeclareExternFunctions();

        var mainType = LLVM.LLVMFunctionType(_i32, Array.Empty<IntPtr>(), 0, isVarArg: false);
        _mainFn = LLVM.LLVMAddFunction(_module, "main", mainType);
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, _mainFn, "entry");
        LLVM.LLVMPositionBuilderAtEnd(_builder, entry);

        _errorFlagPtr = LLVM.LLVMBuildAlloca(_builder, _i32, "errflag");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), _errorFlagPtr);

        foreach (var stmt in program.Statements)
            EmitStatement(stmt, _mainFn);

        LLVM.LLVMBuildRet(_builder, LLVM.LLVMConstInt(_i32, 0, false));

        if (LLVM.LLVMVerifyModule(_module, 2, out var verifyErr) != 0)
            throw new Exception("Generated LLVM IR failed verification: " + LLVM.PtrToStringAndFree(verifyErr));

        EmitObjectFile(objOutputPath);
    }

    private void DeclareExternFunctions()
    {
        _printfType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr }, 1, isVarArg: true);
        _printfFn = LLVM.LLVMAddFunction(_module, "printf", _printfType);

        _sprintfType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i8ptr }, 2, isVarArg: true);
        _sprintfFn = LLVM.LLVMAddFunction(_module, "sprintf", _sprintfType);

        _snprintfType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i64, _i8ptr }, 3, isVarArg: true);
        _snprintfFn = LLVM.LLVMAddFunction(_module, "snprintf", _snprintfType);

        _fopenType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _fopenFn = LLVM.LLVMAddFunction(_module, "fopen", _fopenType);

        _fseekType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i64, _i32 }, 3, isVarArg: false);
        _fseekFn = LLVM.LLVMAddFunction(_module, "fseek", _fseekType);

        _ftellType = LLVM.LLVMFunctionType(_i64, new[] { _i8ptr }, 1, isVarArg: false);
        _ftellFn = LLVM.LLVMAddFunction(_module, "ftell", _ftellType);

        _rewindType = LLVM.LLVMFunctionType(_void, new[] { _i8ptr }, 1, isVarArg: false);
        _rewindFn = LLVM.LLVMAddFunction(_module, "rewind", _rewindType);

        _mallocType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i64 }, 1, isVarArg: false);
        _mallocFn = LLVM.LLVMAddFunction(_module, "malloc", _mallocType);

        _reallocType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i64 }, 2, isVarArg: false);
        _reallocFn = LLVM.LLVMAddFunction(_module, "realloc", _reallocType);

        _freeType = LLVM.LLVMFunctionType(_void, new[] { _i8ptr }, 1, isVarArg: false);
        _freeFn = LLVM.LLVMAddFunction(_module, "free", _freeType);

        _freadType = LLVM.LLVMFunctionType(_i64, new[] { _i8ptr, _i64, _i64, _i8ptr }, 4, isVarArg: false);
        _freadFn = LLVM.LLVMAddFunction(_module, "fread", _freadType);

        _fcloseType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr }, 1, isVarArg: false);
        _fcloseFn = LLVM.LLVMAddFunction(_module, "fclose", _fcloseType);

        _fputsType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _fputsFn = LLVM.LLVMAddFunction(_module, "fputs", _fputsType);

        _fgetsType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i32, _i8ptr }, 3, isVarArg: false);
        _fgetsFn = LLVM.LLVMAddFunction(_module, "fgets", _fgetsType);

        _strstrType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _strstrFn = LLVM.LLVMAddFunction(_module, "strstr", _strstrType);

        _strlenType = LLVM.LLVMFunctionType(_i64, new[] { _i8ptr }, 1, isVarArg: false);
        _strlenFn = LLVM.LLVMAddFunction(_module, "strlen", _strlenType);

        _strcpyType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _strcpyFn = LLVM.LLVMAddFunction(_module, "strcpy", _strcpyType);

        _strncpyType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr, _i64 }, 3, isVarArg: false);
        _strncpyFn = LLVM.LLVMAddFunction(_module, "strncpy", _strncpyType);

        _strcatType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _strcatFn = LLVM.LLVMAddFunction(_module, "strcat", _strcatType);

        _strcmpType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i8ptr }, 2, isVarArg: false);
        _strcmpFn = LLVM.LLVMAddFunction(_module, "strcmp", _strcmpType);

        _strncmpType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr, _i8ptr, _i64 }, 3, isVarArg: false);
        _strncmpFn = LLVM.LLVMAddFunction(_module, "strncmp", _strncmpType);

        _getcharType = LLVM.LLVMFunctionType(_i32, Array.Empty<IntPtr>(), 0, isVarArg: false);
        _getcharFn = LLVM.LLVMAddFunction(_module, "getchar", _getcharType);

        _removeType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr }, 1, isVarArg: false);
        _removeFn = LLVM.LLVMAddFunction(_module, "remove", _removeType);

        _exitType = LLVM.LLVMFunctionType(_void, new[] { _i32 }, 1, isVarArg: false);
        _exitFn = LLVM.LLVMAddFunction(_module, "exit", _exitType);

        _memsetType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i32, _i64 }, 3, isVarArg: false);
        _memsetFn = LLVM.LLVMAddFunction(_module, "memset", _memsetType);

        _memcpyType = LLVM.LLVMFunctionType(_i8ptr, new[] { _i8ptr, _i8ptr, _i64 }, 3, isVarArg: false);
        _memcpyFn = LLVM.LLVMAddFunction(_module, "memcpy", _memcpyType);

        _atoiType = LLVM.LLVMFunctionType(_i32, new[] { _i8ptr }, 1, isVarArg: false);
        _atoiFn = LLVM.LLVMAddFunction(_module, "atoi", _atoiType);

        _atofType = LLVM.LLVMFunctionType(_double, new[] { _i8ptr }, 1, isVarArg: false);
        _atofFn = LLVM.LLVMAddFunction(_module, "atof", _atofType);
    }

    private IntPtr CreateList(string name, IntPtr currentFn)
    {
        var dataPtr = LLVM.LLVMBuildAlloca(_builder, LLVM.LLVMPointerType(_i8ptr, 0), name + "_data");
        var sizePtr = LLVM.LLVMBuildAlloca(_builder, _i32, name + "_size");
        var capPtr = LLVM.LLVMBuildAlloca(_builder, _i32, name + "_cap");

        var initialArr = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn,
            new[] { LLVM.LLVMConstInt(_i64, (ulong)(8 * IntPtr.Size), false) }, 1, Tmp("listinit"));
        LLVM.LLVMBuildStore(_builder, initialArr, dataPtr);
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), sizePtr);
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 8, false), capPtr);

        _lists[name] = (dataPtr, capPtr, sizePtr, "string");
        return dataPtr;
    }

    private void EmitStatement(Stmt stmt, IntPtr currentFn)
    {
        switch (stmt)
        {
            case DefineStmt d: EmitDefine(d, currentFn); break;
            case SetStmt s: EmitSet(s, currentFn); break;
            case IncreaseStmt i: EmitIncrease(i, currentFn); break;
            case DecreaseStmt d: EmitDecrease(d, currentFn); break;
            case MultiplyStmt m: EmitMultiply(m, currentFn); break;
            case DivideStmt d: EmitDivide(d, currentFn); break;
            case SayStmt s: EmitSay(s, currentFn); break;
            case ShowStmt s: EmitShow(s, currentFn); break;
            case AskStmt a: EmitAsk(a, currentFn); break;
            case ReadIntoStmt r: EmitReadInto(r, currentFn); break;
            case WriteIntoStmt w: EmitWriteInto(w, currentFn); break;
            case DeleteStmt d: EmitDelete(d, currentFn); break;
            case AddToListStmt a: EmitAddToList(a, currentFn); break;
            case RemoveFromListStmt r: EmitRemoveFromList(r, currentFn); break;
            case ClearListStmt c: EmitClearList(c, currentFn); break;
            case IfStmt i: EmitIf(i, currentFn); break;
            case RepeatTimesStmt r: EmitRepeatTimes(r, currentFn); break;
            case RepeatForeverStmt r: EmitRepeatForever(r, currentFn); break;
            case WhileStmt w: EmitWhile(w, currentFn); break;
            case ForEachStmt f: EmitForEach(f, currentFn); break;
            case FunctionDeclStmt f: EmitFunctionDecl(f); break;
            case CallStmt c: EmitCallStmt(c, currentFn); break;
            case ReturnStmt r: EmitReturn(r, currentFn); break;
            case TryCatchStmt t: EmitTryCatch(t, currentFn); break;
            default: throw new Exception("Unknown statement type: " + stmt.GetType().Name);
        }
    }

    private IntPtr TypeToLLVM(string type) => type switch
    {
        "int" => _i32,
        "float" => _double,
        "bool" => _i32,
        "string" => _i8ptr,
        "list" => _i8ptr,
        _ => throw new Exception("unknown type " + type)
    };

    private void EmitDefine(DefineStmt d, IntPtr currentFn)
    {
        if (d.Value is ListExpr)
        {
            var dataPtr = CreateList(d.Name, currentFn);
            var alloca = LLVM.LLVMBuildAlloca(_builder, _i8ptr, d.Name);
            LLVM.LLVMBuildStore(_builder, dataPtr, alloca);
            _variables[d.Name] = alloca;
            _varTypes[d.Name] = "list";
            return;
        }

        var (val, type) = EmitExpr(d.Value, currentFn);
        var alloca2 = LLVM.LLVMBuildAlloca(_builder, TypeToLLVM(type), d.Name);
        LLVM.LLVMBuildStore(_builder, val, alloca2);
        _variables[d.Name] = alloca2;
        _varTypes[d.Name] = type;
    }

    private void EmitSet(SetStmt s, IntPtr currentFn)
    {
        var (val, type) = EmitExpr(s.Value, currentFn);
        var strVal = type == "string" ? val : NumberToString(val, type);

        if (s.Target is IdentExpr id)
        {
            if (_variables.TryGetValue(id.Name, out var ptr))
            {
                var existingType = _varTypes[id.Name];
                if (existingType != type)
                    throw new Exception($"cannot assign a {type} value to '{id.Name}', which is {existingType}");
                LLVM.LLVMBuildStore(_builder, type == "string" ? val : NumberToString(val, type), ptr);
            }
            else
            {
                var alloca = LLVM.LLVMBuildAlloca(_builder, TypeToLLVM(type), id.Name);
                LLVM.LLVMBuildStore(_builder, type == "string" ? val : NumberToString(val, type), alloca);
                _variables[id.Name] = alloca;
                _varTypes[id.Name] = type;
            }
            return;
        }

        if (s.Target is ListIndexExpr lie)
        {
            if (lie.Target is not IdentExpr listId)
                throw new Exception("list index assignment requires a list variable");
            if (!_lists.TryGetValue(listId.Name, out var info))
                throw new Exception($"list '{listId.Name}' does not exist");

            var (index, indexType) = EmitExpr(lie.Index, currentFn);
            if (indexType != "int") throw new Exception("list index must be an integer");

            var size = LLVM.LLVMBuildLoad2(_builder, _i32, info.size, Tmp("lsetsize"));
            var isNeg = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, index, LLVM.LLVMConstInt(_i32, 0, false), Tmp("lsetneg"));
            var isTooLarge = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSGE, index, size, Tmp("lsetlarge"));
            var outOfBounds = LLVM.LLVMBuildOr(_builder, isNeg, isTooLarge, Tmp("lsetob"));

            var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "set_index_ok");
            var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "set_index_fail");
            var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "set_index_merge");
            LLVM.LLVMBuildCondBr(_builder, outOfBounds, failBB, okBB);

            LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
            var basePtr = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), info.array, Tmp("lsetbase"));
            var idx64 = LLVM.LLVMBuildZExt(_builder, index, _i64, Tmp("lsetidx64"));
            var elemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, basePtr, new[] { idx64 }, 1, Tmp("lsetptr"));
            LLVM.LLVMBuildStore(_builder, strVal, elemPtr);
            LLVM.LLVMBuildBr(_builder, mergeBB);

            LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
            LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 1, false), _errorFlagPtr);
            LLVM.LLVMBuildBr(_builder, mergeBB);

            LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
            return;
        }

        throw new Exception("assignment target must be a variable or list index");
    }

    private void EmitIncrease(IncreaseStmt stmt, IntPtr currentFn)
    {
        if (!_variables.TryGetValue(stmt.Name, out var ptr))
            throw new Exception($"undefined variable '{stmt.Name}'");
        var type = _varTypes[stmt.Name];
        var (amount, amtType) = EmitExpr(stmt.Amount, currentFn);
        var loaded = LLVM.LLVMBuildLoad2(_builder, TypeToLLVM(type), ptr, Tmp("val"));
        IntPtr result;
        if (type == "float")
        {
            var amtF = amtType == "float" ? amount : LLVM.LLVMBuildSIToFP(_builder, amount, _double, Tmp("af"));
            result = LLVM.LLVMBuildFAdd(_builder, loaded, amtF, Tmp("inc"));
        }
        else
        {
            result = LLVM.LLVMBuildAdd(_builder, loaded, amount, Tmp("inc"));
        }
        LLVM.LLVMBuildStore(_builder, result, ptr);
    }

    private void EmitDecrease(DecreaseStmt stmt, IntPtr currentFn)
    {
        if (!_variables.TryGetValue(stmt.Name, out var ptr))
            throw new Exception($"undefined variable '{stmt.Name}'");
        var type = _varTypes[stmt.Name];
        var (amount, amtType) = EmitExpr(stmt.Amount, currentFn);
        var loaded = LLVM.LLVMBuildLoad2(_builder, TypeToLLVM(type), ptr, Tmp("val"));
        IntPtr result;
        if (type == "float")
        {
            var amtF = amtType == "float" ? amount : LLVM.LLVMBuildSIToFP(_builder, amount, _double, Tmp("af"));
            result = LLVM.LLVMBuildFSub(_builder, loaded, amtF, Tmp("dec"));
        }
        else
        {
            result = LLVM.LLVMBuildSub(_builder, loaded, amount, Tmp("dec"));
        }
        LLVM.LLVMBuildStore(_builder, result, ptr);
    }

    private void EmitMultiply(MultiplyStmt stmt, IntPtr currentFn)
    {
        if (!_variables.TryGetValue(stmt.Name, out var ptr))
            throw new Exception($"undefined variable '{stmt.Name}'");
        var type = _varTypes[stmt.Name];
        var (amount, amtType) = EmitExpr(stmt.Amount, currentFn);
        var loaded = LLVM.LLVMBuildLoad2(_builder, TypeToLLVM(type), ptr, Tmp("val"));
        IntPtr result;
        if (type == "float")
        {
            var amtF = amtType == "float" ? amount : LLVM.LLVMBuildSIToFP(_builder, amount, _double, Tmp("af"));
            result = LLVM.LLVMBuildFMul(_builder, loaded, amtF, Tmp("mul"));
        }
        else
        {
            result = LLVM.LLVMBuildMul(_builder, loaded, amount, Tmp("mul"));
        }
        LLVM.LLVMBuildStore(_builder, result, ptr);
    }

    private void EmitDivide(DivideStmt stmt, IntPtr currentFn)
    {
        if (!_variables.TryGetValue(stmt.Name, out var ptr))
            throw new Exception($"undefined variable '{stmt.Name}'");
        var type = _varTypes[stmt.Name];
        var (amount, amtType) = EmitExpr(stmt.Amount, currentFn);
        var loaded = LLVM.LLVMBuildLoad2(_builder, TypeToLLVM(type), ptr, Tmp("val"));
        IntPtr result;
        if (type == "float")
        {
            var amtF = amtType == "float" ? amount : LLVM.LLVMBuildSIToFP(_builder, amount, _double, Tmp("af"));
            result = LLVM.LLVMBuildFDiv(_builder, loaded, amtF, Tmp("div"));
        }
        else
        {
            result = LLVM.LLVMBuildSDiv(_builder, loaded, amount, Tmp("div"));
        }
        LLVM.LLVMBuildStore(_builder, result, ptr);
    }

    private void EmitSay(SayStmt s, IntPtr currentFn)
    {
        var (val, type) = EmitExpr(s.Value, currentFn);
        switch (type)
        {
            case "int":
                {
                    var fmt = LLVM.LLVMBuildGlobalStringPtr(_builder, "%d\n", Tmp("fmt"));
                    LLVM.LLVMBuildCall2(_builder, _printfType, _printfFn, new[] { fmt, val }, 2, "");
                    break;
                }
            case "float":
                {
                    var fmt = LLVM.LLVMBuildGlobalStringPtr(_builder, "%g\n", Tmp("fmt"));
                    LLVM.LLVMBuildCall2(_builder, _printfType, _printfFn, new[] { fmt, val }, 2, "");
                    break;
                }
            case "bool":
                {
                    var yesStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "yes", Tmp("bstr"));
                    var noStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "no", Tmp("bstr"));
                    var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, val, LLVM.LLVMConstInt(_i32, 0, false), Tmp("bcond"));
                    var chosen = LLVM.LLVMBuildSelect(_builder, cond, yesStr, noStr, Tmp("bsel"));
                    var fmt = LLVM.LLVMBuildGlobalStringPtr(_builder, "%s\n", Tmp("fmt"));
                    LLVM.LLVMBuildCall2(_builder, _printfType, _printfFn, new[] { fmt, chosen }, 2, "");
                    break;
                }
            default:
                {
                    var fmt = LLVM.LLVMBuildGlobalStringPtr(_builder, "%s\n", Tmp("fmt"));
                    LLVM.LLVMBuildCall2(_builder, _printfType, _printfFn, new[] { fmt, val }, 2, "");
                    break;
                }
        }
    }

    private void EmitShow(ShowStmt s, IntPtr currentFn) => EmitSay(new SayStmt(s.Value), currentFn);

    private void EmitAsk(AskStmt a, IntPtr currentFn)
    {
        var (promptVal, promptType) = EmitExpr(a.Prompt, currentFn);
        var fmtPrompt = LLVM.LLVMBuildGlobalStringPtr(_builder, "%s", Tmp("fmt"));
        LLVM.LLVMBuildCall2(_builder, _printfType, _printfFn, new[] { fmtPrompt, promptVal }, 2, "");

        var buffer = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn, new[] { LLVM.LLVMConstInt(_i64, 1024, false) }, 1, Tmp("askbuf"));

        var idx = LLVM.LLVMBuildAlloca(_builder, _i64, "askidx");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i64, 0, false), idx);

        var loopBB = LLVM.LLVMAppendBasicBlock(currentFn, "ask_loop");
        var doneBB = LLVM.LLVMAppendBasicBlock(currentFn, "ask_done");
        LLVM.LLVMBuildBr(_builder, loopBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, loopBB);
        var ch = LLVM.LLVMBuildCall2(_builder, _getcharType, _getcharFn, Array.Empty<IntPtr>(), 0, Tmp("ch"));
        var isNewline = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, ch, LLVM.LLVMConstInt(_i32, 10, false), Tmp("isnl"));
        var isEof = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, ch, LLVM.LLVMConstInt(_i32, 0xFFFFFFFF, true), Tmp("iseof"));
        var isEnd = LLVM.LLVMBuildOr(_builder, isNewline, isEof, Tmp("isend"));
        LLVM.LLVMBuildCondBr(_builder, isEnd, doneBB, loopBB);

        var alloca = LLVM.LLVMBuildAlloca(_builder, _i8ptr, a.VarName);
        LLVM.LLVMBuildStore(_builder, buffer, alloca);
        _variables[a.VarName] = alloca;
        _varTypes[a.VarName] = "string";
    }

    private void EmitReadInto(ReadIntoStmt r, IntPtr currentFn)
    {
        var val = EmitReadFile(r.FilePath, currentFn);
        var alloca = LLVM.LLVMBuildAlloca(_builder, _i8ptr, r.VarName);
        LLVM.LLVMBuildStore(_builder, val, alloca);
        _variables[r.VarName] = alloca;
        _varTypes[r.VarName] = "string";
        EmitBranchToCatchIfError(currentFn);
    }

    private void EmitWriteInto(WriteIntoStmt w, IntPtr currentFn)
    {
        var (val, type) = EmitExpr(w.Value, currentFn);
        IntPtr toWrite = type == "string" ? val : NumberToString(val, type);

        var pathStr = LLVM.LLVMBuildGlobalStringPtr(_builder, w.FilePath, Tmp("wpath"));
        var modeStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "w", Tmp("wmode"));
        var filePtr = LLVM.LLVMBuildCall2(_builder, _fopenType, _fopenFn, new[] { pathStr, modeStr }, 2, Tmp("wfile"));

        var nullPtr = LLVM.LLVMConstPointerNull(_i8ptr);
        var isNull = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, filePtr, nullPtr, Tmp("wisnull"));

        var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "write_ok");
        var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "write_fail");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "write_merge");
        LLVM.LLVMBuildCondBr(_builder, isNull, failBB, okBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
        LLVM.LLVMBuildCall2(_builder, _fputsType, _fputsFn, new[] { toWrite, filePtr }, 2, "");
        LLVM.LLVMBuildCall2(_builder, _fcloseType, _fcloseFn, new[] { filePtr }, 1, "");
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 1, false), _errorFlagPtr);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        EmitBranchToCatchIfError(currentFn);
    }

    private void EmitDelete(DeleteStmt d, IntPtr currentFn)
    {
        var pathStr = LLVM.LLVMBuildGlobalStringPtr(_builder, d.FilePath, Tmp("dpath"));
        LLVM.LLVMBuildCall2(_builder, _removeType, _removeFn, new[] { pathStr }, 1, "");
    }


    private void EmitIf(IfStmt s, IntPtr currentFn)
    {
        var (condVal, condType) = EmitExpr(s.Condition, currentFn);
        if (condType != "bool") throw new Exception("if condition must be a boolean expression");
        var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, condVal, LLVM.LLVMConstInt(_i32, 0, false), Tmp("ifcond"));

        var thenBB = LLVM.LLVMAppendBasicBlock(currentFn, "if_then");
        var elseBB = LLVM.LLVMAppendBasicBlock(currentFn, "if_else");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "if_merge");
        LLVM.LLVMBuildCondBr(_builder, cond, thenBB, elseBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, thenBB);
        foreach (var st in s.ThenBlock) EmitStatement(st, currentFn);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, elseBB);
        if (s.ElseBlock != null)
            foreach (var st in s.ElseBlock) EmitStatement(st, currentFn);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
    }

    private void EmitRepeatTimes(RepeatTimesStmt s, IntPtr currentFn)
    {
        var (countVal, countType) = EmitExpr(s.Count, currentFn);
        if (countType != "int") throw new Exception("repeat times requires an integer count");

        var idxPtr = LLVM.LLVMBuildAlloca(_builder, _i32, "loopidx");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), idxPtr);

        var condBB = LLVM.LLVMAppendBasicBlock(currentFn, "repeat_cond");
        var bodyBB = LLVM.LLVMAppendBasicBlock(currentFn, "repeat_body");
        var afterBB = LLVM.LLVMAppendBasicBlock(currentFn, "repeat_after");

        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, condBB);
        var idx = LLVM.LLVMBuildLoad2(_builder, _i32, idxPtr, Tmp("idx"));
        var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, idx, countVal, Tmp("rcond"));
        LLVM.LLVMBuildCondBr(_builder, cond, bodyBB, afterBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, bodyBB);
        foreach (var st in s.Body) EmitStatement(st, currentFn);
        var nextIdx = LLVM.LLVMBuildAdd(_builder, idx, LLVM.LLVMConstInt(_i32, 1, false), Tmp("next"));
        LLVM.LLVMBuildStore(_builder, nextIdx, idxPtr);
        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, afterBB);
    }

    private void EmitRepeatForever(RepeatForeverStmt s, IntPtr currentFn)
    {
        var bodyBB = LLVM.LLVMAppendBasicBlock(currentFn, "forever_body");
        var afterBB = LLVM.LLVMAppendBasicBlock(currentFn, "forever_after");

        LLVM.LLVMBuildBr(_builder, bodyBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, bodyBB);
        foreach (var st in s.Body) EmitStatement(st, currentFn);
        LLVM.LLVMBuildBr(_builder, bodyBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, afterBB);
    }

    private void EmitWhile(WhileStmt s, IntPtr currentFn)
    {
        var condBB = LLVM.LLVMAppendBasicBlock(currentFn, "while_cond");
        var bodyBB = LLVM.LLVMAppendBasicBlock(currentFn, "while_body");
        var afterBB = LLVM.LLVMAppendBasicBlock(currentFn, "while_after");

        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, condBB);
        var (condVal, condType) = EmitExpr(s.Condition, currentFn);
        if (condType != "bool") throw new Exception("while condition must be a boolean expression");
        var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, condVal, LLVM.LLVMConstInt(_i32, 0, false), Tmp("wcond"));
        LLVM.LLVMBuildCondBr(_builder, cond, bodyBB, afterBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, bodyBB);
        foreach (var st in s.Body) EmitStatement(st, currentFn);
        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, afterBB);
    }

    private void EmitForEach(ForEachStmt s, IntPtr currentFn)
    {
        if (!(s.Collection is IdentExpr id))
            throw new Exception("for every requires a list variable");
        if (!_lists.TryGetValue(id.Name, out var listInfo))
            throw new Exception($"list '{id.Name}' not found");

        var idxPtr = LLVM.LLVMBuildAlloca(_builder, _i32, "feidx");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), idxPtr);

        var condBB = LLVM.LLVMAppendBasicBlock(currentFn, "fe_cond");
        var bodyBB = LLVM.LLVMAppendBasicBlock(currentFn, "fe_body");
        var afterBB = LLVM.LLVMAppendBasicBlock(currentFn, "fe_after");

        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, condBB);
        var idx = LLVM.LLVMBuildLoad2(_builder, _i32, idxPtr, Tmp("idx"));
        var size = LLVM.LLVMBuildLoad2(_builder, _i32, listInfo.size, Tmp("fesize"));
        var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, idx, size, Tmp("fecond"));
        LLVM.LLVMBuildCondBr(_builder, cond, bodyBB, afterBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, bodyBB);
        var arr = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), listInfo.array, Tmp("fearr"));
        var elemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, arr, new[] { idx }, 1, Tmp("feeptr"));
        var elem = LLVM.LLVMBuildLoad2(_builder, _i8ptr, elemPtr, Tmp("feelem"));
        var varPtr = LLVM.LLVMBuildAlloca(_builder, _i8ptr, s.VarName);
        LLVM.LLVMBuildStore(_builder, elem, varPtr);
        _variables[s.VarName] = varPtr;
        _varTypes[s.VarName] = "string";

        foreach (var st in s.Body) EmitStatement(st, currentFn);

        var nextIdx = LLVM.LLVMBuildAdd(_builder, idx, LLVM.LLVMConstInt(_i32, 1, false), Tmp("fenext"));
        LLVM.LLVMBuildStore(_builder, nextIdx, idxPtr);
        LLVM.LLVMBuildBr(_builder, condBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, afterBB);
    }

    private void EmitBranchToCatchIfError(IntPtr currentFn)
    {
        if (_currentTryCatchCatchBlock is not IntPtr catchBB)
            return;

        var flagVal = LLVM.LLVMBuildLoad2(_builder, _i32, _errorFlagPtr, Tmp("errval"));
        var hasError = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, flagVal, LLVM.LLVMConstInt(_i32, 0, false), Tmp("haserr"));
        var continueBB = LLVM.LLVMAppendBasicBlock(currentFn, "try_cont");
        LLVM.LLVMBuildCondBr(_builder, hasError, catchBB, continueBB);
        LLVM.LLVMPositionBuilderAtEnd(_builder, continueBB);
    }

    private void EmitTryCatch(TryCatchStmt t, IntPtr currentFn)
    {
        var catchBB = LLVM.LLVMAppendBasicBlock(currentFn, "catch");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "try_merge");
        var previousCatchBlock = _currentTryCatchCatchBlock;
        _currentTryCatchCatchBlock = catchBB;

        try
        {
            LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), _errorFlagPtr);
            foreach (var st in t.TryBlock) EmitStatement(st, currentFn);

            var flagVal = LLVM.LLVMBuildLoad2(_builder, _i32, _errorFlagPtr, Tmp("errval"));
            var hasError = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, flagVal, LLVM.LLVMConstInt(_i32, 0, false), Tmp("haserr"));
            LLVM.LLVMBuildCondBr(_builder, hasError, catchBB, mergeBB);

            LLVM.LLVMPositionBuilderAtEnd(_builder, catchBB);
            foreach (var st in t.CatchBlock) EmitStatement(st, currentFn);
            LLVM.LLVMBuildBr(_builder, mergeBB);

            LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        }
        finally
        {
            _currentTryCatchCatchBlock = previousCatchBlock;
        }
    }


    private void EmitFunctionDecl(FunctionDeclStmt f)
    {
        var paramTypes = new List<IntPtr>();
        for (int i = 0; i < f.Parameters.Count; i++) paramTypes.Add(_i8ptr);

        var fnType = LLVM.LLVMFunctionType(_i8ptr, paramTypes.ToArray(), (uint)f.Parameters.Count, isVarArg: false);
        var fn = LLVM.LLVMAddFunction(_module, f.Name, fnType);
        var entry = LLVM.LLVMAppendBasicBlockInContext(_ctx, fn, "fn_entry");

        var oldBuilder = _builder;
        var oldVars = new Dictionary<string, IntPtr>(_variables);
        var oldTypes = new Dictionary<string, string>(_varTypes);
        var oldFn = _currentFunction;

        _builder = LLVM.LLVMCreateBuilderInContext(_ctx);
        LLVM.LLVMPositionBuilderAtEnd(_builder, entry);
        _currentFunction = fn;
        _variables.Clear();
        _varTypes.Clear();

        for (int i = 0; i < f.Parameters.Count; i++)
        {
            var param = LLVM.LLVMGetParam(fn, (uint)i);
            var alloca = LLVM.LLVMBuildAlloca(_builder, _i8ptr, f.Parameters[i]);
            LLVM.LLVMBuildStore(_builder, param, alloca);
            _variables[f.Parameters[i]] = alloca;
            _varTypes[f.Parameters[i]] = "string";
        }

        _hasReturned = false;
        foreach (var stmt in f.Body)
        {
            EmitStatement(stmt, fn);
            if (_hasReturned) break;
        }
        if (!_hasReturned)
            LLVM.LLVMBuildRet(_builder, LLVM.LLVMBuildGlobalStringPtr(_builder, "", Tmp("retempty")));

        _builder = oldBuilder;
        _variables = oldVars;
        _varTypes = oldTypes;
        _currentFunction = oldFn;

        _functions[f.Name] = (fn, fnType, f.Parameters);
    }

    private void EmitCallStmt(CallStmt c, IntPtr currentFn)
    {
        if (!_functions.TryGetValue(c.Name, out var fnInfo))
            throw new Exception($"undefined function '{c.Name}'");

        var args = new List<IntPtr>();
        foreach (var arg in c.Arguments)
        {
            var (val, type) = EmitExpr(arg, currentFn);
            args.Add(type == "string" ? val : NumberToString(val, type));
        }

        LLVM.LLVMBuildCall2(_builder, fnInfo.funcType, fnInfo.func, args.ToArray(), (uint)args.Count, "");
    }

    private void EmitReturn(ReturnStmt r, IntPtr currentFn)
    {
        if (r.Value == null)
            LLVM.LLVMBuildRet(_builder, LLVM.LLVMBuildGlobalStringPtr(_builder, "", Tmp("retempty")));
        else
        {
            var (val, type) = EmitExpr(r.Value, currentFn);
            LLVM.LLVMBuildRet(_builder, type == "string" ? val : NumberToString(val, type));
        }
        _hasReturned = true;
    }

    private void EmitAddToList(AddToListStmt a, IntPtr currentFn)
    {
        var (val, type) = EmitExpr(a.Value, currentFn);
        var strVal = type == "string" ? val : NumberToString(val, type);

        if (!_lists.TryGetValue(a.ListName, out var listInfo))
        {
            var dataPtr = LLVM.LLVMBuildAlloca(_builder, LLVM.LLVMPointerType(_i8ptr, 0), a.ListName + "_data");
            var sizePtr = LLVM.LLVMBuildAlloca(_builder, _i32, a.ListName + "_size");
            var capPtr = LLVM.LLVMBuildAlloca(_builder, _i32, a.ListName + "_cap");

            var initialArr = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn,
                new[] { LLVM.LLVMConstInt(_i64, (ulong)(8 * IntPtr.Size), false) }, 1, Tmp("listinit"));
            LLVM.LLVMBuildStore(_builder, initialArr, dataPtr);
            LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), sizePtr);
            LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 8, false), capPtr);

            listInfo = (dataPtr, capPtr, sizePtr, "string");
            _lists[a.ListName] = listInfo;
            _variables[a.ListName] = dataPtr;
            _varTypes[a.ListName] = "list";
        }

        var size = LLVM.LLVMBuildLoad2(_builder, _i32, listInfo.size, Tmp("lsize"));
        var cap = LLVM.LLVMBuildLoad2(_builder, _i32, listInfo.capacity, Tmp("lcap"));
        var needGrow = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSGE, size, cap, Tmp("lgrow"));

        var growBB = LLVM.LLVMAppendBasicBlock(currentFn, "list_grow");
        var addBB = LLVM.LLVMAppendBasicBlock(currentFn, "list_add");
        LLVM.LLVMBuildCondBr(_builder, needGrow, growBB, addBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, growBB);
        var newCap = LLVM.LLVMBuildMul(_builder, cap, LLVM.LLVMConstInt(_i32, 2, false), Tmp("lnewcap"));
        LLVM.LLVMBuildStore(_builder, newCap, listInfo.capacity);
        var data = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), listInfo.array, Tmp("ldata"));
        var newCap64 = LLVM.LLVMBuildZExt(_builder, newCap, _i64, Tmp("lcap64"));
        var elemSize = LLVM.LLVMConstInt(_i64, (ulong)IntPtr.Size, false);
        var newBytes = LLVM.LLVMBuildMul(_builder, newCap64, elemSize, Tmp("lbytes"));
        var newData = LLVM.LLVMBuildCall2(_builder, _reallocType, _reallocFn, new[] { data, newBytes }, 2, Tmp("lrealloc"));
        LLVM.LLVMBuildStore(_builder, newData, listInfo.array);
        LLVM.LLVMBuildBr(_builder, addBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, addBB);
        var finalData = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), listInfo.array, Tmp("lfdata"));
        var idx = LLVM.LLVMBuildZExt(_builder, size, _i64, Tmp("lidx64"));
        var elemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, finalData, new[] { idx }, 1, Tmp("leptr"));
        LLVM.LLVMBuildStore(_builder, strVal, elemPtr);
        var newSize = LLVM.LLVMBuildAdd(_builder, size, LLVM.LLVMConstInt(_i32, 1, false), Tmp("lnsize"));
        LLVM.LLVMBuildStore(_builder, newSize, listInfo.size);
    }

    private void EmitRemoveFromList(RemoveFromListStmt r, IntPtr currentFn)
    {
        if (!_lists.TryGetValue(r.ListName, out var listInfo))
            throw new Exception($"list '{r.ListName}' does not exist");

        var (val, type) = EmitExpr(r.Value, currentFn);
        var strVal = type == "string" ? val : NumberToString(val, type);

        var size = LLVM.LLVMBuildLoad2(_builder, _i32, listInfo.size, Tmp("rsize"));
        var iPtr = LLVM.LLVMBuildAlloca(_builder, _i32, "ri");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), iPtr);

        var loopBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_loop");
        var foundBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_found");
        var shiftBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_shift");
        var shiftLoopBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_shift_loop");
        var shiftLoopCondBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_shift_cond");
        var nextBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_next");
        var doneBB = LLVM.LLVMAppendBasicBlock(currentFn, "rem_done");

        LLVM.LLVMBuildBr(_builder, loopBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, loopBB);
        var i = LLVM.LLVMBuildLoad2(_builder, _i32, iPtr, Tmp("ri"));
        var cond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, i, size, Tmp("rcond"));
        LLVM.LLVMBuildCondBr(_builder, cond, foundBB, doneBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, foundBB);
        var data = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), listInfo.array, Tmp("rdata"));
        var idx = LLVM.LLVMBuildZExt(_builder, i, _i64, Tmp("ridx"));
        var elemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, data, new[] { idx }, 1, Tmp("reptr"));
        var elem = LLVM.LLVMBuildLoad2(_builder, _i8ptr, elemPtr, Tmp("relem"));
        var cmp = LLVM.LLVMBuildCall2(_builder, _strcmpType, _strcmpFn, new[] { elem, strVal }, 2, Tmp("rcmp"));
        var isMatch = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, cmp, LLVM.LLVMConstInt(_i32, 0, false), Tmp("rmatch"));
        LLVM.LLVMBuildCondBr(_builder, isMatch, shiftBB, nextBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, shiftBB);
        var newSize = LLVM.LLVMBuildSub(_builder, size, LLVM.LLVMConstInt(_i32, 1, false), Tmp("rnsize"));
        LLVM.LLVMBuildStore(_builder, newSize, listInfo.size);

        var jPtr = LLVM.LLVMBuildAlloca(_builder, _i32, "rj");
        var startJ = LLVM.LLVMBuildAdd(_builder, i, LLVM.LLVMConstInt(_i32, 1, false), Tmp("rjstart"));
        LLVM.LLVMBuildStore(_builder, startJ, jPtr);
        LLVM.LLVMBuildBr(_builder, shiftLoopCondBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, shiftLoopCondBB);
        var j = LLVM.LLVMBuildLoad2(_builder, _i32, jPtr, Tmp("rj"));
        var shiftCond = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, j, size, Tmp("rshiftcond"));
        LLVM.LLVMBuildCondBr(_builder, shiftCond, shiftLoopBB, nextBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, shiftLoopBB);
        var srcIdx = LLVM.LLVMBuildZExt(_builder, j, _i64, Tmp("rsrcidx"));
        var dstIdx = LLVM.LLVMBuildSub(_builder, j, LLVM.LLVMConstInt(_i32, 1, false), Tmp("rdstidx"));
        var srcElemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, data, new[] { srcIdx }, 1, Tmp("rsrcptr"));
        var dstElemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, data, new[] { LLVM.LLVMBuildZExt(_builder, dstIdx, _i64, Tmp("rdstidx64")) }, 1, Tmp("rdstptr"));
        var srcElem = LLVM.LLVMBuildLoad2(_builder, _i8ptr, srcElemPtr, Tmp("rsrcelm"));
        LLVM.LLVMBuildStore(_builder, srcElem, dstElemPtr);
        var nextJ = LLVM.LLVMBuildAdd(_builder, j, LLVM.LLVMConstInt(_i32, 1, false), Tmp("rnj"));
        LLVM.LLVMBuildStore(_builder, nextJ, jPtr);
        LLVM.LLVMBuildBr(_builder, shiftLoopCondBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, nextBB);
        var nextI = LLVM.LLVMBuildAdd(_builder, i, LLVM.LLVMConstInt(_i32, 1, false), Tmp("rnext"));
        LLVM.LLVMBuildStore(_builder, nextI, iPtr);
        LLVM.LLVMBuildBr(_builder, loopBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, doneBB);
    }

    private void EmitClearList(ClearListStmt c, IntPtr currentFn)
    {
        if (!_lists.TryGetValue(c.ListName, out var listInfo))
            throw new Exception($"list '{c.ListName}' does not exist");
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 0, false), listInfo.size);
    }

    private (IntPtr value, string type) EmitExpr(Expr expr, IntPtr currentFn)
    {
        switch (expr)
        {
            case NumberExpr n: return (LLVM.LLVMConstInt(_i32, unchecked((ulong)(long)n.Value), true), "int");
            case FloatExpr f: return (LLVM.LLVMConstReal(_double, f.Value), "float");
            case BoolExpr b: return (LLVM.LLVMConstInt(_i32, b.Value ? 1u : 0u, false), "bool");
            case StringExpr s: return (LLVM.LLVMBuildGlobalStringPtr(_builder, s.Value, Tmp("str")), "string");

            case IdentExpr id:
                {
                    if (!_variables.TryGetValue(id.Name, out var ptr))
                        throw new Exception($"undefined variable '{id.Name}'");
                    var type = _varTypes[id.Name];
                    var loaded = LLVM.LLVMBuildLoad2(_builder, TypeToLLVM(type), ptr, Tmp("val"));
                    return (loaded, type);
                }

            case ReadFileExpr r: return (EmitReadFile(r.FilePath, currentFn), "string");
            case ExistExpr e: return EmitExist(e.FilePath, currentFn);

            case ListExpr: return (LLVM.LLVMConstPointerNull(LLVM.LLVMPointerType(_i8ptr, 0)), "list");
            case ListLengthExpr lle: return EmitListLength(lle, currentFn);
            case ListIndexExpr lie: return EmitListIndex(lie, currentFn);

            case StartsWithExpr swe: return EmitStartsWith(swe, currentFn);
            case EndsWithExpr ewe: return EmitEndsWith(ewe, currentFn);
            case LengthOfExpr loe: return EmitLengthOf(loe, currentFn);

            case UnaryExpr u: return EmitUnary(u, currentFn);
            case BinaryExpr b: return EmitBinary(b, currentFn);

            case CallExpr c:
                if (!_functions.TryGetValue(c.Name, out var fnInfo))
                    throw new Exception($"undefined function '{c.Name}'");
                var callArgs = new List<IntPtr>();
                foreach (var arg in c.Arguments)
                {
                    var (val, type) = EmitExpr(arg, currentFn);
                    callArgs.Add(type == "string" ? val : NumberToString(val, type));
                }
                var callResult = LLVM.LLVMBuildCall2(_builder, fnInfo.funcType, fnInfo.func,
                    callArgs.ToArray(), (uint)callArgs.Count, Tmp("callres"));
                return (callResult, "string");

            default: throw new Exception("Unknown expression type: " + expr.GetType().Name);
        }
    }

    private (IntPtr, string) EmitUnary(UnaryExpr u, IntPtr currentFn)
    {
        var (val, type) = EmitExpr(u.Operand, currentFn);
        if (u.Op == "-")
        {
            if (type == "int") return (LLVM.LLVMBuildSub(_builder, LLVM.LLVMConstInt(_i32, 0, false), val, Tmp("neg")), "int");
            if (type == "float") return (LLVM.LLVMBuildFSub(_builder, LLVM.LLVMConstReal(_double, 0.0), val, Tmp("negf")), "float");
            throw new Exception("unary '-' requires a number");
        }
        if (u.Op == "not")
        {
            if (type != "bool") throw new Exception("'not' requires a boolean");
            var isZero = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, val, LLVM.LLVMConstInt(_i32, 0, false), Tmp("iszero"));
            return (LLVM.LLVMBuildZExt(_builder, isZero, _i32, Tmp("notz")), "bool");
        }
        throw new Exception("unknown unary operator " + u.Op);
    }

    private (IntPtr, string) EmitBinary(BinaryExpr b, IntPtr currentFn)
    {
        if (b.Op == "and" || b.Op == "or")
        {
            var (lval, ltype) = EmitExpr(b.Left, currentFn);
            var (rval, rtype) = EmitExpr(b.Right, currentFn);
            if (ltype != "bool" || rtype != "bool") throw new Exception($"'{b.Op}' requires boolean operands");
            var li1 = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, lval, LLVM.LLVMConstInt(_i32, 0, false), Tmp("l1"));
            var ri1 = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, rval, LLVM.LLVMConstInt(_i32, 0, false), Tmp("r1"));
            var res = b.Op == "and"
                ? LLVM.LLVMBuildAnd(_builder, li1, ri1, Tmp("andr"))
                : LLVM.LLVMBuildOr(_builder, li1, ri1, Tmp("orr"));
            return (LLVM.LLVMBuildZExt(_builder, res, _i32, Tmp("bz")), "bool");
        }

        {
            var (lval, ltype) = EmitExpr(b.Left, currentFn);
            var (rval, rtype) = EmitExpr(b.Right, currentFn);

            if (b.Op == "contains")
            {
                if (ltype != "string" || rtype != "string") throw new Exception("'contains' requires strings");
                var res = LLVM.LLVMBuildCall2(_builder, _strstrType, _strstrFn, new[] { lval, rval }, 2, Tmp("strstr"));
                var found = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntNE, res, LLVM.LLVMConstPointerNull(_i8ptr), Tmp("found"));
                return (LLVM.LLVMBuildZExt(_builder, found, _i32, Tmp("cz")), "bool");
            }

            if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                if (ltype == "string" && rtype == "string")
                {
                    if (b.Op != "==" && b.Op != "!=") throw new Exception("strings can only be compared with == or !=");
                    var cmp = LLVM.LLVMBuildCall2(_builder, _strcmpType, _strcmpFn, new[] { lval, rval }, 2, Tmp("strcmp"));
                    var pred = b.Op == "==" ? LLVM.LLVMIntPredicate.LLVMIntEQ : LLVM.LLVMIntPredicate.LLVMIntNE;
                    var res = LLVM.LLVMBuildICmp(_builder, pred, cmp, LLVM.LLVMConstInt(_i32, 0, false), Tmp("scmp"));
                    return (LLVM.LLVMBuildZExt(_builder, res, _i32, Tmp("sz")), "bool");
                }
                if (ltype == "string" || rtype == "string") throw new Exception("cannot compare a string with a non-string");

                bool useFloat = ltype == "float" || rtype == "float";
                if (useFloat)
                {
                    var lf = ltype == "float" ? lval : LLVM.LLVMBuildSIToFP(_builder, lval, _double, Tmp("lf"));
                    var rf = rtype == "float" ? rval : LLVM.LLVMBuildSIToFP(_builder, rval, _double, Tmp("rf"));
                    var pred = b.Op switch
                    {
                        "==" => LLVM.LLVMRealPredicate.LLVMRealOEQ,
                        "!=" => LLVM.LLVMRealPredicate.LLVMRealONE,
                        "<" => LLVM.LLVMRealPredicate.LLVMRealOLT,
                        "<=" => LLVM.LLVMRealPredicate.LLVMRealOLE,
                        ">" => LLVM.LLVMRealPredicate.LLVMRealOGT,
                        _ => LLVM.LLVMRealPredicate.LLVMRealOGE
                    };
                    var res = LLVM.LLVMBuildFCmp(_builder, pred, lf, rf, Tmp("fcmp"));
                    return (LLVM.LLVMBuildZExt(_builder, res, _i32, Tmp("fz")), "bool");
                }
                else
                {
                    var pred = b.Op switch
                    {
                        "==" => LLVM.LLVMIntPredicate.LLVMIntEQ,
                        "!=" => LLVM.LLVMIntPredicate.LLVMIntNE,
                        "<" => LLVM.LLVMIntPredicate.LLVMIntSLT,
                        "<=" => LLVM.LLVMIntPredicate.LLVMIntSLE,
                        ">" => LLVM.LLVMIntPredicate.LLVMIntSGT,
                        _ => LLVM.LLVMIntPredicate.LLVMIntSGE
                    };
                    var res = LLVM.LLVMBuildICmp(_builder, pred, lval, rval, Tmp("icmp"));
                    return (LLVM.LLVMBuildZExt(_builder, res, _i32, Tmp("iz")), "bool");
                }
            }

            if (b.Op == "+" && (ltype == "string" || rtype == "string"))
            {
                var lstr = ltype == "string" ? lval : NumberToString(lval, ltype);
                var rstr = rtype == "string" ? rval : NumberToString(rval, rtype);
                return (StringConcat(lstr, rstr), "string");
            }
            if (ltype == "string" || rtype == "string")
                throw new Exception($"operator '{b.Op}' is not supported on strings (only '+' is)");
            if (ltype == "bool" || rtype == "bool")
                throw new Exception($"operator '{b.Op}' is not supported on booleans");

            bool anyFloat = ltype == "float" || rtype == "float";
            if (anyFloat)
            {
                var lf = ltype == "float" ? lval : LLVM.LLVMBuildSIToFP(_builder, lval, _double, Tmp("lf"));
                var rf = rtype == "float" ? rval : LLVM.LLVMBuildSIToFP(_builder, rval, _double, Tmp("rf"));
                IntPtr res = b.Op switch
                {
                    "+" => LLVM.LLVMBuildFAdd(_builder, lf, rf, Tmp("fadd")),
                    "-" => LLVM.LLVMBuildFSub(_builder, lf, rf, Tmp("fsub")),
                    "*" => LLVM.LLVMBuildFMul(_builder, lf, rf, Tmp("fmul")),
                    "/" => LLVM.LLVMBuildFDiv(_builder, lf, rf, Tmp("fdiv")),
                    _ => throw new Exception("unknown operator " + b.Op)
                };
                return (res, "float");
            }
            else
            {
                IntPtr res = b.Op switch
                {
                    "+" => LLVM.LLVMBuildAdd(_builder, lval, rval, Tmp("add")),
                    "-" => LLVM.LLVMBuildSub(_builder, lval, rval, Tmp("sub")),
                    "*" => LLVM.LLVMBuildMul(_builder, lval, rval, Tmp("mul")),
                    "/" => LLVM.LLVMBuildSDiv(_builder, lval, rval, Tmp("div")),
                    _ => throw new Exception("unknown operator " + b.Op)
                };
                return (res, "int");
            }
        }
    }

    private (IntPtr, string) EmitStartsWith(StartsWithExpr swe, IntPtr currentFn)
    {
        var (str, stype) = EmitExpr(swe.Str, currentFn);
        var (prefix, ptype) = EmitExpr(swe.Prefix, currentFn);
        if (stype != "string" || ptype != "string") throw new Exception("'starts with' requires strings");
        var len = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { prefix }, 1, Tmp("swlen"));
        var cmp = LLVM.LLVMBuildCall2(_builder, _strncmpType, _strncmpFn, new[] { str, prefix, len }, 3, Tmp("swcmp"));
        var res = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, cmp, LLVM.LLVMConstInt(_i32, 0, false), Tmp("swres"));
        return (LLVM.LLVMBuildZExt(_builder, res, _i32, Tmp("swz")), "bool");
    }

    private (IntPtr, string) EmitEndsWith(EndsWithExpr ewe, IntPtr currentFn)
    {
        var (str, stype) = EmitExpr(ewe.Str, currentFn);
        var (suffix, sftype) = EmitExpr(ewe.Suffix, currentFn);
        if (stype != "string" || sftype != "string")
            throw new Exception("'ends with' requires strings");

        var strLen = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { str }, 1, Tmp("ewslen"));
        var sufLen = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { suffix }, 1, Tmp("ewfl"));
        var diff = LLVM.LLVMBuildSub(_builder, strLen, sufLen, Tmp("ewdiff"));
        var neg = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, diff, LLVM.LLVMConstInt(_i64, 0, false), Tmp("ewneg"));

        var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "ew_ok");
        var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "ew_fail");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "ew_merge");
        LLVM.LLVMBuildCondBr(_builder, neg, failBB, okBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
        var startPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8, str, new[] { diff }, 1, Tmp("ewsptr"));
        var cmp = LLVM.LLVMBuildCall2(_builder, _strcmpType, _strcmpFn, new[] { startPtr, suffix }, 2, Tmp("ewcmp"));
        var eq = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, cmp, LLVM.LLVMConstInt(_i32, 0, false), Tmp("eweq"));
        
        var eq32 = LLVM.LLVMBuildZExt(_builder, eq, _i32, Tmp("eweq32"));
        var okEnd = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
        var falseVal = LLVM.LLVMConstInt(_i32, 0, false);
        var failEnd = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        var phi = LLVM.LLVMBuildPhi(_builder, _i32, Tmp("ewphi"));
        LLVM.LLVMAddIncoming(phi, new[] { eq32, falseVal }, new[] { okEnd, failEnd }, 2);
        return (phi, "bool");
    }

    private (IntPtr, string) EmitLengthOf(LengthOfExpr loe, IntPtr currentFn)
    {
        var (target, type) = EmitExpr(loe.Target, currentFn);
        if (type == "string")
        {
            var len = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { target }, 1, Tmp("strlen"));
            var len32 = LLVM.LLVMBuildTrunc(_builder, len, _i32, Tmp("len32"));
            return (len32, "int");
        }
        if (type == "list")
        {
            if (loe.Target is IdentExpr id && _lists.TryGetValue(id.Name, out var info))
            {
                var size = LLVM.LLVMBuildLoad2(_builder, _i32, info.size, Tmp("llsize"));
                return (size, "int");
            }
            return (LLVM.LLVMConstInt(_i32, 0, false), "int");
        }
        throw new Exception("'length of' requires a string or list");
    }

    private (IntPtr, string) EmitListLength(ListLengthExpr lle, IntPtr currentFn)
    {
        if (lle.ListExpr is IdentExpr id && _lists.TryGetValue(id.Name, out var info))
        {
            var size = LLVM.LLVMBuildLoad2(_builder, _i32, info.size, Tmp("llsize"));
            return (size, "int");
        }
        return (LLVM.LLVMConstInt(_i32, 0, false), "int");
    }

    private (IntPtr, string) EmitListIndex(ListIndexExpr lie, IntPtr currentFn)
    {
        var (target, targetType) = EmitExpr(lie.Target, currentFn);
        var (index, indexType) = EmitExpr(lie.Index, currentFn);
        if (targetType != "list")
            throw new Exception("list index requires a list variable");
        if (lie.Target is not IdentExpr id)
            throw new Exception("list index requires a list variable");
        if (!_lists.TryGetValue(id.Name, out var info))
            throw new Exception("list index requires a list variable");
        if (indexType != "int")
            throw new Exception("list index must be an integer");

        var size = LLVM.LLVMBuildLoad2(_builder, _i32, info.size, Tmp("lidxsize"));
        var isNeg = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSLT, index, LLVM.LLVMConstInt(_i32, 0, false), Tmp("lidxneg"));
        var isTooLarge = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntSGE, index, size, Tmp("lidxlarge"));
        var outOfBounds = LLVM.LLVMBuildOr(_builder, isNeg, isTooLarge, Tmp("lidxob"));

        var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "index_ok");
        var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "index_fail");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "index_merge");
        LLVM.LLVMBuildCondBr(_builder, outOfBounds, failBB, okBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
        var basePtr = LLVM.LLVMBuildLoad2(_builder, LLVM.LLVMPointerType(_i8ptr, 0), info.array, Tmp("lidxbase"));
        var idx64 = LLVM.LLVMBuildZExt(_builder, index, _i64, Tmp("lidx64"));
        var elemPtr = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8ptr, basePtr, new[] { idx64 }, 1, Tmp("lidxptr"));
        var elem = LLVM.LLVMBuildLoad2(_builder, _i8ptr, elemPtr, Tmp("lidxelem"));
        var okEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 1, false), _errorFlagPtr);
        var emptyStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "", Tmp("empty"));
        var failEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        var phi = LLVM.LLVMBuildPhi(_builder, _i8ptr, Tmp("idxphi"));
        LLVM.LLVMAddIncoming(phi, new[] { elem, emptyStr }, new[] { okEndBB, failEndBB }, 2);
        return (phi, "string");
    }

    private IntPtr NumberToString(IntPtr value, string type)
    {
        var buffer = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn, new[] { LLVM.LLVMConstInt(_i64, 64, false) }, 1, Tmp("numbuf"));
        var fmt = LLVM.LLVMBuildGlobalStringPtr(_builder, type == "float" ? "%g" : "%d", Tmp("numfmt"));
        LLVM.LLVMBuildCall2(_builder, _sprintfType, _sprintfFn, new[] { buffer, fmt, value }, 3, "");
        return buffer;
    }

    private IntPtr StringConcat(IntPtr left, IntPtr right)
    {
        var lenL = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { left }, 1, Tmp("lenl"));
        var lenR = LLVM.LLVMBuildCall2(_builder, _strlenType, _strlenFn, new[] { right }, 1, Tmp("lenr"));
        var total = LLVM.LLVMBuildAdd(_builder, lenL, lenR, Tmp("lensum"));
        var totalPlus1 = LLVM.LLVMBuildAdd(_builder, total, LLVM.LLVMConstInt(_i64, 1, false), Tmp("lentot"));
        var buffer = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn, new[] { totalPlus1 }, 1, Tmp("catbuf"));
        LLVM.LLVMBuildCall2(_builder, _strcpyType, _strcpyFn, new[] { buffer, left }, 2, "");
        LLVM.LLVMBuildCall2(_builder, _strcatType, _strcatFn, new[] { buffer, right }, 2, "");
        return buffer;
    }

    private IntPtr EmitReadFile(string filePath, IntPtr currentFn)
    {
        var pathStr = LLVM.LLVMBuildGlobalStringPtr(_builder, filePath, Tmp("filepath"));
        var modeStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "rb", Tmp("mode"));
        var filePtr = LLVM.LLVMBuildCall2(_builder, _fopenType, _fopenFn, new[] { pathStr, modeStr }, 2, Tmp("file"));

        var nullPtr = LLVM.LLVMConstPointerNull(_i8ptr);
        var isNull = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, filePtr, nullPtr, Tmp("isnull"));

        var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "read_ok");
        var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "read_fail");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "read_merge");
        LLVM.LLVMBuildCondBr(_builder, isNull, failBB, okBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
        var zero64 = LLVM.LLVMConstInt(_i64, 0, false);
        var seekEnd = LLVM.LLVMConstInt(_i32, 2, false);
        LLVM.LLVMBuildCall2(_builder, _fseekType, _fseekFn, new[] { filePtr, zero64, seekEnd }, 3, "");
        var size = LLVM.LLVMBuildCall2(_builder, _ftellType, _ftellFn, new[] { filePtr }, 1, Tmp("size"));
        LLVM.LLVMBuildCall2(_builder, _rewindType, _rewindFn, new[] { filePtr }, 1, "");
        var one64 = LLVM.LLVMConstInt(_i64, 1, false);
        var sizePlus1 = LLVM.LLVMBuildAdd(_builder, size, one64, Tmp("sizep1"));
        var buffer = LLVM.LLVMBuildCall2(_builder, _mallocType, _mallocFn, new[] { sizePlus1 }, 1, Tmp("buffer"));
        var oneElt = LLVM.LLVMConstInt(_i64, 1, false);
        LLVM.LLVMBuildCall2(_builder, _freadType, _freadFn, new[] { buffer, oneElt, size, filePtr }, 4, "");
        var nullPos = LLVM.LLVMBuildInBoundsGEP2(_builder, _i8, buffer, new[] { size }, 1, Tmp("nullpos"));
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i8, 0, false), nullPos);
        LLVM.LLVMBuildCall2(_builder, _fcloseType, _fcloseFn, new[] { filePtr }, 1, "");
        var okEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
        LLVM.LLVMBuildStore(_builder, LLVM.LLVMConstInt(_i32, 1, false), _errorFlagPtr);
        var emptyStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "", Tmp("empty"));
        var failEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        var phi = LLVM.LLVMBuildPhi(_builder, _i8ptr, Tmp("readphi"));
        LLVM.LLVMAddIncoming(phi, new[] { buffer, emptyStr }, new[] { okEndBB, failEndBB }, 2);
        return phi;
    }

    private (IntPtr, string) EmitExist(string filePath, IntPtr currentFn)
    {
        var pathStr = LLVM.LLVMBuildGlobalStringPtr(_builder, filePath, Tmp("epath"));
        var modeStr = LLVM.LLVMBuildGlobalStringPtr(_builder, "r", Tmp("emode"));
        var filePtr = LLVM.LLVMBuildCall2(_builder, _fopenType, _fopenFn, new[] { pathStr, modeStr }, 2, Tmp("efile"));

        var nullPtr = LLVM.LLVMConstPointerNull(_i8ptr);
        var isNull = LLVM.LLVMBuildICmp(_builder, LLVM.LLVMIntPredicate.LLVMIntEQ, filePtr, nullPtr, Tmp("eisnull"));

        var okBB = LLVM.LLVMAppendBasicBlock(currentFn, "exist_ok");
        var failBB = LLVM.LLVMAppendBasicBlock(currentFn, "exist_fail");
        var mergeBB = LLVM.LLVMAppendBasicBlock(currentFn, "exist_merge");
        LLVM.LLVMBuildCondBr(_builder, isNull, failBB, okBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, okBB);
        LLVM.LLVMBuildCall2(_builder, _fcloseType, _fcloseFn, new[] { filePtr }, 1, "");
        var trueVal = LLVM.LLVMConstInt(_i32, 1, false);
        var okEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, failBB);
        var falseVal = LLVM.LLVMConstInt(_i32, 0, false);
        var failEndBB = LLVM.LLVMGetInsertBlock(_builder);
        LLVM.LLVMBuildBr(_builder, mergeBB);

        LLVM.LLVMPositionBuilderAtEnd(_builder, mergeBB);
        var phi = LLVM.LLVMBuildPhi(_builder, _i32, Tmp("existphi"));
        LLVM.LLVMAddIncoming(phi, new[] { trueVal, falseVal }, new[] { okEndBB, failEndBB }, 2);
        return (phi, "bool");
    }

    private void EmitObjectFile(string objOutputPath)
    {
        LLVM.LLVMInitializeX86TargetInfo();
        LLVM.LLVMInitializeX86Target();
        LLVM.LLVMInitializeX86TargetMC();
        LLVM.LLVMInitializeX86AsmPrinter();

        IntPtr triplePtr = LLVM.LLVMGetDefaultTargetTriple();
        string triple = LLVM.PtrToStringAndFree(triplePtr);

        if (LLVM.LLVMGetTargetFromTriple(triple, out var target, out var err) != 0)
            throw new Exception("Failed to get LLVM target: " + LLVM.PtrToStringAndFree(err));

        IntPtr machine = LLVM.LLVMCreateTargetMachine(
            target, triple, "generic", "",
            LLVM.LLVMCodeGenOptLevel.Default,
            LLVM.LLVMRelocMode.PIC,
            LLVM.LLVMCodeModel.Default);

        if (LLVM.LLVMTargetMachineEmitToFile(machine, _module, objOutputPath, LLVM.LLVMCodeGenFileType.ObjectFile, out var emitErr) != 0)
            throw new Exception("Failed to emit object file: " + LLVM.PtrToStringAndFree(emitErr));
    }
}