using System.IO;
using ShaderExplorer.App.Helpers;
using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler;
using ShaderExplorer.Decompiler.Chunks;
using ShaderExplorer.Decompiler.Dxil;
using ShaderExplorer.Decompiler.Metal;

namespace ShaderExplorer.App.Services;

public class ShaderLoadService
{
    public ShaderLoadResult LoadDxbc(byte[] bytecode, string filePath, RenameMapping mapping)
    {
        var parser = new DxbcParser();
        var container = parser.ParseContainer(bytecode);
        var shader = parser.Parse(bytecode);
        shader.FilePath = filePath;
        shader.RawBytecode = bytecode;

        var generator = new HlslGenerator();
        shader.DecompiledHlsl = generator.Generate(shader, container, mapping);

        // Check for embedded SPDB debug info with original HLSL source
        TryExtractSpdbSource(shader, container);

        // Show original HLSL if available and no renames applied
        var hlsl = (shader.OriginalHlsl != null && mapping.VariableRenames.Count == 0)
            ? BuildOriginalSourceHeader(shader) + shader.OriginalHlsl
            : shader.DecompiledHlsl;

        return new ShaderLoadResult(shader, container, null, hlsl);
    }

    public ShaderLoadResult LoadDxil(byte[] bytecode, string filePath, RenameMapping mapping)
    {
        DxbcContainer? container = null;
        ShaderInfo shader;

        // If DXBC-wrapped DXIL, parse container for metadata
        var magic = bytecode.Length >= 4 ? BitConverter.ToUInt32(bytecode, 0) : 0;
        if (magic == 0x43425844) // "DXBC"
        {
            var parser = new DxbcParser();
            container = parser.ParseContainer(bytecode);
            shader = parser.Parse(bytecode);
        }
        else
        {
            // Raw LLVM bitcode — minimal ShaderInfo
            shader = new ShaderInfo { Type = ShaderType.Unknown, IsDxil = true };
        }

        shader.FilePath = filePath;
        shader.RawBytecode = bytecode;
        shader.IsDxil = true;

        // Check for embedded SPDB debug info with original HLSL source
        if (container != null)
            TryExtractSpdbSource(shader, container);

        // Step 1: Disassemble
        var assemblyText = DxilDisassembler.Disassemble(bytecode);
        if (assemblyText == null)
        {
            shader.DecompiledHlsl =
                "// DXIL disassembly failed.\n// DXC compiler may not be available or the bytecode is corrupted.";
            return new ShaderLoadResult(shader, container, null, shader.DecompiledHlsl,
                ErrorMessage: "DXIL disassembly failed");
        }

        // Step 2: Parse assembly text
        DxilModule? module = null;
        try
        {
            var asmParser = new DxilAssemblyParser();
            module = asmParser.Parse(assemblyText);
        }
        catch
        {
            // Parse failure — show assembly text as fallback
            shader.DecompiledHlsl = $"// DXIL assembly parse failed — showing raw disassembly\n\n{assemblyText}";
            return new ShaderLoadResult(shader, container, null, shader.DecompiledHlsl,
                ErrorMessage: "DXIL assembly parse failed");
        }

        // Step 3: Generate HLSL
        try
        {
            var generator = new DxilHlslGenerator();
            shader.DecompiledHlsl = generator.Generate(module, shader, container, mapping);
        }
        catch
        {
            // Generation failure — show assembly text as fallback
            shader.DecompiledHlsl = $"// DXIL HLSL generation failed — showing raw disassembly\n\n{assemblyText}";
        }

        // Show original HLSL if available and no renames applied
        var hlsl = (shader.OriginalHlsl != null && mapping.VariableRenames.Count == 0)
            ? BuildOriginalSourceHeader(shader) + shader.OriginalHlsl
            : shader.DecompiledHlsl;

        return new ShaderLoadResult(shader, container, module, hlsl);
    }

    public ShaderLoadResult LoadMetalSource(byte[] data, string filePath)
    {
        var source = Encoding.UTF8.GetString(data);
        var shader = MetalSourceParser.Parse(source);
        shader.FilePath = filePath;
        shader.RawBytecode = data;

        return new ShaderLoadResult(shader, null, null, source, "cpp");
    }

