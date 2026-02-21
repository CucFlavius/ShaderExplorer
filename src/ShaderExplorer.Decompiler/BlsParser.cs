using System.IO.Compression;
using ShaderExplorer.Core.Models;

namespace ShaderExplorer.Decompiler;

/// <summary>
///     Parses Blizzard BLS shader containers (TAFG + GXSH/GXSD format).
///     Supports GXSH versions 12, 13, and 14, with lazy per-permutation decompression.
///     GXSH header layouts:
///     v12 (0x1000C): 28 bytes — [magic, version, permOff, permCount, blkOff, blkCount, compOff]
///     v13 (0x1000D): 32 bytes — [magic, version, field2, permOff, permCount, blkOff, field6, compOff]
///     v14 (0x1000E): 40 bytes — [magic, version, platformTag, permOff, permCount, blkOff, blkCount, compOff,
///     bytecodeFormat, reserved]
///     Permutation entries:
///     v12/v13: 8 bytes  — [packedOffset(u32), decompressedSize(u32)]
///     v14:     24 bytes — [packedOffset(u32), decompressedSize(u32), contentHash(16 bytes)]
///     packedOffset encodes: bits[13:0]=blockOffset, bits[31:14]=blockIndex
///     which is mathematically equivalent to a flat byte offset when blocks are 0x4000 bytes.
/// </summary>
public class BlsParser
{
    private const uint MagicTafg = 0x47464154;        // "TAFG"
    private const uint MagicGxsh = 0x47585348;        // "GXSH"
    private const uint MagicGxsd = 0x47585344;        // "GXSD" (DXIL variant, same layout as GXSH)
    private const uint MagicDxbc = 0x43425844;        // "DXBC"
    private const uint MagicMtlb = 0x424C544D;        // "MTLB"
    private const int BlockDecompressedSize = 0x4000; // 16KB

    /// <summary>
    ///     Parses BLS headers and permutation table metadata (no decompression).
    /// </summary>
    public BlsContainer Parse(byte[] data)
    {
        var container = new BlsContainer();
        var reader = new ByteReader(data);
        var magic = reader.ReadUInt32();

        if (magic == MagicTafg)
            ParseTafg(data, ref reader, container);
        else if (magic is MagicGxsh or MagicGxsd)
            container.Platforms.Add(ParseGxshPlatform(data, 0, data.Length));
        else
            throw new InvalidDataException($"Not a BLS file: magic 0x{magic:X8}");

        return container;
    }

    /// <summary>
    ///     Extracts and returns the DXBC/DXIL bytecode for a specific permutation.
    ///     Decompresses only the needed zlib blocks.
    /// </summary>
    public byte[]? ExtractPermutationBytecode(byte[] blsData, BlsPlatform platform, BlsPermutation perm)
    {
        if (perm.IsEmpty) return null;
        if (perm.Bytecode != null) return perm.Bytecode;

        var blob = DecompressPermutation(blsData, platform, perm);
        if (blob == null || blob.Length < 8) return null;

        // Parse the permutation blob and extract bytecode
        var (bytecode, info) = ExtractBytecodeFromBlob(blob);
        perm.Bytecode = bytecode;
        perm.Info = info;

        return bytecode;
    }

