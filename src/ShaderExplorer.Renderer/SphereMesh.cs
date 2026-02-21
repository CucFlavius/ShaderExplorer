using System.Numerics;
using System.Runtime.InteropServices;

namespace ShaderExplorer.Renderer;

[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector2 TexCoord;

    public static readonly int SizeInBytes = Marshal.SizeOf<MeshVertex>();
}

public class SphereMesh
{
    public SphereMesh(int slices = 32, int stacks = 16)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<ushort>();

        // Generate vertices
        for (var stack = 0; stack <= stacks; stack++)
        {
            var phi = MathF.PI * stack / stacks;
            var sinPhi = MathF.Sin(phi);
            var cosPhi = MathF.Cos(phi);

            for (var slice = 0; slice <= slices; slice++)
            {
                var theta = 2.0f * MathF.PI * slice / slices;
                var sinTheta = MathF.Sin(theta);
                var cosTheta = MathF.Cos(theta);

                var normal = new Vector3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                var tangent = new Vector3(-sinTheta, 0, cosTheta);

                vertices.Add(new MeshVertex
                {
                    Position = normal, // unit sphere
                    Normal = normal,
                    Tangent = tangent,
                    TexCoord = new Vector2((float)slice / slices, (float)stack / stacks)
                });
            }
        }

        // Generate indices
        for (var stack = 0; stack < stacks; stack++)
        for (var slice = 0; slice < slices; slice++)
        {
            var row1 = stack * (slices + 1);
            var row2 = (stack + 1) * (slices + 1);

            indices.Add((ushort)(row1 + slice));
            indices.Add((ushort)(row2 + slice));
            indices.Add((ushort)(row1 + slice + 1));

            indices.Add((ushort)(row1 + slice + 1));
            indices.Add((ushort)(row2 + slice));
            indices.Add((ushort)(row2 + slice + 1));
        }

        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
    }

    public MeshVertex[] Vertices { get; }
    public ushort[] Indices { get; }
}