    public ShaderLoadResult LoadMetalLib(byte[] data, string filePath)
    {
        var metalInfo = MetalLibParser.Parse(data);
        var shader = new ShaderInfo
        {
            Type = ShaderType.Unknown,
            FilePath = filePath,
            RawBytecode = data,
            IsMetal = true
        };

        // Step 1: Extract bitcode
        var bitcode = MetalLibParser.ExtractBitcode(data);
        if (bitcode == null)
            return FallbackMetalDisplay(shader, metalInfo, "No LLVM bitcode found in MTLB");

        // Step 2: Disassemble bitcode → LLVM IR text
        var irText = MetalBitcodeDisassembler.Disassemble(bitcode, out var llvmError);
        if (irText == null)
        {
            var magic = bitcode.Length >= 4
                ? $"0x{bitcode[0]:X2} 0x{bitcode[1]:X2} 0x{bitcode[2]:X2} 0x{bitcode[3]:X2}"
                : "N/A";
            var detail = $"extracted {bitcode.Length} bytes, magic: {magic}";
            if (!string.IsNullOrEmpty(llvmError))
                detail += $", LLVM: {llvmError}";
            return FallbackMetalDisplay(shader, metalInfo,
                $"LLVM bitcode disassembly failed ({detail})");
        }

        // Step 3: Parse IR text → DxilModule
        DxilModule? module = null;
        try
        {
            var parser = new DxilAssemblyParser();
            module = parser.Parse(irText);
        }
        catch
        {
            // Parse failed — show raw IR as fallback
            return new ShaderLoadResult(shader, null, null,
                $"// Metal AIR parse failed — showing raw LLVM IR\n\n{irText}", "cpp");
        }

        // Step 4: Generate MSL
        try
        {
            var generator = new MetalCodeGenerator();
            shader.DecompiledHlsl = generator.Generate(module, shader);
        }
        catch
        {
            shader.DecompiledHlsl = $"// MSL generation failed — showing raw LLVM IR\n\n{irText}";
        }

        return new ShaderLoadResult(shader, null, module, shader.DecompiledHlsl, "cpp");
    }

    private static ShaderLoadResult FallbackMetalDisplay(ShaderInfo shader, MetalLibInfo? metalInfo, string errorMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Metal Library (MTLB)");
        if (metalInfo != null)
        {
            if (metalInfo.FunctionName != null)
                sb.AppendLine($"// Function: {metalInfo.FunctionName}");
            if (metalInfo.VersionString != null)
                sb.AppendLine($"// Version:  {metalInfo.VersionString}");
            if (metalInfo.Hash != null)
                sb.AppendLine($"// Hash:     {metalInfo.Hash}");
            sb.AppendLine($"// File size: {FormatHelper.FormatSize(metalInfo.FileSize)}");
            if (metalInfo.HeaderBitcodeOffset > 0)
                sb.AppendLine($"// Bitcode offset: 0x{metalInfo.HeaderBitcodeOffset:X} ({FormatHelper.FormatSize((int)metalInfo.HeaderBitcodeSize)})");
            else if (metalInfo.BitcodeSize > 0)
                sb.AppendLine($"// Bitcode:  {FormatHelper.FormatSize(metalInfo.BitcodeSize)}");
            sb.AppendLine($"// Header version: {metalInfo.FileVersion}.{metalInfo.FileVersionMinor}, platform: {metalInfo.TargetPlatform}");
        }
        else
        {
            sb.AppendLine($"// File size: {FormatHelper.FormatSize(shader.RawBytecode.Length)}");
        }

        sb.AppendLine("//");
        sb.AppendLine($"// {errorMessage}");

        return new ShaderLoadResult(shader, null, null, sb.ToString(), ErrorMessage: errorMessage);
    }

    public string RegenerateHlsl(ShaderInfo shader, DxbcContainer? container,
        DxilModule? module, RenameMapping mapping)
    {
        if (shader.IsMetal && module != null)
        {
            var generator = new MetalCodeGenerator();
            shader.DecompiledHlsl = generator.Generate(module, shader);
        }
        else if (shader.IsDxil && module != null)
        {
            var generator = new DxilHlslGenerator();
            shader.DecompiledHlsl = generator.Generate(module, shader, container, mapping);
        }
        else if (container != null)
        {
            var generator = new HlslGenerator();
            shader.DecompiledHlsl = generator.Generate(shader, container, mapping);
        }
        else
        {
            return shader.DecompiledHlsl;
        }

        // If no renames and original HLSL is available, prefer it
        if (mapping.VariableRenames.Count == 0 && shader.OriginalHlsl != null)
            return BuildOriginalSourceHeader(shader) + shader.OriginalHlsl;

        return shader.DecompiledHlsl;
    }

    private static void TryExtractSpdbSource(ShaderInfo shader, DxbcContainer container)
    {
        if (container.SpdbData == null) return;

        var spdbInfo = SpdbParser.ExtractSource(container.SpdbData);
        if (spdbInfo != null)
        {
            shader.OriginalHlsl = spdbInfo.HlslSource;
            shader.OriginalFilePath = spdbInfo.OriginalFilePath;
        }
    }

    private static string BuildOriginalSourceHeader(ShaderInfo shader)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(shader.OriginalFilePath))
            sb.AppendLine($"// Original source: {Path.GetFileName(shader.OriginalFilePath)}");
        sb.AppendLine($"// {shader.Type} SM{shader.MajorVersion}.{shader.MinorVersion}");
        sb.AppendLine("// Source extracted from embedded SPDB debug info");
        sb.AppendLine();
        return sb.ToString();
    }
}
