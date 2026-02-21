namespace ShaderExplorer.Decompiler;

public enum ShaderFormat
{
    Unknown,
    DXBC,
    DXIL,
    BLS,
    MetalLib,
    MetalSource
}

public static class ShaderFormatDetector
{
    /// <summary>
    ///     Detects the shader format by examining the file header bytes.
    ///     DXBC: starts with "DXBC" (0x43425844)
    ///     DXIL: DXBC container with DXIL chunk inside, or raw DXIL bitcode
    /// </summary>
    public static ShaderFormat Detect(byte[] data)
    {
        if (data.Length < 4)
            return ShaderFormat.Unknown;

        var magic = BitConverter.ToUInt32(data, 0);

        // DXBC magic: "DXBC" = 0x43425844
        if (magic == 0x43425844)
        {
            // Check if it contains DXIL chunks (SM6+)
            // DXIL shaders are still wrapped in a DXBC container but have DXIL chunk instead of SHDR/SHEX
            if (ContainsDxilChunk(data))
                return ShaderFormat.DXIL;

            return ShaderFormat.DXBC;
        }

        // BLS container: TAFG (0x47464154), bare GXSH (0x47585348), or GXSD (0x47585344, DXIL variant)
        if (magic is 0x47464154 or 0x47585348 or 0x47585344)
            return ShaderFormat.BLS;

        // Raw LLVM bitcode starts with "BC" (0x4243)
        if (data.Length >= 4 && data[0] == 0x42 && data[1] == 0x43)
            return ShaderFormat.DXIL;

        // Metal library: "MTLB" magic (0x424C544D little-endian)
        if (magic == 0x424C544D)
            return ShaderFormat.MetalLib;

        // Metal source: starts with "#include <metal_"
        if (data.Length >= 16 && data[0] == 0x23) // '#'
        {
            var prefix = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 64));
            if (prefix.StartsWith("#include <metal_"))
                return ShaderFormat.MetalSource;
        }

        return ShaderFormat.Unknown;
    }

    private static bool ContainsDxilChunk(byte[] data)
    {
        if (data.Length < 32)
            return false;

        // Skip to chunk count at offset 28
        var chunkCount = BitConverter.ToUInt32(data, 28);
        var offsetBase = 32;

        for (var i = 0; i < chunkCount && offsetBase + (i + 1) * 4 <= data.Length; i++)
        {
            var chunkOffset = BitConverter.ToUInt32(data, offsetBase + i * 4);
            if (chunkOffset + 4 > data.Length)
                continue;

            var fourcc = BitConverter.ToUInt32(data, (int)chunkOffset);
            // "DXIL" = 0x4C495844 — SM6+ bytecode
            // "ILDB" = 0x42444C49 — SM6+ bytecode with debug info
            // Note: "ILDN" (0x4E444C49) is DXIL-format debug info only (PDB);
            // the actual code is still in SHEX/SHDR, so it does NOT indicate DXIL bytecode.
            if (fourcc is 0x4C495844 or 0x42444C49)
                return true;
        }

        return false;
    }
}