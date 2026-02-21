using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;
using ComponentType = ShaderExplorer.Core.Models.ComponentType;

namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     Generates HLSL source from a parsed DXIL module, using RDEF/signature metadata from the DXBC container.
/// </summary>
public class DxilHlslGenerator
{
    // Resource lookup tables (from RDEF)
    private readonly Dictionary<int, ConstantBufferInfo> _cbuffers = new();

    // Resource handle tracking: %handle → (type, slot)
    private readonly Dictionary<string, (string type, int slot)> _handleMap = new();

    // Rename mapping
    private readonly Dictionary<(OperandType, int slot, int offset), string> _renames = new();
    private readonly Dictionary<int, ResourceBindingInfo> _samplers = new();
    private readonly StringBuilder _sb = new();
    private readonly Dictionary<string, string> _ssaExpressions = new();

    // SSA value tracking
    private readonly Dictionary<string, string> _ssaNames = new();
    private readonly Dictionary<string, int> _ssaUseCount = new();
    private readonly Dictionary<int, ResourceBindingInfo> _textures = new();
    private readonly Dictionary<int, ResourceBindingInfo> _uavs = new();
    private DxbcContainer? _container;
    private int _indentLevel;
    private DxilModule? _module;
    private ShaderInfo? _shaderInfo;
    private int _tempCounter;

    public string Generate(DxilModule module, ShaderInfo info, DxbcContainer? container = null,
        RenameMapping? renames = null)
    {
        _sb.Clear();
        _indentLevel = 0;
        _shaderInfo = info;
        _container = container;
        _module = module;
        _tempCounter = 0;
        _ssaNames.Clear();
        _ssaUseCount.Clear();
        _ssaExpressions.Clear();
        _handleMap.Clear();
        _cbuffers.Clear();
        _textures.Clear();
        _samplers.Clear();
        _uavs.Clear();
        _renames.Clear();

        if (renames != null)
            foreach (var kv in renames.VariableRenames)
            {
                var parts = kv.Key.Split(':');
                if (parts.Length == 3 &&
                    Enum.TryParse<OperandType>(parts[0], out var opType) &&
                    int.TryParse(parts[1], out var slot) &&
                    int.TryParse(parts[2], out var offset))
                    _renames[(opType, slot, offset)] = kv.Value;
            }

        PopulateFromDxilMetadata();
        BuildResourceTables();

        EmitHeader();
        EmitStructDeclarations();
        EmitCBufferDeclarations();
        EmitResourceDeclarations();
        EmitIOStructs();

        var entryPoint = module.EntryPoint;
        if (entryPoint != null)
        {
            CountSsaUses(entryPoint);
            EmitFunctionBody(entryPoint);
        }
        else
        {
            EmitLine("// No entry point found in DXIL module");
        }

        return _sb.ToString();
    }

    private void BuildResourceTables()
    {
        if (_shaderInfo == null) return;

        foreach (var cb in _shaderInfo.ConstantBuffers)
            _cbuffers[cb.RegisterSlot] = cb;

        foreach (var rb in _shaderInfo.ResourceBindings)
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
    }

    /// <summary>
    ///     When RDEF chunk is absent (SM6.0 from DXC), populate ShaderInfo from DXIL metadata comments.
    /// </summary>
    private void PopulateFromDxilMetadata()
    {
        if (_module == null || _shaderInfo == null) return;

        // Only populate if RDEF didn't already provide resource info
        var hasRdef = _shaderInfo.ConstantBuffers.Count > 0 || _shaderInfo.ResourceBindings.Count > 0;
        if (hasRdef || _module.ResourceBindings.Count == 0) return;

        // Track cbuffer ordinal for matching unnamed buffer definitions
        var cbufferOrdinal = 0;

        foreach (var binding in _module.ResourceBindings)
        {
            // Use HLSL bind as fallback name when name is blank
            var displayName = !string.IsNullOrEmpty(binding.Name)
                ? binding.Name
                : binding.HlslBind;

            switch (binding.Type.ToLowerInvariant())
            {
                case "cbuffer":
                {
                    var cb = new ConstantBufferInfo
                    {
                        Name = displayName,
                        RegisterSlot = binding.BindPoint,
                        Size = 0,
                        Variables = []
                    };

                    // Match cbuffer variables: try by name first, then by ordinal for unnamed buffers
                    var matchKey = !string.IsNullOrEmpty(binding.Name)
                        ? binding.Name
                        : $"__cb_ordinal_{cbufferOrdinal}";

                    foreach (var v in _module.CBufferVariables.Where(cv => cv.CBufferName == matchKey))
                    {
                        // Handle raw byte-array: "[N x i8] (type annotation not present)"
                        var sizeMatch = Regex.Match(v.Type, @"\[(\d+)\s+x\s+i8\]");
                        if (sizeMatch.Success)
                        {
                            var byteSize = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                            var vec4Count = (byteSize + 15) / 16;
                            cb.Size = byteSize;
                            cb.Variables.Add(new ConstantVariableInfo
                            {
                                Name = $"{displayName}_data",
                                Offset = 0,
                                Size = byteSize,
                                VariableType = new ShaderVariableType
                                {
                                    Class = ShaderVariableClass.Vector,
                                    Type = ShaderBaseType.Float,
                                    Rows = 1,
                                    Columns = 4,
                                    Elements = vec4Count
                                }
                            });
                        }
                        else
                        {
                            cb.Variables.Add(new ConstantVariableInfo
                            {
                                Name = v.Name,
                                Offset = v.Offset,
                                Size = GuessTypeSize(v.Type),
                                VariableType = GuessVariableType(v.Type)
                            });
                        }
                    }

                    _shaderInfo.ConstantBuffers.Add(cb);
                    cbufferOrdinal++;
                    break;
                }

                case "texture":
                {
                    var dim = binding.Dim.ToLowerInvariant() switch
                    {
                        "1d" => ResourceDimension.Texture1D,
                        "2d" => ResourceDimension.Texture2D,
                        "2darray" => ResourceDimension.Texture2DArray,
                        "3d" => ResourceDimension.Texture3D,
                        "cube" => ResourceDimension.TextureCube,
                        "cubearray" => ResourceDimension.TextureCubeArray,
                        "buf" => ResourceDimension.Buffer,
                        _ => ResourceDimension.Texture2D
                    };

                    _shaderInfo.ResourceBindings.Add(new ResourceBindingInfo
                    {
                        Name = displayName,
                        Type = ResourceType.Texture,
                        BindPoint = binding.BindPoint,
                        Dimension = dim,
                        BindCount = 1
                    });
                    break;
                }

                case "sampler":
                    _shaderInfo.ResourceBindings.Add(new ResourceBindingInfo
                    {
                        Name = displayName,
                        Type = ResourceType.Sampler,
                        BindPoint = binding.BindPoint,
                        BindCount = 1
                    });
                    break;

                case "uav":
                    _shaderInfo.ResourceBindings.Add(new ResourceBindingInfo
                    {
                        Name = displayName,
                        Type = ResourceType.UAVRWTyped,
                        BindPoint = binding.BindPoint,
                        BindCount = 1
                    });
                    break;
            }
        }
    }

