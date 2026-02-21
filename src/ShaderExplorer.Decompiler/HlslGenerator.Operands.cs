using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;

namespace ShaderExplorer.Decompiler;

public partial class HlslGenerator
{
    private string FormatDst(Operand op)
    {
        return FormatOperand(op, true);
    }

    private string FormatSrc(Operand op)
    {
        return FormatOperand(op, false);
    }

    private string FormatSrcInt(Operand op)
    {
        return FormatOperand(op, false, true);
    }

    private string FormatOperand(Operand op, bool isDst, bool asInt = false)
    {
        var name = asInt && op.Type == OperandType.Immediate32
            ? FormatImmediate32Int(op)
            : FormatOperandBase(op);
        var modifier = "";
        var modifierEnd = "";

        if (!isDst)
            switch (op.Modifier)
            {
                case OperandModifier.Negate:
                    modifier = "-";
                    break;
                case OperandModifier.Abs:
                    modifier = "abs(";
                    modifierEnd = ")";
                    break;
                case OperandModifier.AbsNegate:
                    modifier = "-abs(";
                    modifierEnd = ")";
                    break;
            }

        var components = isDst ? FormatWriteMask(op) : FormatSwizzle(op);
        return $"{modifier}{name}{components}{modifierEnd}";
    }

    private string FormatOperandBase(Operand op)
    {
        switch (op.Type)
        {
            case OperandType.Temp:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                if (Renames.TryGetValue((OperandType.Temp, reg, 0), out var renamed))
                    return renamed;
                return $"r{reg}";
            }

            case OperandType.Input:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                // Try to find named input from signature
                var inputElem = _shaderInfo?.InputSignature.FirstOrDefault(e => e.Register == reg);
                if (inputElem != null)
                    return $"input.v{reg}";
                return $"v{reg}";
            }

