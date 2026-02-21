using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler;

/// <summary>
///     TAFG directory entry: maps a platform tag to a GXSH blob range within the BLS file.
/// </summary>
public readonly struct TafgEntry
{
    public const int Size = 12;

    public readonly string Tag;
    public readonly int Start;
    public readonly int End;

    public TafgEntry(ref ByteReader reader)
    {
        var tagRaw = reader.ReadUInt32();
        Tag = Encoding.ASCII.GetString(BitConverter.GetBytes(tagRaw));
        Start = reader.ReadInt32();
        End = reader.ReadInt32();
    }
}

/// <summary>
///     GXSH/GXSD header: version-dependent header at the start of each platform blob.
///     v12 (28B): magic, version, permOff, permCount, blkOff, blkCount, compOff
///     v13 (32B): magic, version, field2, permOff, permCount, blkOff, field6, compOff
///     v14 (40B): magic, version, platTag, permOff, permCount, blkOff, blkCount, compOff, bcFmt, reserved
/// </summary>
public readonly struct GxshHeader
{
    public readonly uint Magic;
    public readonly int Version;
    public readonly string Tag;
    public readonly int PermTableOffset;
    public readonly int PermCount;
    public readonly int BlockTableOffset;
    public readonly int CompDataOffset;
    public readonly uint BytecodeFormatRaw;

    public int BlockEntryCount => CompDataOffset > BlockTableOffset ? (CompDataOffset - BlockTableOffset) / 4 : 0;
    public int BlockCount => BlockEntryCount > 1 ? BlockEntryCount - 1 : 0;

    public BlsBytecodeFormat BytecodeFormat => (BytecodeFormatRaw >> 16) switch
    {
        1 => BlsBytecodeFormat.DXIL,
        3 => BlsBytecodeFormat.MetalAIR,
        _ when BytecodeFormatRaw != 0 => BlsBytecodeFormat.DXBC,
        _ => BlsBytecodeFormat.Unknown
    };

    public GxshHeader(ref ByteReader reader)
    {
        Magic = reader.ReadUInt32();
        var versionRaw = reader.ReadUInt32();
        Version = (int)(versionRaw & 0xFFFF);
        Tag = "";
        BytecodeFormatRaw = 0;

        switch (Version)
        {
            case >= 14:
            {
                var tagRaw = reader.ReadUInt32();
                Tag = Encoding.ASCII.GetString(BitConverter.GetBytes(tagRaw));
                PermTableOffset = reader.ReadInt32();
                PermCount = reader.ReadInt32();
                BlockTableOffset = reader.ReadInt32();
                reader.Skip(4); // blockCount (derive from offsets)
                CompDataOffset = reader.ReadInt32();
                BytecodeFormatRaw = reader.ReadUInt32();
                // +36 reserved (always 0)
                break;
            }
            case 13:
            {
                reader.Skip(4); // field2
                PermTableOffset = reader.ReadInt32();
                PermCount = reader.ReadInt32();
                BlockTableOffset = reader.ReadInt32();
                reader.Skip(4); // field6
                CompDataOffset = reader.ReadInt32();
                break;
            }
            default:
            {
                PermTableOffset = reader.ReadInt32();
                PermCount = reader.ReadInt32();
                BlockTableOffset = reader.ReadInt32();
                reader.Skip(4); // blockCount (derive from offsets)
                CompDataOffset = reader.ReadInt32();
                break;
            }
        }
    }
}

/// <summary>
///     Permutation table entry: packed offset and decompressed size, plus content hash for v14.
/// </summary>
public readonly struct PermutationEntry
{
    public const int SizeV12 = 8;
    public const int SizeV14 = 24;

    public readonly int PackedOffset;
    public readonly int DecompressedSize;
    public readonly byte[]? ContentHash;

    public PermutationEntry(ref ByteReader reader, int version)
    {
        PackedOffset = reader.ReadInt32();
        DecompressedSize = reader.ReadInt32();
        ContentHash = version >= 14 ? reader.ReadBytes(16).ToArray() : null;
    }
}

