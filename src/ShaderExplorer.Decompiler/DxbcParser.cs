using ShaderExplorer.Core.Models;
using ShaderExplorer.Decompiler.Chunks;
using ComponentType = ShaderExplorer.Decompiler.Chunks.ComponentType;
using SignatureElement = ShaderExplorer.Decompiler.Chunks.SignatureElement;

namespace ShaderExplorer.Decompiler;

public class DxbcParser
{
    public ShaderInfo Parse(byte[] data)
    {
        var container = ParseContainer(data);
        return BuildShaderInfo(container);
    }

    public DxbcContainer ParseContainer(byte[] data)
    {
        var reader = new ByteReader(data);
        var container = new DxbcContainer();

        // Header: "DXBC" magic
        var magic = reader.ReadUInt32();
        if (magic != 0x43425844) // "DXBC" in little-endian
            throw new InvalidDataException($"Invalid DXBC magic: 0x{magic:X8}");

        // 16-byte hash (4 x uint32)
        container.Hash = reader.ReadBytes(16).ToArray();
        container.Version = reader.ReadUInt32();
        container.TotalSize = reader.ReadUInt32();
        var chunkCount = reader.ReadUInt32();

        // Chunk offsets
        var offsets = new uint[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            offsets[i] = reader.ReadUInt32();

        // Parse each chunk
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkStart = (int)offsets[i];
            var chunkReader = new ByteReader(data);
            chunkReader.Position = chunkStart;

            var fourccRaw = chunkReader.ReadUInt32();
            var fourcc = Encoding.ASCII.GetString(BitConverter.GetBytes(fourccRaw));
            var chunkSize = chunkReader.ReadUInt32();
            var dataStart = chunkReader.Position;

            var chunk = new DxbcChunk { FourCC = fourcc, Size = chunkSize, DataOffset = dataStart };
            container.Chunks.Add(chunk);

            var chunkData = data.AsSpan(dataStart, (int)chunkSize);

            switch (fourcc)
            {
                case "RDEF":
                    container.ResourceDefinitions = ParseRdef(chunkData, dataStart);
                    break;
                case "ISGN":
                case "ISG1":
                    container.InputSignature = ParseSignature(chunkData, fourcc, dataStart);
                    break;
                case "OSGN":
                case "OSG1":
                    container.OutputSignature = ParseSignature(chunkData, fourcc, dataStart);
                    break;
                case "PCSG":
                case "PSG1":
                    container.PatchConstantSignature = ParseSignature(chunkData, fourcc, dataStart);
                    break;
                case "SHDR":
                case "SHEX":
                    container.ShaderProgram = ParseShaderProgram(chunkData);
                    break;
                case "STAT":
                    container.Statistics = ParseStat(chunkData);
                    break;
                case "DXIL":
                case "ILDB":
                    container.DxilChunkType = fourcc;
                    container.DxilChunkData = chunkData.ToArray();
                    break;
                case "ILDN":
                    // ILDN is DXIL-format debug info (PDB), not bytecode.
                    // The actual code is in SHEX/SHDR. Don't store as DxilChunkData.
                    break;
                case "SPDB":
                    container.SpdbData = chunkData.ToArray();
                    break;
            }
        }

        return container;
    }

