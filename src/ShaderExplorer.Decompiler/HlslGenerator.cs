using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;
using ComponentType = ShaderExplorer.Core.Models.ComponentType;

namespace ShaderExplorer.Decompiler;

public partial class HlslGenerator
{
    private readonly Dictionary<int, ConstantBufferInfo> _cbuffers = new();
    private readonly Dictionary<int, ResourceBindingInfo> _samplers = new();
    private readonly StringBuilder _sb = new();
    private readonly Dictionary<int, ResourceBindingInfo> _textures = new();
    private readonly Dictionary<int, ResourceBindingInfo> _uavs = new();
    private DxbcContainer? _container;
    private OpcodeType _currentOpcode;
    private bool _inCaseBody;
    private int _indentLevel;
    private ShaderInfo? _shaderInfo;

    public Dictionary<(OperandType, int slot, int offset), string> Renames { get; } = new();

    public string Generate(ShaderInfo info, DxbcContainer? container = null, RenameMapping? renames = null)
    {
        _shaderInfo = info;
        _container = container;
        _indentLevel = 0;
        _sb.Clear();

        if (renames != null)
            foreach (var kv in renames.VariableRenames)
            {
                // Parse key format: "Type:Slot:Offset"
                var parts = kv.Key.Split(':');
                if (parts.Length == 3 &&
                    Enum.TryParse<OperandType>(parts[0], out var opType) &&
                    int.TryParse(parts[1], out var slot) &&
                    int.TryParse(parts[2], out var offset))
                    Renames[(opType, slot, offset)] = kv.Value;
            }

        // Build lookup tables
        foreach (var cb in info.ConstantBuffers)
            _cbuffers[cb.RegisterSlot] = cb;

        foreach (var rb in info.ResourceBindings)
            switch (rb.Type)
            {
                case ResourceType.Texture:
                    _textures[rb.BindPoint] = rb;
                    break;
                case ResourceType.Sampler:
                    _samplers[rb.BindPoint] = rb;
                    break;
                case ResourceType.UAVRWTyped:
                case ResourceType.UAVRWStructured:
                case ResourceType.UAVRWByteAddress:
                    _uavs[rb.BindPoint] = rb;
                    break;
            }

        EmitHeader();
        EmitStructDeclarations();
        EmitCBufferDeclarations();
        EmitResourceDeclarations();
        EmitFunctionSignature();

        if (_container?.ShaderProgram != null)
            EmitInstructionBody(_container.ShaderProgram);
        else
            EmitPlaceholderBody();

        return _sb.ToString();
    }

    private void Emit(string text)
    {
        _sb.Append(text);
    }

    private void EmitLine(string line = "")
    {
        if (line.Length > 0)
        {
            for (var i = 0; i < _indentLevel; i++) _sb.Append("    ");
            _sb.AppendLine(line);
        }
        else
        {
            _sb.AppendLine();
        }
    }

    private void EmitInstructionBody(ShaderProgramChunk program)
    {
        // Determine input/output struct names
        var inputStruct = _shaderInfo!.Type switch
        {
            ShaderType.Vertex => "VS_INPUT",
            ShaderType.Pixel => "PS_INPUT",
            _ => "INPUT"
        };
        var outputStruct = _shaderInfo.Type switch
        {
            ShaderType.Vertex => "VS_OUTPUT",
            ShaderType.Pixel => "PS_OUTPUT",
            _ => "OUTPUT"
        };

        EmitLine($"{outputStruct} main({inputStruct} input)");
        EmitLine("{");
        _indentLevel++;

        // Declare temp registers
        uint tempCount = 0;
        foreach (var instr in program.Instructions)
            if (instr.Opcode == OpcodeType.DclTemps)
                tempCount = instr.TempRegCount;
        for (uint i = 0; i < tempCount; i++)
            EmitLine($"float4 r{i};");

        if (tempCount > 0) EmitLine();

        EmitLine($"{outputStruct} output;");
        EmitLine();

        // Emit instructions
        foreach (var instr in program.Instructions)
        {
            if (IsDeclaration(instr.Opcode))
                continue;

            EmitInstruction(instr);
        }

        EmitLine();
        EmitLine("return output;");

        _indentLevel--;
        EmitLine("}");
    }

    private void EmitPlaceholderBody()
    {
        var outputStruct = _shaderInfo!.Type switch
        {
            ShaderType.Vertex => "VS_OUTPUT",
            ShaderType.Pixel => "PS_OUTPUT",
            _ => "OUTPUT"
        };
        var inputStruct = _shaderInfo.Type switch
        {
            ShaderType.Vertex => "VS_INPUT",
            ShaderType.Pixel => "PS_INPUT",
            _ => "INPUT"
        };

        EmitLine($"{outputStruct} main({inputStruct} input)");
        EmitLine("{");
        _indentLevel++;
        EmitLine($"{outputStruct} output;");
        EmitLine("// Instruction body not available (no SHDR/SHEX chunk or no container)");
        EmitLine("return output;");
        _indentLevel--;
        EmitLine("}");
    }

    private static bool IsDeclaration(OpcodeType op)
    {
        return op switch
        {
            OpcodeType.DclGlobalFlags or
                OpcodeType.DclTemps or
                OpcodeType.DclIndexableTemp or
                OpcodeType.DclInput or
                OpcodeType.DclInputSgv or
                OpcodeType.DclInputSiv or
                OpcodeType.DclInputPs or
                OpcodeType.DclInputPsSgv or
                OpcodeType.DclInputPsSiv or
                OpcodeType.DclOutput or
                OpcodeType.DclOutputSgv or
                OpcodeType.DclOutputSiv or
                OpcodeType.DclConstantBuffer or
                OpcodeType.DclSampler or
                OpcodeType.DclResource or
                OpcodeType.DclMaxOutputVertexCount or
                OpcodeType.DclGsOutputPrimitiveTopology or
                OpcodeType.DclGsInputPrimitive or
                OpcodeType.DclStream or
                OpcodeType.DclThreadGroup or
                OpcodeType.DclIndexRange or
                OpcodeType.DclInputControlPointCount or
                OpcodeType.DclOutputControlPointCount or
                OpcodeType.DclTessDomain or
                OpcodeType.DclTessPartitioning or
                OpcodeType.DclTessOutputPrimitive or
                OpcodeType.DclHsMaxTessFactor or
                OpcodeType.DclHsForkPhaseInstanceCount or
                OpcodeType.DclHsJoinPhaseInstanceCount or
                OpcodeType.DclUnorderedAccessViewTyped or
                OpcodeType.DclUnorderedAccessViewRaw or
                OpcodeType.DclUnorderedAccessViewStructured or
                OpcodeType.DclResourceRaw or
                OpcodeType.DclResourceStructured or
                OpcodeType.DclFunctionBody or
                OpcodeType.DclFunctionTable or
                OpcodeType.DclInterface or
                OpcodeType.DclThreadGroupSharedMemoryRaw or
                OpcodeType.DclThreadGroupSharedMemoryStructured or
                OpcodeType.DclGsInstanceCount or
                OpcodeType.HsDecls or
                OpcodeType.HsControlPointPhase or
                OpcodeType.HsForkPhase or
                OpcodeType.HsJoinPhase or
                OpcodeType.CustomData => true,
            _ => false
        };
    }
}