            case OperandType.Output:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                return $"output.o{reg}";
            }

            case OperandType.ConstantBuffer:
            {
                var cbSlot = (int)(op.Indices[0]?.Value ?? 0);
                var cbOffset = (int)(op.Indices[1]?.Value ?? 0);

                // Check renames
                if (Renames.TryGetValue((OperandType.ConstantBuffer, cbSlot, cbOffset), out var renamed))
                    return renamed;

                // Look up in RDEF
                if (_cbuffers.TryGetValue(cbSlot, out var cb))
                {
                    var byteOffset = cbOffset * 16; // each CB element is 16 bytes (float4)
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

                    return $"{cb.Name}[{cbOffset}]";
                }

                return $"cb{cbSlot}[{cbOffset}]";
            }

            case OperandType.Sampler:
                return FormatSamplerName(op);

            case OperandType.Resource:
                return FormatResourceName(op);

            case OperandType.Immediate32:
                return IsIntegerOpcode(_currentOpcode) ? FormatImmediate32Int(op) : FormatImmediate32(op);

            case OperandType.IndexableTemp:
            {
                var idx = (int)(op.Indices[0]?.Value ?? 0);
                var elem = (int)(op.Indices[1]?.Value ?? 0);
                return $"x{idx}[{elem}]";
            }

            case OperandType.Null:
                return "null";

            case OperandType.OutputDepth:
                return "output_depth";

            case OperandType.OutputCoverageMask:
                return "output_coverage";

            case OperandType.InputPrimitiveID:
                return "primitiveID";

            case OperandType.OutputDepthGreaterEqual:
                return "output_depth_ge";

            case OperandType.OutputDepthLessEqual:
                return "output_depth_le";

            case OperandType.OutputStencilRef:
                return "output_stencil";

            case OperandType.InputCoverageMask:
                return "input_coverage";

            case OperandType.UnorderedAccessView:
            {
                var slot = (int)(op.Indices[0]?.Value ?? 0);
                if (_uavs.TryGetValue(slot, out var uav))
                    return uav.Name;
                return $"u{slot}";
            }

            case OperandType.ThreadGroupSharedMemory:
            {
                var slot = (int)(op.Indices[0]?.Value ?? 0);
                return $"g{slot}";
            }

            case OperandType.InputControlPoint:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                var elem = op.IndexDimension >= IndexDimension.D2
                    ? (int)(op.Indices[1]?.Value ?? 0)
                    : reg;
                return $"input_cp[{reg}].v{elem}";
            }

            case OperandType.OutputControlPoint:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                return $"output_cp[{reg}]";
            }

            case OperandType.InputPatchConstant:
            {
                var reg = (int)(op.Indices[0]?.Value ?? 0);
                return $"patch_const.v{reg}";
            }

            case OperandType.InputDomainPoint:
                return "domain_location";

            case OperandType.OutputControlPointID:
                return "output_cp_id";

            case OperandType.InputForkInstanceID:
                return "fork_instance_id";

            case OperandType.InputJoinInstanceID:
                return "join_instance_id";

            case OperandType.InputThreadID:
                return "thread_id";

            case OperandType.InputThreadGroupID:
                return "group_id";

            case OperandType.InputThreadIDInGroup:
                return "thread_id_in_group";

            case OperandType.InputThreadIDInGroupFlattened:
                return "thread_id_in_group_flattened";

            case OperandType.InputGSInstanceID:
                return "gs_instance_id";

            case OperandType.Stream:
            {
                var slot = (int)(op.Indices[0]?.Value ?? 0);
                return $"stream{slot}";
            }

            default:
                return $"{op.Type}_{op.Indices[0]?.Value ?? 0}";
        }
    }

    private string FormatImmediate32(Operand op)
    {
        if (op.NumComponents == 1) return FormatFloat(op.ImmediateValues[0], op.ImmediateValuesInt[0]);

        // 4-component
        var parts = new string[4];
        for (var i = 0; i < 4; i++)
            parts[i] = FormatFloat(op.ImmediateValues[i], op.ImmediateValuesInt[i]);

        // Check if all same
        if (parts[0] == parts[1] && parts[1] == parts[2] && parts[2] == parts[3])
            return $"float4({parts[0]}, {parts[0]}, {parts[0]}, {parts[0]})";

        return $"float4({parts[0]}, {parts[1]}, {parts[2]}, {parts[3]})";
    }

    private static string FormatImmediate32Int(Operand op)
    {
        if (op.NumComponents == 1)
            return FormatInt(op.ImmediateValuesInt[0]);

        // 4-component
        var parts = new string[4];
        for (var i = 0; i < 4; i++)
            parts[i] = FormatInt(op.ImmediateValuesInt[i]);

        if (parts[0] == parts[1] && parts[1] == parts[2] && parts[2] == parts[3])
            return $"int4({parts[0]}, {parts[0]}, {parts[0]}, {parts[0]})";

        return $"int4({parts[0]}, {parts[1]}, {parts[2]}, {parts[3]})";
    }

    private static string FormatInt(int value)
    {
        if (value >= -1024 && value <= 65535)
            return value.ToString();
        return $"0x{(uint)value:X8}";
    }

    private static bool IsIntegerOpcode(OpcodeType op)
    {
        return op is
            OpcodeType.IAdd or OpcodeType.IEq or OpcodeType.IGe or OpcodeType.ILt or
            OpcodeType.IMad or OpcodeType.IMax or OpcodeType.IMin or OpcodeType.IMul or
            OpcodeType.INe or OpcodeType.INeg or OpcodeType.IShl or OpcodeType.IShr or
            OpcodeType.UDiv or OpcodeType.ULt or OpcodeType.UGe or OpcodeType.UMul or
            OpcodeType.UMad or OpcodeType.UMax or OpcodeType.UMin or OpcodeType.UShr or
            OpcodeType.UAddc or OpcodeType.USubb or
            OpcodeType.And or OpcodeType.Or or OpcodeType.Xor or OpcodeType.Not or
            OpcodeType.IBfe or OpcodeType.UBfe or OpcodeType.Bfi or OpcodeType.BfRev or
            OpcodeType.CountBits or OpcodeType.FirstBitHi or OpcodeType.FirstBitLo or
            OpcodeType.Switch or OpcodeType.Case or
            OpcodeType.FtoI or OpcodeType.FtoU or OpcodeType.ItoF or OpcodeType.UtoF;
    }

    private static string FormatFloat(float value, int intBits)
    {
        if (value == 0f) return "0.0";
        if (value == 1f) return "1.0";
        if (value == -1f) return "-1.0";
        if (value == 0.5f) return "0.5";
        if (value == -0.5f) return "-0.5";
        if (value == 2f) return "2.0";

        // Check if this looks more like an integer
        if (float.IsNaN(value) || float.IsInfinity(value) ||
            (intBits != 0 && MathF.Abs(value) > 1e10f))
            return $"asfloat(0x{(uint)intBits:X8})";

        return value.ToString("G9", CultureInfo.InvariantCulture);
    }

    private string FormatResourceName(Operand op)
    {
        var slot = (int)(op.Indices[0]?.Value ?? 0);
        if (_textures.TryGetValue(slot, out var tex))
            return tex.Name;
        return $"t{slot}";
    }

    private string FormatSamplerName(Operand op)
    {
        var slot = (int)(op.Indices[0]?.Value ?? 0);
        if (_samplers.TryGetValue(slot, out var samp))
            return samp.Name;
        return $"s{slot}";
    }

    private static string FormatWriteMask(Operand op)
    {
        if (op.NumComponents <= 1) return "";
        if (op.SelectionMode != SelectionMode.Mask) return FormatSwizzle(op);

        var mask = op.WriteMask;
        if (mask == 0xF || mask == 0) return "";

        var sb = new StringBuilder(".");
        if ((mask & 1) != 0) sb.Append('x');
        if ((mask & 2) != 0) sb.Append('y');
        if ((mask & 4) != 0) sb.Append('z');
        if ((mask & 8) != 0) sb.Append('w');
        return sb.ToString();
    }

    private static string FormatSwizzle(Operand op)
    {
        if (op.NumComponents <= 1) return "";

        switch (op.SelectionMode)
        {
            case SelectionMode.Mask:
                return FormatWriteMask(op);

            case SelectionMode.Swizzle:
            {
                char[] comps = { 'x', 'y', 'z', 'w' };
                var x = comps[op.SwizzleX];
                var y = comps[op.SwizzleY];
                var z = comps[op.SwizzleZ];
                var w = comps[op.SwizzleW];

                // Skip if identity swizzle
                if (x == 'x' && y == 'y' && z == 'z' && w == 'w')
                    return "";

                return $".{x}{y}{z}{w}";
            }

            case SelectionMode.Select1:
            {
                char[] comps = { 'x', 'y', 'z', 'w' };
                return $".{comps[op.SelectComponent]}";
            }

            default:
                return "";
        }
    }
}
