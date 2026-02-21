using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler;

public partial class HlslGenerator
{
    private void EmitHeader()
    {
        EmitLine($"// Decompiled {_shaderInfo!.Type} Shader");
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
                foreach (var m in v.VariableType.Members) EmitLine($"{HlslTypeHelpers.HlslTypeName(m.Type)} {m.Name};");
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

    private void EmitFunctionSignature()
    {
        // Input struct
        if (_shaderInfo!.InputSignature.Count > 0)
        {
            var inputStructName = _shaderInfo.Type switch
            {
                ShaderType.Vertex => "VS_INPUT",
                ShaderType.Pixel => "PS_INPUT",
                ShaderType.Geometry => "GS_INPUT",
                ShaderType.Hull => "HS_INPUT",
                ShaderType.Domain => "DS_INPUT",
                ShaderType.Compute => "CS_INPUT",
                _ => "INPUT"
            };

            EmitLine($"struct {inputStructName}");
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

        // Output struct
        if (_shaderInfo.OutputSignature.Count > 0)
        {
            var outputStructName = _shaderInfo.Type switch
            {
                ShaderType.Vertex => "VS_OUTPUT",
                ShaderType.Pixel => "PS_OUTPUT",
                _ => "OUTPUT"
            };

            EmitLine($"struct {outputStructName}");
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
}
