using System.Numerics;

namespace ShaderExplorer.Renderer;

public class OrbitCamera
{
    public float Distance { get; set; } = 3.0f;
    public float Yaw { get; set; }
    public float Pitch { get; set; } = 0.3f;
    public Vector3 Target { get; set; } = Vector3.Zero;
    public float FieldOfView { get; set; } = MathF.PI / 4.0f;
    public float NearPlane { get; set; } = 0.01f;
    public float FarPlane { get; set; } = 100.0f;

    public Vector3 Eye
    {
        get
        {
            var x = Distance * MathF.Cos(Pitch) * MathF.Sin(Yaw);
            var y = Distance * MathF.Sin(Pitch);
            var z = Distance * MathF.Cos(Pitch) * MathF.Cos(Yaw);
            return Target + new Vector3(x, y, z);
        }
    }

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix(float aspectRatio)
    {
        return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspectRatio, NearPlane, FarPlane);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -MathF.PI / 2.0f + 0.01f, MathF.PI / 2.0f - 0.01f);
    }

    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance + delta, 0.5f, 50.0f);
    }
}