using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.DXGI;

namespace ShaderExplorer.Renderer;

public static class TextureLoader
{
    private const uint DdsMagic = 0x20534444; // "DDS "

    private const uint DdpfFourcc = 0x04;
    private const uint DdpfRgb = 0x40;
    private const uint DdpfAlphaPixels = 0x01;
    private const uint DdsCaps2Cubemap = 0x200;
    private const uint DdsCaps2CubemapAllFaces = 0xFC00;

    public static TextureData Load(byte[] fileBytes)
    {
        if (fileBytes.Length < 4)
            throw new InvalidDataException("File too small to be a texture");

        var magic = BitConverter.ToUInt32(fileBytes, 0);
        if (magic == DdsMagic)
            return LoadDds(fileBytes);

        return LoadWpfImage(fileBytes);
    }

    private static uint MakeFourCC(char a, char b, char c, char d)
    {
        return a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
    }

    private static TextureData LoadDds(byte[] fileBytes)
    {
        if (fileBytes.Length < 128)
            throw new InvalidDataException("DDS file too small for header");

        var header = MemoryMarshal.Read<DdsHeader>(fileBytes.AsSpan(4));

        if (header.Size != 124)
            throw new InvalidDataException($"Invalid DDS header size: {header.Size}");

        var width = (int)header.Width;
        var height = (int)header.Height;
        var mipLevels = Math.Max(1, (int)header.MipMapCount);

        var isCubemap = (header.Caps2 & DdsCaps2Cubemap) != 0;
        var arraySize = isCubemap ? 6 : 1;

        var dataOffset = 4 + 124; // magic + header
        Format format;

        var pf = header.PixelFormat;

        if ((pf.Flags & DdpfFourcc) != 0)
        {
            var fourcc = pf.FourCC;
            if (fourcc == MakeFourCC('D', 'X', '1', '0'))
            {
                // DXT10 extended header
                if (fileBytes.Length < dataOffset + 20)
                    throw new InvalidDataException("DDS file too small for DXT10 header");

                var dxt10 = MemoryMarshal.Read<DdsHeaderDxt10>(fileBytes.AsSpan(dataOffset));
                dataOffset += 20;
                format = (Format)dxt10.DxgiFormat;
                arraySize = Math.Max(1, (int)dxt10.ArraySize);

                if ((dxt10.MiscFlag & 0x4) != 0) // DDS_RESOURCE_MISC_TEXTURECUBE
                    isCubemap = true;
            }
            else
            {
                format = FourCCToFormat(fourcc);
            }
        }
        else
        {
            format = PixelFormatToFormat(pf);
        }

        if (format == Format.Unknown)
            throw new InvalidDataException("Unsupported DDS pixel format");

        // Build subresource slices
        var subresources = new SubresourceSlice[mipLevels * arraySize];
        var offset = dataOffset;

        for (var item = 0; item < arraySize; item++)
        {
            var mipW = width;
            var mipH = height;
            for (var mip = 0; mip < mipLevels; mip++)
            {
                int rowPitch, slicePitch;
                ComputePitch(format, mipW, mipH, out rowPitch, out slicePitch);

                var idx = item * mipLevels + mip;
                subresources[idx] = new SubresourceSlice
                {
                    Offset = offset,
                    RowPitch = rowPitch,
                    SlicePitch = slicePitch
                };
                offset += slicePitch;

                mipW = Math.Max(1, mipW / 2);
                mipH = Math.Max(1, mipH / 2);
            }
        }

        // Extract pixel data (everything after headers)
        var dataSize = fileBytes.Length - dataOffset;
        var pixels = new byte[dataSize];
        Buffer.BlockCopy(fileBytes, dataOffset, pixels, 0, dataSize);

        return new TextureData
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            MipLevels = mipLevels,
            ArraySize = arraySize,
            Format = format,
            IsCubemap = isCubemap,
            Subresources = subresources
        };
    }

    private static Format FourCCToFormat(uint fourcc)
    {
        if (fourcc == MakeFourCC('D', 'X', 'T', '1')) return Format.BC1_UNorm;
        if (fourcc == MakeFourCC('D', 'X', 'T', '3')) return Format.BC2_UNorm;
        if (fourcc == MakeFourCC('D', 'X', 'T', '5')) return Format.BC3_UNorm;
        if (fourcc == MakeFourCC('A', 'T', 'I', '1')) return Format.BC4_UNorm;
        if (fourcc == MakeFourCC('B', 'C', '4', 'U')) return Format.BC4_UNorm;
        if (fourcc == MakeFourCC('B', 'C', '4', 'S')) return Format.BC4_SNorm;
        if (fourcc == MakeFourCC('A', 'T', 'I', '2')) return Format.BC5_UNorm;
        if (fourcc == MakeFourCC('B', 'C', '5', 'U')) return Format.BC5_UNorm;
        if (fourcc == MakeFourCC('B', 'C', '5', 'S')) return Format.BC5_SNorm;

        // D3DFMT codes
        if (fourcc == 36) return Format.R16G16B16A16_UNorm;
        if (fourcc == 110) return Format.R16G16B16A16_SNorm;
        if (fourcc == 111) return Format.R16_Float;
        if (fourcc == 112) return Format.R16G16_Float;
        if (fourcc == 113) return Format.R16G16B16A16_Float;
        if (fourcc == 114) return Format.R32_Float;
        if (fourcc == 115) return Format.R32G32_Float;
        if (fourcc == 116) return Format.R32G32B32A32_Float;

        return Format.Unknown;
    }

    private static Format PixelFormatToFormat(DdsPixelFormat pf)
    {
        var hasAlpha = (pf.Flags & DdpfAlphaPixels) != 0;

        if ((pf.Flags & DdpfRgb) != 0)
            if (pf.RGBBitCount == 32)
            {
                if (pf.RBitMask == 0x00FF0000 && pf.GBitMask == 0x0000FF00 &&
                    pf.BBitMask == 0x000000FF && pf.ABitMask == 0xFF000000)
                    return Format.B8G8R8A8_UNorm;

                if (pf.RBitMask == 0x000000FF && pf.GBitMask == 0x0000FF00 &&
                    pf.BBitMask == 0x00FF0000 && pf.ABitMask == 0xFF000000)
                    return Format.R8G8B8A8_UNorm;

                if (pf.RBitMask == 0x00FF0000 && pf.GBitMask == 0x0000FF00 &&
                    pf.BBitMask == 0x000000FF && !hasAlpha)
                    return Format.B8G8R8X8_UNorm;
            }

        return Format.Unknown;
    }

    private static bool IsBlockCompressed(Format format)
    {
        return format switch
        {
            Format.BC1_UNorm or Format.BC1_UNorm_SRgb or Format.BC1_Typeless => true,
            Format.BC2_UNorm or Format.BC2_UNorm_SRgb or Format.BC2_Typeless => true,
            Format.BC3_UNorm or Format.BC3_UNorm_SRgb or Format.BC3_Typeless => true,
            Format.BC4_UNorm or Format.BC4_SNorm or Format.BC4_Typeless => true,
            Format.BC5_UNorm or Format.BC5_SNorm or Format.BC5_Typeless => true,
            Format.BC6H_Uf16 or Format.BC6H_Sf16 or Format.BC6H_Typeless => true,
            Format.BC7_UNorm or Format.BC7_UNorm_SRgb or Format.BC7_Typeless => true,
            _ => false
        };
    }

    private static int BlockSize(Format format)
    {
        return format switch
        {
            Format.BC1_UNorm or Format.BC1_UNorm_SRgb or Format.BC1_Typeless => 8,
            Format.BC4_UNorm or Format.BC4_SNorm or Format.BC4_Typeless => 8,
            _ => 16
        };
    }

    private static int BitsPerPixel(Format format)
    {
        return format switch
        {
            Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt => 128,
            Format.R32G32B32_Float or Format.R32G32B32_UInt or Format.R32G32B32_SInt => 96,
            Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm or Format.R16G16B16A16_SNorm
                or Format.R16G16B16A16_UInt or Format.R16G16B16A16_SInt => 64,
            Format.R32G32_Float or Format.R32G32_UInt or Format.R32G32_SInt => 64,
            Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R8G8B8A8_UInt
                or Format.R8G8B8A8_SInt or Format.R8G8B8A8_SNorm => 32,
            Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8X8_UNorm => 32,
            Format.R16G16_Float or Format.R16G16_UNorm or Format.R16G16_SNorm => 32,
            Format.R32_Float or Format.R32_UInt or Format.R32_SInt => 32,
            Format.R16_Float or Format.R16_UNorm => 16,
            Format.R8_UNorm => 8,
            _ => 32 // default guess
        };
    }

    private static void ComputePitch(Format format, int width, int height, out int rowPitch, out int slicePitch)
    {
        if (IsBlockCompressed(format))
        {
            var blockW = Math.Max(1, (width + 3) / 4);
            var blockH = Math.Max(1, (height + 3) / 4);
            var bs = BlockSize(format);
            rowPitch = blockW * bs;
            slicePitch = rowPitch * blockH;
        }
        else
        {
            var bpp = BitsPerPixel(format);
            rowPitch = (width * bpp + 7) / 8;
            slicePitch = rowPitch * height;
        }
    }

    // ═══ WPF Image Loading (PNG, JPG, BMP) ═══

    public static TextureData LoadWpfImage(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        // Convert to Pbgra32 (premultiplied BGRA, matches B8G8R8A8_UNorm)
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return new TextureData
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            IsCubemap = false,
            Subresources =
            [
                new SubresourceSlice
                {
                    Offset = 0,
                    RowPitch = stride,
                    SlicePitch = stride * height
                }
            ]
        };
    }

    // ═══ DDS Loading ═══

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DdsHeader
    {
        public uint Size;
        public uint Flags;
        public uint Height;
        public uint Width;
        public uint PitchOrLinearSize;
        public uint Depth;
        public uint MipMapCount;
        public unsafe fixed uint Reserved1[11];
        public DdsPixelFormat PixelFormat;
        public uint Caps;
        public uint Caps2;
        public uint Caps3;
        public uint Caps4;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DdsPixelFormat
    {
        public uint Size;
        public uint Flags;
        public uint FourCC;
        public uint RGBBitCount;
        public uint RBitMask;
        public uint GBitMask;
        public uint BBitMask;
        public uint ABitMask;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DdsHeaderDxt10
    {
        public uint DxgiFormat;
        public uint ResourceDimension;
        public uint MiscFlag;
        public uint ArraySize;
        public uint MiscFlags2;
    }
}