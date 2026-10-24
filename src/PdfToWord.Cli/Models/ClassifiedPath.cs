using UglyToad.PdfPig.Graphics.Colors;

namespace PdfToWord.Cli.Models;

public readonly record struct LineSegment2D(
    double X1,
    double Y1,
    double X2,
    double Y2)
{
    public double Length => Math.Sqrt(((X2 - X1) * (X2 - X1)) + ((Y2 - Y1) * (Y2 - Y1)));
    public bool IsHorizontal(double tolerance = 1.2) => Math.Abs(Y2 - Y1) <= tolerance && Math.Abs(X2 - X1) > tolerance;
    public bool IsVertical(double tolerance = 1.2) => Math.Abs(X2 - X1) <= tolerance && Math.Abs(Y2 - Y1) > tolerance;
    public bool IsDiagonal(double tolerance = 1.5) => Math.Abs(X2 - X1) > tolerance && Math.Abs(Y2 - Y1) > tolerance;
    public PdfRect Bounds => new(Math.Min(X1, X2), Math.Min(Y1, Y2), Math.Max(X1, X2), Math.Max(Y1, Y2));
}

/// <summary>
/// Primitiva vetorial extraída do PdfPig com metadados geométricos e cromáticos pré-calculados.
/// </summary>
public sealed record ClassifiedPath(
    int PathIndex,
    PdfRect Bounds,
    bool IsFilled,
    bool IsStroked,
    string? FillColorHex,
    string? StrokeColorHex,
    double LineWidth,
    bool HasDiagonal,
    bool HasCurve,
    bool IsAxisAlignedRectangle,
    IReadOnlyList<LineSegment2D> Segments)
{
    public bool IsHorizontalLine =>
        !HasDiagonal && !HasCurve && Bounds.Width >= 10.0 && Bounds.Height <= 3.5;

    public bool IsVerticalLine =>
        !HasDiagonal && !HasCurve && Bounds.Height >= 10.0 && Bounds.Width <= 3.5;

    public bool IsWhiteFill =>
        FillColorHex is "FFFFFF" or "FEFEFE" or "FDFDFD";
}

public static class PdfColorHelper
{
    public static string? ToHex(IColor? color)
    {
        if (color == null)
            return null;

        try
        {
            var (r, g, b) = color.ToRGBValues();
            int ri = Math.Clamp((int)Math.Round(r * 255.0), 0, 255);
            int gi = Math.Clamp((int)Math.Round(g * 255.0), 0, 255);
            int bi = Math.Clamp((int)Math.Round(b * 255.0), 0, 255);
            return $"{ri:X2}{gi:X2}{bi:X2}";
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNearWhite(string? hex, int threshold = 254)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 6)
            return false;

        int r = Convert.ToInt32(hex[..2], 16);
        int g = Convert.ToInt32(hex[2..4], 16);
        int b = Convert.ToInt32(hex[4..6], 16);
        return r >= threshold && g >= threshold && b >= threshold;
    }

    public static bool IsDarkColor(string? hex, int threshold = 90)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length != 6)
            return false;

        int r = Convert.ToInt32(hex[..2], 16);
        int g = Convert.ToInt32(hex[2..4], 16);
        int b = Convert.ToInt32(hex[4..6], 16);
        double luminance = (0.299 * r) + (0.587 * g) + (0.114 * b);
        return luminance < threshold;
    }
}