    private RdefChunk ParseRdef(ReadOnlySpan<byte> data, int chunkBase)
    {
        var reader = new ByteReader(data);
        var rdef = new RdefChunk();

        var cbCount = reader.ReadUInt32();
        var cbOffset = reader.ReadUInt32();
        var bindingCount = reader.ReadUInt32();
        var bindingOffset = reader.ReadUInt32();
        rdef.TargetVersion = reader.ReadUInt32();
        rdef.Flags = reader.ReadUInt32();

        var creatorOffset = reader.ReadUInt32();
        rdef.Creator = reader.ReadStringAtOffset(0, (int)creatorOffset);

        // Parse resource bindings
        reader.Position = (int)bindingOffset;
        for (var i = 0; i < bindingCount; i++)
        {
            var binding = new RdefResourceBinding();
            var nameOffset = reader.ReadUInt32();
            binding.Name = reader.ReadStringAtOffset(0, (int)nameOffset);
            binding.Type = (RdefShaderInputType)reader.ReadUInt32();
            binding.ReturnType = (RdefResourceReturnType)reader.ReadUInt32();
            binding.Dimension = (RdefResourceDimension)reader.ReadUInt32();
            binding.NumSamples = reader.ReadUInt32();
            binding.BindPoint = reader.ReadUInt32();
            binding.BindCount = reader.ReadUInt32();
            binding.Flags = reader.ReadUInt32();
            rdef.ResourceBindings.Add(binding);
        }

        // Parse constant buffers
        reader.Position = (int)cbOffset;
        for (var i = 0; i < cbCount; i++)
        {
            var cb = new RdefConstantBuffer();
            var nameOffset = reader.ReadUInt32();
            cb.Name = reader.ReadStringAtOffset(0, (int)nameOffset);
            var varCount = reader.ReadUInt32();
            var varOffset = reader.ReadUInt32();
            cb.Size = reader.ReadUInt32();
            cb.Flags = reader.ReadUInt32();
            cb.Type = reader.ReadUInt32();

            var savedPos = reader.Position;
            reader.Position = (int)varOffset;

            for (var v = 0; v < varCount; v++)
            {
                var variable = new RdefVariable();
                var varNameOffset = reader.ReadUInt32();
                variable.Name = reader.ReadStringAtOffset(0, (int)varNameOffset);
                variable.StartOffset = reader.ReadUInt32();
                variable.Size = reader.ReadUInt32();
                variable.Flags = reader.ReadUInt32();
                var typeOffset = reader.ReadUInt32();
                variable.StartTexture = reader.ReadUInt32();
                variable.TextureSize = reader.ReadUInt32();
                variable.StartSampler = reader.ReadUInt32();
                variable.SamplerSize = reader.ReadUInt32();

                // Unused default value offset
                var defaultValueOffset = reader.ReadUInt32();

                var varSavedPos = reader.Position;
                variable.Type = ParseRdefType(data, (int)typeOffset);
                reader.Position = varSavedPos;

                cb.Variables.Add(variable);
            }

            reader.Position = savedPos;
            rdef.ConstantBuffers.Add(cb);
        }

        return rdef;
    }

    private RdefType ParseRdefType(ReadOnlySpan<byte> data, int offset)
    {
        var reader = new ByteReader(data);
        reader.Position = offset;

        var type = new RdefType();
        type.Class = (RdefVariableClass)reader.ReadUInt16();
        type.Type = (RdefVariableType)reader.ReadUInt16();
        type.Rows = reader.ReadUInt16();
        type.Columns = reader.ReadUInt16();
        type.Elements = reader.ReadUInt16();
        type.MemberCount = reader.ReadUInt16();
        var memberOffset = reader.ReadUInt32();

        // For SM5.0+ there may be additional fields here, skip them for now

        if (type.MemberCount > 0 && memberOffset > 0)
        {
            reader.Position = (int)memberOffset;
            for (var m = 0; m < type.MemberCount; m++)
            {
                var member = new RdefStructMember();
                var nameOff = reader.ReadUInt32();
                member.Name = reader.ReadStringAtOffset(0, (int)nameOff);
                var memberTypeOff = reader.ReadUInt32();
                member.Offset = reader.ReadUInt32();

                var savedPos = reader.Position;
                member.Type = ParseRdefType(data, (int)memberTypeOff);
                reader.Position = savedPos;

                type.Members.Add(member);
            }
        }

        return type;
    }

