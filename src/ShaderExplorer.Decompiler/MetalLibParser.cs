namespace ShaderExplorer.Decompiler;

/// <summary>
///     Parses Metal library binary (MTLB) headers to extract metadata.
///     MTLB format: 88-byte header, then function list / metadata / bitcode sections.
///     Header layout:
///       0-3:   "MTLB" magic
///       4-5:   Target platform
///       6-7:   File version major
///       8-9:   File version minor
///       10:    Library type
///       11:    Target OS
///       12-13: OS version major
///       14-15: OS version minor
///       16-23: File size (uint64)
///       24-31: Function list offset (uint64)
///       32-39: Function list size (uint64)
///       40-47: Public metadata offset (uint64)
///       48-55: Public metadata size (uint64)
///       56-63: Private metadata offset (uint64)
///       64-71: Private metadata size (uint64)
///       72-79: Bitcode offset (uint64)
///       80-87: Bitcode size (uint64)
///     Function list entries use tag format: 4-char tag + uint16 size + data bytes.
/// </summary>
public static class MetalLibParser
{
    private const uint MagicMtlb = 0x424C544D; // "MTLB"
    private const int HeaderSize = 88;

    public static MetalLibInfo? Parse(byte[] data)
    {
        if (data.Length < 24 || BitConverter.ToUInt32(data, 0) != MagicMtlb)
            return null;

        var info = new MetalLibInfo
        {
            FileSize = data.Length
        };

        // Read header fields
        if (data.Length >= 10)
        {
            info.TargetPlatform = BitConverter.ToUInt16(data, 4);
            info.FileVersion = BitConverter.ToUInt16(data, 6);
            info.FileVersionMinor = BitConverter.ToUInt16(data, 8);
        }

        if (data.Length >= 16)
        {
            info.LibraryType = data[10];
            info.TargetOs = data[11];
        }

        // Read section offsets from fixed header positions
        if (data.Length >= HeaderSize)
        {
            info.HeaderFileSize = (long)BitConverter.ToUInt64(data, 16);
            info.FunctionListOffset = (long)BitConverter.ToUInt64(data, 24);
            info.FunctionListSize = (long)BitConverter.ToUInt64(data, 32);
            info.PublicMetadataOffset = (long)BitConverter.ToUInt64(data, 40);
            info.PublicMetadataSize = (long)BitConverter.ToUInt64(data, 48);
            info.PrivateMetadataOffset = (long)BitConverter.ToUInt64(data, 56);
            info.PrivateMetadataSize = (long)BitConverter.ToUInt64(data, 64);
            info.HeaderBitcodeOffset = (long)BitConverter.ToUInt64(data, 72);
            info.HeaderBitcodeSize = (long)BitConverter.ToUInt64(data, 80);

            if (info.HeaderBitcodeSize > 0)
                info.BitcodeSize = (int)Math.Min(info.HeaderBitcodeSize, data.Length);
        }

        // Parse function list tags for function metadata
        var tagStart = info.FunctionListOffset > 0 ? (int)info.FunctionListOffset : FindTagSection(data);
        if (tagStart >= 0)
            ParseTags(data, tagStart, info);

        return info;
    }

    private static void ParseTags(byte[] data, int pos, MetalLibInfo info)
    {
        while (pos + 6 <= data.Length)
        {
            if (pos + 4 > data.Length) break;
            var tagName = Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;

            if (pos + 2 > data.Length) break;
            int dataSize = BitConverter.ToUInt16(data, pos);
            pos += 2;

            if (pos + dataSize > data.Length) break;

            switch (tagName)
            {
                case "NAME":
                    info.FunctionName = ReadNullTermString(data, pos, dataSize);
                    break;
                case "HASH":
                    if (dataSize >= 32)
                        info.Hash = Convert.ToHexString(data, pos, 32);
                    else if (dataSize > 0)
                        info.Hash = Convert.ToHexString(data, pos, dataSize);
                    break;
                case "VERS":
                    info.VersionString = ReadNullTermString(data, pos, dataSize);
                    break;
                case "MDSZ":
                    if (dataSize >= 4)
                        info.MetadataSize = BitConverter.ToInt32(data, pos);
                    break;
                case "TYPE":
                    if (dataSize >= 1)
                        info.FunctionType = data[pos];
                    break;
                case "OFFT":
                    if (dataSize >= 8)
                        info.BitcodeOffset = BitConverter.ToInt64(data, pos);
                    break;
                case "ENDT":
                    info.TagsEndOffset = pos + dataSize;
                    return;
            }

            pos += dataSize;
        }
    }