    /// <summary>
    ///     Quickly probes all non-empty permutations in a platform to determine their
    ///     content type (MetalSource vs MetalAIR vs DXBC etc.) without full extraction.
    ///     Only decompresses each permutation's first block and reads the blob header.
    ///     Results are stored in <see cref="BlsPermutation.Info" />.<see cref="BlsPermutationInfo.BytecodeFormat" />.
    /// </summary>
    public void ProbePermutationFormats(byte[] blsData, BlsPlatform platform)
    {
        var gxshOffset = platform.GxshOffset;
        var gxshSize = Math.Min(platform.GxshSize, blsData.Length - gxshOffset);
        if (gxshSize < 28) return;

        var reader = new ByteReader(blsData.AsSpan(gxshOffset, gxshSize));

        var blockTableOff = platform.BlockTableOffset;
        var compDataOff = platform.CompDataOffset;
        var blockEntryCount = compDataOff > blockTableOff ? (compDataOff - blockTableOff) / 4 : 0;

        // Read block offset table
        reader.Position = blockTableOff;
        var blockOffsets = new int[blockEntryCount];
        for (var i = 0; i < blockEntryCount; i++)
            blockOffsets[i] = reader.ReadInt32();

        // Cache decompressed blocks — multiple perms often share the same first block
        var blockCache = new Dictionary<int, byte[]?>();

        foreach (var perm in platform.Permutations)
        {
            if (perm.IsEmpty) continue;
            // Skip if already fully extracted
            if (perm.Bytecode != null) continue;

            var startBlock = perm.DecompressedOffset / BlockDecompressedSize;
            if (startBlock + 1 >= blockEntryCount) continue;

            // Decompress the first block (cached)
            if (!blockCache.TryGetValue(startBlock, out var decompBlock))
            {
                var compOffset = gxshOffset + compDataOff + blockOffsets[startBlock];
                var compSize = blockOffsets[startBlock + 1] - blockOffsets[startBlock];

                if (compOffset >= 0 && compSize > 0 && compOffset + compSize <= blsData.Length)
                    try
                    {
                        decompBlock = DecompressBlock(blsData, compOffset, compSize);
                    }
                    catch
                    {
                        decompBlock = null;
                    }

                blockCache[startBlock] = decompBlock;
            }

            if (decompBlock == null) continue;

            // Extract just the bytes we need from the decompressed block
            var skipInBlock = perm.DecompressedOffset - startBlock * BlockDecompressedSize;
            var available = decompBlock.Length - skipInBlock;
            if (available <= 0) continue;

            // We need up to 56 bytes (blob header + Metal sub-header with formatFlag).
            // If the perm straddles a block boundary, combine bytes from two blocks.
            const int probeNeed = 56;
            byte[] probeArr;
            if (available >= probeNeed)
            {
                probeArr = decompBlock.AsSpan(skipInBlock, probeNeed).ToArray();
            }
            else
            {
                probeArr = new byte[Math.Min(probeNeed, perm.DecompressedSize)];
                decompBlock.AsSpan(skipInBlock, available).CopyTo(probeArr);
                var filled = available;

                // Read from next block to fill remaining bytes
                var nextBlock = startBlock + 1;
                if (filled < probeArr.Length && nextBlock + 1 < blockEntryCount)
                {
                    if (!blockCache.TryGetValue(nextBlock, out var nextDecomp))
                    {
                        var compOff2 = gxshOffset + compDataOff + blockOffsets[nextBlock];
                        var compSz2 = blockOffsets[nextBlock + 1] - blockOffsets[nextBlock];
                        if (compOff2 >= 0 && compSz2 > 0 && compOff2 + compSz2 <= blsData.Length)
                            try
                            {
                                nextDecomp = DecompressBlock(blsData, compOff2, compSz2);
                            }
                            catch
                            {
                                nextDecomp = null;
                            }

                        blockCache[nextBlock] = nextDecomp;
                    }

                    if (nextDecomp != null)
                    {
                        var take = Math.Min(probeArr.Length - filled, nextDecomp.Length);
                        nextDecomp.AsSpan(0, take).CopyTo(probeArr.AsSpan(filled));
                    }
                }
            }

            var probeLen = probeArr.Length;
            var probe = probeArr.AsSpan();

            var blobHeader = new BlsBlobHeader(probe);
            var codeOffset = blobHeader.CodeOffset;

            if (blobHeader.CodeSize == 0)
            {
                perm.Info ??= new BlsPermutationInfo { BytecodeSize = perm.DecompressedSize };
                perm.Info.BytecodeFormat = BlsBytecodeFormat.Empty;
                continue;
            }

            if (codeOffset + 4 > probeLen) continue;

            var magic = BitConverter.ToUInt32(probe.Slice(codeOffset));

            perm.Info ??= new BlsPermutationInfo { BytecodeSize = perm.DecompressedSize };

            if (magic == 0x00000001 && codeOffset + MetalSubHeader.Size <= probeLen)
            {
                var metalHeader = new MetalSubHeader(probe.Slice(codeOffset));
                perm.Info.BytecodeFormat = metalHeader.BytecodeFormat;
            }
            else if (magic == MagicMtlb)
            {
                perm.Info.BytecodeFormat = BlsBytecodeFormat.MetalAIR;
            }
            else if (magic == MagicDxbc)
            {
                perm.Info.BytecodeFormat = BlsBytecodeFormat.DXBC;
            }
            else if ((magic & 0xFFFF) == 0x4243) // "BC" raw LLVM bitcode
            {
                perm.Info.BytecodeFormat = BlsBytecodeFormat.DXIL;
            }
        }
    }