    private SignatureChunk ParseSignature(ReadOnlySpan<byte> data, string chunkType, int chunkBase)
    {
        var reader = new ByteReader(data);
        var sig = new SignatureChunk { ChunkType = chunkType };

        var elementCount = reader.ReadUInt32();
        var unknown = reader.ReadUInt32(); // always 8

        var isExtended = chunkType is "ISG1" or "OSG1" or "PSG1";

        for (uint i = 0; i < elementCount; i++)
        {
            var element = new SignatureElement();

            if (isExtended) element.Stream = reader.ReadUInt32();

            var nameOffset = reader.ReadUInt32();
            element.SemanticName = reader.ReadStringAtOffset(0, (int)nameOffset);
            element.SemanticIndex = reader.ReadUInt32();
            element.SystemValueType = reader.ReadUInt32();
            element.ComponentType = (ComponentType)reader.ReadUInt32();
            element.Register = reader.ReadUInt32();
            var mask = reader.ReadByte();
            var rwMask = reader.ReadByte();
            element.Mask = mask;
            element.ReadWriteMask = rwMask;
            reader.Skip(2); // padding

            if (isExtended) element.MinPrecision = reader.ReadUInt32();

            sig.Elements.Add(element);
        }

        return sig;
    }

    private ShaderProgramChunk ParseShaderProgram(ReadOnlySpan<byte> data)
    {
        var reader = new ByteReader(data);
        var program = new ShaderProgramChunk();

        // Version token
        var versionToken = reader.ReadUInt32();
        program.MinorVersion = (int)(versionToken & 0xF);
        program.MajorVersion = (int)((versionToken >> 4) & 0xF);
        var typeCode = (versionToken >> 16) & 0xFFFF;
        program.ShaderType = typeCode switch
        {
            0x0000 => ShaderType.Pixel,
            0x0001 => ShaderType.Vertex,
            0x0002 => ShaderType.Geometry,
            0x0003 => ShaderType.Hull,
            0x0004 => ShaderType.Domain,
            0x0005 => ShaderType.Compute,
            _ => ShaderType.Unknown
        };

        // Length token (in DWORDs including version + length tokens)
        program.Length = reader.ReadUInt32();

        var endOffset = (int)program.Length * 4;

        // Parse instructions
        while (reader.Position < endOffset && reader.Remaining >= 4)
        {
            var instr = ParseInstruction(ref reader, endOffset);
            if (instr != null)
                program.Instructions.Add(instr);
        }

        return program;
    }

    private Instruction? ParseInstruction(ref ByteReader reader, int endOffset)
    {
        var startPos = reader.Position;
        var opcodeToken = reader.ReadUInt32();

        var instr = new Instruction();
        instr.Opcode = (OpcodeType)(opcodeToken & 0x7FF); // bits [10:0]
        instr.IsSaturated = ((opcodeToken >> 13) & 1) != 0;
        instr.ControlBits = (opcodeToken >> 11) & 0x1FFF; // bits [23:11]
        instr.Length = (opcodeToken >> 24) & 0x7F;        // bits [30:24]
        instr.IsExtended = ((opcodeToken >> 31) & 1) != 0;

        // CustomData has different layout
        if (instr.Opcode == OpcodeType.CustomData)
        {
            var customLength = reader.ReadUInt32(); // length in DWORDs including opcode token
            if (customLength > 2)
                reader.Skip((int)(customLength - 2) * 4);
            return instr;
        }

        // If length is 0, it's encoded as the next DWORD (for very long instructions)
        if (instr.Length == 0) instr.Length = reader.ReadUInt32();

        var instrEndPos = startPos + (int)instr.Length * 4;
        if (instrEndPos > endOffset)
            instrEndPos = endOffset;

        // Parse extended opcode tokens
        var hasExtended = instr.IsExtended;
        while (hasExtended && reader.Position < instrEndPos)
        {
            var extToken = reader.ReadUInt32();
            var ext = new ExtendedToken();
            ext.RawToken = extToken;
            ext.Type = (ExtendedOpcodeType)(extToken & 0x3F);
            hasExtended = ((extToken >> 31) & 1) != 0;

            switch (ext.Type)
            {
                case ExtendedOpcodeType.SampleControls:
                    ext.OffsetU = SignExtend4((int)((extToken >> 9) & 0xF));
                    ext.OffsetV = SignExtend4((int)((extToken >> 13) & 0xF));
                    ext.OffsetW = SignExtend4((int)((extToken >> 17) & 0xF));
                    break;
                case ExtendedOpcodeType.ResourceDim:
                    ext.ResourceDim = (byte)((extToken >> 6) & 0x1F);
                    break;
                case ExtendedOpcodeType.ResourceReturnType:
                    ext.ReturnTypeX = (byte)((extToken >> 6) & 0xF);
                    ext.ReturnTypeY = (byte)((extToken >> 10) & 0xF);
                    ext.ReturnTypeZ = (byte)((extToken >> 14) & 0xF);
                    ext.ReturnTypeW = (byte)((extToken >> 18) & 0xF);
                    break;
            }

            instr.ExtendedTokens.Add(ext);
        }

        // Parse operands based on opcode
        ParseOpcodeSpecific(ref reader, instr, instrEndPos);

        // Make sure we advance to end of instruction
        if (reader.Position < instrEndPos)
            reader.Position = instrEndPos;

        return instr;
    }

