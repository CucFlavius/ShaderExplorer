using System.Globalization;
using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Dxil;

namespace ShaderExplorer.Decompiler.Metal;

/// <summary>
///     Generates Metal Shading Language (MSL) source from a <see cref="DxilModule" /> parsed from Metal AIR IR.
///     Uses analysis passes (struct recovery, parameter classification, GEP chain folding,
///     insertvalue chain folding) before emission to produce clean, readable MSL.
/// </summary>
public class MetalCodeGenerator
{
    private readonly StringBuilder _sb = new();
    private readonly Dictionary<string, string> _ssaNames = new();
    private readonly Dictionary<string, string> _ssaExpressions = new();
    private readonly Dictionary<string, int> _ssaUseCount = new();

    // Resource tracking from IR analysis
    private readonly List<MetalResource> _resources = [];
    private readonly Dictionary<string, MetalResource> _ssaResourceMap = new();

    private DxilModule? _module;
    private ShaderInfo? _shaderInfo;
    private int _indentLevel;
    private int _tempCounter;
    private MetalShaderStage _stage = MetalShaderStage.Fragment;
    private string _entryPointName = "main0";

    // Parsed function parameter info
    private readonly List<MetalParam> _params = [];
    private MetalReturnInfo? _returnInfo;

    // Analysis pass results
    private readonly Dictionary<string, MetalStructInfo> _structInfos = new();
    private readonly Dictionary<string, string> _paramSsaMap = new(); // SSA name → param name
    private readonly Dictionary<string, string> _gepFolded = new(); // SSA name → readable expr
    private readonly Dictionary<string, string> _insertValueFolded = new(); // SSA name → struct ctor expr
    private readonly Dictionary<string, string> _ssaTypeMap = new(); // SSA name → MSL type string

    public string Generate(DxilModule module, ShaderInfo info)
    {
        _sb.Clear();
        _indentLevel = 0;
        _module = module;
        _shaderInfo = info;
        _tempCounter = 0;
        _ssaNames.Clear();
        _ssaExpressions.Clear();
        _ssaUseCount.Clear();
        _resources.Clear();
        _ssaResourceMap.Clear();
        _params.Clear();
        _returnInfo = null;
        _structInfos.Clear();
        _paramSsaMap.Clear();
        _gepFolded.Clear();
        _insertValueFolded.Clear();
        _ssaTypeMap.Clear();

        DetectShaderStage();

        var entryPoint = module.EntryPoint;
        if (entryPoint != null)
        {
            _entryPointName = SanitizeName(entryPoint.Name);
            // Run analysis passes in order
            RunStructTypeRecovery();
            RunParameterAnalysis(entryPoint);
            AnalyzeResources();
            CountSsaUses(entryPoint);
            RunGepFolding(entryPoint);
            RunInsertValueFolding(entryPoint);
        }

        EmitHeader();
        EmitStructDeclarations();
        EmitFunctionSignature();

        if (entryPoint != null)
        {
            EmitLine("{");
            _indentLevel++;
            EmitFunctionBody(entryPoint);
            _indentLevel--;
            EmitLine("}");
        }
        else
        {
            EmitLine("// No entry point found in Metal AIR module");
        }

        return _sb.ToString();
    }

    // ═══ Analysis Pass 1: Struct Type Recovery ═══

    private void RunStructTypeRecovery()
    {
        if (_module == null) return;

        foreach (var st in _module.StructTypes)
        {
            var mslName = st.Name;
            // Clean up LLVM-style names: "struct.FragmentIn" → "FragmentIn"
            if (mslName.StartsWith("struct."))
                mslName = mslName["struct.".Length..];
            mslName = SanitizeName(mslName);

            var info = new MetalStructInfo
            {
                LlvmName = st.Name,
                MslName = mslName
            };

            foreach (var field in st.Fields)
            {
                info.Fields.Add(new MetalStructFieldInfo
                {
                    Name = field.Name,
                    MslType = MapLlvmTypeToMsl(field.Type.Name),
                    Index = field.Offset
                });
            }

            _structInfos[st.Name] = info;
        }
    }

    // ═══ Analysis Pass 2: Parameter Analysis ═══

    private void RunParameterAnalysis(DxilFunction func)
    {
        var texCount = 0;
        var sampCount = 0;
        var bufCount = 0;
        var paramIndex = 0;

        foreach (var param in func.Parameters)
        {
            var rawType = param.RawTypeText;
            var typeName = param.Type.Name;
            var addrSpace = param.AddressSpace;
            var name = string.IsNullOrEmpty(param.Name) ? $"arg{paramIndex}" : SanitizeName(param.Name);

            var mp = new MetalParam
            {
                Name = name,
                TypeName = typeName,
                RawType = rawType,
                Index = paramIndex,
                AddressSpace = addrSpace
            };

            // Classify by address space and type
            if (rawType.Contains("texture") || typeName.Contains("texture"))
            {
                mp.Kind = MetalParamKind.Texture;
                mp.MslType = InferTextureType(rawType, typeName);
                mp.Attribute = $"[[texture({texCount++})]]";
            }
            else if (rawType.Contains("sampler") || typeName.Contains("sampler"))
            {
                mp.Kind = MetalParamKind.Sampler;
                mp.MslType = "sampler";
                mp.Attribute = $"[[sampler({sampCount++})]]";
            }
            else if (addrSpace == 2 || rawType.Contains("addrspace(2)") || rawType.Contains("constant"))
            {
                mp.Kind = MetalParamKind.Buffer;
                mp.MslType = InferBufferType(rawType, typeName, "constant");
                mp.Attribute = $"[[buffer({bufCount++})]]";
            }
            else if (addrSpace == 1 || rawType.Contains("addrspace(1)") || rawType.Contains("device"))
            {
                mp.Kind = MetalParamKind.Buffer;
                mp.MslType = InferBufferType(rawType, typeName, "device");
                mp.Attribute = $"[[buffer({bufCount++})]]";
            }
            else if (typeName.Contains("%struct") || typeName.StartsWith("%"))
            {
                // Struct parameter — likely stage_in if it's the first struct param
                var hasStageIn = _params.Any(p => p.Kind == MetalParamKind.StageIn);
                if (!hasStageIn)
                {
                    mp.Kind = MetalParamKind.StageIn;
                    mp.MslType = ResolveStructMslName(typeName);
                    mp.Attribute = "[[stage_in]]";
                }
                else
                {
                    mp.Kind = MetalParamKind.Other;
                    mp.MslType = ResolveStructMslName(typeName);
                }
            }
            else if (paramIndex == 0 && func.Parameters.Count > 1 && addrSpace < 0)
            {
                // First non-addrspace param is likely stage_in
                mp.Kind = MetalParamKind.StageIn;
                mp.MslType = _stage == MetalShaderStage.Vertex ? "VertexIn" : "FragmentIn";
                mp.Attribute = "[[stage_in]]";
            }
            else
            {
                mp.Kind = MetalParamKind.Other;
                mp.MslType = MapLlvmTypeToMsl(typeName);
            }

            _params.Add(mp);

            // Map SSA name to parameter for GEP resolution
            // Handles both named params (%texName) and unnamed params (%0, %1, etc.)
            if (!string.IsNullOrEmpty(param.Name))
                _paramSsaMap[param.Name] = name;

            paramIndex++;
        }

        // Analyze return type
        if (func.ReturnType != DxilType.Void)
            _returnInfo = new MetalReturnInfo { TypeName = func.ReturnType.Name };
    }

    private string InferTextureType(string rawType, string typeName)
    {
        var dim = "2d";
        if (rawType.Contains("1d") || typeName.Contains("1d")) dim = "1d";
        else if (rawType.Contains("3d") || typeName.Contains("3d")) dim = "3d";
        else if (rawType.Contains("cube") || typeName.Contains("cube")) dim = "cube";
        else if (rawType.Contains("2d_array") || typeName.Contains("2d_array")) dim = "2d_array";

        var access = "sample";
        if (rawType.Contains("write") || typeName.Contains("write")) access = "write";
        else if (rawType.Contains("read_write")) access = "read_write";
        else if (rawType.Contains("read") || typeName.Contains("read")) access = "read";

        var elemType = "float";
        if (rawType.Contains("half") || typeName.Contains("half")) elemType = "half";
        else if (rawType.Contains("int") || typeName.Contains("int")) elemType = "int";
        else if (rawType.Contains("uint") || typeName.Contains("uint")) elemType = "uint";

        return access == "sample"
            ? $"texture{dim}<{elemType}>"
            : $"texture{dim}<{elemType}, access::{access}>";
    }

    private string InferBufferType(string rawType, string typeName, string qualifier)
    {
        // Try to resolve struct type from pointer
        var structName = ExtractStructNameFromType(rawType);
        if (structName != null && _structInfos.TryGetValue(structName, out var si))
            return $"{qualifier} {si.MslName}&";

        // Check for typed pointer (float*, float4*, etc.)
        if (typeName.Contains("float"))
            return $"{qualifier} float*";
        if (typeName.Contains("i32"))
            return $"{qualifier} int*";

        return $"{qualifier} void*";
    }

    private string ResolveStructMslName(string llvmTypeName)
    {
        var cleanName = llvmTypeName.TrimStart('%').TrimEnd('*').Trim();
        if (_structInfos.TryGetValue(cleanName, out var si))
            return si.MslName;

        // Try with "struct." prefix stripped
        if (cleanName.StartsWith("struct."))
            cleanName = cleanName["struct.".Length..];
        return SanitizeName(cleanName);
    }

