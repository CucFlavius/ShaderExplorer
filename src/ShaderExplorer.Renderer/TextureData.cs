using Vortice.DXGI;

namespace ShaderExplorer.Renderer;

public class TextureData
{
    public byte[] Pixels { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipLevels { get; set; } = 1;
    public int ArraySize { get; set; } = 1;
    public Format Format { get; set; } = Format.B8G8R8A8_UNorm;
    public bool IsCubemap { get; set; }
    public SubresourceSlice[] Subresources { get; set; } = [];
}

public struct SubresourceSlice
{
    public int Offset;
    public int RowPitch;
    public int SlicePitch;
}