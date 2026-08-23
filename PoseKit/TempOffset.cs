namespace PoseKit;

/// <summary>Uncommitted, in-progress offset edit driven by the numeric drag fields in the main window.
/// Consumed by OffsetEngine every frame until the user hits Save (captured into a NamedPose) or Reset.</summary>
public sealed class TempOffset
{
    public bool Active;
    public PoseOffset Offset = PoseOffset.Zero;

    public void Reset()
    {
        Active = false;
        Offset = PoseOffset.Zero;
    }
}
