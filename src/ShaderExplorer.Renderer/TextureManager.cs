using System.Runtime.InteropServices;
using ShaderExplorer.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShaderExplorer.Renderer;

public class TextureManager : IDisposable
{
    // Cache loaded textures by file path
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly ID3D11Device _device;
    private readonly Dictionary<int, ID3D11SamplerState> _samplerSlots = new();

    // Per-slot state
    private readonly Dictionary<int, SlotEntry> _textureSlots = new();
    private ID3D11ShaderResourceView? _blackSRV;
    private ID3D11Texture2D? _blackTex;
    private ID3D11ShaderResourceView? _flatNormalSRV;
    private ID3D11Texture2D? _flatNormalTex;
    private ID3D11ShaderResourceView? _whiteSRV;

    // Placeholders
    private ID3D11Texture2D? _whiteTex;

    public TextureManager(ID3D11Device device)
    {
        _device = device;
        InitializePlaceholders();
    }

    public void Dispose()
    {
        Reset();

        _whiteSRV?.Dispose();
        _whiteTex?.Dispose();
        _flatNormalSRV?.Dispose();
        _flatNormalTex?.Dispose();
        _blackSRV?.Dispose();
        _blackTex?.Dispose();
    }

    private void InitializePlaceholders()
    {
        // White 1x1
        (_whiteTex, _whiteSRV) = CreateSolidTexture(255, 255, 255, 255);

        // Flat normal 1x1 (128, 128, 255, 255 = tangent-space (0,0,1))
        (_flatNormalTex, _flatNormalSRV) = CreateSolidTexture(128, 128, 255, 255);

        // Black 1x1
        (_blackTex, _blackSRV) = CreateSolidTexture(0, 0, 0, 255);
    }

