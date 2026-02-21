using System.Text;
using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Renderer;

public static class VertexShaderGenerator
{
    public static string GenerateCompatibleVertexShader(List<SignatureElement> psInputSig)
    {
        var sb = new StringBuilder();

        // Constant buffer (same as default VS)
        sb.AppendLine("cbuffer Transform : register(b0)");
        sb.AppendLine("{");
        sb.AppendLine("    float4x4 worldViewProj;");
        sb.AppendLine("    float4x4 world;");
        sb.AppendLine("    float4x4 view;");
        sb.AppendLine("    float4x4 projection;");
        sb.AppendLine("    float4 cameraPos;");
        sb.AppendLine("    float4 lightDir;");
        sb.AppendLine("    float4 time;");
        sb.AppendLine("};");
        sb.AppendLine();

        // VS_INPUT: Fixed sphere layout
        sb.AppendLine("struct VS_INPUT");
        sb.AppendLine("{");
        sb.AppendLine("    float3 pos : POSITION;");
        sb.AppendLine("    float3 normal : NORMAL;");
        sb.AppendLine("    float3 tangent : TANGENT;");
        sb.AppendLine("    float2 uv : TEXCOORD0;");
        sb.AppendLine("};");
        sb.AppendLine();

        // VS_OUTPUT: One field per PS input element, skipping rasterizer-generated values
        sb.AppendLine("struct VS_OUTPUT");
        sb.AppendLine("{");
        var fieldIdx = 0;
        foreach (var elem in psInputSig)
        {
            if (IsRasterizerGenerated(elem))
                continue;

            var compCount = PopCount(elem.Mask);
            if (compCount == 0) compCount = 4;
            var hlslType = MaskToHlslType(compCount, elem.ComponentType);
            var semantic = elem.SemanticIndex > 0
                ? $"{elem.SemanticName}{elem.SemanticIndex}"
                : elem.SemanticName;
            sb.AppendLine($"    {hlslType} field{fieldIdx} : {semantic};");
            fieldIdx++;
        }

        sb.AppendLine("};");
        sb.AppendLine();

        // main()
        sb.AppendLine("VS_OUTPUT main(VS_INPUT input)");
        sb.AppendLine("{");
        sb.AppendLine("    VS_OUTPUT output = (VS_OUTPUT)0;");
        sb.AppendLine("    float4 worldPos = mul(float4(input.pos, 1.0), world);");
        sb.AppendLine("    float3 worldNormal = normalize(mul(float4(input.normal, 0.0), world).xyz);");
        sb.AppendLine("    float3 worldTangent = normalize(mul(float4(input.tangent, 0.0), world).xyz);");
        sb.AppendLine("    float3 bitangent = cross(worldNormal, worldTangent);");
        sb.AppendLine();

        fieldIdx = 0;
        foreach (var elem in psInputSig)
        {
            if (IsRasterizerGenerated(elem))
                continue;

            var compCount = PopCount(elem.Mask);
            if (compCount == 0) compCount = 4;
            var dstType = MaskToHlslType(compCount, elem.ComponentType);
            var isInt = elem.ComponentType is ComponentType.Int32 or ComponentType.UInt32;
            var fieldName = $"output.field{fieldIdx}";

            var expr = GetSemanticAssignment(elem, compCount);
            if (isInt)
                expr = $"{dstType}({expr})";

            sb.AppendLine($"    {fieldName} = {expr};");
            fieldIdx++;
        }

        sb.AppendLine("    return output;");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static bool IsRasterizerGenerated(SignatureElement elem)
    {
        return elem.SystemValue is SystemValueType.IsFrontFace
            or SystemValueType.PrimitiveID
            or SystemValueType.SampleIndex;
    }

    private static string GetSemanticAssignment(SignatureElement elem, int compCount)
    {
        var name = elem.SemanticName.ToUpperInvariant();
        var index = elem.SemanticIndex;

        // SV_POSITION
        if (name == "SV_POSITION" || elem.SystemValue == SystemValueType.Position)
            return TruncOrPad("mul(float4(input.pos, 1.0), worldViewProj)", 4, compCount);

        // TEXCOORD with various indices
        if (name == "TEXCOORD")
            return index switch
            {
                0 => TruncOrPad("float4(input.uv, 0.0, 0.0)", 4, compCount),
                1 => TruncOrPad("worldPos", 4, compCount),
                2 => TruncOrPad("float4(worldNormal, 0.0)", 4, compCount),
                3 => TruncOrPad("float4(worldTangent, 0.0)", 4, compCount),
                4 => TruncOrPad("float4(bitangent, 0.0)", 4, compCount),
                5 => TruncOrPad("float4(normalize(cameraPos.xyz - worldPos.xyz), 0.0)", 4, compCount),
                _ => TruncOrPad("float4(0,0,0,0)", 4, compCount)
            };

        // NORMAL
        if (name == "NORMAL")
            return TruncOrPad("float4(worldNormal, 0.0)", 4, compCount);

        // TANGENT
        if (name == "TANGENT")
            return TruncOrPad("float4(worldTangent, 0.0)", 4, compCount);

        // BINORMAL / BITANGENT
        if (name is "BINORMAL" or "BITANGENT")
            return TruncOrPad("float4(bitangent, 0.0)", 4, compCount);

        // COLOR
        if (name == "COLOR")
        {
            if (index == 0)
                return TruncOrPad("float4(1,1,1,1)", 4, compCount);
            if (index == 1)
                return TruncOrPad("float4(worldNormal * 0.5 + 0.5, 1.0)", 4, compCount);
            return TruncOrPad("float4(1,1,1,1)", 4, compCount);
        }

        // POSITION (non-SV)
        if (name == "POSITION")
            return TruncOrPad("worldPos", 4, compCount);

        // Unknown — zero
        return TruncOrPad("float4(0,0,0,0)", 4, compCount);
    }

    private static string TruncOrPad(string expr, int srcCount, int dstCount)
    {
        if (dstCount >= srcCount)
            return expr;

        var swizzle = dstCount switch
        {
            1 => ".x",
            2 => ".xy",
            3 => ".xyz",
            _ => ""
        };
        return $"({expr}){swizzle}";
    }

    private static string MaskToHlslType(int componentCount, ComponentType componentType)
    {
        var baseType = componentType switch
        {
            ComponentType.Int32 => "int",
            ComponentType.UInt32 => "uint",
            _ => "float"
        };

        return componentCount switch
        {
            1 => baseType,
            2 => $"{baseType}2",
            3 => $"{baseType}3",
            _ => $"{baseType}4"
        };
    }

    private static int PopCount(byte mask)
    {
        var count = 0;
        for (var i = 0; i < 4; i++)
            if ((mask & (1 << i)) != 0)
                count++;
        return count;
    }
}
