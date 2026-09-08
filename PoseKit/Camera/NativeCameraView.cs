using System;
using System.Numerics;

namespace PoseKit.Camera;

/// <summary>The native render loader reads four rows with MOVAPS, requiring 16-byte alignment.</summary>
internal static unsafe class NativeCameraView
{
    internal delegate void Loader(nint renderCamera, Matrix4x4* matrix);

    internal static void Validate(nint renderCamera, Matrix4x4* matrix)
    {
        if (renderCamera == 0)
            throw new InvalidOperationException("The render camera is unavailable.");
        if (matrix == null || ((nuint)matrix & 15) != 0)
            throw new InvalidOperationException("The camera view matrix must be 16-byte aligned.");
    }

    internal static void Load(Loader loader, nint renderCamera, Matrix4x4* matrix)
    {
        Validate(renderCamera, matrix);
        loader(renderCamera, matrix);
    }
}
