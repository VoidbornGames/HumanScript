using System.Runtime.InteropServices;

namespace HSharp;

internal static class LLVM
{
    private const string Lib = "HSharpLLVM";

    static LLVM()
    {
        NativeLibrary.SetDllImportResolver(typeof(LLVM).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            string realName = OperatingSystem.IsWindows() ? "LLVM-C" : "LLVM-18";
            return NativeLibrary.Load(realName, assembly, path);
        });
    }

    public enum LLVMCodeGenOptLevel { None = 0, Less = 1, Default = 2, Aggressive = 3 }
    public enum LLVMRelocMode { Default = 0, Static = 1, PIC = 2, DynamicNoPic = 3 }
    public enum LLVMCodeModel { Default = 0 }
    public enum LLVMCodeGenFileType { AssemblyFile = 0, ObjectFile = 1 }

    public enum LLVMIntPredicate
    {
        LLVMIntEQ = 32,
        LLVMIntNE = 33,
        LLVMIntUGT = 34,
        LLVMIntUGE = 35,
        LLVMIntULT = 36,
        LLVMIntULE = 37,
        LLVMIntSGT = 38,
        LLVMIntSGE = 39,
        LLVMIntSLT = 40,
        LLVMIntSLE = 41
    }

    public enum LLVMRealPredicate
    {
        LLVMRealOEQ = 1,
        LLVMRealOGT = 2,
        LLVMRealOGE = 3,
        LLVMRealOLT = 4,
        LLVMRealOLE = 5,
        LLVMRealONE = 6
    }

    [DllImport(Lib)] public static extern void LLVMInitializeX86TargetInfo();
    [DllImport(Lib)] public static extern void LLVMInitializeX86Target();
    [DllImport(Lib)] public static extern void LLVMInitializeX86TargetMC();
    [DllImport(Lib)] public static extern void LLVMInitializeX86AsmPrinter();

    [DllImport(Lib)] public static extern void LLVMInitializeAArch64TargetInfo();
    [DllImport(Lib)] public static extern void LLVMInitializeAArch64Target();
    [DllImport(Lib)] public static extern void LLVMInitializeAArch64TargetMC();
    [DllImport(Lib)] public static extern void LLVMInitializeAArch64AsmPrinter();


    [DllImport(Lib)] public static extern IntPtr LLVMContextCreate();
    [DllImport(Lib)] public static extern IntPtr LLVMModuleCreateWithNameInContext(string name, IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMCreateBuilderInContext(IntPtr ctx);


    [DllImport(Lib)] public static extern IntPtr LLVMInt32TypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMInt64TypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMInt8TypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMVoidTypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMDoubleTypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMInt1TypeInContext(IntPtr ctx);
    [DllImport(Lib)] public static extern IntPtr LLVMPointerType(IntPtr elementType, uint addressSpace);
    [DllImport(Lib)] public static extern IntPtr LLVMFunctionType(IntPtr returnType, IntPtr[] paramTypes, uint paramCount, [MarshalAs(UnmanagedType.Bool)] bool isVarArg);


    [DllImport(Lib)] public static extern IntPtr LLVMAddFunction(IntPtr module, string name, IntPtr functionType);
    [DllImport(Lib)] public static extern IntPtr LLVMAppendBasicBlockInContext(IntPtr ctx, IntPtr fn, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMAppendBasicBlock(IntPtr fn, string name);
    [DllImport(Lib)] public static extern void LLVMPositionBuilderAtEnd(IntPtr builder, IntPtr block);


    [DllImport(Lib)] public static extern IntPtr LLVMConstInt(IntPtr intType, ulong value, [MarshalAs(UnmanagedType.Bool)] bool signExtend);
    [DllImport(Lib)] public static extern IntPtr LLVMConstPointerNull(IntPtr type);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildGlobalStringPtr(IntPtr builder, string str, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMConstReal(IntPtr realType, double value);
    [DllImport(Lib)] public static extern IntPtr LLVMGetInsertBlock(IntPtr builder);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildAlloca(IntPtr builder, IntPtr type, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildStore(IntPtr builder, IntPtr value, IntPtr ptr);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildLoad2(IntPtr builder, IntPtr type, IntPtr ptr, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildInBoundsGEP2(IntPtr builder, IntPtr type, IntPtr ptr, IntPtr[] indices, uint numIndices, string name);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildAdd(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSub(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildMul(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSDiv(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFAdd(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFSub(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFMul(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFDiv(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSIToFP(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildAnd(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildOr(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildXor(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildZExt(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSExt(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildNot(IntPtr builder, IntPtr value, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSelect(IntPtr builder, IntPtr cond, IntPtr thenVal, IntPtr elseVal, string name);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildICmp(IntPtr builder, LLVMIntPredicate op, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFCmp(IntPtr builder, LLVMRealPredicate op, IntPtr lhs, IntPtr rhs, string name);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildCondBr(IntPtr builder, IntPtr cond, IntPtr thenBB, IntPtr elseBB);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildBr(IntPtr builder, IntPtr dest);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildRet(IntPtr builder, IntPtr value);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildRetVoid(IntPtr builder);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildPhi(IntPtr builder, IntPtr type, string name);
    [DllImport(Lib)] public static extern void LLVMAddIncoming(IntPtr phiNode, IntPtr[] incomingValues, IntPtr[] incomingBlocks, uint count);


    [DllImport(Lib)] public static extern IntPtr LLVMBuildCall2(IntPtr builder, IntPtr fnType, IntPtr fn, IntPtr[] args, uint numArgs, string name);


    [DllImport(Lib)] public static extern IntPtr LLVMPrintModuleToString(IntPtr module);
    [DllImport(Lib)] public static extern void LLVMDisposeMessage(IntPtr message);
    [DllImport(Lib)] public static extern int LLVMVerifyModule(IntPtr module, int action, out IntPtr outMessage);


    [DllImport(Lib)] public static extern IntPtr LLVMGetDefaultTargetTriple();
    [DllImport(Lib)] public static extern int LLVMGetTargetFromTriple(string triple, out IntPtr target, out IntPtr errorMessage);
    [DllImport(Lib)]
    public static extern IntPtr LLVMCreateTargetMachine(IntPtr target, string triple, string cpu, string features,
        LLVMCodeGenOptLevel level, LLVMRelocMode reloc, LLVMCodeModel model);
    [DllImport(Lib)]
    public static extern int LLVMTargetMachineEmitToFile(IntPtr machine, IntPtr module, string filename,
        LLVMCodeGenFileType codegen, out IntPtr errorMessage);

    [DllImport(Lib)] public static extern IntPtr LLVMGetParam(IntPtr fn, uint index);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildTrunc(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildSRem(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildFRem(IntPtr builder, IntPtr lhs, IntPtr rhs, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildPtrToInt(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildIntToPtr(IntPtr builder, IntPtr value, IntPtr destType, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMStructCreateNamed(IntPtr ctx, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMStructSetBody(IntPtr structTy, IntPtr[] elemTypes, uint count, [MarshalAs(UnmanagedType.Bool)] bool packed);
    [DllImport(Lib)] public static extern IntPtr LLVMBuildStructGEP2(IntPtr builder, IntPtr structTy, IntPtr ptr, uint idx, string name);
    [DllImport(Lib)] public static extern IntPtr LLVMAddGlobal(IntPtr module, IntPtr type, string name);
    [DllImport(Lib)] public static extern void LLVMSetInitializer(IntPtr globalVar, IntPtr value);
    [DllImport(Lib)] public static extern IntPtr LLVMGetBasicBlockTerminator(IntPtr basicBlock);

    // new pass manager, used to run default<O2> before emitting
    [DllImport(Lib)] public static extern IntPtr LLVMCreatePassBuilderOptions();
    [DllImport(Lib)] public static extern void LLVMDisposePassBuilderOptions(IntPtr options);
    [DllImport(Lib)] public static extern void LLVMPassBuilderOptionsSetVerifyEach(IntPtr options, [MarshalAs(UnmanagedType.Bool)] bool verifyEach);
    [DllImport(Lib)] public static extern IntPtr LLVMRunPasses(IntPtr module, string passes, IntPtr targetMachine, IntPtr options);
    [DllImport(Lib)] public static extern IntPtr LLVMGetErrorMessage(IntPtr error);
    [DllImport(Lib)] public static extern void LLVMDisposeErrorMessage(IntPtr message);

    public static string PtrToStringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        string s = Marshal.PtrToStringAnsi(ptr) ?? "";
        LLVMDisposeMessage(ptr);
        return s;
    }
}