    private void ParseOpcodeSpecific(ref ByteReader reader, Instruction instr, int instrEnd)
    {
        if (IsDclNoOperands(instr.Opcode))
            return;
        if (TryParseDclScalarData(ref reader, instr, instrEnd))
            return;
        ParseOperandsUntilEnd(ref reader, instr, instrEnd);
    }

    private static bool IsDclNoOperands(OpcodeType op)
    {
        return op is OpcodeType.HsDecls
            or OpcodeType.HsControlPointPhase
            or OpcodeType.HsForkPhase
            or OpcodeType.HsJoinPhase
            or OpcodeType.DclTessDomain
            or OpcodeType.DclTessPartitioning
            or OpcodeType.DclTessOutputPrimitive
            or OpcodeType.DclInputControlPointCount
            or OpcodeType.DclOutputControlPointCount;
    }

    private static bool TryParseDclScalarData(ref ByteReader reader, Instruction instr, int instrEnd)
    {
        switch (instr.Opcode)
        {
            case OpcodeType.DclGlobalFlags:
                instr.GlobalFlags = instr.ControlBits;
                return true;

            case OpcodeType.DclTemps:
                if (reader.Position + 4 <= instrEnd)
                    instr.TempRegCount = reader.ReadUInt32();
                return true;

            case OpcodeType.DclIndexableTemp:
                if (reader.Position + 12 <= instrEnd)
                {
                    instr.TempRegIndex = reader.ReadUInt32();
                    instr.TempRegCount = reader.ReadUInt32();
                    instr.TempRegComponents = reader.ReadUInt32();
                }
                return true;

            case OpcodeType.DclMaxOutputVertexCount:
                if (reader.Position + 4 <= instrEnd)
                    reader.ReadUInt32();
                return true;

            case OpcodeType.DclThreadGroup:
                if (reader.Position + 12 <= instrEnd)
                {
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                }
                return true;

            case OpcodeType.DclHsMaxTessFactor:
            case OpcodeType.DclHsForkPhaseInstanceCount:
            case OpcodeType.DclHsJoinPhaseInstanceCount:
            case OpcodeType.DclGsInstanceCount:
                if (reader.Position + 4 <= instrEnd)
                    reader.ReadUInt32();
                return true;

            default:
                return false;
        }
    }

    private void ParseOperandsUntilEnd(ref ByteReader reader, Instruction instr, int instrEnd)
    {
        while (reader.Position < instrEnd)
        {
            var operand = ParseOperand(ref reader, instrEnd);
            if (operand != null)
                instr.Operands.Add(operand);
            else
                break;
        }
    }