    private void ParseTafg(byte[] data, ref ByteReader reader, BlsContainer container)
    {
        reader.Skip(4); // skip version
        var platformCount = reader.ReadUInt32();

        var entries = new TafgEntry[platformCount];
        for (var i = 0; i < platformCount; i++)
            entries[i] = new TafgEntry(ref reader);

        foreach (var entry in entries)
        {
            if (entry.Start < 0 || entry.End <= entry.Start || entry.Start >= data.Length) continue;
            var innerMagic = BitConverter.ToUInt32(data, entry.Start);
            if (innerMagic is not MagicGxsh and not MagicGxsd) continue;

            var platform = ParseGxshPlatform(data, entry.Start, entry.End - entry.Start);
            // Override tag from TAFG entry (more reliable)
            platform.Tag = NormalizeTag(entry.Tag);
            container.Platforms.Add(platform);
        }
    }

    private BlsPlatform ParseGxshPlatform(byte[] data, int offset, int size)
    {
        var reader = new ByteReader(data.AsSpan(offset, Math.Min(size, data.Length - offset)));
        var header = new GxshHeader(ref reader);
        if (header.Magic is not MagicGxsh and not MagicGxsd)
            throw new InvalidDataException($"Expected GXSH/GXSD magic at offset {offset}, got 0x{header.Magic:X8}");

        var platform = new BlsPlatform
        {
            Tag = NormalizeTag(header.Tag),
            GxshVersion = header.Version,
            PermCount = header.PermCount,
            BlockCount = header.BlockCount,
            GxshOffset = offset,
            GxshSize = size,
            BytecodeFormat = header.BytecodeFormat,
            BlockTableOffset = header.BlockTableOffset,
            CompDataOffset = header.CompDataOffset
        };

        // Parse permutation table entries
        reader.Position = header.PermTableOffset;

        for (var i = 0; i < header.PermCount; i++)
        {
            var entry = new PermutationEntry(ref reader, header.Version);

            var perm = new BlsPermutation
            {
                Index = i,
                DecompressedOffset = entry.PackedOffset,
                DecompressedSize = entry.DecompressedSize,
                ContentHash = entry.ContentHash
            };

            // Pre-populate info with size from table (actual format detection on extraction)
            if (entry.DecompressedSize > 0)
                perm.Info = new BlsPermutationInfo
                {
                    BytecodeSize = entry.DecompressedSize,
                    BytecodeFormat = header.BytecodeFormat
                };

            platform.Permutations.Add(perm);
        }

        return platform;
    }