    private static int GuessTypeSize(string type)
    {
        return type switch
        {
            "float" or "int" or "uint" or "bool" => 4,
            "float2" or "int2" or "uint2" => 8,
            "float3" or "int3" or "uint3" => 12,
            "float4" or "int4" or "uint4" => 16,
            "float3x3" => 36,
            "float4x4" => 64,
            "double" => 8,
            _ => 16
        };
    }

    private static ShaderVariableType GuessVariableType(string type)
    {
        var baseType = ShaderBaseType.Float;
        int rows = 1, cols = 1;
        var varClass = ShaderVariableClass.Scalar;

        var remaining = type;
        if (remaining.StartsWith("uint"))
        {
            baseType = ShaderBaseType.UInt;
            remaining = remaining[4..];
        }
        else if (remaining.StartsWith("int"))
        {
            baseType = ShaderBaseType.Int;
            remaining = remaining[3..];
        }
        else if (remaining.StartsWith("bool"))
        {
            baseType = ShaderBaseType.Bool;
            remaining = remaining[4..];
        }
        else if (remaining.StartsWith("double"))
        {
            baseType = ShaderBaseType.Double;
            remaining = remaining[6..];
        }
        else if (remaining.StartsWith("float"))
        {
            baseType = ShaderBaseType.Float;
            remaining = remaining[5..];
        }

        if (remaining.Contains('x'))
        {
            var dims = remaining.Split('x');
            if (dims.Length == 2 && int.TryParse(dims[0], out rows) && int.TryParse(dims[1], out cols))
                varClass = ShaderVariableClass.MatrixColumns;
        }
        else if (int.TryParse(remaining, out cols))
        {
            varClass = cols > 1 ? ShaderVariableClass.Vector : ShaderVariableClass.Scalar;
        }

        return new ShaderVariableType
        {
            Type = baseType,
            Class = varClass,
            Rows = rows,
            Columns = cols
        };
    }

    // ═══ Header / Declarations (reuse patterns from HlslGenerator) ═══

    private void EmitHeader()
    {
        EmitLine($"// Decompiled {_shaderInfo!.Type} Shader (DXIL)");
        EmitLine($"// Shader Model {_shaderInfo.MajorVersion}.{_shaderInfo.MinorVersion}");
        if (!string.IsNullOrEmpty(_shaderInfo.FilePath))
            EmitLine($"// Source: {Path.GetFileName(_shaderInfo.FilePath)}");
        EmitLine();
    }

    private void EmitStructDeclarations()
    {
        foreach (var cb in _shaderInfo!.ConstantBuffers)
        foreach (var v in cb.Variables)
            if (v.VariableType.Class == ShaderVariableClass.Struct && v.VariableType.Members.Count > 0)
            {
                EmitLine($"struct {v.Name}_t");
                EmitLine("{");
                _indentLevel++;
                foreach (var m in v.VariableType.Members)
                    EmitLine($"{HlslTypeHelpers.HlslTypeName(m.Type)} {m.Name};");
                _indentLevel--;
                EmitLine("};");
                EmitLine();
            }
    }

    private void EmitCBufferDeclarations()
    {
        foreach (var cb in _shaderInfo!.ConstantBuffers)
        {
            EmitLine($"cbuffer {cb.Name} : register(b{cb.RegisterSlot})");
            EmitLine("{");
            _indentLevel++;
            foreach (var v in cb.Variables)
            {
                var typeName = HlslTypeHelpers.HlslTypeName(v.VariableType);
                var arrayPart = v.VariableType.Elements > 0 ? $"[{v.VariableType.Elements}]" : "";
                EmitLine($"{typeName} {v.Name}{arrayPart}; // offset: {v.Offset}, size: {v.Size}");
            }

            _indentLevel--;
            EmitLine("}");
            EmitLine();
        }
    }