    private static string? ExtractStructNameFromType(string rawType)
    {
        var idx = rawType.IndexOf("%struct.", StringComparison.Ordinal);
        if (idx < 0)
            idx = rawType.IndexOf('%');
        if (idx < 0) return null;

        var start = idx + 1; // skip %
        var end = start;
        while (end < rawType.Length && rawType[end] != '*' && rawType[end] != ' ' && rawType[end] != ',')
            end++;
        return rawType[start..end];
    }

    // ═══ Analysis Pass 3: GEP Chain Folding ═══

    private void RunGepFolding(DxilFunction func)
    {
        if (_module == null) return;

        // Build instruction map: SSA name → instruction
        var instrMap = new Dictionary<string, DxilInstruction>();
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.ResultName != null)
                instrMap[instr.ResultName] = instr;
        }

        // Process all GEP instructions
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.Kind == DxilInstructionKind.GetElementPtr && instr.ResultName != null)
                FoldGep(instr, instrMap);
        }

        // Process loads: fold load(gep(...)) into the GEP expression
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.Kind == DxilInstructionKind.Load && instr.ResultName != null)
                FoldLoad(instr, instrMap);
        }
    }

    private void FoldGep(DxilInstruction instr, Dictionary<string, DxilInstruction> instrMap)
    {
        if (instr.ResultName == null || instr.GepBase == null) return;

        var baseName = ResolveGepBase(instr.GepBase, instrMap);
        var baseTypeName = instr.GepBaseType?.Name;
        var indices = instr.GepIndices;

        // Simple cases with struct types
        if (baseTypeName != null && indices.Count >= 2)
        {
            var cleanBase = baseTypeName.TrimStart('%');
            // First index is the pointer offset (usually 0), second is struct field
            if (indices[1].Kind == DxilOperandKind.IntConstant && indices[1].IntValue.HasValue)
            {
                var fieldIdx = (int)indices[1].IntValue!.Value;
                var fieldName = ResolveStructField(cleanBase, fieldIdx);

                if (fieldName != null)
                {
                    var expr = $"{baseName}.{fieldName}";
                    // If there are more indices, chain them
                    for (var i = 2; i < indices.Count; i++)
                    {
                        if (indices[i].Kind == DxilOperandKind.IntConstant && indices[i].IntValue.HasValue)
                            expr += $"[{indices[i].IntValue!.Value}]";
                        else
                            expr += $"[{FormatOperandRaw(indices[i])}]";
                    }
                    _gepFolded[instr.ResultName] = expr;

                    // Track the type of this GEP result for downstream inference
                    if (TryResolveFieldType(cleanBase, fieldIdx, out var fieldType))
                        _ssaTypeMap[instr.ResultName] = fieldType;
                    return;
                }
            }
        }

        // Non-struct: array/pointer access
        if (indices.Count >= 2)
        {
            // First index offsets the base pointer, second indexes into the element
            var lastIdx = indices[^1];
            var idxStr = lastIdx.Kind == DxilOperandKind.IntConstant
                ? lastIdx.IntValue?.ToString(CultureInfo.InvariantCulture) ?? "0"
                : FormatOperandRaw(lastIdx);
            _gepFolded[instr.ResultName] = $"{baseName}[{idxStr}]";
        }
        else if (indices.Count == 1)
        {
            var idx = indices[0];
            if (idx.Kind == DxilOperandKind.IntConstant && idx.IntValue == 0)
                _gepFolded[instr.ResultName] = baseName;
            else
            {
                var idxStr = idx.Kind == DxilOperandKind.IntConstant
                    ? idx.IntValue?.ToString(CultureInfo.InvariantCulture) ?? "0"
                    : FormatOperandRaw(idx);
                _gepFolded[instr.ResultName] = $"{baseName}[{idxStr}]";
            }
        }
        else
        {
            _gepFolded[instr.ResultName] = baseName;
        }
    }

    private string ResolveGepBase(DxilOperand baseOp, Dictionary<string, DxilInstruction> instrMap)
    {
        if (baseOp.Kind == DxilOperandKind.SsaRef && baseOp.Name != null)
        {
            // Check if base is a param
            if (_paramSsaMap.TryGetValue(baseOp.Name, out var paramName))
                return paramName;

            // Check if base is another folded GEP
            if (_gepFolded.TryGetValue(baseOp.Name, out var foldedExpr))
                return foldedExpr;

            // Check if base is a cast/addrspacecast that points to a param
            if (instrMap.TryGetValue(baseOp.Name, out var baseInstr))
            {
                if (baseInstr.Kind == DxilInstructionKind.Cast && baseInstr.CastSource != null)
                    return ResolveGepBase(baseInstr.CastSource, instrMap);

                // If base is another GEP, fold it first
                if (baseInstr.Kind == DxilInstructionKind.GetElementPtr)
                {
                    FoldGep(baseInstr, instrMap);
                    if (_gepFolded.TryGetValue(baseOp.Name, out var folded))
                        return folded;
                }
            }

            // Check SSA expressions (for casts stored as expressions)
            if (_ssaExpressions.TryGetValue(baseOp.Name, out var expr))
                return expr;

            // Fallback: use SSA name as-is (will get a temp name during emission)
            if (_ssaNames.TryGetValue(baseOp.Name, out var tempName))
                return tempName;

            return $"t_{baseOp.Name}";
        }

        return FormatOperandRaw(baseOp);
    }

    private string? ResolveStructField(string structName, int fieldIndex)
    {
        if (_structInfos.TryGetValue(structName, out var si))
        {
            if (fieldIndex < si.Fields.Count)
                return si.Fields[fieldIndex].Name;
        }

        // Also try without "struct." prefix
        var plainName = structName.StartsWith("struct.") ? structName["struct.".Length..] : structName;
        if (plainName != structName && _structInfos.TryGetValue(plainName, out si))
        {
            if (fieldIndex < si.Fields.Count)
                return si.Fields[fieldIndex].Name;
        }

        // Return a default field name
        return $"field{fieldIndex}";
    }

    private bool TryResolveFieldType(string structName, int fieldIndex, out string mslType)
    {
        mslType = "float";
        if (_structInfos.TryGetValue(structName, out var si) && fieldIndex < si.Fields.Count)
        {
            mslType = si.Fields[fieldIndex].MslType;
            return true;
        }

        var plainName = structName.StartsWith("struct.") ? structName["struct.".Length..] : structName;
        if (plainName != structName && _structInfos.TryGetValue(plainName, out si) && fieldIndex < si.Fields.Count)
        {
            mslType = si.Fields[fieldIndex].MslType;
            return true;
        }
        return false;
    }

    private void FoldLoad(DxilInstruction instr, Dictionary<string, DxilInstruction> instrMap)
    {
        if (instr.ResultName == null || instr.LoadStorePointer == null) return;
        var ptr = instr.LoadStorePointer;

        if (ptr.Kind == DxilOperandKind.SsaRef && ptr.Name != null)
        {
            // If the pointer is a folded GEP, use its expression directly (no dereference)
            if (_gepFolded.TryGetValue(ptr.Name, out var foldedExpr))
            {
                _gepFolded[instr.ResultName] = foldedExpr;
                if (_ssaTypeMap.TryGetValue(ptr.Name, out var ptrType))
                    _ssaTypeMap[instr.ResultName] = ptrType;
                return;
            }

            // If the pointer is a param, the load dereferences it
            if (_paramSsaMap.TryGetValue(ptr.Name, out var paramName))
            {
                _gepFolded[instr.ResultName] = paramName;
                return;
            }
        }
    }

    // ═══ Analysis Pass 4: InsertValue Chain Folding ═══

    private void RunInsertValueFolding(DxilFunction func)
    {
        if (_module == null) return;

        // Build instruction map
        var instrMap = new Dictionary<string, DxilInstruction>();
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.ResultName != null)
                instrMap[instr.ResultName] = instr;
        }

        // Find chains of insertvalue starting from undef
        foreach (var bb in func.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.Kind != DxilInstructionKind.InsertValue || instr.ResultName == null) continue;

            // Only process the final insertvalue in a chain (used more than once or not fed to another insertvalue)
            var isChainEnd = true;
            foreach (var bb2 in func.BasicBlocks)
            foreach (var instr2 in bb2.Instructions)
            {
                if (instr2.Kind == DxilInstructionKind.InsertValue &&
                    instr2.AggregateOperand?.Name == instr.ResultName)
                {
                    isChainEnd = false;
                    break;
                }
                if (!isChainEnd) break;
            }

            if (!isChainEnd) continue;

            // Walk the chain backward to collect all values
            var values = new SortedDictionary<int, string>();
            var current = instr;
            while (current != null && current.Kind == DxilInstructionKind.InsertValue)
            {
                if (current.Indices.Count > 0 && current.InsertedValue != null)
                {
                    var idx = current.Indices[0];
                    values[idx] = FormatOperandRaw(current.InsertedValue);
                }

                // Follow the chain
                if (current.AggregateOperand?.Kind == DxilOperandKind.SsaRef &&
                    current.AggregateOperand.Name != null &&
                    instrMap.TryGetValue(current.AggregateOperand.Name, out var prev) &&
                    prev.Kind == DxilInstructionKind.InsertValue)
                {
                    current = prev;
                }
                else
                {
                    break;
                }
            }

            if (values.Count > 0)
            {
                // Try to identify the struct type
                var structType = TryInferInsertValueType(instr, instrMap);
                var valueList = string.Join(", ", values.Values);
                var ctorExpr = structType != null
                    ? $"{structType}({valueList})"
                    : $"{{ {valueList} }}";
                _insertValueFolded[instr.ResultName] = ctorExpr;
            }
        }
    }

    private string? TryInferInsertValueType(DxilInstruction instr, Dictionary<string, DxilInstruction> instrMap)
    {
        // Check if this insertvalue result is used in a return → use return type
        if (_returnInfo != null)
        {
            var retType = MapLlvmTypeToMsl(_returnInfo.TypeName);
            if (retType != "float" && retType != "void")
                return retType;
        }

        // Check struct infos for matching field count
        // (heuristic: find struct with same field count)
        return null;
    }

    // ═══ Stage Detection ═══

    private void DetectShaderStage()
    {
        if (_module == null) return;

        var entry = _module.EntryPoint;
        if (entry != null)
        {
            var name = entry.Name.ToLowerInvariant();
            if (name.Contains("vertex"))
                _stage = MetalShaderStage.Vertex;
            else if (name.Contains("fragment") || name.Contains("pixel"))
                _stage = MetalShaderStage.Fragment;
            else if (name.Contains("kernel") || name.Contains("compute"))
                _stage = MetalShaderStage.Kernel;
        }

        if (_shaderInfo != null)
        {
            _stage = _shaderInfo.Type switch
            {
                ShaderType.Vertex => MetalShaderStage.Vertex,
                ShaderType.Pixel => MetalShaderStage.Fragment,
                ShaderType.Compute => MetalShaderStage.Kernel,
                _ => _stage
            };
        }

        foreach (var md in _module.NamedMetadata)
        {
            var val = md.Value.ToLowerInvariant();
            if (val.Contains("vertex"))
                _stage = MetalShaderStage.Vertex;
            else if (val.Contains("fragment"))
                _stage = MetalShaderStage.Fragment;
            else if (val.Contains("kernel") || val.Contains("compute"))
                _stage = MetalShaderStage.Kernel;
        }
    }

    // ═══ Resource Analysis ═══

    private void AnalyzeResources()
    {
        if (_module == null) return;

        var entry = _module.EntryPoint;
        if (entry == null) return;

        foreach (var bb in entry.BasicBlocks)
        foreach (var instr in bb.Instructions)
        {
            if (instr.Kind != DxilInstructionKind.Call) continue;
            AnalyzeCallForResources(instr);
        }
    }

    private void AnalyzeCallForResources(DxilInstruction instr)
    {
        var funcName = instr.CalledFunction;

        if (funcName.StartsWith("air.sample_texture"))
        {
            TryTrackTextureResource(instr, GetTextureDimFromIntrinsic(funcName));
            TryTrackSamplerResource(instr);
        }
        else if (funcName.StartsWith("air.read_texture"))
        {
            TryTrackTextureResource(instr, GetTextureDimFromIntrinsic(funcName));
        }
        else if (funcName.StartsWith("air.write_texture"))
        {
            TryTrackTextureResource(instr, GetTextureDimFromIntrinsic(funcName));
        }
    }

    private void TryTrackTextureResource(DxilInstruction instr, string dim)
    {
        if (instr.Arguments.Count < 1) return;
        var texArg = instr.Arguments[0];
        if (texArg.Name == null) return;

        if (!_ssaResourceMap.ContainsKey(texArg.Name))
        {
            // Try to use parameter name if this SSA refs a param
            var resName = _paramSsaMap.TryGetValue(texArg.Name, out var pn) ? pn
                : $"tex{_resources.Count(r => r.Kind == MetalResourceKind.Texture)}";

            var res = new MetalResource
            {
                Kind = MetalResourceKind.Texture,
                Name = resName,
                Slot = _resources.Count(r => r.Kind == MetalResourceKind.Texture),
                Dimension = dim,
                ElementType = "float"
            };
            _resources.Add(res);
            _ssaResourceMap[texArg.Name] = res;
        }
    }

    private void TryTrackSamplerResource(DxilInstruction instr)
    {
        if (instr.Arguments.Count < 2) return;
        var sampArg = instr.Arguments[1];
        if (sampArg.Name == null) return;

        if (!_ssaResourceMap.ContainsKey(sampArg.Name))
        {
            var resName = _paramSsaMap.TryGetValue(sampArg.Name, out var pn) ? pn
                : $"samp{_resources.Count(r => r.Kind == MetalResourceKind.Sampler)}";

            var res = new MetalResource
            {
                Kind = MetalResourceKind.Sampler,
                Name = resName,
                Slot = _resources.Count(r => r.Kind == MetalResourceKind.Sampler)
            };
            _resources.Add(res);
            _ssaResourceMap[sampArg.Name] = res;
        }
    }

    private static string GetTextureDimFromIntrinsic(string intrinsicName)
    {
        if (intrinsicName.Contains("_1d")) return "1d";
        if (intrinsicName.Contains("_3d")) return "3d";
        if (intrinsicName.Contains("_cube")) return "cube";
        if (intrinsicName.Contains("_2d_array")) return "2d_array";
        return "2d";
    }

    // ═══ Emission ═══

    private void EmitHeader()
    {
        EmitLine("// Decompiled Metal Shader (AIR)");
        if (!string.IsNullOrEmpty(_shaderInfo?.FilePath))
            EmitLine($"// Source: {Path.GetFileName(_shaderInfo.FilePath)}");
        if (!string.IsNullOrEmpty(_module?.TargetTriple))
            EmitLine($"// Target: {_module.TargetTriple}");
        EmitLine();
        EmitLine("#include <metal_stdlib>");
        EmitLine("using namespace metal;");
        EmitLine();
    }

    private void EmitStructDeclarations()
    {
        // Emit recovered struct types from IR
        foreach (var (_, si) in _structInfos)
        {
            // Skip empty or internal structs
            if (si.Fields.Count == 0) continue;

            EmitLine($"struct {si.MslName}");
            EmitLine("{");
            _indentLevel++;
            foreach (var field in si.Fields)
                EmitLine($"{field.MslType} {field.Name};");
            _indentLevel--;
            EmitLine("};");
            EmitLine();
        }

        // Emit input struct from shader info if we have signatures but no IR struct
        var hasStageIn = _params.Any(p => p.Kind == MetalParamKind.StageIn);
        if (hasStageIn && _shaderInfo != null && _shaderInfo.InputSignature.Count > 0)
        {
            var structName = _stage == MetalShaderStage.Vertex ? "VertexIn" : "FragmentIn";
            // Only emit if not already covered by IR struct recovery
            if (!_structInfos.Values.Any(s => s.MslName == structName))
            {
                EmitLine($"struct {structName}");
                EmitLine("{");
                _indentLevel++;
                foreach (var sig in _shaderInfo.InputSignature)
                {
                    var mslType = ComponentTypeToMsl(sig.ComponentType, sig.Mask);
                    var attr = GetMetalAttribute(sig);
                    EmitLine($"{mslType} {sig.SemanticName.ToLowerInvariant()}{sig.SemanticIndex} {attr};");
                }
                _indentLevel--;
                EmitLine("};");
                EmitLine();
            }
        }

        // Emit output struct for vertex shaders
        if (_shaderInfo != null && _shaderInfo.OutputSignature.Count > 0 &&
            _stage != MetalShaderStage.Fragment)
        {
            var structName = _stage == MetalShaderStage.Vertex ? "VertexOut" : "ShaderOut";
            if (!_structInfos.Values.Any(s => s.MslName == structName))
            {
                EmitLine($"struct {structName}");
                EmitLine("{");
                _indentLevel++;
                foreach (var sig in _shaderInfo.OutputSignature)
                {
                    var mslType = ComponentTypeToMsl(sig.ComponentType, sig.Mask);
                    var attr = GetMetalAttribute(sig);
                    EmitLine($"{mslType} {sig.SemanticName.ToLowerInvariant()}{sig.SemanticIndex} {attr};");
                }
                _indentLevel--;
                EmitLine("};");
                EmitLine();
            }
        }
    }

    private void EmitFunctionSignature()
    {
        var stageQualifier = _stage switch
        {
            MetalShaderStage.Vertex => "vertex",
            MetalShaderStage.Fragment => "fragment",
            MetalShaderStage.Kernel => "kernel",
            _ => "fragment"
        };

        var returnType = InferReturnType();
        var paramList = BuildParameterList();

        EmitLine($"{stageQualifier} {returnType} {_entryPointName}({paramList})");
    }

    private string InferReturnType()
    {
        if (_returnInfo != null)
        {
            var typeName = _returnInfo.TypeName;

            // If it's a named struct type (%struct.Foo), resolve it
            if (typeName.StartsWith('%'))
            {
                var resolved = ResolveStructMslName(typeName);
                if (resolved != "float" && !string.IsNullOrEmpty(resolved))
                    return resolved;
            }

            // If it's an anonymous struct type { ... } or packed <{ ... }>, synthesize output struct
            if (typeName.Contains('{'))
            {
                var outputName = _stage == MetalShaderStage.Vertex ? "VertexOut" : "ShaderOutput";
                // If we don't already have this struct, create it from the return type fields
                if (!_structInfos.Values.Any(s => s.MslName == outputName))
                    SynthesizeOutputStruct(typeName, outputName);
                return outputName;
            }

            return MapLlvmTypeToMsl(typeName);
        }

        return _stage switch
        {
            MetalShaderStage.Vertex => _shaderInfo?.OutputSignature.Count > 0 ? "VertexOut" : "float4",
            MetalShaderStage.Fragment => "float4",
            MetalShaderStage.Kernel => "void",
            _ => "float4"
        };
    }

    /// <summary>
    ///     Creates a synthetic output struct from an anonymous LLVM struct return type
    ///     like <c>&lt;{ &lt;4 x float&gt;, i32 }&gt;</c>.
    /// </summary>
    private void SynthesizeOutputStruct(string llvmStructType, string mslName)
    {
        var inner = llvmStructType;
        // Strip packed struct markers
        if (inner.StartsWith("<{")) inner = inner[1..];
        if (inner.EndsWith("}>")) inner = inner[..^1];
        inner = inner.Trim('{', '}', ' ');

        var si = new MetalStructInfo
        {
            LlvmName = "__return_type",
            MslName = mslName
        };

        var fieldIdx = 0;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || (inner[i] == ',' && depth == 0))
            {
                var fieldType = inner[start..i].Trim();
                if (!string.IsNullOrEmpty(fieldType))
                {
                    var mslType = MapLlvmTypeToMsl(fieldType);
                    // Assign meaningful names based on type
                    var fieldName = mslType switch
                    {
                        "float4" when fieldIdx == 0 => "color",
                        "float4" => $"value{fieldIdx}",
                        "int" or "uint" => "flags",
                        _ => $"field{fieldIdx}"
                    };
                    si.Fields.Add(new MetalStructFieldInfo
                    {
                        Name = fieldName,
                        MslType = mslType,
                        Index = fieldIdx
                    });
                    fieldIdx++;
                }
                start = i + 1;
            }
            else if (inner[i] is '<' or '{' or '(')
            {
                depth++;
            }
            else if (inner[i] is '>' or '}' or ')')
            {
                depth--;
            }
        }

        _structInfos["__return_type"] = si;
    }

    private string BuildParameterList()
    {
        var parts = new List<string>();

        if (_params.Count > 0)
        {
            foreach (var p in _params)
            {
                var mslType = p.MslType ?? MapParamType(p);
                var attr = p.Attribute ?? "";
                if (!string.IsNullOrEmpty(attr))
                    parts.Add($"{mslType} {p.Name} {attr}");
                else
                    parts.Add($"{mslType} {p.Name}");
            }
        }
        else
        {
            // Synthesize from discovered resources
            var hasStageIn = _shaderInfo?.InputSignature.Count > 0;
            if (hasStageIn)
            {
                var structName = _stage == MetalShaderStage.Vertex ? "VertexIn" : "FragmentIn";
                parts.Add($"{structName} in [[stage_in]]");
            }

            foreach (var res in _resources)
            {
                switch (res.Kind)
                {
                    case MetalResourceKind.Texture:
                        parts.Add($"texture{res.Dimension}<{res.ElementType}> {res.Name} [[texture({res.Slot})]]");
                        break;
                    case MetalResourceKind.Sampler:
                        parts.Add($"sampler {res.Name} [[sampler({res.Slot})]]");
                        break;
                    case MetalResourceKind.Buffer:
                        parts.Add($"constant void* {res.Name} [[buffer({res.Slot})]]");
                        break;
                }
            }
        }

        if (parts.Count == 0)
            return "";

        if (parts.Count <= 2)
            return string.Join(", ", parts);

        // Multi-line for readability
        var sb = new StringBuilder();
        sb.AppendLine();
        for (var i = 0; i < parts.Count; i++)
        {
            sb.Append("    ");
            sb.Append(parts[i]);
            if (i < parts.Count - 1) sb.Append(',');
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private void EmitFunctionBody(DxilFunction func)
    {
        var cfRecovery = new ControlFlowRecovery();
        var cfTree = cfRecovery.Recover(func);
        EmitCfNode(cfTree);
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
        if (block.Label != "entry" && block.Label != "0")
            EmitLine($"// {block.Label}:");

        foreach (var instr in block.Instructions)
            EmitInstruction(instr);

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

            case DxilInstructionKind.InsertValue:
                EmitInsertValue(instr);
                break;

            case DxilInstructionKind.Cast:
                EmitCast(instr);
                break;

            case DxilInstructionKind.GetElementPtr:
                EmitGep(instr);
                break;

            case DxilInstructionKind.Load:
                EmitLoad(instr);
                break;

            case DxilInstructionKind.Store:
                EmitStore(instr);
                break;

            case DxilInstructionKind.Alloca:
                EmitAlloca(instr);
                break;

            case DxilInstructionKind.ExtractElement:
                EmitExtractElement(instr);
                break;

            case DxilInstructionKind.InsertElement:
                EmitInsertElement(instr);
                break;

            case DxilInstructionKind.ShuffleVector:
                EmitShuffleVector(instr);
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

        if (funcName.StartsWith("air."))
        {
            EmitAirIntrinsic(instr);
            return;
        }

        if (funcName.StartsWith("llvm."))
        {
            EmitLlvmIntrinsic(instr);
            return;
        }

        if (funcName.StartsWith("dx.op."))
        {
            EmitFallbackCall(instr);
            return;
        }

        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"auto {varName} = {SanitizeName(funcName)}({FormatArgList(instr.Arguments)});");
        }
        else
        {
            EmitLine($"{SanitizeName(funcName)}({FormatArgList(instr.Arguments)});");
        }
    }

    // ═══ AIR Intrinsic Emission ═══

    private void EmitAirIntrinsic(DxilInstruction instr)
    {
        var funcName = instr.CalledFunction;

        // Interpolation intrinsics — in MSL, stage_in is implicitly interpolated
        if (funcName.StartsWith("air.interpolate"))
        {
            EmitAirInterpolate(instr);
            return;
        }

        if (funcName.StartsWith("air.sample_texture"))
        {
            EmitAirSample(instr);
            return;
        }

        if (funcName.StartsWith("air.read_texture"))
        {
            EmitAirTextureRead(instr);
            return;
        }

        if (funcName.StartsWith("air.write_texture"))
        {
            EmitAirTextureWrite(instr);
            return;
        }

        if (funcName.StartsWith("air.get_width") || funcName.StartsWith("air.get_height") ||
            funcName.StartsWith("air.get_depth") || funcName.StartsWith("air.get_num"))
        {
            EmitAirGetDimension(instr);
            return;
        }

        if (funcName.StartsWith("air.convert"))
        {
            EmitAirConvert(instr);
            return;
        }

        if (TryEmitAirMath(instr))
            return;

        if (funcName.StartsWith("air.discard"))
        {
            EmitLine("discard_fragment();");
            return;
        }

        if (funcName.Contains("thread_position_in_grid") || funcName.Contains("thread_index_in_threadgroup") ||
            funcName.Contains("threadgroup_position_in_grid") || funcName.Contains("threads_per_threadgroup"))
        {
            EmitAirThreadId(instr);
            return;
        }

        if (funcName.Contains("barrier") || funcName.Contains("fence"))
        {
            EmitLine("threadgroup_barrier(mem_flags::mem_threadgroup);");
            return;
        }

        if (funcName.Contains("atomic"))
        {
            EmitAirAtomic(instr);
            return;
        }

        EmitFallbackAirCall(instr);
    }

    private void EmitAirInterpolate(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        // The argument is a pointer to a stage_in field (resolved via GEP/param chains)
        var fieldExpr = args.Count > 0 ? FormatOperand(args[0]) : "in";

        // Determine result type from intrinsic name
        var funcName = instr.CalledFunction;
        var typeStr = "float4";
        if (funcName.Contains("v2f32")) typeStr = "float2";
        else if (funcName.Contains("v3f32")) typeStr = "float3";
        else if (funcName.Contains("_f32") && !funcName.Contains("v")) typeStr = "float";
        else if (funcName.Contains("v2f16") || funcName.Contains("v2half")) typeStr = "half2";
        else if (funcName.Contains("v3f16") || funcName.Contains("v3half")) typeStr = "half3";
        else if (funcName.Contains("v4f16") || funcName.Contains("v4half")) typeStr = "half4";
        else if (funcName.Contains("_f16") || funcName.Contains("_half")) typeStr = "half";

        // In MSL, stage_in interpolation is implicit — just read the field
        var varName = GetTempName(instr.ResultName);
        EmitLine($"{typeStr} {varName} = {fieldExpr};");
    }

    private void EmitAirSample(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        var texName = args.Count > 0 ? ResolveResourceName(args[0]) : "tex0";
        var sampName = args.Count > 1 ? ResolveResourceName(args[1]) : "samp0";

        var dim = GetTextureDimFromIntrinsic(instr.CalledFunction);
        var coordCount = dim switch
        {
            "1d" => 1,
            "3d" or "cube" => 3,
            _ => 2
        };

        var coords = new List<string>();
        for (var i = 0; i < coordCount && 2 + i < args.Count; i++)
        {
            // Skip non-coordinate args (booleans, integers used as flags)
            var arg = args[2 + i];
            if (arg.Kind is DxilOperandKind.BoolConstant or DxilOperandKind.IntConstant)
                break;
            coords.Add(FormatOperand(arg));
        }

        var coordStr = coords.Count switch
        {
            1 => coords[0],
            2 => $"float2({string.Join(", ", coords)})",
            3 => $"float3({string.Join(", ", coords)})",
            _ => $"float2({string.Join(", ", coords)})"
        };

        var extra = "";
        var funcName = instr.CalledFunction;
        if (funcName.Contains("bias") && 2 + coordCount < args.Count)
            extra = $", bias({FormatOperand(args[2 + coordCount])})";
        else if (funcName.Contains("level") && 2 + coordCount < args.Count)
            extra = $", level({FormatOperand(args[2 + coordCount])})";
        else if (funcName.Contains("gradient") && 2 + coordCount + 1 < args.Count)
            extra = $", gradient2d({FormatOperand(args[2 + coordCount])}, {FormatOperand(args[2 + coordCount + 1])})";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float4 {varName} = {texName}.sample({sampName}, {coordStr}{extra});");
    }

    private void EmitAirTextureRead(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;

        var texName = args.Count > 0 ? ResolveResourceName(args[0]) : "tex0";

        var coords = new List<string>();
        for (var i = 1; i < Math.Min(args.Count, 4); i++)
        {
            var c = FormatOperand(args[i]);
            if (c != "0 /* undef */") coords.Add(c);
        }

        var coordStr = coords.Count switch
        {
            0 => "uint2(0, 0)",
            1 => coords[0],
            2 => $"uint2({string.Join(", ", coords)})",
            _ => $"uint3({string.Join(", ", coords)})"
        };

        var varName = GetTempName(instr.ResultName);
        EmitLine($"float4 {varName} = {texName}.read({coordStr});");
    }

    private void EmitAirTextureWrite(DxilInstruction instr)
    {
        var args = instr.Arguments;
        var texName = args.Count > 0 ? ResolveResourceName(args[0]) : "tex0";

        var values = new List<string>();
        var coords = new List<string>();

        if (args.Count >= 7)
        {
            for (var i = 1; i <= 4 && i < args.Count; i++)
                values.Add(FormatOperand(args[i]));
            for (var i = 5; i < Math.Min(args.Count, 7); i++)
                coords.Add(FormatOperand(args[i]));
        }

        var valueStr = values.Count == 4
            ? $"float4({string.Join(", ", values)})"
            : values.Count > 0 ? string.Join(", ", values) : "float4(0)";
        var coordStr = coords.Count == 2
            ? $"uint2({string.Join(", ", coords)})"
            : coords.Count == 1 ? coords[0] : "uint2(0, 0)";

        EmitLine($"{texName}.write({valueStr}, {coordStr});");
    }

    private void EmitAirGetDimension(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var texName = args.Count > 0 ? ResolveResourceName(args[0]) : "tex0";
        var funcName = instr.CalledFunction;

        var method = "get_width()";
        if (funcName.Contains("height")) method = "get_height()";
        else if (funcName.Contains("depth")) method = "get_depth()";
        else if (funcName.Contains("num_mip")) method = "get_num_mip_levels()";
        else if (funcName.Contains("num_samples")) method = "get_num_samples()";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"uint {varName} = {texName}.{method};");
    }

    private void EmitAirConvert(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var args = instr.Arguments;
        var funcName = instr.CalledFunction;

        var destType = "float";
        if (funcName.Contains(".f.") || funcName.Contains("_to_f"))
            destType = "float";
        else if (funcName.Contains(".i.") || funcName.Contains("_to_s"))
            destType = "int";
        else if (funcName.Contains(".u.") || funcName.Contains("_to_u"))
            destType = "uint";
        else if (funcName.Contains(".half") || funcName.Contains("_to_h"))
            destType = "half";

        if (funcName.Contains("v4"))
            destType += "4";
        else if (funcName.Contains("v3"))
            destType += "3";
        else if (funcName.Contains("v2"))
            destType += "2";

        var src = args.Count > 0 ? FormatOperand(args[0]) : "0";
        var varName = GetTempName(instr.ResultName);
        EmitLine($"{destType} {varName} = {destType}({src});");
    }

    private bool TryEmitAirMath(DxilInstruction instr)
    {
        if (instr.ResultName == null) return false;
        var funcName = instr.CalledFunction;
        var args = instr.Arguments;

        // Normalize fast variants: air.fast_sin_f32 → air.sin_f32
        var normalizedFunc = funcName.Replace(".fast_", ".");

        string? mslFunc = null;
        var argCount = 1;

        // Use normalizedFunc (with .fast_ → . normalization) for all matching
        if (normalizedFunc.Contains("air.fabs") || normalizedFunc.Contains("air.abs"))
        { mslFunc = "abs"; }
        else if (normalizedFunc.Contains("air.saturate"))
        { mslFunc = "saturate"; }
        else if (normalizedFunc.Contains("air.sqrt"))
        { mslFunc = "sqrt"; }
        else if (normalizedFunc.Contains("air.rsqrt"))
        { mslFunc = "rsqrt"; }
        else if (normalizedFunc.Contains("air.sin"))
        { mslFunc = "sin"; }
        else if (normalizedFunc.Contains("air.cos"))
        { mslFunc = "cos"; }
        else if (normalizedFunc.Contains("air.exp2"))
        { mslFunc = "exp2"; }
        else if (normalizedFunc.Contains("air.exp"))
        { mslFunc = "exp"; }
        else if (normalizedFunc.Contains("air.log2"))
        { mslFunc = "log2"; }
        else if (normalizedFunc.Contains("air.log"))
        { mslFunc = "log"; }
        else if (normalizedFunc.Contains("air.floor"))
        { mslFunc = "floor"; }
        else if (normalizedFunc.Contains("air.ceil"))
        { mslFunc = "ceil"; }
        else if (normalizedFunc.Contains("air.round"))
        { mslFunc = "round"; }
        else if (normalizedFunc.Contains("air.trunc"))
        { mslFunc = "trunc"; }
        else if (normalizedFunc.Contains("air.fract"))
        { mslFunc = "fract"; }
        else if (normalizedFunc.Contains("air.sign"))
        { mslFunc = "sign"; }
        else if (normalizedFunc.Contains("air.clamp"))
        { mslFunc = "clamp"; argCount = 3; }
        else if (normalizedFunc.Contains("air.fmin") || normalizedFunc.Contains("air.min"))
        { mslFunc = "min"; argCount = 2; }
        else if (normalizedFunc.Contains("air.fmax") || normalizedFunc.Contains("air.max"))
        { mslFunc = "max"; argCount = 2; }
        else if (normalizedFunc.Contains("air.fma"))
        { mslFunc = "fma"; argCount = 3; }
        else if (normalizedFunc.Contains("air.mix") || normalizedFunc.Contains("air.lerp"))
        { mslFunc = "mix"; argCount = 3; }
        else if (normalizedFunc.Contains("air.step"))
        { mslFunc = "step"; argCount = 2; }
        else if (normalizedFunc.Contains("air.smoothstep"))
        { mslFunc = "smoothstep"; argCount = 3; }
        else if (normalizedFunc.Contains("air.normalize"))
        { mslFunc = "normalize"; }
        else if (normalizedFunc.Contains("air.length"))
        { mslFunc = "length"; }
        else if (normalizedFunc.Contains("air.distance"))
        { mslFunc = "distance"; argCount = 2; }
        else if (normalizedFunc.Contains("air.dot"))
        { mslFunc = "dot"; argCount = 2; }
        else if (normalizedFunc.Contains("air.cross"))
        { mslFunc = "cross"; argCount = 2; }
        else if (normalizedFunc.Contains("air.reflect"))
        { mslFunc = "reflect"; argCount = 2; }
        else if (normalizedFunc.Contains("air.refract"))
        { mslFunc = "refract"; argCount = 3; }
        else if (normalizedFunc.Contains("air.pow"))
        { mslFunc = "pow"; argCount = 2; }
        else if (normalizedFunc.Contains("air.asin"))
        { mslFunc = "asin"; }
        else if (normalizedFunc.Contains("air.acos"))
        { mslFunc = "acos"; }
        else if (normalizedFunc.Contains("air.atan2"))
        { mslFunc = "atan2"; argCount = 2; }
        else if (normalizedFunc.Contains("air.atan"))
        { mslFunc = "atan"; }
        else if (normalizedFunc.Contains("air.tan"))
        { mslFunc = "tan"; }
        else if (normalizedFunc.Contains("air.dfdx"))
        { mslFunc = "dfdx"; }
        else if (normalizedFunc.Contains("air.dfdy"))
        { mslFunc = "dfdy"; }
        else if (normalizedFunc.Contains("air.isnan"))
        { mslFunc = "isnan"; }
        else if (normalizedFunc.Contains("air.isinf"))
        { mslFunc = "isinf"; }
        else if (normalizedFunc.Contains("air.popcount"))
        { mslFunc = "popcount"; }
        else if (normalizedFunc.Contains("air.ctz"))
        { mslFunc = "ctz"; }
        else if (normalizedFunc.Contains("air.clz"))
        { mslFunc = "clz"; }
        else if (normalizedFunc.Contains("air.reverse_bits"))
        { mslFunc = "reverse_bits"; }
        else if (normalizedFunc.Contains("air.select"))
        { mslFunc = "select"; argCount = 3; }

        if (mslFunc == null) return false;

        var argStrs = new List<string>();
        for (var i = 0; i < argCount && i < args.Count; i++)
            argStrs.Add(FormatOperand(args[i]));

        var varName = GetTempName(instr.ResultName);
        var typeStr = InferMslType(instr);
        EmitLine($"{typeStr} {varName} = {mslFunc}({string.Join(", ", argStrs)});");
        return true;
    }

    private void EmitAirThreadId(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var funcName = instr.CalledFunction;

        var idExpr = "thread_position_in_grid";
        if (funcName.Contains("thread_index_in_threadgroup"))
            idExpr = "thread_index_in_threadgroup";
        else if (funcName.Contains("threadgroup_position_in_grid"))
            idExpr = "threadgroup_position_in_grid";
        else if (funcName.Contains("threads_per_threadgroup"))
            idExpr = "threads_per_threadgroup";

        StoreSsaExpression(instr.ResultName, idExpr);
    }

    private void EmitAirAtomic(DxilInstruction instr)
    {
        var funcName = instr.CalledFunction;
        var args = instr.Arguments;

        var op = "exchange";
        if (funcName.Contains("add")) op = "fetch_add";
        else if (funcName.Contains("sub")) op = "fetch_sub";
        else if (funcName.Contains("min")) op = "fetch_min";
        else if (funcName.Contains("max")) op = "fetch_max";
        else if (funcName.Contains("and")) op = "fetch_and";
        else if (funcName.Contains("or")) op = "fetch_or";
        else if (funcName.Contains("xor")) op = "fetch_xor";
        else if (funcName.Contains("cmpxchg") || funcName.Contains("compare_exchange")) op = "compare_exchange_weak";

        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            var argStrs = args.Select(FormatOperand);
            EmitLine($"auto {varName} = atomic_{op}({string.Join(", ", argStrs)});");
        }
        else
        {
            var argStrs = args.Select(FormatOperand);
            EmitLine($"atomic_{op}({string.Join(", ", argStrs)});");
        }
    }

    // ═══ LLVM Intrinsic Emission ═══

    private void EmitLlvmIntrinsic(DxilInstruction instr)
    {
        if (instr.ResultName == null && !instr.CalledFunction.Contains("lifetime"))
        {
            EmitFallbackCall(instr);
            return;
        }

        var funcName = instr.CalledFunction;
        var args = instr.Arguments;

        string? mslFunc = null;
        var argCount = 1;

        if (funcName.StartsWith("llvm.fma"))
        { mslFunc = "fma"; argCount = 3; }
        else if (funcName.StartsWith("llvm.sqrt"))
        { mslFunc = "sqrt"; }
        else if (funcName.StartsWith("llvm.fabs") || funcName.StartsWith("llvm.abs"))
        { mslFunc = "abs"; }
        else if (funcName.StartsWith("llvm.sin"))
        { mslFunc = "sin"; }
        else if (funcName.StartsWith("llvm.cos"))
        { mslFunc = "cos"; }
        else if (funcName.StartsWith("llvm.exp"))
        { mslFunc = "exp"; }
        else if (funcName.StartsWith("llvm.exp2"))
        { mslFunc = "exp2"; }
        else if (funcName.StartsWith("llvm.log"))
        { mslFunc = "log"; }
        else if (funcName.StartsWith("llvm.log2"))
        { mslFunc = "log2"; }
        else if (funcName.StartsWith("llvm.pow"))
        { mslFunc = "pow"; argCount = 2; }
        else if (funcName.StartsWith("llvm.floor"))
        { mslFunc = "floor"; }
        else if (funcName.StartsWith("llvm.ceil"))
        { mslFunc = "ceil"; }
        else if (funcName.StartsWith("llvm.round"))
        { mslFunc = "round"; }
        else if (funcName.StartsWith("llvm.trunc"))
        { mslFunc = "trunc"; }
        else if (funcName.StartsWith("llvm.minnum"))
        { mslFunc = "min"; argCount = 2; }
        else if (funcName.StartsWith("llvm.maxnum"))
        { mslFunc = "max"; argCount = 2; }
        else if (funcName.StartsWith("llvm.copysign"))
        { mslFunc = "copysign"; argCount = 2; }
        else if (funcName.StartsWith("llvm.ctpop"))
        { mslFunc = "popcount"; }
        else if (funcName.StartsWith("llvm.ctlz"))
        { mslFunc = "clz"; }
        else if (funcName.StartsWith("llvm.cttz"))
        { mslFunc = "ctz"; }
        else if (funcName.StartsWith("llvm.bitreverse"))
        { mslFunc = "reverse_bits"; }
        else if (funcName.Contains("lifetime"))
        {
            return;
        }

        if (mslFunc != null && instr.ResultName != null)
        {
            var argStrs = new List<string>();
            for (var i = 0; i < argCount && i < args.Count; i++)
                argStrs.Add(FormatOperand(args[i]));

            var varName = GetTempName(instr.ResultName);
            var typeStr = InferMslType(instr);
            EmitLine($"{typeStr} {varName} = {mslFunc}({string.Join(", ", argStrs)});");
            return;
        }

        EmitFallbackCall(instr);
    }

    private void EmitFallbackCall(DxilInstruction instr)
    {
        var funcName = SanitizeName(instr.CalledFunction);
        var argStrs = instr.Arguments.Select(FormatOperand);

        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"auto {varName} = /* {funcName}({string.Join(", ", argStrs)}) */;");
        }
        else
        {
            EmitLine($"/* {funcName}({string.Join(", ", argStrs)}) */;");
        }
    }

    private void EmitFallbackAirCall(DxilInstruction instr)
    {
        var funcName = instr.CalledFunction;
        var cleanName = funcName.Replace("air.", "").Replace(".", "_");
        var argStrs = instr.Arguments.Select(FormatOperand);

        if (instr.ResultName != null)
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"auto {varName} = /* air.{cleanName}({string.Join(", ", argStrs)}) */;");
        }
        else
        {
            EmitLine($"/* air.{cleanName}({string.Join(", ", argStrs)}) */;");
        }
    }

    // ═══ LLVM binary/compare/phi/select ops ═══

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
            "shl" => "<<",
            "lshr" or "ashr" => ">>",
            "and" => "&",
            "or" => "|",
            "xor" => "^",
            _ => instr.Operator
        };

        var a = instr.Operand1 != null ? FormatOperand(instr.Operand1) : "0";
        var b = instr.Operand2 != null ? FormatOperand(instr.Operand2) : "0";

        var varName = GetTempName(instr.ResultName);
        var typeStr = InferBinaryOpType(instr);
        EmitLine($"{typeStr} {varName} = {a} {op} {b};");
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
            "ord" => "==",
            "uno" => "!=",
            _ => instr.Predicate ?? "=="
        };

        var a = instr.Operand1 != null ? FormatOperand(instr.Operand1) : "0";
        var b = instr.Operand2 != null ? FormatOperand(instr.Operand2) : "0";

        StoreSsaExpression(instr.ResultName, $"{a} {op} {b}");
    }

    private void EmitPhi(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var varName = GetTempName(instr.ResultName);
        var typeStr = InferPhiType(instr);
        EmitLine($"{typeStr} {varName}; // phi from {string.Join(", ", instr.PhiIncoming.Select(p => p.Block))}");
    }

    private void EmitSelect(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var cond = instr.SelectCondition != null ? FormatOperand(instr.SelectCondition) : "true";
        var trueVal = instr.SelectTrue != null ? FormatOperand(instr.SelectTrue) : "0";
        var falseVal = instr.SelectFalse != null ? FormatOperand(instr.SelectFalse) : "0";

        var varName = GetTempName(instr.ResultName);
        EmitLine($"auto {varName} = {cond} ? {trueVal} : {falseVal};");
    }

    private void EmitExtractValue(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var aggr = instr.AggregateOperand != null ? FormatOperand(instr.AggregateOperand) : "?";
        var index = instr.Indices.Count > 0 ? instr.Indices[0] : 0;

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

    private void EmitInsertValue(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        // Check if this is the end of a folded chain
        if (_insertValueFolded.TryGetValue(instr.ResultName, out var ctorExpr))
        {
            StoreSsaExpression(instr.ResultName, ctorExpr);
            return;
        }

        // Single insertvalue — emit as field assignment
        var aggr = instr.AggregateOperand != null ? FormatOperand(instr.AggregateOperand) : "{}";
        var val = instr.InsertedValue != null ? FormatOperand(instr.InsertedValue) : "0";
        var index = instr.Indices.Count > 0 ? instr.Indices[0] : 0;

        var component = index switch
        {
            0 => ".x",
            1 => ".y",
            2 => ".z",
            3 => ".w",
            _ => $"[{index}]"
        };

        // If aggregate is undef, this is the start of a chain — just store the partial expression
        if (instr.AggregateOperand?.Kind == DxilOperandKind.Undef)
        {
            StoreSsaExpression(instr.ResultName, $"/* partial insert {component} = {val} */");
        }
        else
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"auto {varName} = {aggr};");
            EmitLine($"{varName}{component} = {val};");
        }
    }

    private void EmitCast(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var src = instr.CastSource != null ? FormatOperand(instr.CastSource) : "0";
        var destType = instr.CastDestType?.Name ?? "float";

        var mslType = MapLlvmTypeToMsl(destType);

        var castExpr = instr.Operator switch
        {
            "fptoui" => $"uint({src})",
            "fptosi" => $"int({src})",
            "uitofp" or "sitofp" => $"float({src})",
            "bitcast" when destType.Contains('*') || destType.Contains("ptr") => src,
            "bitcast" => $"as_type<{mslType}>({src})",
            "fptrunc" or "fpext" => $"{mslType}({src})",
            "trunc" or "zext" or "sext" => $"{mslType}({src})",
            "addrspacecast" => src,
            "inttoptr" or "ptrtoint" => src,
            _ => $"{mslType}({src})"
        };

        if (instr.Operator is "bitcast" or "addrspacecast" or "inttoptr" or "ptrtoint" &&
            (destType.Contains('*') || destType.Contains("ptr")))
        {
            StoreSsaExpression(instr.ResultName, src);
            return;
        }

        var varName = GetTempName(instr.ResultName);
        EmitLine($"{mslType} {varName} = {castExpr};");
    }

    private void EmitGep(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        // Use folded expression from analysis pass
        if (_gepFolded.TryGetValue(instr.ResultName, out var foldedExpr))
        {
            StoreSsaExpression(instr.ResultName, foldedExpr);
            return;
        }

        // Fallback: build expression directly
        var basePtr = instr.GepBase != null ? FormatOperand(instr.GepBase) : "ptr";
        var indices = instr.GepIndices.Select(FormatOperand).ToList();

        if (indices.Count >= 2)
        {
            // Check if we have struct type info for field resolution
            if (instr.GepBaseType != null)
            {
                var structName = instr.GepBaseType.Name.TrimStart('%');
                var lastIdx = instr.GepIndices[^1];
                if (lastIdx.Kind == DxilOperandKind.IntConstant && lastIdx.IntValue.HasValue)
                {
                    var fieldName = ResolveStructField(structName, (int)lastIdx.IntValue.Value);
                    if (fieldName != null)
                    {
                        StoreSsaExpression(instr.ResultName, $"{basePtr}.{fieldName}");
                        return;
                    }
                }
            }
            StoreSsaExpression(instr.ResultName, $"{basePtr}[{indices[^1]}]");
        }
        else if (indices.Count == 1)
        {
            if (indices[0] == "0")
                StoreSsaExpression(instr.ResultName, basePtr);
            else
                StoreSsaExpression(instr.ResultName, $"{basePtr}[{indices[0]}]");
        }
        else
        {
            StoreSsaExpression(instr.ResultName, basePtr);
        }
    }

    private void EmitLoad(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        // Use folded expression from analysis pass (GEP+load folding)
        if (_gepFolded.TryGetValue(instr.ResultName, out var foldedExpr))
        {
            StoreSsaExpression(instr.ResultName, foldedExpr);
            return;
        }

        var ptr = instr.LoadStorePointer != null ? FormatOperand(instr.LoadStorePointer) : "ptr";

        // If the pointer expression already looks like a field access, don't add dereference
        if (ptr.Contains('.') || ptr.Contains('['))
            StoreSsaExpression(instr.ResultName, ptr);
        else
            StoreSsaExpression(instr.ResultName, ptr);
    }

    private void EmitStore(DxilInstruction instr)
    {
        var val = instr.StoreValue != null ? FormatOperand(instr.StoreValue) : "0";

        // Check if the store pointer has a folded GEP expression
        if (instr.LoadStorePointer?.Kind == DxilOperandKind.SsaRef && instr.LoadStorePointer.Name != null &&
            _gepFolded.TryGetValue(instr.LoadStorePointer.Name, out var foldedExpr))
        {
            EmitLine($"{foldedExpr} = {val};");
            return;
        }

        var ptr = instr.LoadStorePointer != null ? FormatOperand(instr.LoadStorePointer) : "ptr";

        // If pointer already looks like a field access, emit clean assignment
        if (ptr.Contains('.') || ptr.Contains('['))
            EmitLine($"{ptr} = {val};");
        else
            EmitLine($"{ptr} = {val};");
    }

    private void EmitAlloca(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;
        var typeName = instr.AllocaType?.Name ?? "float";
        var mslType = MapLlvmTypeToMsl(typeName);
        var varName = GetTempName(instr.ResultName);
        EmitLine($"{mslType} {varName};");
    }

    private void EmitExtractElement(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var vec = instr.VectorOperand != null ? FormatOperand(instr.VectorOperand) : "vec";
        var idx = instr.VectorIndex;

        string component;
        if (idx != null && idx.Kind == DxilOperandKind.IntConstant && idx.IntValue.HasValue)
        {
            component = idx.IntValue.Value switch
            {
                0 => ".x",
                1 => ".y",
                2 => ".z",
                3 => ".w",
                _ => $"[{idx.IntValue.Value}]"
            };
        }
        else
        {
            var idxStr = idx != null ? FormatOperand(idx) : "0";
            component = $"[{idxStr}]";
        }

        StoreSsaExpression(instr.ResultName, $"{vec}{component}");
    }

    private void EmitInsertElement(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var vec = instr.VectorOperand != null ? FormatOperand(instr.VectorOperand) : "vec";
        var val = instr.InsertScalar != null ? FormatOperand(instr.InsertScalar) : "0";
        var idx = instr.VectorIndex;

        string component;
        if (idx != null && idx.Kind == DxilOperandKind.IntConstant && idx.IntValue.HasValue)
        {
            component = idx.IntValue.Value switch
            {
                0 => ".x",
                1 => ".y",
                2 => ".z",
                3 => ".w",
                _ => $"[{idx.IntValue.Value}]"
            };
        }
        else
        {
            var idxStr = idx != null ? FormatOperand(idx) : "0";
            component = $"[{idxStr}]";
        }

        if (instr.VectorOperand?.Kind == DxilOperandKind.Undef)
        {
            // Start of a vector construction — just track it
            StoreSsaExpression(instr.ResultName, $"/* vec_build{component}={val} */");
        }
        else
        {
            var varName = GetTempName(instr.ResultName);
            EmitLine($"auto {varName} = {vec};");
            EmitLine($"{varName}{component} = {val};");
        }
    }

    private void EmitShuffleVector(DxilInstruction instr)
    {
        if (instr.ResultName == null) return;

        var v1 = instr.VectorOperand != null ? FormatOperand(instr.VectorOperand) : "v1";
        var mask = instr.ShuffleMask;

        // Try to emit as a simple swizzle (all from v1, contiguous or standard pattern)
        if (mask.Count > 0 && mask.All(m => m >= 0 && m < 4))
        {
            var swizzle = new string(mask.Select(m => "xyzw"[m]).ToArray());
            StoreSsaExpression(instr.ResultName, $"{v1}.{swizzle}");
            return;
        }

        // Fallback: emit as shuffle
        var maskStr = string.Join(", ", mask.Select(m => m < 0 ? "0" : m.ToString()));
        var varName = GetTempName(instr.ResultName);
        EmitLine($"auto {varName} = /* shuffle({v1}, <{maskStr}>) */;");
    }

    private void EmitTerminator(DxilTerminator term)
    {
        switch (term.Kind)
        {
            case DxilTerminatorKind.Return:
                if (term.ReturnValue != null)
                    EmitLine($"return {FormatOperand(term.ReturnValue)};");
                else if (_stage != MetalShaderStage.Kernel)
                    EmitLine("return output;");
                break;

            case DxilTerminatorKind.Branch:
            case DxilTerminatorKind.ConditionalBranch:
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
            CountOperandUse(instr.InsertedValue);
            CountOperandUse(instr.SelectCondition);
            CountOperandUse(instr.SelectTrue);
            CountOperandUse(instr.SelectFalse);
            CountOperandUse(instr.CastSource);
            CountOperandUse(instr.LoadStorePointer);
            CountOperandUse(instr.StoreValue);
            CountOperandUse(instr.GepBase);
            foreach (var idx in instr.GepIndices)
                CountOperandUse(idx);
            CountOperandUse(instr.VectorOperand);
            CountOperandUse(instr.VectorIndex);
            CountOperandUse(instr.InsertScalar);
            CountOperandUse(instr.ShuffleVector2);
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

                // Check GEP folded expressions first
                if (_gepFolded.TryGetValue(name, out var foldedExpr))
                    return foldedExpr;

                // Check insertvalue folded expressions
                if (_insertValueFolded.TryGetValue(name, out var ivExpr))
                    return ivExpr;

                // Check param SSA map
                if (_paramSsaMap.TryGetValue(name, out var paramName))
                    return paramName;

                // Check resource map
                if (_ssaResourceMap.TryGetValue(name, out var res))
                    return res.Name;

                if (_ssaExpressions.TryGetValue(name, out var expr))
                {
                    var useCount = _ssaUseCount.GetValueOrDefault(name);
                    if (useCount <= 1 || IsSimpleExpression(expr))
                        return expr;
                }

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
    ///     Formats an operand without SSA resolution — used during analysis passes
    ///     before SSA expressions are populated.
    /// </summary>
    private string FormatOperandRaw(DxilOperand operand)
    {
        switch (operand.Kind)
        {
            case DxilOperandKind.SsaRef:
                var name = operand.Name ?? operand.RawText;
                if (_paramSsaMap.TryGetValue(name, out var paramName))
                    return paramName;
                if (_gepFolded.TryGetValue(name, out var foldedExpr))
                    return foldedExpr;
                if (_ssaNames.TryGetValue(name, out var tempName))
                    return tempName;
                return $"t_{name}";

            case DxilOperandKind.IntConstant:
                return operand.IntValue?.ToString(CultureInfo.InvariantCulture) ?? "0";

            case DxilOperandKind.FloatConstant:
                if (operand.FloatValue.HasValue)
                {
                    var v = operand.FloatValue.Value;
                    if (v == 0.0) return "0.0";
                    if (v == 1.0) return "1.0";
                    return v.ToString("G9", CultureInfo.InvariantCulture);
                }
                return operand.RawText;

            case DxilOperandKind.BoolConstant:
                return operand.BoolValue == true ? "true" : "false";

            case DxilOperandKind.Undef:
                return "0";

            case DxilOperandKind.ZeroInit:
            case DxilOperandKind.Null:
                return "0";

            default:
                return operand.RawText;
        }
    }

    private static bool IsSimpleExpression(string expr)
    {
        return !expr.Contains(' ') && !expr.Contains('(') && !expr.Contains('?');
    }

    private string FormatArgList(List<DxilOperand> args)
    {
        return string.Join(", ", args.Select(FormatOperand));
    }

    private string ResolveResourceName(DxilOperand operand)
    {
        if (operand.Kind == DxilOperandKind.SsaRef && operand.Name != null)
        {
            if (_ssaResourceMap.TryGetValue(operand.Name, out var res))
                return res.Name;
            if (_paramSsaMap.TryGetValue(operand.Name, out var paramName))
                return paramName;
        }
        return FormatOperand(operand);
    }

    // ═══ Type inference helpers ═══

    private string InferMslType(DxilInstruction instr)
    {
        // Try GEP-propagated type
        if (instr.ResultName != null && _ssaTypeMap.TryGetValue(instr.ResultName, out var mappedType))
            return mappedType;

        if (instr.CallReturnType != null)
            return MapLlvmTypeToMsl(instr.CallReturnType.Name);

        return "float";
    }

    private string InferBinaryOpType(DxilInstruction instr)
    {
        var op = instr.Operator;
        if (op.StartsWith('f'))
            return "float";
        if (op is "and" or "or" or "xor" or "shl" or "lshr" or "ashr")
            return "uint";
        if (op is "udiv" or "urem")
            return "uint";
        return "int";
    }

    private string InferPhiType(DxilInstruction instr)
    {
        if (instr.PhiType != null)
            return MapLlvmTypeToMsl(instr.PhiType.Name);
        return "float";
    }

    private static string MapLlvmTypeToMsl(string llvmType)
    {
        var t = llvmType.Trim();

        // Strip address space qualifiers
        if (t.Contains("addrspace"))
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*addrspace\(\d+\)\s*", " ").Trim();

        // Strip pointer
        t = t.TrimEnd('*').Trim();

        // Handle packed struct types: <{ <4 x float>, i32 }> → ShaderOutput struct
        if ((t.StartsWith("<{") && t.EndsWith("}>")) || (t.StartsWith('{') && t.EndsWith('}')))
            return MapStructTypeToMsl(t);

        return t switch
        {
            "void" => "void",
            "float" => "float",
            "double" => "float", // Metal doesn't have double
            "half" => "half",
            "i1" => "bool",
            "i8" => "char",
            "i16" => "short",
            "i32" => "int",
            "i64" => "long",
            "<2 x float>" => "float2",
            "<3 x float>" => "float3",
            "<4 x float>" => "float4",
            "<2 x half>" => "half2",
            "<3 x half>" => "half3",
            "<4 x half>" => "half4",
            "<2 x i32>" => "int2",
            "<3 x i32>" => "int3",
            "<4 x i32>" => "int4",
            "<2 x i16>" => "short2",
            "<3 x i16>" => "short3",
            "<4 x i16>" => "short4",
            _ when t.StartsWith('<') && t.EndsWith('>') => ParseVectorType(t),
            _ when t.StartsWith("%struct.") => t["%struct.".Length..].Replace(".", "_"),
            _ when t.StartsWith('%') => t[1..].Replace(".", "_"),
            _ => "float"
        };
    }

    /// <summary>
    ///     Maps an LLVM struct type like <c>{ &lt;4 x float&gt;, i32 }</c> or packed
    ///     <c>&lt;{ &lt;4 x float&gt;, i32 }&gt;</c> to a named MSL struct with fields.
    /// </summary>
    private static string MapStructTypeToMsl(string structType)
    {
        // Strip packed struct markers: <{ ... }> → { ... }
        var inner = structType;
        if (inner.StartsWith("<{"))
            inner = inner[1..]; // strip leading <
        if (inner.EndsWith("}>"))
            inner = inner[..^1]; // strip trailing >
        inner = inner.Trim('{', '}', ' ');

        // Parse fields
        var fields = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || (inner[i] == ',' && depth == 0))
            {
                var field = inner[start..i].Trim();
                if (!string.IsNullOrEmpty(field))
                    fields.Add(MapLlvmTypeToMsl(field));
                start = i + 1;
            }
            else if (inner[i] is '<' or '{' or '(')
            {
                depth++;
            }
            else if (inner[i] is '>' or '}' or ')')
            {
                depth--;
            }
        }

        // For a single-field struct returning float4, just use float4
        if (fields.Count == 1)
            return fields[0];

        // Build an inline struct description for the signature
        // This will be matched against recovered struct types during emission
        return $"ShaderOutput /* {string.Join(", ", fields)} */";
    }

    private static string ParseVectorType(string vecType)
    {
        var inner = vecType.Trim('<', '>', ' ');
        var parts = inner.Split('x', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var count))
        {
            var elemType = MapLlvmTypeToMsl(parts[1]);
            return $"{elemType}{count}";
        }
        return "float4";
    }

    private static string MapParamType(MetalParam param)
    {
        return param.Kind switch
        {
            MetalParamKind.Texture => "texture2d<float>",
            MetalParamKind.Sampler => "sampler",
            MetalParamKind.Buffer => "constant float4*",
            MetalParamKind.StageIn => param.TypeName.Contains("struct")
                ? "VertexIn"
                : MapLlvmTypeToMsl(param.TypeName),
            _ => MapLlvmTypeToMsl(param.TypeName)
        };
    }

    private static string ComponentTypeToMsl(ComponentType type, byte mask)
    {
        var count = 0;
        for (var i = 0; i < 4; i++)
            if ((mask & (1 << i)) != 0) count++;
        if (count == 0) count = 4;

        var baseType = type switch
        {
            ComponentType.Float32 => "float",
            ComponentType.Int32 => "int",
            ComponentType.UInt32 => "uint",
            _ => "float"
        };

        return count > 1 ? $"{baseType}{count}" : baseType;
    }

    private static string GetMetalAttribute(SignatureElement sig)
    {
        if (sig.SystemValue != SystemValueType.Undefined)
        {
            return sig.SystemValue switch
            {
                SystemValueType.Position => "[[position]]",
                SystemValueType.VertexID => "[[vertex_id]]",
                SystemValueType.InstanceID => "[[instance_id]]",
                SystemValueType.IsFrontFace => "[[front_facing]]",
                SystemValueType.RenderTargetArrayIndex => "[[render_target_array_index]]",
                SystemValueType.ViewportArrayIndex => "[[viewport_array_index]]",
                SystemValueType.SampleIndex => "[[sample_id]]",
                SystemValueType.Target => $"[[color({sig.Register})]]",
                SystemValueType.Depth => "[[depth(any)]]",
                SystemValueType.Coverage => "[[sample_mask]]",
                SystemValueType.PrimitiveID => "[[primitive_id]]",
                _ => $"[[user({sig.SemanticName.ToLowerInvariant()}{sig.SemanticIndex})]]"
            };
        }

        return $"[[user({sig.SemanticName.ToLowerInvariant()}{sig.SemanticIndex})]]";
    }

    private static string SanitizeName(string name)
    {
        return name.Replace("\"", "").Replace("\\", "").Replace(".", "_");
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

    // ═══ Internal types ═══

    private enum MetalShaderStage
    {
        Vertex,
        Fragment,
        Kernel
    }

    private enum MetalResourceKind
    {
        Texture,
        Sampler,
        Buffer
    }

    private enum MetalParamKind
    {
        StageIn,
        Texture,
        Sampler,
        Buffer,
        Other
    }

    private class MetalResource
    {
        public MetalResourceKind Kind { get; set; }
        public string Name { get; set; } = "";
        public int Slot { get; set; }
        public string Dimension { get; set; } = "2d";
        public string ElementType { get; set; } = "float";
    }

    private class MetalParam
    {
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string RawType { get; set; } = "";
        public MetalParamKind Kind { get; set; }
        public string? Attribute { get; set; }
        public string? MslType { get; set; }
        public int Index { get; set; }
        public int AddressSpace { get; set; } = -1;
    }

    private class MetalReturnInfo
    {
        public string TypeName { get; set; } = "";
    }

    private class MetalStructInfo
    {
        public string LlvmName { get; set; } = "";
        public string MslName { get; set; } = "";
        public List<MetalStructFieldInfo> Fields { get; set; } = [];
    }

    private class MetalStructFieldInfo
    {
        public string Name { get; set; } = "";
        public string MslType { get; set; } = "float";
        public int Index { get; set; }
    }
}
