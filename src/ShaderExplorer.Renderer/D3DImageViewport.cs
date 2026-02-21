using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vortice.Direct3D9;
using Vortice.DXGI;

namespace ShaderExplorer.Renderer;

public class D3DImageViewport : Image
{
    private IDirect3D9Ex? _d3d9;
    private IDirect3DDevice9Ex? _d3d9Device;
    private D3DImage? _d3dImage;
    private bool _isDragging;
    private System.Drawing.Point _lastMousePos;
    private DispatcherTimer? _renderTimer;
    private IDirect3DTexture9? _sharedTexture9;
    private float _time;

    public D3D11Renderer? Renderer { get; private set; }

    public void Initialize()
    {
        _d3dImage = new D3DImage();
        Source = _d3dImage;
        Stretch = Stretch.Fill;

        Renderer = new D3D11Renderer();
        var w = Math.Max((int)ActualWidth, 64);
        var h = Math.Max((int)ActualHeight, 64);
        Renderer.Initialize(w, h);

        CreateD3D9Device();
        CreateSharedSurface();
        RenderFrame();

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += (_, _) =>
        {
            _time += 0.033f;
            RenderFrame();
        };

        SizeChanged += OnSizeChanged;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
    }

    private void CreateD3D9Device()
    {
        _d3d9 = D3D9.Direct3DCreate9Ex();

        var presentParams = new Vortice.Direct3D9.PresentParameters
        {
            Windowed = true,
            SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
            PresentationInterval = PresentInterval.Immediate,
            BackBufferFormat = Vortice.Direct3D9.Format.Unknown,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            DeviceWindowHandle = GetDesktopWindow()
        };

        _d3d9Device = _d3d9.CreateDeviceEx(0,
            DeviceType.Hardware,
            IntPtr.Zero,
            CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
            presentParams);
    }

    private void CreateSharedSurface()
    {
        if (Renderer?.RenderTarget == null || _d3d9Device == null || Renderer.Width <= 0 || Renderer.Height <= 0)
            return;

        _sharedTexture9?.Dispose();

        using var dxgiResource = Renderer.RenderTarget.QueryInterface<IDXGIResource>();
        var sharedHandle = dxgiResource.SharedHandle;

        var handle = sharedHandle;
        _sharedTexture9 = _d3d9Device.CreateTexture(
            (uint)Renderer.Width, (uint)Renderer.Height, 1,
            Vortice.Direct3D9.Usage.RenderTarget,
            Vortice.Direct3D9.Format.A8R8G8B8,
            Pool.Default,
            ref handle);
    }

    public void RenderFrame()
    {
        if (Renderer == null || _d3dImage == null || _sharedTexture9 == null) return;
        if (Renderer.Width <= 0 || Renderer.Height <= 0) return;

        Renderer.Render(_time);

        _d3dImage.Lock();
        try
        {
            using var surface = _sharedTexture9.GetSurfaceLevel(0);
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, Renderer.Width, Renderer.Height));
        }
        finally
        {
            _d3dImage.Unlock();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = Math.Max((int)ActualWidth, 1);
        var h = Math.Max((int)ActualHeight, 1);

        Renderer?.Resize(w, h);
        CreateSharedSurface();
        RenderFrame();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        var pos = e.GetPosition(this);
        _lastMousePos = new System.Drawing.Point((int)pos.X, (int)pos.Y);
        CaptureMouse();
        _renderTimer?.Start();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
        _renderTimer?.Stop();
        RenderFrame();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Renderer == null) return;

        var pos = e.GetPosition(this);
        var dx = (int)pos.X - _lastMousePos.X;
        var dy = (int)pos.Y - _lastMousePos.Y;
        _lastMousePos = new System.Drawing.Point((int)pos.X, (int)pos.Y);

        Renderer.Camera.Rotate(dx * 0.01f, dy * 0.01f);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Renderer?.Camera.Zoom(-e.Delta * 0.002f);
        RenderFrame();
    }

    public void Shutdown()
    {
        _renderTimer?.Stop();
        _sharedTexture9?.Dispose();
        _d3d9Device?.Dispose();
        _d3d9?.Dispose();
        Renderer?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}