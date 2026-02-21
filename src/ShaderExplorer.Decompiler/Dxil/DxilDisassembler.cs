using System.Runtime.InteropServices;
using Vortice.Dxc;

namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     Wraps Vortice.Dxc to disassemble DXIL bytecode into text DXIL assembly (LLVM IR).
/// </summary>
public static class DxilDisassembler
{
    /// <summary>
    ///     Disassembles DXIL bytecode (either DXBC-wrapped or raw LLVM bitcode) into text.
    ///     Returns null on failure.
    /// </summary>
    public static string? Disassemble(byte[] bytecode)
    {
        try
        {
            using var compiler = Dxc.CreateDxcCompiler<IDxcCompiler>();
            using var utils = Dxc.CreateDxcUtils();

            var handle = GCHandle.Alloc(bytecode, GCHandleType.Pinned);
            try
            {
                using var blob = utils.CreateBlobFromPinned(
                    handle.AddrOfPinnedObject(),
                    (uint)bytecode.Length,
                    0);

                using var disassembly = compiler.Disassemble(blob);
                var textBytes = disassembly.AsBytes();
                return Encoding.UTF8.GetString(textBytes);
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }
}