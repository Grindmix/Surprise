using System;

namespace Surprise.Core;

public readonly record struct PlacementSize(double Width, double Height);
public static class PlacementGeometry
{
    public static PlacementSize Resize(double width, double height, double dx, double dy, double maxWidth, double maxHeight, bool preserveAspect, double aspect)
    {
        maxWidth = Math.Max(1, maxWidth); maxHeight = Math.Max(1, maxHeight);
        double minWidth = Math.Min(16, maxWidth), minHeight = Math.Min(16, maxHeight);
        if (!preserveAspect || !double.IsFinite(aspect) || aspect <= 0)
            return new(Math.Clamp(width + dx, minWidth, maxWidth), Math.Clamp(height + dy, minHeight, maxHeight));
        double desiredWidth = Math.Abs(dx) >= Math.Abs(dy * aspect) ? width + dx : (height + dy) * aspect;
        double upper = Math.Min(maxWidth, maxHeight * aspect);
        double lower = Math.Min(upper, Math.Max(minWidth, minHeight * aspect));
        double w = Math.Clamp(desiredWidth, lower, upper);
        return new(w, w / aspect);
    }
    public static (double X, double Y) Center(double canvasWidth, double canvasHeight, double width, double height) => (Math.Max(0, (canvasWidth - width) / 2), Math.Max(0, (canvasHeight - height) / 2));
}