    private void EmitResourceDeclarations()
    {
        foreach (var rb in _shaderInfo!.ResourceBindings)
            switch (rb.Type)
            {
                case ResourceType.Texture:
                    EmitLine($"{HlslTypeHelpers.TextureDimensionType(rb.Dimension)} {rb.Name} : register(t{rb.BindPoint});");
                    break;
                case ResourceType.Sampler:
                    EmitLine($"SamplerState {rb.Name} : register(s{rb.BindPoint});");
                    break;
                case ResourceType.UAVRWTyped:
                    EmitLine($"RWTexture2D<float4> {rb.Name} : register(u{rb.BindPoint});");
                    break;
                case ResourceType.Structured:
                    EmitLine($"StructuredBuffer<float4> {rb.Name} : register(t{rb.BindPoint});");
                    break;
                case ResourceType.UAVRWStructured:
                    EmitLine($"RWStructuredBuffer<float4> {rb.Name} : register(u{rb.BindPoint});");
                    break;
                case ResourceType.ByteAddress:
                    EmitLine($"ByteAddressBuffer {rb.Name} : register(t{rb.BindPoint});");
                    break;
                case ResourceType.UAVRWByteAddress:
                    EmitLine($"RWByteAddressBuffer {rb.Name} : register(u{rb.BindPoint});");
                    break;
            }

        if (_shaderInfo.ResourceBindings.Count > 0)
            EmitLine();
    }

    private void EmitIOStructs()
    {
        if (_shaderInfo!.InputSignature.Count > 0)
        {
            var name = IOStructName("INPUT");
            EmitLine($"struct {name}");
            EmitLine("{");
            _indentLevel++;
            foreach (var elem in _shaderInfo.InputSignature)
            {
                var type = HlslTypeHelpers.ComponentTypeToHlsl(elem.ComponentType, elem.Mask);
                var semantic = elem.SemanticIndex > 0
                    ? $"{elem.SemanticName}{elem.SemanticIndex}"
                    : elem.SemanticName;
                EmitLine($"{type} v{elem.Register} : {semantic};");
            }

            _indentLevel--;
            EmitLine("};");
            EmitLine();
        }

        if (_shaderInfo.OutputSignature.Count > 0)
        {
            var name = IOStructName("OUTPUT");
            EmitLine($"struct {name}");
            EmitLine("{");
            _indentLevel++;
            foreach (var elem in _shaderInfo.OutputSignature)
            {
                var type = HlslTypeHelpers.ComponentTypeToHlsl(elem.ComponentType, elem.Mask);
                var semantic = elem.SemanticIndex > 0
                    ? $"{elem.SemanticName}{elem.SemanticIndex}"
                    : elem.SemanticName;
                EmitLine($"{type} o{elem.Register} : {semantic};");
            }

            _indentLevel--;
            EmitLine("};");
            EmitLine();
        }
    }

    // ═══ Function Body ═══

    private void EmitFunctionBody(DxilFunction func)
    {
        var inputStruct = IOStructName("INPUT");
        var outputStruct = IOStructName("OUTPUT");

        EmitLine($"{outputStruct} main({inputStruct} input)");
        EmitLine("{");
        _indentLevel++;

        EmitLine($"{outputStruct} output;");
        EmitLine();

        var cfRecovery = new ControlFlowRecovery();
        var cfTree = cfRecovery.Recover(func);
        EmitCfNode(cfTree);

        EmitLine();
        EmitLine("return output;");

        _indentLevel--;
        EmitLine("}");
    }

    private void EmitCfNode(ControlFlowRecovery.CfNode node)
    {
        switch (node)
        {
            case ControlFlowRecovery.SequenceNode seq:
                foreach (var child in seq.Children)
                    EmitCfNode(child);
                break;

            case ControlFlowRecovery.BlockNode block:
                EmitBasicBlock(block.Block);
                break;

            case ControlFlowRecovery.IfNode ifNode:
                EmitLine($"if ({FormatOperand(ifNode.Condition)})");
                EmitLine("{");
                _indentLevel++;
                EmitCfNode(ifNode.ThenBody);
                _indentLevel--;
                if (ifNode.ElseBody != null)
                {
                    EmitLine("}");
                    EmitLine("else");
                    EmitLine("{");
                    _indentLevel++;
                    EmitCfNode(ifNode.ElseBody);
                    _indentLevel--;
                }

                EmitLine("}");
                break;

            case ControlFlowRecovery.LoopNode loop:
                EmitLine("while (true)");
                EmitLine("{");
                _indentLevel++;
                EmitCfNode(loop.Body);
                _indentLevel--;
                EmitLine("}");
                break;

            case ControlFlowRecovery.SwitchNode switchNode:
                EmitLine($"switch ({FormatOperand(switchNode.SwitchValue)})");
                EmitLine("{");
                _indentLevel++;
                foreach (var (val, caseBody) in switchNode.Cases)
                {
                    EmitLine($"case {FormatOperand(val)}:");
                    EmitLine("{");
                    _indentLevel++;
                    EmitCfNode(caseBody);
                    EmitLine("break;");
                    _indentLevel--;
                    EmitLine("}");
                }
                if (switchNode.DefaultBody != null)
                {
                    EmitLine("default:");
                    EmitLine("{");
                    _indentLevel++;
                    EmitCfNode(switchNode.DefaultBody);
                    EmitLine("break;");
                    _indentLevel--;
                    EmitLine("}");
                }
                _indentLevel--;
                EmitLine("}");
                break;

            case ControlFlowRecovery.GotoNode gotoNode:
                EmitLine($"// goto {gotoNode.TargetLabel};");
                break;
        }
    }

    private void EmitBasicBlock(DxilBasicBlock block)
    {
        // Emit a comment for block label if not entry
        if (block.Label != "entry" && block.Label != "0")
            EmitLine($"// {block.Label}:");

        foreach (var instr in block.Instructions)
            EmitInstruction(instr);

        // Handle terminator
        if (block.Terminator != null)
            EmitTerminator(block.Terminator);
    }

