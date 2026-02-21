using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;

namespace ShaderExplorer.Decompiler;

public partial class HlslGenerator
{
    private void EmitInstruction(Instruction instr)
    {
        _currentOpcode = instr.Opcode;
        var sat = instr.IsSaturated ? "saturate(" : "";
        var satEnd = instr.IsSaturated ? ")" : "";

        switch (instr.Opcode)
        {
            // Arithmetic
            case OpcodeType.Add:
            case OpcodeType.IAdd:
                EmitBinaryOp(instr, "+", sat, satEnd);
                break;
            case OpcodeType.Mul:
                EmitBinaryOp(instr, "*", sat, satEnd);
                break;
            case OpcodeType.Div:
            case OpcodeType.UDiv:
                EmitBinaryOp(instr, "/", sat, satEnd);
                break;
            case OpcodeType.Mad:
            case OpcodeType.IMad:
            case OpcodeType.UMad:
                if (instr.Operands.Count >= 4)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {sat}{FormatSrc(instr.Operands[1])} * {FormatSrc(instr.Operands[2])} + {FormatSrc(instr.Operands[3])}{satEnd};");
                break;

            // Dot products
            case OpcodeType.Dp2:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {sat}dot({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}){satEnd};");
                break;
            case OpcodeType.Dp3:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {sat}dot({FormatSrc(instr.Operands[1])}.xyz, {FormatSrc(instr.Operands[2])}.xyz){satEnd};");
                break;
            case OpcodeType.Dp4:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {sat}dot({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}){satEnd};");
                break;

            // Move / conditional move
            case OpcodeType.Mov:
            case OpcodeType.IMul:
                if (instr.Opcode == OpcodeType.IMul && instr.Operands.Count >= 4)
                {
                    // imul has dest_hi, dest_lo, src0, src1
                    if (instr.Operands[0].Type != OperandType.Null)
                        EmitLine(
                            $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[2])} * {FormatSrc(instr.Operands[3])}; // hi");
                    if (instr.Operands[1].Type != OperandType.Null)
                        EmitLine(
                            $"{FormatDst(instr.Operands[1])} = {FormatSrc(instr.Operands[2])} * {FormatSrc(instr.Operands[3])};");
                }
                else if (instr.Operands.Count >= 2)
                {
                    EmitLine($"{FormatDst(instr.Operands[0])} = {sat}{FormatSrc(instr.Operands[1])}{satEnd};");
                }

                break;
            case OpcodeType.Movc:
                if (instr.Operands.Count >= 4)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[1])} ? {FormatSrc(instr.Operands[2])} : {FormatSrc(instr.Operands[3])};");
                break;

            // Math functions
            case OpcodeType.Rsq:
                EmitUnaryFunc(instr, "rsqrt", sat, satEnd);
                break;
            case OpcodeType.Sqrt:
                EmitUnaryFunc(instr, "sqrt", sat, satEnd);
                break;
            case OpcodeType.Exp:
                EmitUnaryFunc(instr, "exp2", sat, satEnd);
                break;
            case OpcodeType.Log:
                EmitUnaryFunc(instr, "log2", sat, satEnd);
                break;
            case OpcodeType.Rcp:
                EmitUnaryFunc(instr, "rcp", sat, satEnd);
                break;
            case OpcodeType.Frc:
                EmitUnaryFunc(instr, "frac", sat, satEnd);
                break;
            case OpcodeType.RoundNe:
                EmitUnaryFunc(instr, "round", sat, satEnd);
                break;
            case OpcodeType.RoundNi:
                EmitUnaryFunc(instr, "floor", sat, satEnd);
                break;
            case OpcodeType.RoundPi:
                EmitUnaryFunc(instr, "ceil", sat, satEnd);
                break;
            case OpcodeType.RoundZ:
                EmitUnaryFunc(instr, "trunc", sat, satEnd);
                break;

            case OpcodeType.Min:
            case OpcodeType.IMin:
            case OpcodeType.UMin:
                EmitBinaryFunc(instr, "min", sat, satEnd);
                break;
            case OpcodeType.Max:
            case OpcodeType.IMax:
            case OpcodeType.UMax:
                EmitBinaryFunc(instr, "max", sat, satEnd);
                break;

            // Comparisons
            case OpcodeType.Lt:
            case OpcodeType.ILt:
            case OpcodeType.ULt:
                EmitBinaryOp(instr, "<");
                break;
            case OpcodeType.Ge:
            case OpcodeType.IGe:
            case OpcodeType.UGe:
                EmitBinaryOp(instr, ">=");
                break;
            case OpcodeType.Eq:
            case OpcodeType.IEq:
                EmitBinaryOp(instr, "==");
                break;
            case OpcodeType.Ne:
            case OpcodeType.INe:
                EmitBinaryOp(instr, "!=");
                break;

            // Bitwise
            case OpcodeType.And:
                EmitBinaryOp(instr, "&");
                break;
            case OpcodeType.Or:
                EmitBinaryOp(instr, "|");
                break;
            case OpcodeType.Xor:
                EmitBinaryOp(instr, "^");
                break;
            case OpcodeType.Not:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = ~{FormatSrc(instr.Operands[1])};");
                break;
            case OpcodeType.IShl:
                EmitBinaryOp(instr, "<<");
                break;
            case OpcodeType.IShr:
                EmitBinaryOp(instr, ">>");
                break;
            case OpcodeType.UShr:
                EmitBinaryOp(instr, ">>");
                break;

            // Conversions
            case OpcodeType.FtoI:
                EmitCast(instr, "int");
                break;
            case OpcodeType.FtoU:
                EmitCast(instr, "uint");
                break;
            case OpcodeType.ItoF:
            case OpcodeType.UtoF:
                EmitCast(instr, "float");
                break;

            // Trig
            case OpcodeType.SinCos:
                if (instr.Operands.Count >= 3)
                {
                    if (instr.Operands[0].Type != OperandType.Null)
                        EmitLine($"{FormatDst(instr.Operands[0])} = sin({FormatSrc(instr.Operands[2])});");
                    if (instr.Operands[1].Type != OperandType.Null)
                        EmitLine($"{FormatDst(instr.Operands[1])} = cos({FormatSrc(instr.Operands[2])});");
                }

                break;

            // Derivatives
            case OpcodeType.DerivRtx:
            case OpcodeType.DerivRtxCoarse:
            case OpcodeType.DerivRtxFine:
                EmitUnaryFunc(instr, "ddx", sat, satEnd);
                break;
            case OpcodeType.DerivRty:
            case OpcodeType.DerivRtyCoarse:
            case OpcodeType.DerivRtyFine:
                EmitUnaryFunc(instr, "ddy", sat, satEnd);
                break;

            // Texture sampling
            case OpcodeType.Sample:
                EmitSample(instr, "Sample");
                break;
            case OpcodeType.SampleL:
                EmitSampleLod(instr);
                break;
            case OpcodeType.SampleB:
                EmitSampleBias(instr);
                break;
            case OpcodeType.SampleC:
                EmitSampleCmp(instr);
                break;
            case OpcodeType.SampleCLz:
                EmitSampleCmpLevelZero(instr);
                break;
            case OpcodeType.SampleD:
                EmitSampleGrad(instr);
                break;
            case OpcodeType.Ld:
                EmitLoad(instr);
                break;
            case OpcodeType.LdMs:
                EmitLoadMs(instr);
                break;
            case OpcodeType.Gather4:
                EmitGather(instr);
                break;
            case OpcodeType.ResInfo:
                EmitResInfo(instr);
                break;

            // Negate
            case OpcodeType.INeg:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = -{FormatSrc(instr.Operands[1])};");
                break;

            // Control flow
            case OpcodeType.If:
            {
                var nz = (instr.ControlBits & 0x1) != 0;
                var cond = instr.Operands.Count > 0 ? FormatSrc(instr.Operands[0]) : "true";
                EmitLine(nz ? $"if ({cond})" : $"if (!{cond})");
                EmitLine("{");
                _indentLevel++;
            }
                break;
            case OpcodeType.Else:
                _indentLevel--;
                EmitLine("}");
                EmitLine("else");
                EmitLine("{");
                _indentLevel++;
                break;
            case OpcodeType.EndIf:
                _indentLevel--;
                EmitLine("}");
                break;
            case OpcodeType.Loop:
                EmitLine("while (true)");
                EmitLine("{");
                _indentLevel++;
                break;
            case OpcodeType.EndLoop:
                _indentLevel--;
                EmitLine("}");
                break;
            case OpcodeType.Break:
                EmitLine("break;");
                break;
            case OpcodeType.BreakC:
            {
                var nz = (instr.ControlBits & 0x1) != 0;
                var cond = instr.Operands.Count > 0 ? FormatSrc(instr.Operands[0]) : "true";
                EmitLine(nz ? $"if ({cond}) break;" : $"if (!{cond}) break;");
            }
                break;
            case OpcodeType.Continue:
                EmitLine("continue;");
                break;
            case OpcodeType.ContinueC:
            {
                var nz = (instr.ControlBits & 0x1) != 0;
                var cond = instr.Operands.Count > 0 ? FormatSrc(instr.Operands[0]) : "true";
                EmitLine(nz ? $"if ({cond}) continue;" : $"if (!{cond}) continue;");
            }
                break;
            case OpcodeType.Switch:
                if (instr.Operands.Count > 0)
                    EmitLine($"switch ({FormatSrcInt(instr.Operands[0])})");
                EmitLine("{");
                _indentLevel++;
                _inCaseBody = false;
                break;
            case OpcodeType.Case:
                if (_inCaseBody)
                    _indentLevel--;
                if (instr.Operands.Count > 0)
                    EmitLine($"case {FormatSrcInt(instr.Operands[0])}:");
                _indentLevel++;
                _inCaseBody = true;
                break;
            case OpcodeType.Default:
                if (_inCaseBody)
                    _indentLevel--;
                EmitLine("default:");
                _indentLevel++;
                _inCaseBody = true;
                break;
            case OpcodeType.EndSwitch:
                if (_inCaseBody)
                    _indentLevel--;
                _inCaseBody = false;
                _indentLevel--;
                EmitLine("}");
                break;

            // Return
            case OpcodeType.Ret:
                // Don't emit explicit return, we add it at the end
                break;

            // Discard
            case OpcodeType.Discard:
            {
                var nz = (instr.ControlBits & 0x1) != 0;
                var cond = instr.Operands.Count > 0 ? FormatSrc(instr.Operands[0]) : "true";
                EmitLine(nz ? $"if ({cond}) discard;" : $"if (!{cond}) discard;");
            }
                break;

            case OpcodeType.Nop:
                break;

            // Bit field operations
            case OpcodeType.UBfe:
            case OpcodeType.IBfe:
                if (instr.Operands.Count >= 4)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = ({FormatSrc(instr.Operands[3])} >> {FormatSrc(instr.Operands[2])}) & ((1u << {FormatSrc(instr.Operands[1])}) - 1u);");
                break;
            case OpcodeType.Bfi:
                if (instr.Operands.Count >= 5)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = (({FormatSrc(instr.Operands[3])} << {FormatSrc(instr.Operands[2])}) & (((1u << {FormatSrc(instr.Operands[1])}) - 1u) << {FormatSrc(instr.Operands[2])})) | ({FormatSrc(instr.Operands[4])} & ~(((1u << {FormatSrc(instr.Operands[1])}) - 1u) << {FormatSrc(instr.Operands[2])}));");
                break;
            case OpcodeType.BfRev:
                EmitUnaryFunc(instr, "reversebits");
                break;
            case OpcodeType.CountBits:
                EmitUnaryFunc(instr, "countbits");
                break;
            case OpcodeType.FirstBitHi:
                EmitUnaryFunc(instr, "firstbithigh");
                break;
            case OpcodeType.FirstBitLo:
                EmitUnaryFunc(instr, "firstbitlow");
                break;
            case OpcodeType.FirstBitShi:
                EmitUnaryFunc(instr, "firstbithigh");
                break;

            case OpcodeType.F32toF16:
                EmitUnaryFunc(instr, "f32tof16");
                break;
            case OpcodeType.F16toF32:
                EmitUnaryFunc(instr, "f16tof32");
                break;

            case OpcodeType.Swapc:
                if (instr.Operands.Count >= 5)
                {
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[2])} ? {FormatSrc(instr.Operands[4])} : {FormatSrc(instr.Operands[3])};");
                    EmitLine(
                        $"{FormatDst(instr.Operands[1])} = {FormatSrc(instr.Operands[2])} ? {FormatSrc(instr.Operands[3])} : {FormatSrc(instr.Operands[4])};");
                }

                break;

            case OpcodeType.UMul:
                if (instr.Operands.Count >= 4)
                {
                    if (instr.Operands[0].Type != OperandType.Null)
                        EmitLine(
                            $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[2])} * {FormatSrc(instr.Operands[3])}; // hi");
                    if (instr.Operands[1].Type != OperandType.Null)
                        EmitLine(
                            $"{FormatDst(instr.Operands[1])} = {FormatSrc(instr.Operands[2])} * {FormatSrc(instr.Operands[3])};");
                }

                break;

            case OpcodeType.UAddc:
                if (instr.Operands.Count >= 4)
                {
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[2])} + {FormatSrc(instr.Operands[3])};");
                    EmitLine(
                        $"{FormatDst(instr.Operands[1])} = ({FormatSrc(instr.Operands[2])} + {FormatSrc(instr.Operands[3])} < {FormatSrc(instr.Operands[2])}) ? 1 : 0; // carry");
                }

                break;
            case OpcodeType.USubb:
                if (instr.Operands.Count >= 4)
                {
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[2])} - {FormatSrc(instr.Operands[3])};");
                    EmitLine(
                        $"{FormatDst(instr.Operands[1])} = ({FormatSrc(instr.Operands[2])} < {FormatSrc(instr.Operands[3])}) ? 1 : 0; // borrow");
                }

                break;

            // Geometry stream ops
            case OpcodeType.EmitStream:
                EmitLine(instr.Operands.Count > 0
                    ? $"stream{FormatSrc(instr.Operands[0])}.Append(); // emit_stream"
                    : "stream.Append(); // emit_stream");
                break;
            case OpcodeType.CutStream:
                EmitLine(instr.Operands.Count > 0
                    ? $"stream{FormatSrc(instr.Operands[0])}.RestartStrip(); // cut_stream"
                    : "stream.RestartStrip(); // cut_stream");
                break;
            case OpcodeType.EmitThenCutStream:
                EmitLine(instr.Operands.Count > 0
                    ? $"stream{FormatSrc(instr.Operands[0])}.Append(); stream{FormatSrc(instr.Operands[0])}.RestartStrip(); // emitthencut_stream"
                    : "stream.Append(); stream.RestartStrip(); // emitthencut_stream");
                break;

            // Interface call (rare dynamic shader linking)
            case OpcodeType.InterfaceCall:
                EmitLine($"// interface_call (dynamic shader linking, {instr.Operands.Count} operands)");
                break;

            // Buffer info
            case OpcodeType.BufInfo:
                if (instr.Operands.Count >= 2)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var buf = FormatSrc(instr.Operands[1]);
                    EmitLine($"{buf}.GetDimensions({dst}); // bufinfo");
                }

                break;

            // Gather variants
            case OpcodeType.Gather4C:
                if (instr.Operands.Count >= 5)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var coords = FormatSrc(instr.Operands[1]);
                    var tex = FormatResourceName(instr.Operands[2]);
                    var samp = FormatSamplerName(instr.Operands[3]);
                    var cmpVal = FormatSrc(instr.Operands[4]);
                    EmitLine($"{dst} = {tex}.GatherCmp({samp}, {coords}, {cmpVal});");
                }

                break;
            case OpcodeType.Gather4Po:
                if (instr.Operands.Count >= 5)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var coords = FormatSrc(instr.Operands[1]);
                    var offset = FormatSrc(instr.Operands[2]);
                    var tex = FormatResourceName(instr.Operands[3]);
                    var samp = FormatSamplerName(instr.Operands[4]);
                    EmitLine($"{dst} = {tex}.Gather({samp}, {coords}, {offset});");
                }

                break;
            case OpcodeType.Gather4PoC:
                if (instr.Operands.Count >= 6)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var coords = FormatSrc(instr.Operands[1]);
                    var offset = FormatSrc(instr.Operands[2]);
                    var tex = FormatResourceName(instr.Operands[3]);
                    var samp = FormatSamplerName(instr.Operands[4]);
                    var cmpVal = FormatSrc(instr.Operands[5]);
                    EmitLine($"{dst} = {tex}.GatherCmp({samp}, {coords}, {cmpVal}, {offset});");
                }

                break;

            // Sample position / info
            case OpcodeType.SamplePos:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = GetRenderTargetSamplePosition({FormatSrc(instr.Operands[2])}); // sample_pos");
                break;
            case OpcodeType.SampleInfo:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = GetRenderTargetSampleCount(); // sample_info");
                break;

            // LOD calculation
            case OpcodeType.Lod:
                if (instr.Operands.Count >= 4)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var coords = FormatSrc(instr.Operands[1]);
                    var tex = FormatResourceName(instr.Operands[2]);
                    var samp = FormatSamplerName(instr.Operands[3]);
                    EmitLine($"{dst} = {tex}.CalculateLevelOfDetail({samp}, {coords});");
                }

                break;

            // Raw/structured buffer loads and stores
            case OpcodeType.LdRaw:
                if (instr.Operands.Count >= 3)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var byteOffset = FormatSrc(instr.Operands[1]);
                    var buf = FormatSrc(instr.Operands[2]);
                    EmitLine($"{dst} = {buf}.Load({byteOffset});");
                }

                break;
            case OpcodeType.StoreRaw:
                if (instr.Operands.Count >= 3)
                {
                    var dst = FormatSrc(instr.Operands[0]);
                    var byteOffset = FormatSrc(instr.Operands[1]);
                    var value = FormatSrc(instr.Operands[2]);
                    EmitLine($"{dst}.Store({byteOffset}, {value});");
                }

                break;
            case OpcodeType.LdStructured:
                if (instr.Operands.Count >= 4)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var index = FormatSrc(instr.Operands[1]);
                    var offset = FormatSrc(instr.Operands[2]);
                    var buf = FormatSrc(instr.Operands[3]);
                    EmitLine($"{dst} = {buf}.Load({index}, {offset}); // ld_structured");
                }

                break;
            case OpcodeType.StoreStructured:
                if (instr.Operands.Count >= 4)
                {
                    var dst = FormatSrc(instr.Operands[0]);
                    var index = FormatSrc(instr.Operands[1]);
                    var offset = FormatSrc(instr.Operands[2]);
                    var value = FormatSrc(instr.Operands[3]);
                    EmitLine($"{dst}.Store({index}, {offset}, {value}); // store_structured");
                }

                break;
            case OpcodeType.LdUavTyped:
                if (instr.Operands.Count >= 3)
                {
                    var dst = FormatDst(instr.Operands[0]);
                    var coord = FormatSrc(instr.Operands[1]);
                    var uav = FormatSrc(instr.Operands[2]);
                    EmitLine($"{dst} = {uav}[{coord}];");
                }

                break;
            case OpcodeType.StoreUavTyped:
                if (instr.Operands.Count >= 3)
                {
                    var uav = FormatSrc(instr.Operands[0]);
                    var coord = FormatSrc(instr.Operands[1]);
                    var value = FormatSrc(instr.Operands[2]);
                    EmitLine($"{uav}[{coord}] = {value};");
                }

                break;

            // Sync/barrier
            case OpcodeType.Sync:
            {
                var syncFlags = instr.ControlBits;
                var globalBarrier = (syncFlags & 0x2) != 0;
                var groupSync = (syncFlags & 0x1) != 0;
                var uavGlobal = (syncFlags & 0x4) != 0;
                var tgsm = (syncFlags & 0x8) != 0;
                if (groupSync && tgsm)
                    EmitLine("GroupMemoryBarrierWithGroupSync();");
                else if (groupSync && uavGlobal)
                    EmitLine("DeviceMemoryBarrierWithGroupSync();");
                else if (groupSync)
                    EmitLine("AllMemoryBarrierWithGroupSync();");
                else if (tgsm)
                    EmitLine("GroupMemoryBarrier();");
                else if (uavGlobal)
                    EmitLine("DeviceMemoryBarrier();");
                else
                    EmitLine("AllMemoryBarrier();");
            }
                break;

            // Atomic ops (no return value)
            case OpcodeType.AtomicAnd:
                EmitAtomicOp(instr, "InterlockedAnd");
                break;
            case OpcodeType.AtomicOr:
                EmitAtomicOp(instr, "InterlockedOr");
                break;
            case OpcodeType.AtomicXor:
                EmitAtomicOp(instr, "InterlockedXor");
                break;
            case OpcodeType.AtomicIAdd:
                EmitAtomicOp(instr, "InterlockedAdd");
                break;
            case OpcodeType.AtomicIMax:
                EmitAtomicOp(instr, "InterlockedMax");
                break;
            case OpcodeType.AtomicIMin:
                EmitAtomicOp(instr, "InterlockedMin");
                break;
            case OpcodeType.AtomicUMax:
                EmitAtomicOp(instr, "InterlockedMax");
                break;
            case OpcodeType.AtomicUMin:
                EmitAtomicOp(instr, "InterlockedMin");
                break;
            case OpcodeType.AtomicCmpStore:
                if (instr.Operands.Count >= 4)
                    EmitLine(
                        $"InterlockedCompareStore({FormatSrc(instr.Operands[0])}, {FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}, {FormatSrc(instr.Operands[3])});");
                break;

            // Immediate atomic ops (with return value in first operand)
            case OpcodeType.ImmAtomicIAdd:
                EmitImmAtomicOp(instr, "InterlockedAdd");
                break;
            case OpcodeType.ImmAtomicAnd:
                EmitImmAtomicOp(instr, "InterlockedAnd");
                break;
            case OpcodeType.ImmAtomicOr:
                EmitImmAtomicOp(instr, "InterlockedOr");
                break;
            case OpcodeType.ImmAtomicXor:
                EmitImmAtomicOp(instr, "InterlockedXor");
                break;
            case OpcodeType.ImmAtomicExch:
                EmitImmAtomicOp(instr, "InterlockedExchange");
                break;
            case OpcodeType.ImmAtomicIMax:
                EmitImmAtomicOp(instr, "InterlockedMax");
                break;
            case OpcodeType.ImmAtomicIMin:
                EmitImmAtomicOp(instr, "InterlockedMin");
                break;
            case OpcodeType.ImmAtomicUMax:
                EmitImmAtomicOp(instr, "InterlockedMax");
                break;
            case OpcodeType.ImmAtomicUMin:
                EmitImmAtomicOp(instr, "InterlockedMin");
                break;
            case OpcodeType.ImmAtomicCmpExch:
                if (instr.Operands.Count >= 5)
                    EmitLine(
                        $"InterlockedCompareExchange({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}, {FormatSrc(instr.Operands[3])}, {FormatSrc(instr.Operands[4])}, {FormatDst(instr.Operands[0])});");
                break;

            // Atomic alloc/consume (append/consume buffer)
            case OpcodeType.ImmAtomicAlloc:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[1])}.IncrementCounter();");
                break;
            case OpcodeType.ImmAtomicConsume:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[1])}.DecrementCounter();");
                break;

            // Double-precision ops
            case OpcodeType.DAdd:
                EmitBinaryOp(instr, "+", sat, satEnd);
                break;
            case OpcodeType.DMul:
                EmitBinaryOp(instr, "*", sat, satEnd);
                break;
            case OpcodeType.DMax:
                EmitBinaryFunc(instr, "max", sat, satEnd);
                break;
            case OpcodeType.DMin:
                EmitBinaryFunc(instr, "min", sat, satEnd);
                break;
            case OpcodeType.DEq:
                EmitBinaryOp(instr, "==");
                break;
            case OpcodeType.DGe:
                EmitBinaryOp(instr, ">=");
                break;
            case OpcodeType.DLt:
                EmitBinaryOp(instr, "<");
                break;
            case OpcodeType.DNe:
                EmitBinaryOp(instr, "!=");
                break;
            case OpcodeType.DMov:
                if (instr.Operands.Count >= 2)
                    EmitLine($"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[1])}; // dmov");
                break;
            case OpcodeType.DMovc:
                if (instr.Operands.Count >= 4)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = {FormatSrc(instr.Operands[1])} ? {FormatSrc(instr.Operands[2])} : {FormatSrc(instr.Operands[3])}; // dmovc");
                break;
            case OpcodeType.DtoF:
                EmitCast(instr, "(float)");
                break;
            case OpcodeType.FtoD:
                EmitCast(instr, "(double)");
                break;

            // Eval ops (pixel shader)
            case OpcodeType.EvalSnapped:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = EvaluateAttributeSnapped({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])});");
                break;
            case OpcodeType.EvalSampleIndex:
                if (instr.Operands.Count >= 3)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = EvaluateAttributeAtSample({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])});");
                break;
            case OpcodeType.EvalCentroid:
                if (instr.Operands.Count >= 2)
                    EmitLine(
                        $"{FormatDst(instr.Operands[0])} = EvaluateAttributeAtCentroid({FormatSrc(instr.Operands[1])});");
                break;

            default:
                // Unknown opcode - emit as comment
                EmitLine($"// {instr.Opcode} (unhandled, {instr.Operands.Count} operands)");
                break;
        }
    }

    private void EmitBinaryOp(Instruction instr, string op, string sat = "", string satEnd = "")
    {
        if (instr.Operands.Count >= 3)
            EmitLine(
                $"{FormatDst(instr.Operands[0])} = {sat}{FormatSrc(instr.Operands[1])} {op} {FormatSrc(instr.Operands[2])}{satEnd};");
    }

    private void EmitUnaryFunc(Instruction instr, string func, string sat = "", string satEnd = "")
    {
        if (instr.Operands.Count >= 2)
            EmitLine($"{FormatDst(instr.Operands[0])} = {sat}{func}({FormatSrc(instr.Operands[1])}){satEnd};");
    }

    private void EmitBinaryFunc(Instruction instr, string func, string sat = "", string satEnd = "")
    {
        if (instr.Operands.Count >= 3)
            EmitLine(
                $"{FormatDst(instr.Operands[0])} = {sat}{func}({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}){satEnd};");
    }

    private void EmitCast(Instruction instr, string type)
    {
        if (instr.Operands.Count >= 2)
            EmitLine($"{FormatDst(instr.Operands[0])} = ({type}){FormatSrc(instr.Operands[1])};");
    }

    private void EmitAtomicOp(Instruction instr, string func)
    {
        // atomic_xxx dst_uav, dst_address, src0
        if (instr.Operands.Count >= 3)
            EmitLine(
                $"{func}({FormatSrc(instr.Operands[0])}, {FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])});");
    }

    private void EmitImmAtomicOp(Instruction instr, string func)
    {
        // imm_atomic_xxx dst_return, dst_uav, dst_address, src0
        if (instr.Operands.Count >= 4)
            EmitLine(
                $"{func}({FormatSrc(instr.Operands[1])}, {FormatSrc(instr.Operands[2])}, {FormatSrc(instr.Operands[3])}, {FormatDst(instr.Operands[0])});");
    }

    private void EmitSample(Instruction instr, string method)
    {
        if (instr.Operands.Count >= 4)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            EmitLine($"{dst} = {tex}.{method}({samp}, {coords});");
        }
    }

    private void EmitSampleLod(Instruction instr)
    {
        if (instr.Operands.Count >= 5)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            var lod = FormatSrc(instr.Operands[4]);
            EmitLine($"{dst} = {tex}.SampleLevel({samp}, {coords}, {lod});");
        }
    }

    private void EmitSampleBias(Instruction instr)
    {
        if (instr.Operands.Count >= 5)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            var bias = FormatSrc(instr.Operands[4]);
            EmitLine($"{dst} = {tex}.SampleBias({samp}, {coords}, {bias});");
        }
    }

    private void EmitSampleCmp(Instruction instr)
    {
        if (instr.Operands.Count >= 5)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            var cmpVal = FormatSrc(instr.Operands[4]);
            EmitLine($"{dst} = {tex}.SampleCmp({samp}, {coords}, {cmpVal});");
        }
    }

    private void EmitSampleCmpLevelZero(Instruction instr)
    {
        if (instr.Operands.Count >= 5)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            var cmpVal = FormatSrc(instr.Operands[4]);
            EmitLine($"{dst} = {tex}.SampleCmpLevelZero({samp}, {coords}, {cmpVal});");
        }
    }

    private void EmitSampleGrad(Instruction instr)
    {
        if (instr.Operands.Count >= 6)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            var ddx = FormatSrc(instr.Operands[4]);
            var ddy = FormatSrc(instr.Operands[5]);
            EmitLine($"{dst} = {tex}.SampleGrad({samp}, {coords}, {ddx}, {ddy});");
        }
    }

    private void EmitLoad(Instruction instr)
    {
        if (instr.Operands.Count >= 3)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            EmitLine($"{dst} = {tex}.Load({coords});");
        }
    }

    private void EmitLoadMs(Instruction instr)
    {
        if (instr.Operands.Count >= 4)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var sample = FormatSrc(instr.Operands[3]);
            EmitLine($"{dst} = {tex}.Load({coords}, {sample});");
        }
    }

    private void EmitGather(Instruction instr)
    {
        if (instr.Operands.Count >= 4)
        {
            var dst = FormatDst(instr.Operands[0]);
            var coords = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            var samp = FormatSamplerName(instr.Operands[3]);
            EmitLine($"{dst} = {tex}.Gather({samp}, {coords});");
        }
    }

    private void EmitResInfo(Instruction instr)
    {
        if (instr.Operands.Count >= 3)
        {
            var dst = FormatDst(instr.Operands[0]);
            var mipLevel = FormatSrc(instr.Operands[1]);
            var tex = FormatResourceName(instr.Operands[2]);
            EmitLine($"{tex}.GetDimensions({mipLevel}, {dst}); // resinfo");
        }
    }
}