    private static int FindTagSection(byte[] data)
    {
        // Known tags to search for
        var nameTag = "NAME"u8;
        var typeTag = "TYPE"u8;

        // Start scanning after header (88 bytes) to avoid false matches in header fields
        var start = data.Length >= HeaderSize ? HeaderSize : 8;
        for (var i = start; i <= data.Length - 6; i++)
        {
            var span = data.AsSpan(i, 4);
            if (span.SequenceEqual(nameTag) || span.SequenceEqual(typeTag))
                return i;
        }

        return -1;
    }

    /// <summary>
    ///     Extracts LLVM bitcode bytes from an MTLB container.
    ///     Uses the header's bitcode section offset/size (bytes 72-87) when available,
    ///     then falls back to scanning for LLVM bitcode magic bytes.
    ///     Apple Metal bitcode uses wrapper format with magic 0xDEC0170B.
    /// </summary>
    public static byte[]? ExtractBitcode(byte[] data)
    {
        var info = Parse(data);
        if (info == null) return null;

        // Primary: use header-specified bitcode offset and size (bytes 72-87)
        if (info.HeaderBitcodeOffset > 0 && info.HeaderBitcodeSize > 0)
        {
            var offset = (int)info.HeaderBitcodeOffset;
            var size = (int)Math.Min(info.HeaderBitcodeSize, data.Length - offset);
            if (offset >= 0 && offset + size <= data.Length && size > 0)
                return data[offset..(offset + size)];
        }

        // Scan for both raw bitcode and wrapper magics
        var searchStart = info.TagsEndOffset > 0 ? info.TagsEndOffset : HeaderSize;
        for (var i = searchStart; i + 4 <= data.Length; i++)
        {
            // LLVM bitcode wrapper: 0x0B 0x17 0xC0 0xDE (little-endian 0x0B17C0DE)
            if (data[i] == 0xDE && data[i + 1] == 0xC0 && data[i + 2] == 0x17 && data[i + 3] == 0x0B)
                return data[i..];

            // Raw LLVM bitcode: 'B' 'C' 0xC0 0xDE
            if (data[i] == 0x42 && data[i + 1] == 0x43 && data[i + 2] == 0xC0 && data[i + 3] == 0xDE)
                return data[i..];
        }

        // Last resort: return everything after tags
        if (info.TagsEndOffset > 0 && info.TagsEndOffset < data.Length)
            return data[info.TagsEndOffset..];

        return null;
    }

    private static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        var limit = offset + maxLen;
        while (end < limit && end < data.Length && data[end] != 0)
            end++;
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }
}

public class MetalLibInfo
{
    public int FileSize { get; set; }
    public int FileVersion { get; set; }
    public int FileVersionMinor { get; set; }
    public int TargetPlatform { get; set; }
    public int LibraryType { get; set; }
    public int TargetOs { get; set; }
    public string? FunctionName { get; set; }
    public int FunctionType { get; set; }
    public string? Hash { get; set; }
    public string? VersionString { get; set; }
    public int MetadataSize { get; set; }
    public long BitcodeOffset { get; set; }
    public int BitcodeSize { get; set; }
    public int TagsEndOffset { get; set; }

    // From fixed 88-byte header
    public long HeaderFileSize { get; set; }
    public long FunctionListOffset { get; set; }
    public long FunctionListSize { get; set; }
    public long PublicMetadataOffset { get; set; }
    public long PublicMetadataSize { get; set; }
    public long PrivateMetadataOffset { get; set; }
    public long PrivateMetadataSize { get; set; }
    public long HeaderBitcodeOffset { get; set; }
    public long HeaderBitcodeSize { get; set; }
}