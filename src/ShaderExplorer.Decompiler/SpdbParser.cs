namespace ShaderExplorer.Decompiler;

public class SpdbSourceInfo
{
    public string HlslSource { get; set; } = string.Empty;
    public string? OriginalFilePath { get; set; }
    public string? CompilerTarget { get; set; }
    public string? EntryPoint { get; set; }
}

public static class SpdbParser
{
    // "Microsoft C/C++ MSF 7.00\r\n\x1aDS" — can't use Encoding.ASCII because it replaces \x1a with '?'
    private static readonly byte[] MsfSignature =
    [
        0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // Microsof
        0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // t C/C++
        0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30, // MSF 7.00
        0x0D, 0x0A, 0x1A, 0x44, 0x53                    // \r\n\x1aDS
    ];

    public static SpdbSourceInfo? ExtractSource(byte[] spdbData)
    {
        try
        {
            var streams = ParseMsf(spdbData);
            if (streams == null || streams.Count == 0)
                return null;

            var info = ExtractFromStreams(streams);
            if (info != null)
                info.HlslSource = CleanupPreprocessedSource(info.HlslSource);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Cleans up preprocessed HLSL source extracted from SPDB:
    ///     - Removes #line directives that precede only blank lines (stripped #ifdef blocks)
    ///     - Collapses multiple consecutive blank lines into one
    ///     - Strips the trailing PDB filename table
    /// </summary>
    internal static string CleanupPreprocessedSource(string source)
    {
        var lines = source.Split('\n');
        var result = new List<string>(lines.Length);

        var i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();

            if (trimmed.StartsWith("#line "))
            {
                // Look ahead: is there any non-blank, non-#line content before the next #line?
                var j = i + 1;
                var hasContent = false;
                while (j < lines.Length)
                {
                    var nextTrimmed = lines[j].TrimEnd('\r').Trim();
                    if (nextTrimmed.StartsWith("#line "))
                        break;
                    if (nextTrimmed.Length > 0)
                    {
                        hasContent = true;
                        break;
                    }

                    j++;
                }

                if (!hasContent)
                {
                    // Skip this #line and all its trailing blank lines
                    i = j;
                    continue;
                }
            }

            result.Add(lines[i].TrimEnd('\r'));
            i++;
        }

        // Collapse multiple consecutive blank lines into a single blank line
        var collapsed = new List<string>(result.Count);
        var lastWasBlank = false;
        foreach (var line in result)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank)
            {
                if (!lastWasBlank)
                    collapsed.Add("");
                lastWasBlank = true;
            }
            else
            {
                collapsed.Add(line);
                lastWasBlank = false;
            }
        }