    private Operand? ParseOperand(ref ByteReader reader, int instrEnd)
    {
        if (reader.Position + 4 > instrEnd)
            return null;

        var token = reader.ReadUInt32();
        var operand = new Operand();

        // Bits [1:0] - number of components
        var numComp = token & 0x3;
        operand.NumComponents = numComp switch
        {
            0 => 0,
            1 => 1,
            2 => 4,
            3 => 0, // N (for some special operands)
            _ => 0
        };

        // Bits [3:2] - selection mode (only valid for 4-component)
        if (numComp == 2)
        {
            operand.SelectionMode = (SelectionMode)((token >> 2) & 0x3);

            switch (operand.SelectionMode)
            {
                case SelectionMode.Mask:
                    operand.WriteMask = (byte)((token >> 4) & 0xF);
                    break;
                case SelectionMode.Swizzle:
                    operand.SwizzleX = (byte)((token >> 4) & 0x3);
                    operand.SwizzleY = (byte)((token >> 6) & 0x3);
                    operand.SwizzleZ = (byte)((token >> 8) & 0x3);
                    operand.SwizzleW = (byte)((token >> 10) & 0x3);
                    break;
                case SelectionMode.Select1:
                    operand.SelectComponent = (byte)((token >> 4) & 0x3);
                    break;
            }
        }

        // Bits [19:12] - operand type
        operand.Type = (OperandType)((token >> 12) & 0xFF);

        // Bits [21:20] - index dimension
        operand.IndexDimension = (IndexDimension)((token >> 20) & 0x3);

        // Bits [24:22], [27:25], [30:28] - index representations for each dimension
        var dimCount = (int)operand.IndexDimension;
        for (var d = 0; d < dimCount; d++)
            operand.IndexRepresentations[d] = (IndexRepresentation)((token >> (22 + d * 3)) & 0x7);

        // Bit [31] - extended
        operand.IsExtended = ((token >> 31) & 1) != 0;

        // Parse extended operand token (modifiers)
        if (operand.IsExtended)
        {
            if (reader.Position + 4 > instrEnd) return operand;
            var extToken = reader.ReadUInt32();
            var modType = (extToken >> 6) & 0x3;
            operand.Modifier = (OperandModifier)modType;
        }

        // Handle immediate values
        if (operand.Type == OperandType.Immediate32)
        {
            var count = operand.NumComponents == 4 ? 4 : 1;
            for (var i = 0; i < count && reader.Position + 4 <= instrEnd; i++)
            {
                var raw = reader.ReadUInt32();
                operand.ImmediateValues[i] = BitConverter.Int32BitsToSingle((int)raw);
                operand.ImmediateValuesInt[i] = (int)raw;
            }

            return operand;
        }

        if (operand.Type == OperandType.Immediate64)
        {
            var count = operand.NumComponents == 4 ? 4 : 1;
            for (var i = 0; i < count && reader.Position + 8 <= instrEnd; i++)
            {
                reader.ReadUInt32(); // low
                reader.ReadUInt32(); // high
            }

            return operand;
        }

        // Parse indices
        for (var d = 0; d < dimCount; d++)
        {
            operand.Indices[d] = new OperandIndex();
            switch (operand.IndexRepresentations[d])
            {
                case IndexRepresentation.Immediate32:
                    if (reader.Position + 4 <= instrEnd)
                        operand.Indices[d].Value = reader.ReadUInt32();
                    break;
                case IndexRepresentation.Immediate64:
                    if (reader.Position + 8 <= instrEnd)
                    {
                        var lo = reader.ReadUInt32();
                        var hi = reader.ReadUInt32();
                        operand.Indices[d].Value = ((ulong)hi << 32) | lo;
                    }

                    break;
                case IndexRepresentation.Relative:
                    operand.Indices[d].RelativeOperand = ParseOperand(ref reader, instrEnd);
                    break;
                case IndexRepresentation.Immediate32PlusRelative:
                    if (reader.Position + 4 <= instrEnd)
                        operand.Indices[d].Value = reader.ReadUInt32();
                    operand.Indices[d].RelativeOperand = ParseOperand(ref reader, instrEnd);
                    break;
            }
        }

        return operand;
    }