    private void EmitInstruction(DxilInstruction instr)
    {
        switch (instr.Kind)
        {
            case DxilInstructionKind.Call:
                EmitCallInstruction(instr);
                break;

            case DxilInstructionKind.BinaryOp:
                EmitBinaryOp(instr);
                break;

            case DxilInstructionKind.CompareOp:
                EmitCompareOp(instr);
                break;

            case DxilInstructionKind.Phi:
                EmitPhi(instr);
                break;

            case DxilInstructionKind.Select:
                EmitSelect(instr);
                break;

            case DxilInstructionKind.ExtractValue:
                EmitExtractValue(instr);
                break;

            case DxilInstructionKind.Cast:
                EmitCast(instr);
                break;

            case DxilInstructionKind.Load:
            case DxilInstructionKind.Store:
            case DxilInstructionKind.Alloca:
                // These are typically internal SSA operations, skip for HLSL output
                break;

            case DxilInstructionKind.Unknown:
                if (!string.IsNullOrEmpty(instr.RawText) && !instr.RawText.TrimStart().StartsWith(';'))
                    EmitLine($"// {instr.RawText.Trim()}");
                break;
        }
    }

    private void EmitCallInstruction(DxilInstruction instr)
    {
        var funcName = instr.CalledFunction;

        // dx.op.* calls — dispatch based on DXIL opcode
        if (funcName.StartsWith("dx.op.") && instr.DxilOpCode >= 0)
        {
            EmitDxilOp(instr);
            return;
        }

        // Other function calls
        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"float4 {varName} = {funcName}({FormatArgList(instr.Arguments)});");
        }
        else
        {
            EmitLine($"{funcName}({FormatArgList(instr.Arguments)});");
        }
    }

    private void EmitDxilOp(DxilInstruction instr)
    {
        var opcode = instr.DxilOpCode;
        var args = instr.Arguments;

        // CreateHandle — track resource handles
        if (opcode == (int)DxilOpCode.CreateHandle || opcode == (int)DxilOpCode.CreateHandleFromBinding ||
            opcode == (int)DxilOpCode.AnnotateHandle || opcode == (int)DxilOpCode.CreateHandleForLib ||
            opcode == (int)DxilOpCode.CreateHandleFromHeap)
        {
            TrackHandle(instr);
            return;
        }

        // LoadInput
        if (opcode == (int)DxilOpCode.LoadInput)
        {
            EmitLoadInput(instr);
            return;
        }

        // StoreOutput
        if (opcode == (int)DxilOpCode.StoreOutput)
        {
            EmitStoreOutput(instr);
            return;
        }

        // CBufferLoadLegacy
        if (opcode == (int)DxilOpCode.CBufferLoadLegacy || opcode == (int)DxilOpCode.CBufferLoad)
        {
            EmitCBufferLoad(instr);
            return;
        }

        // Unary math
        if (DxilOpMapping.IsUnaryMath(opcode))
        {
            EmitUnaryMathOp(instr);
            return;
        }

        // Binary math (min/max)
        if (DxilOpMapping.IsBinaryMath(opcode))
        {
            EmitBinaryMathOp(instr);
            return;
        }

        // Ternary math (mad/fma)
        if (DxilOpMapping.IsTernaryMath(opcode))
        {
            EmitTernaryMathOp(instr);
            return;
        }

        // Dot products
        if (DxilOpMapping.IsDot(opcode))
        {
            EmitDotOp(instr);
            return;
        }

        // Sample operations
        if (DxilOpMapping.IsSample(opcode))
        {
            EmitSampleOp(instr);
            return;
        }

        // TextureLoad
        if (opcode == (int)DxilOpCode.TextureLoad)
        {
            EmitTextureLoad(instr);
            return;
        }

        // TextureStore
        if (opcode == (int)DxilOpCode.TextureStore)
        {
            EmitTextureStore(instr);
            return;
        }

        // BufferLoad
        if (opcode == (int)DxilOpCode.BufferLoad || opcode == (int)DxilOpCode.RawBufferLoad)
        {
            EmitBufferLoad(instr);
            return;
        }

        // Discard
        if (opcode == (int)DxilOpCode.Discard)
        {
            if (args.Count > 1)
                EmitLine($"if ({FormatOperand(args[1])}) discard;");
            else
                EmitLine("discard;");
            return;
        }

        // Derivatives
        if (opcode is (int)DxilOpCode.DerivCoarseX or (int)DxilOpCode.DerivCoarseY or
            (int)DxilOpCode.DerivFineX or (int)DxilOpCode.DerivFineY)
        {
            var hlslFunc = DxilOpMapping.GetHlslEquivalent(opcode) ?? "ddx";
            if (instr.ResultName != null && args.Count > 1)
            {
                var varName = GetTempName(instr.ResultName);
                EmitLine($"float {varName} = {hlslFunc}({FormatOperand(args[1])});");
            }

            return;
        }

        // Barrier
        if (opcode == (int)DxilOpCode.Barrier)
        {
            EmitLine("GroupMemoryBarrierWithGroupSync();");
            return;
        }

        // Thread/Group IDs
        if (opcode is (int)DxilOpCode.ThreadId or (int)DxilOpCode.GroupId or
            (int)DxilOpCode.ThreadIdInGroup or (int)DxilOpCode.FlattenedThreadIdInGroup)
        {
            if (instr.ResultName != null)
            {
                var varName = GetTempName(instr.ResultName);
                var idName = opcode switch
                {
                    (int)DxilOpCode.ThreadId => "thread_id",
                    (int)DxilOpCode.GroupId => "group_id",
                    (int)DxilOpCode.ThreadIdInGroup => "thread_id_in_group",
                    (int)DxilOpCode.FlattenedThreadIdInGroup => "thread_id_flat",
                    _ => "id"
                };
                var comp = args.Count > 1 ? ComponentFromArg(args[1]) : "";
                EmitLine($"uint {varName} = {idName}{comp};");
            }

            return;
        }

        // GetDimensions
        if (opcode == (int)DxilOpCode.GetDimensions)
        {
            if (instr.ResultName != null)
            {
                var varName = GetTempName(instr.ResultName);
                var texName = args.Count > 1 ? ResolveHandle(args[1]) : "texture";
                EmitLine($"// {varName} = {texName}.GetDimensions(...)");
            }

            return;
        }

        // TextureGather / TextureGatherCmp
        if (opcode is (int)DxilOpCode.TextureGather or (int)DxilOpCode.TextureGatherCmp)
        {
            EmitGatherOp(instr);
            return;
        }

        // Fallback: emit as comment
        var hlslName = DxilOpMapping.GetHlslEquivalent(opcode) ?? $"dxop_{opcode}";
        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            var argStrs = args.Skip(1).Select(FormatOperand);
            EmitLine($"float {varName} = {hlslName}({string.Join(", ", argStrs)}); // dx.op {opcode}");
        }
        else
        {
            var argStrs = args.Skip(1).Select(FormatOperand);
            EmitLine($"{hlslName}({string.Join(", ", argStrs)}); // dx.op {opcode}");
        }
    }

    // ═══ Specific DXIL op emitters ═══

    private void TrackHandle(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        // CreateHandle: args = [opcode, resourceClass, rangeId, index, nonUniform]
        // resourceClass: 0=SRV, 1=UAV, 2=CBV, 3=Sampler
        if (args.Count >= 4)
        {
            var resourceClass = (int)(args[1].IntValue ?? 0);
            var rangeId = (int)(args[2].IntValue ?? 0);

            var type = resourceClass switch
            {
                0 => "SRV",
                1 => "UAV",
                2 => "CBV",
                3 => "Sampler",
                _ => "Unknown"
            };

            _handleMap[instr.ResultName] = (type, rangeId);
        }
    }

    private void EmitLoadInput(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        // LoadInput: args = [opcode, inputSigId, rowIndex, colIndex, gsVertexIndex]
        var sigId = args.Count > 1 ? (int)(args[1].IntValue ?? 0) : 0;
        var col = args.Count > 3 ? ComponentFromArg(args[3]) : "";

        string inputName;
        if (sigId < _shaderInfo!.InputSignature.Count)
        {
            var sig = _shaderInfo.InputSignature[sigId];
            inputName = $"input.v{sig.Register}{col}";
        }
        else
        {
            inputName = $"input.v{sigId}{col}";
        }

        var varName = GetTempName(instr.ResultName);
        StoreSsaExpression(instr.ResultName, inputName);
    }

    private void EmitStoreOutput(DxilInstruction instr)
    {
        var args = instr.Arguments;

        // StoreOutput: args = [opcode, outputSigId, rowIndex, colIndex, value]
        var sigId = args.Count > 1 ? (int)(args[1].IntValue ?? 0) : 0;
        var col = args.Count > 3 ? ComponentFromArg(args[3]) : "";
        var value = args.Count > 4 ? FormatOperand(args[4]) : "0";

        string outputName;
        if (sigId < _shaderInfo!.OutputSignature.Count)
        {
            var sig = _shaderInfo.OutputSignature[sigId];
            outputName = $"output.o{sig.Register}{col}";
        }
        else
        {
            outputName = $"output.o{sigId}{col}";
        }

        EmitLine($"{outputName} = {value};");
    }

    private void EmitCBufferLoad(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        // CBufferLoadLegacy: args = [opcode, handle, regIndex]
        var handleName = args.Count > 1 ? ResolveHandle(args[1]) : "cb?";
        var regIndex = args.Count > 2 ? (int)(args[2].IntValue ?? 0) : 0;

        // Resolve to named variable via RDEF
        var cbLoadName = ResolveCBufferAccess(handleName, regIndex);
        StoreSsaExpression(instr.ResultName, cbLoadName);
    }

    private void EmitUnaryMathOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var hlslFunc = DxilOpMapping.GetHlslEquivalent(instr.DxilOpCode) ?? "unknown";
        var input = args.Count > 1 ? FormatOperand(args[1]) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float {varName} = {hlslFunc}({input});");
    }

    private void EmitBinaryMathOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var hlslFunc = DxilOpMapping.GetHlslEquivalent(instr.DxilOpCode) ?? "unknown";
        var a = args.Count > 1 ? FormatOperand(args[1]) : "0";
        var b = args.Count > 2 ? FormatOperand(args[2]) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float {varName} = {hlslFunc}({a}, {b});");
    }

    private void EmitTernaryMathOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var a = args.Count > 1 ? FormatOperand(args[1]) : "0";
        var b = args.Count > 2 ? FormatOperand(args[2]) : "0";
        var c = args.Count > 3 ? FormatOperand(args[3]) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float {varName} = {a} * {b} + {c};");
    }

    private void EmitDotOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var count = DxilOpMapping.DotComponentCount(instr.DxilOpCode);

        // Dot2: args = [opcode, Ax, Ay, Bx, By]
        // Dot3: args = [opcode, Ax, Ay, Az, Bx, By, Bz]
        // Dot4: args = [opcode, Ax, Ay, Az, Aw, Bx, By, Bz, Bw]
        var aComps = new List<string>();
        var bComps = new List<string>();
        for (var i = 0; i < count && 1 + i < args.Count; i++)
            aComps.Add(FormatOperand(args[1 + i]));
        for (var i = 0; i < count && 1 + count + i < args.Count; i++)
            bComps.Add(FormatOperand(args[1 + count + i]));

        var varName = GetTempName(instr.ResultName);
        var aVec = count switch
        {
            2 => $"float2({string.Join(", ", aComps)})",
            3 => $"float3({string.Join(", ", aComps)})",
            4 => $"float4({string.Join(", ", aComps)})",
            _ => string.Join(", ", aComps)
        };
        var bVec = count switch
        {
            2 => $"float2({string.Join(", ", bComps)})",
            3 => $"float3({string.Join(", ", bComps)})",
            4 => $"float4({string.Join(", ", bComps)})",
            _ => string.Join(", ", bComps)
        };

        EmitLine($"float {varName} = dot({aVec}, {bVec});");
    }

    private void EmitSampleOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        // Sample: args = [opcode, texHandle, sampHandle, coord0, coord1, coord2, coord3, offset0, offset1, offset2, clamp, ...]
        var texName = args.Count > 1 ? ResolveHandle(args[1]) : "texture";
        var sampName = args.Count > 2 ? ResolveHandle(args[2]) : "sampler";

        // Build coordinate vector based on texture dimension
        var coordCount = GetTextureDimensionCount(texName);
        var coords = new List<string>();
        for (var i = 0; i < coordCount && 3 + i < args.Count; i++)
            coords.Add(FormatOperand(args[3 + i]));

        var coordStr = coords.Count switch
        {
            1 => coords[0],
            2 => $"float2({string.Join(", ", coords)})",
            3 => $"float3({string.Join(", ", coords)})",
            _ => $"float4({string.Join(", ", coords)})"
        };

        var hlslMethod = DxilOpMapping.GetHlslEquivalent(instr.DxilOpCode) ?? "Sample";

        var varName = GetTempName(instr.ResultName);
        var extra = "";

        // SampleBias: extra bias arg
        if (instr.DxilOpCode == (int)DxilOpCode.SampleBias && 3 + coordCount < args.Count)
            extra = $", {FormatOperand(args[3 + coordCount])}";

        // SampleLevel: extra LOD arg after coords
        if (instr.DxilOpCode == (int)DxilOpCode.SampleLevel && 7 < args.Count)
            extra = $", {FormatOperand(args[7])}";

        // SampleCmp / SampleCmpLevelZero: extra compare value
        if (instr.DxilOpCode is (int)DxilOpCode.SampleCmp or (int)DxilOpCode.SampleCmpLevelZero && 7 < args.Count)
            extra = $", {FormatOperand(args[7])}";

        EmitLine($"float4 {varName} = {texName}.{hlslMethod}({sampName}, {coordStr}{extra});");
    }

    private void EmitTextureLoad(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        var texName = args.Count > 1 ? ResolveHandle(args[1]) : "texture";
        // TextureLoad: args = [opcode, handle, mipLevelOrSampleCount, coord0, coord1, coord2, ...]
        var mip = args.Count > 2 ? FormatOperand(args[2]) : "0";

        var coords = new List<string>();
        for (var i = 3; i < Math.Min(args.Count, 6); i++)
        {
            var c = FormatOperand(args[i]);
            if (c != "undef") coords.Add(c);
        }

        var coordStr = coords.Count switch
        {
            0 => "0",
            1 => coords[0],
            2 => $"int2({string.Join(", ", coords)})",
            _ => $"int3({string.Join(", ", coords)})"
        };

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float4 {varName} = {texName}.Load({coordStr});");
    }

    private void EmitTextureStore(DxilInstruction instr)
    {
        var args = instr.Arguments;
        var texName = args.Count > 1 ? ResolveHandle(args[1]) : "texture";

        var coords = new List<string>();
        for (var i = 2; i < Math.Min(args.Count, 5); i++)
        {
            var c = FormatOperand(args[i]);
            if (c != "undef") coords.Add(c);
        }

        var values = new List<string>();
        for (var i = 5; i < Math.Min(args.Count, 9); i++)
            values.Add(FormatOperand(args[i]));

        var coordStr = coords.Count == 1 ? coords[0] : $"int2({string.Join(", ", coords)})";
        var valueStr = values.Count == 1 ? values[0] : $"float4({string.Join(", ", values)})";

        EmitLine($"{texName}[{coordStr}] = {valueStr};");
    }

    private void EmitBufferLoad(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        var bufName = args.Count > 1 ? ResolveHandle(args[1]) : "buffer";
        var index = args.Count > 2 ? FormatOperand(args[2]) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float4 {varName} = {bufName}.Load({index});");
    }

    private void EmitGatherOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        var texName = args.Count > 1 ? ResolveHandle(args[1]) : "texture";
        var sampName = args.Count > 2 ? ResolveHandle(args[2]) : "sampler";

        var coords = new List<string>();
        for (var i = 3; i < Math.Min(args.Count, 7); i++)
        {
            var c = FormatOperand(args[i]);
            if (c != "undef") coords.Add(c);
        }

        var coordStr = coords.Count switch
        {
            1 => coords[0],
            2 => $"float2({string.Join(", ", coords)})",
            _ => $"float3({string.Join(", ", coords)})"
        };

        var method = instr.DxilOpCode == (int)DxilOpCode.TextureGatherCmp ? "GatherCmp" : "Gather";
        var varName = GetTempName(instr.ResultName);
        EmitLine($"float4 {varName} = {texName}.{method}({sampName}, {coordStr});");
    }

    // ═══ LLVM binary/compare ops ═══

    private void EmitBinaryOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var op = instr.Operator switch
        {
            "fadd" or "add" => "+",
            "fsub" or "sub" => "-",
            "fmul" or "mul" => "*",
            "fdiv" or "udiv" or "sdiv" => "/",
            "frem" or "urem" or "srem" => "%",
            _ => instr.Operator
        };

        var a = instr.Operand1 != null ? FormatOperand(instr.Operand1) : "0";
        var b = instr.Operand2 != null ? FormatOperand(instr.Operand2) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float {varName} = {a} {op} {b};");
    }

    private void EmitCompareOp(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var op = instr.Predicate switch
        {
            "oeq" or "eq" => "==",
            "one" or "ne" => "!=",
            "olt" or "slt" or "ult" => "<",
            "ogt" or "sgt" or "ugt" => ">",
            "ole" or "sle" or "ule" => "<=",
            "oge" or "sge" or "uge" => ">=",
            "ord" => "==", // ordered comparison
            "uno" => "!=", // unordered comparison
            _ => instr.Predicate ?? "=="
        };

        var a = instr.Operand1 != null ? FormatOperand(instr.Operand1) : "0";
        var b = instr.Operand2 != null ? FormatOperand(instr.Operand2) : "0";

        var varName = GetTempName(instr.ResultName);
        StoreSsaExpression(instr.ResultName, $"{a} {op} {b}");
    }

    private void EmitPhi(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        // Phi nodes map to variable assignments — in structured HLSL,
        // the variable should have been assigned in the predecessor blocks.
        // For now, declare as a local variable.
        var varName = GetTempName(instr.ResultName);
        if (instr.PhiIncoming.Count > 0)
        {
            var firstVal = FormatOperand(instr.PhiIncoming[0].Value);
            EmitLine($"float {varName}; // phi from {string.Join(", ", instr.PhiIncoming.Select(p => p.Block))}");
        }
    }

    private void EmitSelect(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var cond = instr.SelectCondition != null ? FormatOperand(instr.SelectCondition) : "true";
        var trueVal = instr.SelectTrue != null ? FormatOperand(instr.SelectTrue) : "0";
        var falseVal = instr.SelectFalse != null ? FormatOperand(instr.SelectFalse) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float {varName} = {cond} ? {trueVal} : {falseVal};");
    }

    private void EmitExtractValue(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var aggr = instr.AggregateOperand != null ? FormatOperand(instr.AggregateOperand) : "?";
        var index = instr.Indices.Count > 0 ? instr.Indices[0] : 0;

        // extractvalue from a multi-return dx.op call (Sample, CBufferLoadLegacy, etc.)
        // maps to component access: .x, .y, .z, .w
        var component = index switch
        {
            0 => ".x",
            1 => ".y",
            2 => ".z",
            3 => ".w",
            _ => $"[{index}]"
        };

        StoreSsaExpression(instr.ResultName, $"{aggr}{component}");
    }

    private void EmitCast(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var src = instr.CastSource != null ? FormatOperand(instr.CastSource) : "0";
        var destType = instr.CastDestType?.Name ?? "float";

        var hlslType = destType switch
        {
            "float" => "float",
            "double" => "double",
            "half" => "half",
            "i32" => "int",
            "i16" => "int",
            "i1" => "bool",
            _ => "float"
        };

        var castFunc = instr.Operator switch
        {
            "fptoui" => "uint",
            "fptosi" => "int",
            "uitofp" or "sitofp" => "float",
            "bitcast" => "asfloat",
            "fptrunc" or "fpext" => hlslType,
            "trunc" or "zext" or "sext" => hlslType,
            _ => hlslType
        };

        if (instr.Operator is "bitcast" && (destType.Contains('*') || destType.Contains("ptr")))
        {
            StoreSsaExpression(instr.ResultName, src);
            return;
        }

        var varName = GetTempName(instr.ResultName);
        EmitLine($"{hlslType} {varName} = ({castFunc}){src};");
    }

    private void EmitTerminator(DxilTerminator term)
    {
        switch (term.Kind)
        {
            case DxilTerminatorKind.Return:
                // Don't emit explicit return — we add "return output;" at the end
                break;

            case DxilTerminatorKind.Branch:
                // Unconditional branches are handled by control flow recovery
                break;

            case DxilTerminatorKind.ConditionalBranch:
                // Handled by control flow recovery
                break;

            case DxilTerminatorKind.Unreachable:
                break;

            case DxilTerminatorKind.Switch:
                // Handled by ControlFlowRecovery SwitchNode
                break;
        }
    }

    // ═══ SSA / naming helpers ═══

    private void CountSsaUses(DxilFunction func)
    {
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            foreach (var arg in instr.Arguments)
                if (arg.Kind == DxilOperandKind.SsaRef && arg.Name != null)
                    _ssaUseCount[arg.Name] = _ssaUseCount.GetValueOrDefault(arg.Name) + 1;

            CountOperandUse(instr.Operand1);
            CountOperandUse(instr.Operand2);
            CountOperandUse(instr.AggregateOperand);
            CountOperandUse(instr.SelectCondition);
            CountOperandUse(instr.SelectTrue);
            CountOperandUse(instr.SelectFalse);
            CountOperandUse(instr.CastSource);
            CountOperandUse(instr.LoadStorePointer);
            CountOperandUse(instr.StoreValue);
        }
    }

    private void CountOperandUse(DxilOperand? operand)
    {
        if (operand?.Kind == DxilOperandKind.SsaRef && operand.Name != null)
            _ssaUseCount[operand.Name] = _ssaUseCount.GetValueOrDefault(operand.Name) + 1;
    }

    private void StoreSsaExpression(string ssaName, string expression)
    {
        _ssaExpressions[ssaName] = expression;
        // Also register a temp name for fallback
        if (!_ssaNames.ContainsKey(ssaName))
            _ssaNames[ssaName] = $"t{_tempCounter++}";
    }

    private string GetTempName(string ssaName)
    {
        if (!_ssaNames.TryGetValue(ssaName, out var name))
        {
            name = $"t{_tempCounter++}";
            _ssaNames[ssaName] = name;
        }

        return name;
    }

    private string FormatOperand(DxilOperand operand)
    {
        switch (operand.Kind)
        {
            case DxilOperandKind.SsaRef:
            {
                var name = operand.Name ?? operand.RawText;

                // Check if we have an inlined expression for this SSA value
                if (_ssaExpressions.TryGetValue(name, out var expr))
                {
                    // Always inline simple expressions (variable names, member access);
                    // only inline complex expressions when single-use
                    var useCount = _ssaUseCount.GetValueOrDefault(name);
                    if (useCount <= 1 || IsSimpleExpression(expr))
                        return expr;
                }

                // Return the temp variable name
                if (_ssaNames.TryGetValue(name, out var tempName))
                    return tempName;

                return $"t_{name}";
            }

            case DxilOperandKind.IntConstant:
                return operand.IntValue?.ToString(CultureInfo.InvariantCulture) ?? "0";

            case DxilOperandKind.FloatConstant:
            {
                if (operand.FloatValue.HasValue)
                {
                    var v = operand.FloatValue.Value;
                    if (v == 0.0) return "0.0";
                    if (v == 1.0) return "1.0";
                    if (v == -1.0) return "-1.0";
                    if (v == 0.5) return "0.5";
                    return v.ToString("G9", CultureInfo.InvariantCulture);
                }

                return operand.RawText;
            }

            case DxilOperandKind.BoolConstant:
                return operand.BoolValue == true ? "true" : "false";

            case DxilOperandKind.Undef:
                return "0 /* undef */";

            case DxilOperandKind.ZeroInit:
            case DxilOperandKind.Null:
                return "0";

            case DxilOperandKind.Global:
                return operand.Name ?? operand.RawText;

            default:
                return operand.RawText;
        }
    }

    /// <summary>
    ///     Returns true if the expression is a simple variable/member/array access
    ///     that's cheap to inline even when multi-used (no computation involved).
    /// </summary>
    private static bool IsSimpleExpression(string expr)
    {
        return !expr.Contains(' ') && !expr.Contains('(') && !expr.Contains('?');
    }

    private string FormatArgList(List<DxilOperand> args)
    {
        // Skip the first arg (DXIL opcode number) for dx.op calls
        var relevant = args.Skip(1);
        return string.Join(", ", relevant.Select(FormatOperand));
    }

    private string ResolveHandle(DxilOperand operand)
    {
        if (operand.Kind == DxilOperandKind.SsaRef && operand.Name != null)
            if (_handleMap.TryGetValue(operand.Name, out var handle))
                return handle.type switch
                {
                    "CBV" => _cbuffers.TryGetValue(handle.slot, out var cb) ? cb.Name : $"cb{handle.slot}",
                    "SRV" => _textures.TryGetValue(handle.slot, out var tex) ? tex.Name : $"t{handle.slot}",
                    "Sampler" => _samplers.TryGetValue(handle.slot, out var samp) ? samp.Name : $"s{handle.slot}",
                    "UAV" => _uavs.TryGetValue(handle.slot, out var uav) ? uav.Name : $"u{handle.slot}",
                    _ => $"resource_{handle.slot}"
                };

        return FormatOperand(operand);
    }

    private string ResolveCBufferAccess(string handleName, int regIndex)
    {
        // Find the CB by name
        foreach (var cb in _cbuffers.Values)
            if (cb.Name == handleName || handleName == $"cb{cb.RegisterSlot}")
            {
                var byteOffset = regIndex * 16;
                var variable = cb.Variables.FirstOrDefault(v =>
                    v.Offset <= byteOffset && v.Offset + v.Size > byteOffset);
                if (variable != null)
                {
                    if (variable.VariableType.Elements > 0)
                    {
                        var arrayIdx = (byteOffset - variable.Offset) / 16;
                        return $"{variable.Name}[{arrayIdx}]";
                    }

                    return variable.Name;
                }

                return $"{cb.Name}[{regIndex}]";
            }

        // Try by slot directly from handle
        // The handleName might be resolved from _handleMap already
        foreach (var (slot, cb) in _cbuffers)
        {
            var byteOffset = regIndex * 16;
            var variable = cb.Variables.FirstOrDefault(v =>
                v.Offset <= byteOffset && v.Offset + v.Size > byteOffset);
            if (variable != null)
                return variable.Name;
        }

        return $"{handleName}[{regIndex}]";
    }

    private static string ComponentFromArg(DxilOperand arg)
    {
        if (arg.IntValue.HasValue)
            return arg.IntValue.Value switch
            {
                0 => ".x",
                1 => ".y",
                2 => ".z",
                3 => ".w",
                _ => $"[{arg.IntValue.Value}]"
            };
        return "";
    }

    private int GetTextureDimensionCount(string texName)
    {
        foreach (var tex in _textures.Values)
            if (tex.Name == texName)
                return tex.Dimension switch
                {
                    ResourceDimension.Texture1D or ResourceDimension.Texture1DArray => 1,
                    ResourceDimension.Texture2D or ResourceDimension.Texture2DArray or
                        ResourceDimension.Texture2DMultisampled => 2,
                    ResourceDimension.Texture3D or ResourceDimension.TextureCube or
                        ResourceDimension.TextureCubeArray => 3,
                    _ => 2
                };

        return 2; // default to 2D
    }

    // ═══ Emit helpers ═══

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

    private string IOStructName(string suffix)
    {
        return _shaderInfo!.Type switch
        {
            ShaderType.Vertex => $"VS_{suffix}",
            ShaderType.Pixel => $"PS_{suffix}",
            ShaderType.Geometry => $"GS_{suffix}",
            ShaderType.Hull => $"HS_{suffix}",
            ShaderType.Domain => $"DS_{suffix}",
            ShaderType.Compute => $"CS_{suffix}",
            _ => suffix
        };
    }

}