        // Remove trailing PDB filename table (concatenated source file names)
        var last = collapsed.Count - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(collapsed[last]))
            last--;
        if (last >= 0 && IsFilenameTable(collapsed[last]))
            collapsed.RemoveAt(last);

        // Remove trailing orphaned #line (was only followed by the filename table)
        last = collapsed.Count - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(collapsed[last]))
            last--;
        if (last >= 0 && collapsed[last].Trim().StartsWith("#line "))
            collapsed.RemoveAt(last);

        // Trim trailing blank lines
        while (collapsed.Count > 0 && string.IsNullOrWhiteSpace(collapsed[^1]))
            collapsed.RemoveAt(collapsed.Count - 1);

        return string.Join("\n", collapsed);
    }

    /// <summary>
    ///     Detects the concatenated filename table that PDB streams append after source text.
    ///     Example: "Inject row_majorModel2.hCommon.h../Common/Fog.hEnvironment.h"
    /// </summary>
    private static bool IsFilenameTable(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 10) return false;

        // Must not contain code syntax
        if (trimmed.Contains(';') || trimmed.Contains('{') || trimmed.Contains('}') ||
            trimmed.Contains('(') || trimmed.Contains(')') || trimmed.Contains('='))
            return false;

        // Must not be a directive or comment
        if (trimmed[0] == '#' || trimmed.StartsWith("//"))
            return false;

        // Count ".h" occurrences — matches .h, .hlsl, etc.
        var count = 0;
        var idx = 0;
        while ((idx = trimmed.IndexOf(".h", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += 2;
        }

        return count >= 3;
    }

    private static List<byte[]>? ParseMsf(byte[] data)
    {
        // Validate MSF 7.00 signature (32 bytes)
        if (data.Length < 56)
            return null;

        for (var i = 0; i < MsfSignature.Length; i++)
            if (data[i] != MsfSignature[i])
                return null;

        // Read super block fields
        var pageSize = BitConverter.ToInt32(data, 32);
        // skip freeBlockMapBlock at +36
        var numPages = BitConverter.ToInt32(data, 40);
        var directoryByteSize = BitConverter.ToInt32(data, 44);
        // skip unknown at +48
        var directoryPageMapPage = BitConverter.ToInt32(data, 52);

        if (pageSize <= 0 || pageSize > 0x10000 || numPages <= 0)
            return null;

        // The directory page map is a single page that lists the pages of the directory stream
        var directoryPageCount = (directoryByteSize + pageSize - 1) / pageSize;

        // Read the directory page map — it's at offset directoryPageMapPage * pageSize
        // and contains directoryPageCount page numbers (each 4 bytes)
        var pageMapOffset = directoryPageMapPage * pageSize;
        if (pageMapOffset + directoryPageCount * 4 > data.Length)
            return null;

        var directoryPages = new int[directoryPageCount];
        for (var i = 0; i < directoryPageCount; i++)
            directoryPages[i] = BitConverter.ToInt32(data, pageMapOffset + i * 4);

        // Reassemble directory content from its pages
        var directoryData = AssemblePages(data, directoryPages, directoryByteSize, pageSize);
        if (directoryData == null)
            return null;

        // Parse directory: stream count, then stream sizes, then per-stream page lists
        var dirPos = 0;
        if (directoryData.Length < 4)
            return null;

        var streamCount = BitConverter.ToInt32(directoryData, dirPos);
        dirPos += 4;

        if (streamCount < 0 || streamCount > 10000)
            return null;

        // Read stream sizes
        var streamSizes = new int[streamCount];
        for (var i = 0; i < streamCount; i++)
        {
            if (dirPos + 4 > directoryData.Length)
                return null;
            streamSizes[i] = BitConverter.ToInt32(directoryData, dirPos);
            dirPos += 4;
        }

        // Read per-stream page lists and assemble stream content
        var streams = new List<byte[]>(streamCount);
        for (var i = 0; i < streamCount; i++)
        {
            var size = streamSizes[i];
            if (size <= 0 || size == -1)
            {
                streams.Add([]);
                continue;
            }

            var streamPageCount = (size + pageSize - 1) / pageSize;
            var pages = new int[streamPageCount];
            for (var p = 0; p < streamPageCount; p++)
            {
                if (dirPos + 4 > directoryData.Length)
                    return null;
                pages[p] = BitConverter.ToInt32(directoryData, dirPos);
                dirPos += 4;
            }

            var streamData = AssemblePages(data, pages, size, pageSize);
            streams.Add(streamData ?? []);
        }

        return streams;
    }

    private static byte[]? AssemblePages(byte[] data, int[] pages, int totalSize, int pageSize)
    {
        var result = new byte[totalSize];
        var remaining = totalSize;
        var destOffset = 0;

        for (var i = 0; i < pages.Length; i++)
        {
            var srcOffset = pages[i] * pageSize;
            var copyLen = Math.Min(remaining, pageSize);
            if (srcOffset + copyLen > data.Length)
                return null;

            Buffer.BlockCopy(data, srcOffset, result, destOffset, copyLen);
            destOffset += copyLen;
            remaining -= copyLen;
        }

        return result;
    }

    private static SpdbSourceInfo? ExtractFromStreams(List<byte[]> streams)
    {
        var info = new SpdbSourceInfo();

        // Strategy 1: Look for stream with magic 0xEFFEEFFE (wrapped source with header)
        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            if (stream.Length >= 8)
            {
                var magic = BitConverter.ToUInt32(stream, 0);
                if (magic == 0xEFFEEFFE)
                {
                    var source = ParseWrappedSource(stream, info);
                    if (source != null)
                    {
                        info.HlslSource = source;
                        ExtractCompilerMetadata(streams, info);
                        return info;
                    }
                }
            }
        }

        // Strategy 2: Find a stream starting with #line or #pragma (raw source)
        byte[]? bestRawStream = null;
        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            if (stream.Length > 10 && StartsWithSourceMarker(stream))
                if (bestRawStream == null || stream.Length > bestRawStream.Length)
                    bestRawStream = stream;
        }

        if (bestRawStream != null)
        {
            info.HlslSource = ExtractUtf8String(bestRawStream, 0, bestRawStream.Length);
            ExtractCompilerMetadata(streams, info);
            return info;
        }

        return null;
    }

    private static string? ParseWrappedSource(byte[] stream, SpdbSourceInfo info)
    {
        // Format: magic(4) + version(4) + then data
        // The header contains file path entries followed by source text
        if (stream.Length < 12)
            return null;

        var pos = 4; // skip magic
        var version = BitConverter.ToUInt32(stream, pos);
        pos += 4;

        // Skip version-dependent header fields
        // Try to find embedded paths and source by scanning for null-terminated strings
        // after the fixed header

        // Read number of entries or skip to find paths
        // The format varies but typically has: count/offset fields, then file paths, then source
        // We'll look for the source text by scanning for common HLSL markers

        // Try reading file path entries
        // Some versions: uint numEntries at pos, then entries
        if (pos + 4 > stream.Length)
            return null;

        // Scan forward looking for null-terminated path strings or source text
        // The wrapped format typically has path(s) followed by the actual HLSL
        // Try to extract any file paths we find
        string? firstPath = null;
        var sourceStart = -1;

        // Look for source markers in the stream
        for (var i = pos; i < stream.Length - 5; i++)
            if (StartsWithSourceMarkerAt(stream, i))
            {
                sourceStart = i;
                break;
            }

        // Extract file paths: scan for strings ending in .hlsl or .fx before the source
        if (sourceStart > pos) firstPath = FindEmbeddedPath(stream, pos, sourceStart);

        if (sourceStart >= 0)
        {
            info.OriginalFilePath = firstPath;
            return ExtractUtf8String(stream, sourceStart, stream.Length - sourceStart);
        }

        // Fallback: try to get anything after the header as text
        // Skip known header sizes for common versions
        foreach (var headerSize in new[] { 20, 16, 12, 24, 28 })
            if (headerSize < stream.Length)
            {
                var text = ExtractUtf8String(stream, headerSize, stream.Length - headerSize);
                if (text.Length > 20 && LooksLikeHlsl(text))
                {
                    info.OriginalFilePath = firstPath;
                    return text;
                }
            }

        return null;
    }

    private static string? FindEmbeddedPath(byte[] data, int start, int end)
    {
        // Scan for null-terminated strings that look like file paths
        var strStart = start;
        for (var i = start; i < end; i++)
            if (data[i] == 0)
            {
                if (i > strStart)
                {
                    var str = Encoding.UTF8.GetString(data, strStart, i - strStart);
                    if (str.Contains(".hlsl", StringComparison.OrdinalIgnoreCase) ||
                        str.Contains(".fx", StringComparison.OrdinalIgnoreCase) ||
                        str.Contains(":\\", StringComparison.Ordinal) ||
                        str.Contains("/Shaders/", StringComparison.OrdinalIgnoreCase))
                        return str;
                }

                strStart = i + 1;
            }

        return null;
    }

    private static bool StartsWithSourceMarker(byte[] data)
    {
        return StartsWithSourceMarkerAt(data, 0);
    }

    private static bool StartsWithSourceMarkerAt(byte[] data, int offset)
    {
        if (offset + 5 >= data.Length) return false;

        // Check for "#line" or "#prag"
        if (data[offset] == (byte)'#')
        {
            if (offset + 5 < data.Length &&
                data[offset + 1] == (byte)'l' &&
                data[offset + 2] == (byte)'i' &&
                data[offset + 3] == (byte)'n' &&
                data[offset + 4] == (byte)'e')
                return true;

            if (offset + 7 < data.Length &&
                data[offset + 1] == (byte)'p' &&
                data[offset + 2] == (byte)'r' &&
                data[offset + 3] == (byte)'a' &&
                data[offset + 4] == (byte)'g' &&
                data[offset + 5] == (byte)'m' &&
                data[offset + 6] == (byte)'a')
                return true;
        }

        return false;
    }

    private static bool LooksLikeHlsl(string text)
    {
        // Quick heuristic: contains common HLSL keywords
        return text.Contains("float", StringComparison.Ordinal) ||
               text.Contains("struct", StringComparison.Ordinal) ||
               text.Contains("void", StringComparison.Ordinal) ||
               text.Contains("cbuffer", StringComparison.Ordinal) ||
               text.Contains("#line", StringComparison.Ordinal) ||
               text.Contains("return", StringComparison.Ordinal);
    }

    private static void ExtractCompilerMetadata(List<byte[]> streams, SpdbSourceInfo info)
    {
        // Look for stream containing hlslTarget/hlslEntry strings
        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            if (stream.Length < 10) continue;

            // Quick check: does this stream contain "hlsl" ascii bytes?
            var idx = FindBytes(stream, "hlsl"u8);
            if (idx < 0) continue;

            var text = Encoding.UTF8.GetString(stream);

            // Extract target: "hlslTarget\0vs_5_0" or "hlslTarget.vs_5_0"
            var targetIdx = text.IndexOf("hlslTarget", StringComparison.Ordinal);
            if (targetIdx >= 0)
            {
                var valueStart = targetIdx + "hlslTarget".Length;
                // Skip separator (null byte or period)
                while (valueStart < text.Length && (text[valueStart] == '\0' || text[valueStart] == '.'))
                    valueStart++;
                var valueEnd = valueStart;
                while (valueEnd < text.Length && text[valueEnd] != '\0' && text[valueEnd] != '\n')
                    valueEnd++;
                if (valueEnd > valueStart)
                    info.CompilerTarget = text[valueStart..valueEnd].Trim();
            }

            // Extract entry point
            var entryIdx = text.IndexOf("hlslEntry", StringComparison.Ordinal);
            if (entryIdx >= 0)
            {
                var valueStart = entryIdx + "hlslEntry".Length;
                while (valueStart < text.Length && (text[valueStart] == '\0' || text[valueStart] == '.'))
                    valueStart++;
                var valueEnd = valueStart;
                while (valueEnd < text.Length && text[valueEnd] != '\0' && text[valueEnd] != '\n')
                    valueEnd++;
                if (valueEnd > valueStart)
                    info.EntryPoint = text[valueStart..valueEnd].Trim();
            }

            // Also check stream 1 for original file path if not already set
            if (info.OriginalFilePath == null && streams.Count > 1 && streams[1].Length > 4)
            {
                var s1text = ExtractUtf8String(streams[1], 0, streams[1].Length);
                if (s1text.Contains(".hlsl", StringComparison.OrdinalIgnoreCase) ||
                    s1text.Contains(".fx", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract path: find the longest path-like substring
                    var path = FindPathInString(s1text);
                    if (path != null)
                        info.OriginalFilePath = path;
                }
            }

            break;
        }
    }

    private static string? FindPathInString(string text)
    {
        // Find substrings that look like file paths
        // Scan for patterns like X:\... or /...
        var bestStart = -1;
        var bestLen = 0;

        for (var i = 0; i < text.Length; i++)
            // Drive letter path: X:\...
            if (i + 2 < text.Length && char.IsLetter(text[i]) && text[i + 1] == ':' &&
                (text[i + 2] == '\\' || text[i + 2] == '/'))
            {
                var end = i;
                while (end < text.Length && text[end] >= ' ' && text[end] != '\0')
                    end++;
                if (end - i > bestLen)
                {
                    bestStart = i;
                    bestLen = end - i;
                }
            }

        return bestStart >= 0 ? text.Substring(bestStart, bestLen) : null;
    }

    private static int FindBytes(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }

            if (match) return i;
        }

        return -1;
    }

    private static string ExtractUtf8String(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        var limit = Math.Min(offset + maxLen, data.Length);

        // Find the actual end of the text (stop at null or end of data)
        while (end < limit)
        {
            // Allow embedded nulls if there's more text after
            if (data[end] == 0)
            {
                // Check if there's meaningful text after a short gap
                var nextNonNull = end;
                while (nextNonNull < limit && data[nextNonNull] == 0)
                    nextNonNull++;

                // If more than 4 consecutive nulls, or we're near the end, stop here
                if (nextNonNull - end > 4 || nextNonNull >= limit)
                    break;

                // Single/few nulls might be part of the format; keep going
                end = nextNonNull;
                continue;
            }

            end++;
        }

        if (end <= offset)
            return string.Empty;

        return Encoding.UTF8.GetString(data, offset, end - offset)
            .Replace("\0", "");
    }
}