    private static int SignExtend4(int value)
    {
        if ((value & 0x8) != 0)
            return value | unchecked((int)0xFFFFFFF0);
        return value;
    }

    private StatChunk ParseStat(ReadOnlySpan<byte> data)
    {
        var reader = new ByteReader(data);
        var stat = new StatChunk();

        stat.InstructionCount = reader.ReadUInt32();
        stat.TempRegisterCount = reader.ReadUInt32();
        stat.DefCount = reader.ReadUInt32();
        stat.DclCount = reader.ReadUInt32();
        stat.FloatInstructionCount = reader.ReadUInt32();
        stat.IntInstructionCount = reader.ReadUInt32();
        stat.UIntInstructionCount = reader.ReadUInt32();
        stat.StaticFlowControlCount = reader.ReadUInt32();
        stat.DynamicFlowControlCount = reader.ReadUInt32();
        // There are more fields in STAT but these are the most useful
        if (reader.Remaining >= 4) stat.EmitInstructionCount = reader.ReadUInt32();
        if (reader.Remaining >= 4) stat.TempArrayCount = reader.ReadUInt32();
        if (reader.Remaining >= 4) stat.ArrayInstructionCount = reader.ReadUInt32();
        if (reader.Remaining >= 4) stat.CutInstructionCount = reader.ReadUInt32();

        return stat;
    }

