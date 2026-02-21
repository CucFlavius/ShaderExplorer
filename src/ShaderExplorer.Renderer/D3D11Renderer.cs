using System.Numerics;
using System.Runtime.InteropServices;
using ShaderExplorer.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using SystemValueType = ShaderExplorer.Core.Models.SystemValueType;

namespace ShaderExplorer.Renderer;

public class D3D11Renderer : IDisposable
{
    private const string DefaultVertexShader = @"
        cbuffer Transform : register(b0)
        {
            float4x4 worldViewProj;
            float4x4 world;
            float4x4 view;
            float4x4 projection;
            float4 cameraPos;
            float4 lightDir;
            float4 time;
        };
        struct VS_INPUT { float3 pos : POSITION; float3 normal : NORMAL; float3 tangent : TANGENT; float2 uv : TEXCOORD0; };
        struct VS_OUTPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL; float3 worldPos : TEXCOORD1; float3 tangent : TANGENT; };
        VS_OUTPUT main(VS_INPUT input)
        {
            VS_OUTPUT output;
            output.pos = mul(float4(input.pos, 1.0), worldViewProj);
            output.uv = input.uv;
            output.normal = normalize(mul(float4(input.normal, 0.0), world).xyz);
            output.worldPos = mul(float4(input.pos, 1.0), world).xyz;
            output.tangent = normalize(mul(float4(input.tangent, 0.0), world).xyz);
            return output;
        }
    ";

    private const string DefaultPixelShader = @"
        cbuffer Transform : register(b0)
        {
            float4x4 worldViewProj;
            float4x4 world;
            float4x4 view;
            float4x4 projection;
            float4 cameraPos;
            float4 lightDir;
            float4 time;
        };
        struct PS_INPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL; float3 worldPos : TEXCOORD1; float3 tangent : TANGENT; };
        float4 main(PS_INPUT input) : SV_TARGET
        {
            float3 N = normalize(input.normal);
            float3 L = normalize(lightDir.xyz);
            float3 V = normalize(cameraPos.xyz - input.worldPos);
            float3 H = normalize(L + V);
            float ndotl = saturate(dot(N, L));
            float ndoth = saturate(dot(N, H));
            float spec = pow(ndoth, 32.0);
            float3 ambient = float3(0.08, 0.08, 0.12);
            float3 diffuse = float3(0.7, 0.7, 0.75) * ndotl;
            float3 specular = float3(1.0, 1.0, 1.0) * spec;
            float2 uv = input.uv * 8.0;
            float checker = fmod(floor(uv.x) + floor(uv.y), 2.0);
            float3 baseColor = lerp(float3(0.3, 0.3, 0.35), float3(0.6, 0.6, 0.65), checker);
            float3 color = baseColor * (ambient + diffuse) + specular;
            return float4(color, 1.0);
        }
    ";

    private ID3D11DeviceContext? _context;
    private ID3D11PixelShader? _defaultPS;

    private ID3D11VertexShader? _defaultVS;
    private ID3D11Texture2D? _depthBuffer;
    private ID3D11DepthStencilState? _depthState;
    private ID3D11Device? _device;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11Buffer? _indexBuffer;
    private uint _indexCount;
    private ID3D11InputLayout? _inputLayout;

    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Buffer? _transformCB;
    private ID3D11Buffer? _userCB;

    private ID3D11PixelShader? _userPS;
    private ID3D11VertexShader? _userVS;

    private ID3D11Buffer? _vertexBuffer;

    public OrbitCamera Camera { get; } = new();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsInitialized => _device != null;

    public ID3D11Texture2D? RenderTarget { get; private set; }

    public ID3D11Device? Device => _device;

    public TextureManager? TextureManager { get; private set; }

    public void Dispose()
    {
        ClearUserShaders();
        TextureManager?.Dispose();
        _transformCB?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _defaultVS?.Dispose();
        _defaultPS?.Dispose();
        _inputLayout?.Dispose();
        _rasterizerState?.Dispose();
        _depthState?.Dispose();
        _rtv?.Dispose();
        _dsv?.Dispose();
        RenderTarget?.Dispose();
        _depthBuffer?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    public void Initialize(int width, int height)
    {
        Width = width;
        Height = height;

        FeatureLevel[] featureLevels = [FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _context);

        CreateResources();
        CreateDefaultShaders();
        CreateSphereMesh();
        TextureManager = new TextureManager(_device);
    }

    private void CreateResources()
    {
        if (_device == null) return;

        _rtv?.Dispose();
        _dsv?.Dispose();
        RenderTarget?.Dispose();
        _depthBuffer?.Dispose();

        if (Width <= 0 || Height <= 0) return;

        var rtDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.Shared
        };
        RenderTarget = _device.CreateTexture2D(rtDesc);
        _rtv = _device.CreateRenderTargetView(RenderTarget);

        var dsDesc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil
        };
        _depthBuffer = _device.CreateTexture2D(dsDesc);
        _dsv = _device.CreateDepthStencilView(_depthBuffer);
    }

    private static byte[] CompileShader(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, entryPoint, "shader", profile,
            out var blob, out var errorBlob);

        if (result.Failure || blob == null)
        {
            var error = "Unknown compilation error";
            if (errorBlob != null)
            {
                var errorSpan = errorBlob.AsBytes();
                error = Encoding.UTF8.GetString(errorSpan.ToArray()).TrimEnd('\0');
            }

            errorBlob?.Dispose();
            throw new InvalidOperationException($"Shader compilation failed: {error}");
        }

        errorBlob?.Dispose();
        var bytes = blob.AsBytes().ToArray();
        blob.Dispose();
        return bytes;
    }

    private void CreateDefaultShaders()
    {
        if (_device == null) return;

        var vsBytes = CompileShader(DefaultVertexShader, "main", "vs_5_0");
        _defaultVS = _device.CreateVertexShader(vsBytes);

        var layoutDesc = new InputElementDescription[]
        {
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 36, 0)
        };
        _inputLayout = _device.CreateInputLayout(layoutDesc, vsBytes);

        var psBytes = CompileShader(DefaultPixelShader, "main", "ps_5_0");
        _defaultPS = _device.CreatePixelShader(psBytes);

        // Transform constant buffer
        var cbSize = (uint)((Marshal.SizeOf<TransformConstants>() + 15) & ~15);
        _transformCB =
            _device.CreateBuffer(cbSize, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);

        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.Back,
            FrontCounterClockwise = false,
            DepthClipEnable = true
        });

        _depthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Less
        });
    }

    private void CreateSphereMesh()
    {
        if (_device == null) return;

        var sphere = new SphereMesh();

        _vertexBuffer = _device.CreateBuffer(
            sphere.Vertices,
            BindFlags.VertexBuffer);

        _indexBuffer = _device.CreateBuffer(
            sphere.Indices,
            BindFlags.IndexBuffer);

        _indexCount = (uint)sphere.Indices.Length;
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        Width = width;
        Height = height;
        CreateResources();
    }

    public void Render(float time = 0)
    {
        if (_device == null || _context == null || _rtv == null || _dsv == null) return;
        if (Width <= 0 || Height <= 0) return;

        _context.ClearRenderTargetView(_rtv, new Color4(0.1f, 0.1f, 0.15f));
        _context.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1.0f, 0);

        _context.OMSetRenderTargets(_rtv, _dsv);

        var viewport = new Viewport(0, 0, Width, Height, 0f, 1f);
        _context.RSSetViewport(viewport);

        if (_rasterizerState != null) _context.RSSetState(_rasterizerState);
        if (_depthState != null) _context.OMSetDepthStencilState(_depthState);

        var aspect = (float)Width / Height;
        var world = Matrix4x4.Identity;
        var view = Camera.ViewMatrix;
        var proj = Camera.ProjectionMatrix(aspect);
        var wvp = world * view * proj;

        var transforms = new TransformConstants
        {
            WorldViewProj = Matrix4x4.Transpose(wvp),
            World = Matrix4x4.Transpose(world),
            View = Matrix4x4.Transpose(view),
            Projection = Matrix4x4.Transpose(proj),
            CameraPos = new Vector4(Camera.Eye, 1.0f),
            LightDir = new Vector4(Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.3f)), 0.0f),
            Time = new Vector4(time, MathF.Sin(time), MathF.Cos(time), 0.016f)
        };

        var mapped = _context.Map(_transformCB!, MapMode.WriteDiscard);
        Marshal.StructureToPtr(transforms, mapped.DataPointer, false);
        _context.Unmap(_transformCB!, 0);

        _context.VSSetShader(_userVS ?? _defaultVS);
        _context.PSSetShader(_userPS ?? _defaultPS);
        _context.VSSetConstantBuffer(0, _transformCB);
        _context.PSSetConstantBuffer(0, _transformCB);

        if (_userCB != null)
            _context.PSSetConstantBuffer(1, _userCB);

        TextureManager?.BindToContext(_context);

        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer!, (uint)MeshVertex.SizeInBytes);
        _context.IASetIndexBuffer(_indexBuffer, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        _context.DrawIndexed(_indexCount, 0, 0);
        _context.Flush();
    }

    public void SetUserPixelShader(byte[] bytecode, List<SignatureElement>? psInputSignature = null)
    {
        _userPS?.Dispose();
        _userPS = null;
        _userVS?.Dispose();
        _userVS = null;

        if (_device == null) return;

        try
        {
            _userPS = _device.CreatePixelShader(bytecode);
        }
        catch
        {
            _userPS = null;
            return;
        }

        if (psInputSignature != null && psInputSignature.Count > 0)
            try
            {
                var vsHlsl = VertexShaderGenerator.GenerateCompatibleVertexShader(psInputSignature);
                var vsBytes = CompileShader(vsHlsl, "main", "vs_5_0");
                _userVS = _device.CreateVertexShader(vsBytes);
            }
            catch
            {
                _userVS = null; // fall back to default VS
            }
    }

    public void SetupTexturesFromReflection(List<ResourceBindingInfo> bindings)
    {
        TextureManager?.SetupFromReflection(bindings);
    }

    public void SetSlotTexture(int slot, TextureData data, string? filePath = null)
    {
        TextureManager?.SetTexture(slot, data, filePath);
    }

    public void ClearSlotTexture(int slot)
    {
        TextureManager?.ClearTexture(slot);
    }

    public void ClearUserShaders()
    {
        _userPS?.Dispose();
        _userVS?.Dispose();
        _userCB?.Dispose();
        _userPS = null;
        _userVS = null;
        _userCB = null;

        TextureManager?.Reset();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TransformConstants
    {
        public Matrix4x4 WorldViewProj;
        public Matrix4x4 World;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Vector4 CameraPos;
        public Vector4 LightDir;
        public Vector4 Time;
    }
}