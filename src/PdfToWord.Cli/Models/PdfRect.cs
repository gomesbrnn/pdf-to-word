using UglyToad.PdfPig.Core;

namespace PdfToWord.Cli.Models;

/// <summary>
/// Representa um retângulo delimitador no sistema de coordenadas do PDF
/// (Origem Bottom-Left, unidade em pontos tipográficos: 1/72 polegada).
/// </summary>
public readonly record struct PdfRect(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => Math.Max(0, MaxX - MinX);
    public double Height => Math.Max(0, MaxY - MinY);
    public double Area => Width * Height;
    public double CenterX => (MinX + MaxX) * 0.5;
    public double CenterY => (MinY + MaxY) * 0.5;

    public static PdfRect FromPdfPig(PdfRectangle rect) =>
        new(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Bottom, rect.Top),
            Math.Max(rect.Left, rect.Right),
            Math.Max(rect.Bottom, rect.Top)
        );

    /// <summary>
    /// Retorna a união (Envelope Agregado) entre este retângulo e outro.
    /// </summary>
    public PdfRect Union(PdfRect other) =>
        new(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY)
        );

    /// <summary>
    /// Expande o retângulo aplicando uma margem de respiro (padding) e restringindo aos limites da página.
    /// </summary>
    public PdfRect Inflate(double paddingX, double paddingY, double pageWidth, double pageHeight) =>
        new(
            Math.Max(0, MinX - paddingX),
            Math.Max(0, MinY - paddingY),
            Math.Min(pageWidth, MaxX + paddingX),
            Math.Min(pageHeight, MaxY + paddingY)
        );

    public bool Contains(PdfRect other, double tolerance = 1.5) =>
        other.MinX >= MinX - tolerance &&
        other.MaxX <= MaxX + tolerance &&
        other.MinY >= MinY - tolerance &&
        other.MaxY <= MaxY + tolerance;

    public bool ContainsPoint(double x, double y, double tolerance = 1.0) =>
        x >= MinX - tolerance &&
        x <= MaxX + tolerance &&
        y >= MinY - tolerance &&
        y <= MaxY + tolerance;

    public bool Intersects(PdfRect other, double tolerance = 0.0) =>
        !(other.MinX > MaxX + tolerance ||
          other.MaxX < MinX - tolerance ||
          other.MinY > MaxY + tolerance ||
          other.MaxY < MinY - tolerance);

    public double VerticalOverlapRatio(PdfRect other)
    {
        double overlap = Math.Max(0, Math.Min(MaxY, other.MaxY) - Math.Max(MinY, other.MinY));
        double minHeight = Math.Min(Height, other.Height);
        return minHeight <= 0.01 ? 0 : overlap / minHeight;
    }

    public double HorizontalOverlapRatio(PdfRect other)
    {
        double overlap = Math.Max(0, Math.Min(MaxX, other.MaxX) - Math.Max(MinX, other.MinX));
        double minWidth = Math.Min(Width, other.Width);
        return minWidth <= 0.01 ? 0 : overlap / minWidth;
    }

    /// <summary>
    /// Calcula a menor distância euclidiana entre as bordas de dois retângulos (0 se houver interseção).
    /// </summary>
    public double EuclideanDistanceTo(PdfRect other)
    {
        double dx = Math.Max(0, Math.Max(MinX - other.MaxX, other.MinX - MaxX));
        double dy = Math.Max(0, Math.Max(MinY - other.MaxY, other.MinY - MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() =>
        $"[{MinX:F1}, {MinY:F1} -> {MaxX:F1}, {MaxY:F1} ({Width:F1}x{Height:F1})]";
}