    private ShaderInfo BuildShaderInfo(DxbcContainer container)
    {
        var info = new ShaderInfo();

        if (container.ShaderProgram != null)
        {
            info.Type = container.ShaderProgram.ShaderType;
            info.MajorVersion = container.ShaderProgram.MajorVersion;
            info.MinorVersion = container.ShaderProgram.MinorVersion;
        }
        else if (container.DxilChunkData != null && container.DxilChunkData.Length >= 8)
        {
            // DXIL program header: first 4 bytes are the version token (same layout as SHDR)
            // followed by 4 bytes for the size, then the DXIL bitcode
            var versionToken = BitConverter.ToUInt32(container.DxilChunkData, 0);
            info.MinorVersion = (int)(versionToken & 0xF);
            info.MajorVersion = (int)((versionToken >> 4) & 0xF);
            var typeCode = (versionToken >> 16) & 0xFFFF;
            info.Type = typeCode switch
            {
                0x0000 => ShaderType.Pixel,
                0x0001 => ShaderType.Vertex,
                0x0002 => ShaderType.Geometry,
                0x0003 => ShaderType.Hull,
                0x0004 => ShaderType.Domain,
                0x0005 => ShaderType.Compute,
                _ => ShaderType.Unknown
            };
            info.IsDxil = true;
        }

        // Build input signature
        if (container.InputSignature != null)
            foreach (var elem in container.InputSignature.Elements)
                info.InputSignature.Add(new Core.Models.SignatureElement
                {
                    SemanticName = elem.SemanticName,
                    SemanticIndex = (int)elem.SemanticIndex,
                    Register = (int)elem.Register,
                    ComponentType = (Core.Models.ComponentType)(int)elem.ComponentType,
                    Mask = elem.Mask,
                    ReadWriteMask = elem.ReadWriteMask,
                    SystemValue = (SystemValueType)elem.SystemValueType,
                    Stream = (int)elem.Stream
                });

        // Build output signature
        if (container.OutputSignature != null)
            foreach (var elem in container.OutputSignature.Elements)
                info.OutputSignature.Add(new Core.Models.SignatureElement
                {
                    SemanticName = elem.SemanticName,
                    SemanticIndex = (int)elem.SemanticIndex,
                    Register = (int)elem.Register,
                    ComponentType = (Core.Models.ComponentType)(int)elem.ComponentType,
                    Mask = elem.Mask,
                    ReadWriteMask = elem.ReadWriteMask,
                    SystemValue = (SystemValueType)elem.SystemValueType,
                    Stream = (int)elem.Stream
                });

        // Build constant buffers from RDEF
        if (container.ResourceDefinitions != null)
        {
            foreach (var cb in container.ResourceDefinitions.ConstantBuffers)
            {
                var cbInfo = new ConstantBufferInfo
                {
                    Name = cb.Name,
                    Size = (int)cb.Size
                };

                // Find register slot from resource bindings
                var binding = container.ResourceDefinitions.ResourceBindings
                    .FirstOrDefault(b => b.Name == cb.Name && b.Type == RdefShaderInputType.CBuffer);
                if (binding != null)
                    cbInfo.RegisterSlot = (int)binding.BindPoint;

                foreach (var v in cb.Variables)
                    cbInfo.Variables.Add(new ConstantVariableInfo
                    {
                        Name = v.Name,
                        Offset = (int)v.StartOffset,
                        Size = (int)v.Size,
                        VariableType = ConvertType(v.Type)
                    });

                info.ConstantBuffers.Add(cbInfo);
            }

            // Build resource bindings (textures, samplers, UAVs)
            foreach (var rb in container.ResourceDefinitions.ResourceBindings)
            {
                if (rb.Type == RdefShaderInputType.CBuffer || rb.Type == RdefShaderInputType.TBuffer)
                    continue; // already handled above

                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = rb.Name,
                    Type = (ResourceType)(int)rb.Type,
                    Dimension = (ResourceDimension)(int)rb.Dimension,
                    BindPoint = (int)rb.BindPoint,
                    BindCount = (int)rb.BindCount,
                    ReturnType = (ResourceReturnType)(int)rb.ReturnType
                });
            }
        }
        else if (container.ShaderProgram != null)
        {
            // No RDEF chunk (e.g., stripped BLS shaders) — synthesize from SHEX declarations
            SynthesizeBindingsFromInstructions(container.ShaderProgram, info);
        }

        return info;
    }

    private static void SynthesizeBindingsFromInstructions(ShaderProgramChunk program, ShaderInfo info)
    {
        foreach (var instr in program.Instructions)
            switch (instr.Opcode)
            {
                case OpcodeType.DclConstantBuffer:
                    SynthesizeCBBinding(instr, info);
                    break;
                case OpcodeType.DclResource:
                case OpcodeType.DclResourceRaw:
                case OpcodeType.DclResourceStructured:
                    SynthesizeSrvBinding(instr, info);
                    break;
                case OpcodeType.DclSampler:
                    SynthesizeSamplerBinding(instr, info);
                    break;
                case OpcodeType.DclUnorderedAccessViewTyped:
                case OpcodeType.DclUnorderedAccessViewRaw:
                case OpcodeType.DclUnorderedAccessViewStructured:
                    SynthesizeUavBinding(instr, info);
                    break;
            }
    }

    private static void SynthesizeCBBinding(Instruction instr, ShaderInfo info)
    {
        if (instr.Operands.Count < 1) return;
        var op = instr.Operands[0];
        if (op.Type != OperandType.ConstantBuffer) return;
        var slot = (int)(op.Indices[0]?.Value ?? 0);
        var size = (int)(op.Indices[1]?.Value ?? 0);

        if (info.ConstantBuffers.Any(c => c.RegisterSlot == slot)) return;

        var cbInfo = new ConstantBufferInfo
        {
            Name = $"cb{slot}",
            RegisterSlot = slot,
            Size = size * 16
        };
        if (size > 0)
            cbInfo.Variables.Add(new ConstantVariableInfo
            {
                Name = $"cb{slot}_data",
                Offset = 0,
                Size = size * 16,
                VariableType = new ShaderVariableType
                {
                    Class = ShaderVariableClass.Vector,
                    Type = ShaderBaseType.Float,
                    Rows = 1,
                    Columns = 4,
                    Elements = size
                }
            });
        info.ConstantBuffers.Add(cbInfo);
    }

    private static void SynthesizeSrvBinding(Instruction instr, ShaderInfo info)
    {
        if (instr.Operands.Count < 1) return;
        var op = instr.Operands[0];
        var slot = (int)(op.Indices[0]?.Value ?? 0);

        switch (instr.Opcode)
        {
            case OpcodeType.DclResource:
                if (op.Type != OperandType.Resource) return;
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"t{slot}", Type = ResourceType.Texture,
                    Dimension = (ResourceDimension)(instr.ControlBits & 0x1F),
                    BindPoint = slot, BindCount = 1,
                    ReturnType = GetReturnTypeFromExtended(instr)
                });
                break;
            case OpcodeType.DclResourceRaw:
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"t{slot}", Type = ResourceType.ByteAddress,
                    Dimension = ResourceDimension.RawBuffer,
                    BindPoint = slot, BindCount = 1
                });
                break;
            case OpcodeType.DclResourceStructured:
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"t{slot}", Type = ResourceType.Structured,
                    Dimension = ResourceDimension.StructuredBuffer,
                    BindPoint = slot, BindCount = 1
                });
                break;
        }
    }

    private static void SynthesizeSamplerBinding(Instruction instr, ShaderInfo info)
    {
        if (instr.Operands.Count < 1) return;
        var op = instr.Operands[0];
        if (op.Type != OperandType.Sampler) return;
        var slot = (int)(op.Indices[0]?.Value ?? 0);

        info.ResourceBindings.Add(new ResourceBindingInfo
        {
            Name = $"s{slot}", Type = ResourceType.Sampler,
            Dimension = ResourceDimension.Unknown,
            BindPoint = slot, BindCount = 1
        });
    }

    private static void SynthesizeUavBinding(Instruction instr, ShaderInfo info)
    {
        if (instr.Operands.Count < 1) return;
        var op = instr.Operands[0];
        var slot = (int)(op.Indices[0]?.Value ?? 0);

        switch (instr.Opcode)
        {
            case OpcodeType.DclUnorderedAccessViewTyped:
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"u{slot}", Type = ResourceType.UAVRWTyped,
                    Dimension = (ResourceDimension)(instr.ControlBits & 0x1F),
                    BindPoint = slot, BindCount = 1,
                    ReturnType = GetReturnTypeFromExtended(instr)
                });
                break;
            case OpcodeType.DclUnorderedAccessViewRaw:
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"u{slot}", Type = ResourceType.UAVRWByteAddress,
                    Dimension = ResourceDimension.RawBuffer,
                    BindPoint = slot, BindCount = 1
                });
                break;
            case OpcodeType.DclUnorderedAccessViewStructured:
                info.ResourceBindings.Add(new ResourceBindingInfo
                {
                    Name = $"u{slot}", Type = ResourceType.UAVRWStructured,
                    Dimension = ResourceDimension.StructuredBuffer,
                    BindPoint = slot, BindCount = 1
                });
                break;
        }
    }

    private static ResourceReturnType GetReturnTypeFromExtended(Instruction instr)
    {
        foreach (var ext in instr.ExtendedTokens)
            if (ext.Type == ExtendedOpcodeType.ResourceReturnType)
                // ReturnTypeX represents the primary return type
                return ext.ReturnTypeX switch
                {
                    1 => ResourceReturnType.UNorm,
                    2 => ResourceReturnType.SNorm,
                    3 => ResourceReturnType.SInt,
                    4 => ResourceReturnType.UInt,
                    5 => ResourceReturnType.Float,
                    6 => ResourceReturnType.Mixed,
                    7 => ResourceReturnType.Double,
                    _ => ResourceReturnType.Float
                };

        return ResourceReturnType.Float;
    }

    private ShaderVariableType ConvertType(RdefType t)
    {
        var result = new ShaderVariableType
        {
            Class = (ShaderVariableClass)(int)t.Class,
            Type = (ShaderBaseType)(int)t.Type,
            Rows = t.Rows,
            Columns = t.Columns,
            Elements = t.Elements
        };

        foreach (var m in t.Members)
            result.Members.Add(new StructMemberInfo
            {
                Name = m.Name,
                Offset = (int)m.Offset,
                Type = ConvertType(m.Type)
            });

        return result;
    }
}