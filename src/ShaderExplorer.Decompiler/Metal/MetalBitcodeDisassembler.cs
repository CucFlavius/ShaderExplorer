using LLVMSharp.Interop;

namespace ShaderExplorer.Decompiler.Metal;

/// <summary>
///     Uses LLVMSharp to disassemble LLVM bitcode (from Metal AIR) into text LLVM IR.
/// </summary>
public static class MetalBitcodeDisassembler
{
    /// <summary>
    ///     Disassembles LLVM bitcode into LLVM IR text.
    ///     Returns null on failure. Sets errorMessage with diagnostics.
    /// </summary>
    public static unsafe string? Disassemble(byte[] bitcode, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var context = LLVMContextRef.Create();
            try
            {
                LLVMMemoryBufferRef memBuf;
                fixed (byte* ptr = bitcode)
                {
                    memBuf = LLVM.CreateMemoryBufferWithMemoryRangeCopy(
                        (sbyte*)ptr,
                        (nuint)bitcode.Length,
                        (sbyte*)null);
                }

                // Try ParseBitcode first (eager, validates module)
                if (context.TryParseBitcode(memBuf, out var module, out var parseError))
                {
                    try
                    {
                        return module.PrintToString();
                    }
                    finally
                    {
                        module.Dispose();
                    }
                }

                // ParseBitcode failed — try GetBitcodeModule (lazy, may accept more formats)
                fixed (byte* ptr = bitcode)
                {
                    memBuf = LLVM.CreateMemoryBufferWithMemoryRangeCopy(
                        (sbyte*)ptr,
                        (nuint)bitcode.Length,
                        (sbyte*)null);
                }

                if (context.TryGetBitcodeModule(memBuf, out module, out var lazyError))
                {
                    try
                    {
                        return module.PrintToString();
                    }
                    finally
                    {
                        module.Dispose();
                    }
                }

                errorMessage = parseError ?? lazyError ?? "Unknown LLVM parse error";
            }
            finally
            {
                context.Dispose();
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        return null;
    }

    /// <summary>
    ///     Convenience overload without error message output.
    /// </summary>
    public static string? Disassemble(byte[] bitcode)
    {
        return Disassemble(bitcode, out _);
    }
}
