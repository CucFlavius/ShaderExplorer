namespace ShaderExplorer.Core.Models;

public enum BlsBytecodeFormat
{
    Unknown,
    DXBC,
    DXIL,
    MetalAIR,
    MetalSource,
    Empty,
    Invalid
}

[Flags]
public enum BlsBlobFlags : uint
{
    None = 0,
    HasBindInfo = 1 << 0,
    CodeOffsetExplicit = 1 << 1
}

public class BlsContainer
{
    public string FilePath { get; set; } = "";
    public List<BlsPlatform> Platforms { get; set; } = [];
}

public class BlsPlatform
{
    public string Tag { get; set; } = "";
    public int GxshVersion { get; set; }
    public int PermCount { get; set; }
    public int BlockCount { get; set; }

    /// <summary>Offset within the BLS file where this platform's GXSH blob starts.</summary>
    public int GxshOffset { get; set; }

    /// <summary>Size of this platform's GXSH blob in bytes.</summary>
    public int GxshSize { get; set; }

    /// <summary>Offset of the block offset table within the GXSH blob.</summary>
    public int BlockTableOffset { get; set; }

    /// <summary>Offset of the compressed data within the GXSH blob.</summary>
    public int CompDataOffset { get; set; }

    /// <summary>v14 bytecodeFormat field. High word: 0=DXBC, 1=DXIL, 3=MetalAIR.</summary>
    public BlsBytecodeFormat BytecodeFormat { get; set; }

    public List<BlsPermutation> Permutations { get; set; } = [];
}

public class BlsPermutation
{
    public int Index { get; set; }
    public int DecompressedOffset { get; set; }
    public int DecompressedSize { get; set; }
    public bool IsEmpty => DecompressedSize == 0;
    public byte[]? ContentHash { get; set; }

    /// <summary>Extracted DXBC/DXIL bytecode, populated lazily on selection.</summary>
    public byte[]? Bytecode { get; set; }

    /// <summary>Parsed metadata from the permutation blob header.</summary>
    public BlsPermutationInfo? Info { get; set; }
}

public class BlsPermutationInfo
{
    public int CodeVersion { get; set; }
    public int BytecodeSize { get; set; }
    public BlsBytecodeFormat BytecodeFormat { get; set; }
    public uint ShaderFlags { get; set; }
    public ulong CbvBindMask { get; set; }
    public ulong SrvBindMaskLo { get; set; }
    public ulong SrvBindMaskHi { get; set; }
    public ulong UavBindMask { get; set; }
    public ulong SamplerBindMask { get; set; }
}