/// <summary>
///     Blob header at the start of each decompressed permutation (16 bytes).
///     Flags determine how the code offset is calculated.
/// </summary>
public readonly struct BlsBlobHeader
{
    public const int Size = 16;

    public readonly BlsBlobFlags Flags;
    public readonly uint CodeSize;
    public readonly uint Field8;
    public readonly uint Field12;

    public int CodeOffset
    {
        get
        {
            if (Flags.HasFlag(BlsBlobFlags.CodeOffsetExplicit))
                return (int)Field8;
            return Flags.HasFlag(BlsBlobFlags.HasBindInfo) ? 36 : 16;
        }
    }

    /// <summary>ShaderFlags value (only meaningful when CodeOffsetExplicit is NOT set).</summary>
    public uint ShaderFlags => Flags.HasFlag(BlsBlobFlags.CodeOffsetExplicit) ? 0 : Field8;

    public BlsBlobHeader(ReadOnlySpan<byte> data)
    {
        Flags = (BlsBlobFlags)BitConverter.ToUInt32(data);
        CodeSize = BitConverter.ToUInt32(data.Slice(4));
        Field8 = BitConverter.ToUInt32(data.Slice(8));
        Field12 = BitConverter.ToUInt32(data.Slice(12));
    }
}

/// <summary>
///     pmDxShaderCode structure at the computed code offset within a blob (56 bytes).
///     Contains binding masks and bytecode location.
/// </summary>
public readonly struct PmDxShaderCode
{
    public const int Size = 56;

    public readonly int Version;
    public readonly uint Field1;
    public readonly ulong CbvBindMask;
    public readonly ulong SrvBindMaskLo;
    public readonly ulong SrvBindMaskHi;
    public readonly ulong UavBindMask;
    public readonly ulong SamplerBindMask;
    public readonly uint BytecodeSize;
    public readonly uint BytecodeRelOffset;

    /// <summary>Computes the absolute bytecode start offset within the blob.</summary>
    public int BytecodeStart(int codeOffset) => codeOffset + 52 + (int)BytecodeRelOffset;

    public PmDxShaderCode(ReadOnlySpan<byte> data)
    {
        Version = BitConverter.ToInt32(data);
        Field1 = BitConverter.ToUInt32(data.Slice(4));
        CbvBindMask = BitConverter.ToUInt64(data.Slice(8));
        SrvBindMaskLo = BitConverter.ToUInt64(data.Slice(16));
        SrvBindMaskHi = BitConverter.ToUInt64(data.Slice(24));
        UavBindMask = BitConverter.ToUInt64(data.Slice(32));
        SamplerBindMask = BitConverter.ToUInt64(data.Slice(40));
        BytecodeSize = BitConverter.ToUInt32(data.Slice(48));
        BytecodeRelOffset = BitConverter.ToUInt32(data.Slice(52));
    }
}

/// <summary>
///     Metal sub-header at the code offset for Metal shaders (20 bytes).
///     FormatFlag: 0 = Metal source text, 1 = compiled MTLB binary.
/// </summary>
public readonly struct MetalSubHeader
{
    public const int Size = 20;

    public readonly uint Version;
    public readonly uint Unk;
    public readonly uint ContentSize;
    public readonly uint RelOffset;
    public readonly uint FormatFlag;

    public BlsBytecodeFormat BytecodeFormat => FormatFlag == 0
        ? BlsBytecodeFormat.MetalSource
        : BlsBytecodeFormat.MetalAIR;

    public MetalSubHeader(ReadOnlySpan<byte> data)
    {
        Version = BitConverter.ToUInt32(data);
        Unk = BitConverter.ToUInt32(data.Slice(4));
        ContentSize = BitConverter.ToUInt32(data.Slice(8));
        RelOffset = BitConverter.ToUInt32(data.Slice(12));
        FormatFlag = BitConverter.ToUInt32(data.Slice(16));
    }
}
