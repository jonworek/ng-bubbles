namespace NgBubbles.Core;

public sealed record WindowSettings(double Width = 1400, double Height = 900, bool Maximized = false)
{
    public (int Width, int Height) Fit(int workWidth, int workHeight, double scale)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var width = double.IsFinite(Width) && Width >= 480 && Width <= 10000 ? Width : 1400;
        var height = double.IsFinite(Height) && Height >= 320 && Height <= 10000 ? Height : 900;
        return ((int)Math.Clamp(Math.Round(width * scale), 1, Math.Max(1, workWidth - 32)),
            (int)Math.Clamp(Math.Round(height * scale), 1, Math.Max(1, workHeight - 32)));
    }
}