    private (ID3D11Texture2D tex, ID3D11ShaderResourceView srv) CreateSolidTexture(byte r, byte g, byte b, byte a)
    {
        byte[] pixels = [b, g, r, a]; // BGRA order for B8G8R8A8

        var desc = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource
        };

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var initData = new SubresourceData(handle.AddrOfPinnedObject(), 4);
            var tex = _device.CreateTexture2D(desc, [initData]);
            var srv = _device.CreateShaderResourceView(tex);
            return (tex, srv);
        }
        finally
        {
            handle.Free();
        }
    }

    public void SetupFromReflection(List<ResourceBindingInfo> bindings)
    {
        Reset();

        foreach (var binding in bindings)
            if (binding.Type == ResourceType.Texture)
            {
                var placeholder = ChoosePlaceholder(binding.Name);
                var entry = new SlotEntry
                {
                    ResourceName = binding.Name,
                    PlaceholderSRV = placeholder
                };
                _textureSlots[binding.BindPoint] = entry;
            }
            else if (binding.Type == ResourceType.Sampler)
            {
                if (!_samplerSlots.ContainsKey(binding.BindPoint))
                {
                    var samplerDesc = new SamplerDescription
                    {
                        Filter = Filter.MinMagMipLinear,
                        AddressU = TextureAddressMode.Wrap,
                        AddressV = TextureAddressMode.Wrap,
                        AddressW = TextureAddressMode.Wrap,
                        MaxAnisotropy = 4,
                        ComparisonFunc = ComparisonFunction.Never,
                        MinLOD = 0,
                        MaxLOD = float.MaxValue
                    };
                    _samplerSlots[binding.BindPoint] = _device.CreateSamplerState(samplerDesc);
                }
            }
    }

    private ID3D11ShaderResourceView ChoosePlaceholder(string resourceName)
    {
        var lower = resourceName.ToLowerInvariant();
        if (lower.Contains("normal") || lower.Contains("bump"))
            return _flatNormalSRV!;
        return _whiteSRV!;
    }

    public void SetTexture(int slot, TextureData data, string? filePath = null)
    {
        if (!_textureSlots.TryGetValue(slot, out var entry))
            return;

        // If slot already has a user texture, release it
        ReleaseSlotUserTexture(entry);

        // Check cache
        if (filePath != null && _cache.TryGetValue(filePath, out var cached))
        {
            cached.RefCount++;
            entry.UserSRV = cached.SRV;
            entry.FilePath = filePath;
            return;
        }

        // Create GPU texture
        var (tex, srv) = CreateTextureFromData(data);
        entry.UserSRV = srv;
        entry.FilePath = filePath;

        if (filePath != null)
            _cache[filePath] = new CacheEntry
            {
                Texture = tex,
                SRV = srv,
                RefCount = 1
            };
    }

    public void ClearTexture(int slot)
    {
        if (!_textureSlots.TryGetValue(slot, out var entry))
            return;

        ReleaseSlotUserTexture(entry);
        entry.UserSRV = null;
        entry.FilePath = null;
    }

    private void ReleaseSlotUserTexture(SlotEntry entry)
    {
        if (entry.FilePath != null && _cache.TryGetValue(entry.FilePath, out var cached))
        {
            cached.RefCount--;
            if (cached.RefCount <= 0)
            {
                cached.SRV?.Dispose();
                cached.Texture?.Dispose();
                _cache.Remove(entry.FilePath);
            }
        }
        else if (entry.UserSRV != null)
        {
            // Not cached — dispose directly
            entry.UserSRV.Dispose();
        }
    }

    private (ID3D11Texture2D tex, ID3D11ShaderResourceView srv) CreateTextureFromData(TextureData data)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)data.Width,
            Height = (uint)data.Height,
            MipLevels = (uint)data.MipLevels,
            ArraySize = (uint)data.ArraySize,
            Format = data.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            MiscFlags = data.IsCubemap ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None
        };

        var handle = GCHandle.Alloc(data.Pixels, GCHandleType.Pinned);
        try
        {
            var basePtr = handle.AddrOfPinnedObject();
            var initData = new SubresourceData[data.Subresources.Length];
            for (var i = 0; i < data.Subresources.Length; i++)
            {
                var sub = data.Subresources[i];
                initData[i] = new SubresourceData(
                    basePtr + sub.Offset,
                    (uint)sub.RowPitch,
                    (uint)sub.SlicePitch);
            }

            var tex = _device.CreateTexture2D(desc, initData);

            var srvDesc = new ShaderResourceViewDescription();
            if (data.IsCubemap)
            {
                srvDesc.Format = data.Format;
                srvDesc.ViewDimension = ShaderResourceViewDimension.TextureCube;
                srvDesc.TextureCube.MipLevels = (uint)data.MipLevels;
                srvDesc.TextureCube.MostDetailedMip = 0;
            }
            else if (data.ArraySize > 1)
            {
                srvDesc.Format = data.Format;
                srvDesc.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                srvDesc.Texture2DArray.MipLevels = (uint)data.MipLevels;
                srvDesc.Texture2DArray.MostDetailedMip = 0;
                srvDesc.Texture2DArray.ArraySize = (uint)data.ArraySize;
                srvDesc.Texture2DArray.FirstArraySlice = 0;
            }
            else
            {
                srvDesc.Format = data.Format;
                srvDesc.ViewDimension = ShaderResourceViewDimension.Texture2D;
                srvDesc.Texture2D.MipLevels = (uint)data.MipLevels;
                srvDesc.Texture2D.MostDetailedMip = 0;
            }

            var srv = _device.CreateShaderResourceView(tex, srvDesc);
            return (tex, srv);
        }
        finally
        {
            handle.Free();
        }
    }

    public void BindToContext(ID3D11DeviceContext context)
    {
        foreach (var (slot, entry) in _textureSlots)
        {
            var srv = entry.UserSRV ?? entry.PlaceholderSRV;
            if (srv != null)
                context.PSSetShaderResource((uint)slot, srv);
        }

        foreach (var (slot, sampler) in _samplerSlots) context.PSSetSampler((uint)slot, sampler);
    }

    public IReadOnlyDictionary<int, string> GetSlotNames()
    {
        var result = new Dictionary<int, string>();
        foreach (var (slot, entry) in _textureSlots)
            result[slot] = entry.ResourceName;
        return result;
    }

    public string? GetSlotFilePath(int slot)
    {
        return _textureSlots.TryGetValue(slot, out var entry) ? entry.FilePath : null;
    }

    public void Reset()
    {
        // Dispose user textures and cache
        foreach (var entry in _textureSlots.Values) ReleaseSlotUserTexture(entry);
        _textureSlots.Clear();

        // Dispose any remaining cache entries
        foreach (var cached in _cache.Values)
        {
            cached.SRV?.Dispose();
            cached.Texture?.Dispose();
        }

        _cache.Clear();

        // Dispose samplers
        foreach (var sampler in _samplerSlots.Values)
            sampler.Dispose();
        _samplerSlots.Clear();
    }

    private class SlotEntry
    {
        public string? FilePath;
        public ID3D11ShaderResourceView? PlaceholderSRV;
        public string ResourceName = "";
        public ID3D11ShaderResourceView? UserSRV;
    }

    private class CacheEntry
    {
        public int RefCount;
        public ID3D11ShaderResourceView? SRV;
        public ID3D11Texture2D? Texture;
    }
}