    private byte[]? DecompressPermutation(byte[] blsData, BlsPlatform platform, BlsPermutation perm)
    {
        var gxshOffset = platform.GxshOffset;
        var gxshSize = Math.Min(platform.GxshSize, blsData.Length - gxshOffset);
        var reader = new ByteReader(blsData.AsSpan(gxshOffset, gxshSize));

        var blockTableOff = platform.BlockTableOffset;
        var compDataOff = platform.CompDataOffset;

        // Derive block count from offset table (last entry is sentinel)
        var blockEntryCount = compDataOff > blockTableOff ? (compDataOff - blockTableOff) / 4 : 0;
        var blockCount = blockEntryCount > 1 ? blockEntryCount - 1 : 0;

        // Read block offset table
        reader.Position = blockTableOff;
        var blockOffsets = new int[blockEntryCount];
        for (var i = 0; i < blockEntryCount; i++)
            blockOffsets[i] = reader.ReadInt32();

        // Determine which blocks we need
        var startBlock = perm.DecompressedOffset / BlockDecompressedSize;
        var endBlock = (perm.DecompressedOffset + perm.DecompressedSize - 1) / BlockDecompressedSize;
        endBlock = Math.Min(endBlock, blockCount - 1);

        // Decompress needed blocks and assemble the permutation data
        using var ms = new MemoryStream(perm.DecompressedSize);
        var bytesNeeded = perm.DecompressedSize;
        var currentDecompOffset = startBlock * BlockDecompressedSize;

        for (var b = startBlock; b <= endBlock && bytesNeeded > 0; b++)
        {
            if (b + 1 >= blockEntryCount) break;

            var compOffset = gxshOffset + compDataOff + blockOffsets[b];
            var compSize = blockOffsets[b + 1] - blockOffsets[b];

            if (compOffset < 0 || compSize <= 0 || compOffset + compSize > blsData.Length)
                return null;

            byte[] decompBlock;
            try
            {
                decompBlock = DecompressBlock(blsData, compOffset, compSize);
            }
            catch
            {
                return null;
            }

            // Calculate which part of this block we need
            var skipInBlock = Math.Max(0, perm.DecompressedOffset - currentDecompOffset);
            var takeFromBlock = Math.Min(decompBlock.Length - skipInBlock, bytesNeeded);

            if (skipInBlock < decompBlock.Length && takeFromBlock > 0)
            {
                ms.Write(decompBlock, skipInBlock, takeFromBlock);
                bytesNeeded -= takeFromBlock;
            }

            currentDecompOffset += decompBlock.Length;
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     Decompresses a single block. If compressedSize == 0x4000, the block is stored raw
    ///     (uncompressed). Otherwise it is zlib 1.2.13 compressed.
    /// </summary>
    private static byte[] DecompressBlock(byte[] data, int offset, int compressedSize)
    {
        // Raw block: stored uncompressed when compressedSize == BlockDecompressedSize
        if (compressedSize == BlockDecompressedSize)
            return data.AsSpan(offset, compressedSize).ToArray();

        // Too small for zlib framing (2-byte header + data + 4-byte Adler32)
        if (compressedSize <= 6)
            return data.AsSpan(offset, compressedSize).ToArray();

        // zlib format: skip 2-byte header, strip 4-byte checksum, decompress deflate stream
        var deflateStart = offset + 2;
        var deflateSize = compressedSize - 6;

        using var compStream = new MemoryStream(data, deflateStart, deflateSize);
        using var deflate = new DeflateStream(compStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    ///     Extracts bytecode from a decompressed permutation blob using the flags-based
    ///     code offset calculation from the blob header, then reads pmDxShaderCode fields.
    ///     Blob header format:
    ///     +0  uint32 flags         — Bit0=hasBindInfo, Bit1=codeOffsetAt+8
    ///     +4  uint32 codeSize      — Size of code region (pmDxShaderCode + bytecode)
    ///     +8  uint32 codeOffset/shaderFlags — depends on flags bit1
    ///     +12 uint32 field3        — Always 0
    ///     Code offset determination:
    ///     flags &amp; 2 → codeOffset = value at +8
    ///     flags &amp; 1 (not &amp; 2) → codeOffset = 36 (20-byte old binding data)
    ///     otherwise → codeOffset = 16
    ///     pmDxShaderCode at codeOffset (56 bytes):
    ///     +0  uint32 version (must be 3)
    ///     +4  uint32 field1
    ///     +8  uint64 cbvBindMask
    ///     +16 uint64 srvBindMask_lo
    ///     +24 uint64 srvBindMask_hi
    ///     +32 uint64 uavBindMask
    ///     +40 uint64 samplerBindMask
    ///     +48 uint32 bytecodeSize
    ///     +52 uint32 bytecodeRelOffset
    ///     Bytecode starts at: codeOffset + 52 + bytecodeRelOffset
    /// </summary>
    private static (byte[]? bytecode, BlsPermutationInfo info) ExtractBytecodeFromBlob(byte[] blob)
    {
        var info = new BlsPermutationInfo();

        if (blob.Length < BlsBlobHeader.Size)
        {
            info.BytecodeFormat = BlsBytecodeFormat.Unknown;
            info.BytecodeSize = blob.Length;
            return (null, info);
        }

        var blobHeader = new BlsBlobHeader(blob);
        var codeOffset = blobHeader.CodeOffset;

        if (!blobHeader.Flags.HasFlag(BlsBlobFlags.CodeOffsetExplicit))
            info.ShaderFlags = blobHeader.ShaderFlags;

        if (blobHeader.CodeSize == 0)
        {
            info.BytecodeFormat = BlsBytecodeFormat.Empty;
            return (null, info);
        }

        // Parse extended binding info if present
        if (blobHeader.Flags.HasFlag(BlsBlobFlags.HasBindInfo) &&
            blobHeader.Flags.HasFlag(BlsBlobFlags.CodeOffsetExplicit) &&
            blob.Length >= 40)
            // v14 format: 24-byte binding info at +16
            info.ShaderFlags = BitConverter.ToUInt32(blob, 16);

        // Parse pmDxShaderCode at computed offset
        if (blob.Length < codeOffset + PmDxShaderCode.Size)
            // Blob too small for pmDxShaderCode, fall back to scanning
            return (FallbackScanBytecode(blob, info), info);

        var shaderCode = new PmDxShaderCode(blob.AsSpan(codeOffset));
        info.CodeVersion = shaderCode.Version;

        // Validate version (must be 3 for DX pmDxShaderCode)
        if (shaderCode.Version != 3)
        {
            // Check for Metal content at the code offset
            var metalResult = TryExtractMetalContent(blob, codeOffset, (int)blobHeader.CodeSize, info);
            if (metalResult != null) return (metalResult, info);

            return (FallbackScanBytecode(blob, info), info);
        }

        // Copy binding masks from pmDxShaderCode
        info.CbvBindMask = shaderCode.CbvBindMask;
        info.SrvBindMaskLo = shaderCode.SrvBindMaskLo;
        info.SrvBindMaskHi = shaderCode.SrvBindMaskHi;
        info.UavBindMask = shaderCode.UavBindMask;
        info.SamplerBindMask = shaderCode.SamplerBindMask;

        var bytecodeStart = shaderCode.BytecodeStart(codeOffset);
        info.BytecodeSize = (int)shaderCode.BytecodeSize;

        if (shaderCode.BytecodeSize == 0 || bytecodeStart + shaderCode.BytecodeSize > blob.Length)
        {
            info.BytecodeFormat = BlsBytecodeFormat.Invalid;
            return (FallbackScanBytecode(blob, info), info);
        }

        // Detect bytecode format
        var bcMagic = BitConverter.ToUInt32(blob, bytecodeStart);
        if (bcMagic == MagicDxbc)
        {
            var bcSpan = blob.AsSpan(bytecodeStart, (int)shaderCode.BytecodeSize);
            info.BytecodeFormat = ContainsDxilChunk(bcSpan) ? BlsBytecodeFormat.DXIL : BlsBytecodeFormat.DXBC;
        }
        else if (bcMagic == 0x4243) // "BC" — raw LLVM bitcode
        {
            info.BytecodeFormat = BlsBytecodeFormat.DXIL;
        }
        else
        {
            info.BytecodeFormat = BlsBytecodeFormat.Unknown;
        }

        var bytecode = new byte[shaderCode.BytecodeSize];
        Array.Copy(blob, bytecodeStart, bytecode, 0, (int)shaderCode.BytecodeSize);
        return (bytecode, info);
    }

    /// <summary>
    ///     Tries to extract Metal content (MTLB binary or Metal source text) from the blob.
    /// </summary>
    private static byte[]? TryExtractMetalContent(byte[] blob, int codeOffset, int codeSize, BlsPermutationInfo info)
    {
        if (codeOffset < 0 || codeOffset >= blob.Length) return null;

        var available = Math.Min(codeSize, blob.Length - codeOffset);
        if (available < 4) return null;

        var magic = BitConverter.ToUInt32(blob, codeOffset);

        if (magic == 0x00000001 && available >= MetalSubHeader.Size + 4)
        {
            var metalHeader = new MetalSubHeader(blob.AsSpan(codeOffset));
            var contentOffset = codeOffset + MetalSubHeader.Size;
            var contentSize = available - MetalSubHeader.Size;
            if (contentOffset >= blob.Length || contentSize <= 0) return null;
            contentSize = Math.Min(contentSize, blob.Length - contentOffset);

            var bytecode = new byte[contentSize];
            Array.Copy(blob, contentOffset, bytecode, 0, contentSize);
            info.BytecodeSize = contentSize;
            info.BytecodeFormat = metalHeader.BytecodeFormat;
            return bytecode;
        }

        // Direct MTLB magic (no sub-header)
        if (magic == MagicMtlb)
        {
            var bytecode = new byte[available];
            Array.Copy(blob, codeOffset, bytecode, 0, available);
            info.BytecodeSize = available;
            info.BytecodeFormat = BlsBytecodeFormat.MetalAIR;
            return bytecode;
        }

        // Direct Metal source text (starts with '#')
        if (blob[codeOffset] == 0x23 && available >= 16)
        {
            var prefix = Encoding.UTF8.GetString(blob, codeOffset, Math.Min(available, 64));
            if (prefix.StartsWith("#include <metal_"))
            {
                var bytecode = new byte[available];
                Array.Copy(blob, codeOffset, bytecode, 0, available);
                info.BytecodeSize = available;
                info.BytecodeFormat = BlsBytecodeFormat.MetalSource;
                return bytecode;
            }
        }

        return null;
    }

    /// <summary>
    ///     Fallback: scan blob for DXBC magic or LLVM bitcode when flags-based parsing fails.
    /// </summary>
    private static byte[]? FallbackScanBytecode(byte[] blob, BlsPermutationInfo info)
    {
        // Scan for DXBC magic
        for (var i = 0; i <= blob.Length - 4; i += 4)
            if (BitConverter.ToUInt32(blob, i) == MagicDxbc)
            {
                var bytecode = new byte[blob.Length - i];
                Array.Copy(blob, i, bytecode, 0, bytecode.Length);
                info.BytecodeSize = bytecode.Length;
                var bcSpan = blob.AsSpan(i);
                info.BytecodeFormat = ContainsDxilChunk(bcSpan) ? BlsBytecodeFormat.DXIL : BlsBytecodeFormat.DXBC;
                return bytecode;
            }

        // Scan for MTLB magic (must be before "BC" scan — MTLB contains LLVM bitcode internally)
        for (var i = 0; i <= blob.Length - 4; i += 4)
            if (BitConverter.ToUInt32(blob, i) == MagicMtlb)
            {
                var bytecode = new byte[blob.Length - i];
                Array.Copy(blob, i, bytecode, 0, bytecode.Length);
                info.BytecodeSize = bytecode.Length;
                info.BytecodeFormat = BlsBytecodeFormat.MetalAIR;
                return bytecode;
            }

        // Check for raw LLVM bitcode ("BC" = 0x4243)
        for (var i = 0; i <= blob.Length - 4; i += 4)
            if (blob[i] == 0x42 && blob[i + 1] == 0x43)
            {
                var bytecode = new byte[blob.Length - i];
                Array.Copy(blob, i, bytecode, 0, bytecode.Length);
                info.BytecodeSize = bytecode.Length;
                info.BytecodeFormat = BlsBytecodeFormat.DXIL;
                return bytecode;
            }

        // Scan for Metal source text
        for (var i = 0; i <= blob.Length - 16; i++)
            if (blob[i] == 0x23 && i + 16 <= blob.Length) // '#'
            {
                var prefix = Encoding.UTF8.GetString(blob, i, Math.Min(blob.Length - i, 64));
                if (prefix.StartsWith("#include <metal_"))
                {
                    var bytecode = new byte[blob.Length - i];
                    Array.Copy(blob, i, bytecode, 0, bytecode.Length);
                    info.BytecodeSize = bytecode.Length;
                    info.BytecodeFormat = BlsBytecodeFormat.MetalSource;
                    return bytecode;
                }
            }

        info.BytecodeFormat = BlsBytecodeFormat.Unknown;
        info.BytecodeSize = blob.Length;
        return null;
    }

    private static bool ContainsDxilChunk(ReadOnlySpan<byte> dxbcData)
    {
        if (dxbcData.Length < 32) return false;
        var chunkCount = BitConverter.ToUInt32(dxbcData.Slice(28, 4));
        var offsetBase = 32;

        for (var i = 0; i < chunkCount && offsetBase + (i + 1) * 4 <= dxbcData.Length; i++)
        {
            var chunkOffset = BitConverter.ToUInt32(dxbcData.Slice(offsetBase + i * 4, 4));
            if (chunkOffset + 4 > dxbcData.Length) continue;
            var fourcc = BitConverter.ToUInt32(dxbcData.Slice((int)chunkOffset, 4));
            // "DXIL" = 0x4C495844 — SM6+ bytecode
            // "ILDB" = 0x42444C49 — SM6+ bytecode with debug info
            // Note: "ILDN" is debug-only, not actual DXIL bytecode
            if (fourcc is 0x4C495844 or 0x42444C49)
                return true;
        }

        return false;
    }

    private static string NormalizeTag(string tag)
    {
        // Tags are stored reversed in the file (e.g., "05XD" -> "DX50")
        if (tag.Length >= 4)
        {
            var chars = tag.ToCharArray();
            Array.Reverse(chars);
            return new string(chars).Trim('\0').Trim();
        }

        return tag.Trim('\0').Trim();
    }
}