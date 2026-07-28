namespace Hypertree.Layout;

/// <summary>
/// A minimal axis-aligned rectangle for world-space layout maths. Core has no Avalonia dependency, so it
/// can't use <c>Avalonia.Rect</c>; the app converts to/from its own rect type at the drawing edge.
/// <see cref="X"/>/<see cref="Y"/> are the top-left corner; width and height extend right and down.
/// </summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    /// <summary>Translate by (<paramref name="dx"/>, <paramref name="dy"/>) — the camera turns a world rect
    /// into a screen rect by offsetting it.</summary>
    public LayoutRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
}
