using PdfToWord.Cli.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace PdfToWord.Cli.Layout;

/// <summary>
/// Extrai e classifica todos os caminhos vetoriais (PdfPath) de uma página PDF.
/// </summary>
public static class VectorPathClassifier
{
    /// <summary>
    /// Extrai os retângulos delimitadores de pequenos pontos/círculos vetoriais preenchidos
    /// que representam marcadores de lista (bullets '•') desenhados como curvas no PDF.
    /// </summary>
    public static IReadOnlyList<PdfRect> ExtractVectorBullets(Page page)
    {
        var rawPaths = page.ExperimentalAccess.Paths;
        if (rawPaths == null || rawPaths.Count == 0)
            return Array.Empty<PdfRect>();

        var bullets = new List<PdfRect>();

        foreach (var pdfPath in rawPaths)
        {
            if (pdfPath.IsClipping || !pdfPath.IsFilled)
                continue;

            PdfRect? combinedBounds = null;
            foreach (var subpath in pdfPath)
            {
                var subRect = subpath.GetBoundingRectangle();
                if (subRect.HasValue)
                {
                    var b = PdfRect.FromPdfPig(subRect.Value);
                    combinedBounds = combinedBounds.HasValue ? combinedBounds.Value.Union(b) : b;
                }
            }

            if (!combinedBounds.HasValue)
                continue;

            var bounds = combinedBounds.Value;
            if (bounds.Width is >= 1.0 and <= 9.0 &&
                bounds.Height is >= 1.0 and <= 9.0)
            {
                bullets.Add(bounds);
            }
        }

        return bullets;
    }

    public static IReadOnlyList<ClassifiedPath> ExtractClassifiedPaths(Page page)
    {
        var rawPaths = page.ExperimentalAccess.Paths;
        if (rawPaths == null || rawPaths.Count == 0)
            return Array.Empty<ClassifiedPath>();

        var result = new List<ClassifiedPath>(rawPaths.Count);

        for (int i = 0; i < rawPaths.Count; i++)
        {
            var pdfPath = rawPaths[i];
            if (pdfPath.IsClipping || (!pdfPath.IsFilled && !pdfPath.IsStroked))
                continue;

            bool hasDiagonal = false;
            bool hasCurve = false;
            var segments = new List<LineSegment2D>();
            PdfRect? combinedBounds = null;

            foreach (var subpath in pdfPath)
            {
                var subRect = subpath.GetBoundingRectangle();
                if (subRect.HasValue)
                {
                    var b = PdfRect.FromPdfPig(subRect.Value);
                    combinedBounds = combinedBounds.HasValue ? combinedBounds.Value.Union(b) : b;
                }

                PdfPoint? subStart = null;
                PdfPoint? currentPoint = null;

                foreach (var cmd in subpath.Commands)
                {
                    switch (cmd)
                    {
                        case PdfSubpath.Move move:
                            subStart = move.Location;
                            currentPoint = move.Location;
                            break;

                        case PdfSubpath.Line line:
                        {
                            var seg = new LineSegment2D(line.From.X, line.From.Y, line.To.X, line.To.Y);
                            if (seg.Length > 0.2)
                            {
                                segments.Add(seg);
                                if (seg.IsDiagonal() && seg.Length > 3.5)
                                    hasDiagonal = true;
                            }
                            currentPoint = line.To;
                            break;
                        }

                        case PdfSubpath.CubicBezierCurve cubic:
                        {
                            hasCurve = true;
                            currentPoint = cubic.EndPoint;
                            break;
                        }

                        case PdfSubpath.QuadraticBezierCurve quad:
                        {
                            hasCurve = true;
                            currentPoint = quad.EndPoint;
                            break;
                        }

                        case PdfSubpath.Close:
                        {
                            if (currentPoint.HasValue && subStart.HasValue)
                            {
                                var closeSeg = new LineSegment2D(
                                    currentPoint.Value.X,
                                    currentPoint.Value.Y,
                                    subStart.Value.X,
                                    subStart.Value.Y);
                                if (closeSeg.Length > 0.2)
                                {
                                    segments.Add(closeSeg);
                                    if (closeSeg.IsDiagonal() && closeSeg.Length > 3.5)
                                        hasDiagonal = true;
                                }
                            }
                            break;
                        }
                    }
                }
            }

            if (!combinedBounds.HasValue)
                continue;

            var bounds = combinedBounds.Value;

            // Descartar canvas/background que cobre a página inteira
            if (bounds.Width > page.Width * 0.94 && bounds.Height > page.Height * 0.92)
                continue;

            // Ignorar pequenos pontos vetoriais de marcadores de lista (bullets <= 6.5 x 6.5 pt)
            if (bounds.Width <= 6.5 && bounds.Height <= 6.5 && segments.Count <= 2)
                continue;

            // Se o caminho é uma tira fina de borda horizontal (Height <= 6pt, Width >= 15pt)
            // ou vertical (Width <= 6pt, Height >= 15pt), chanfros de canto (miter joins) nas pontas
            // NÃO devem classificá-la como diagonal ou curva de diagrama!
            bool isThinHorizontalBorderStrip = bounds.Height <= 6.0 && bounds.Width >= 15.0;
            bool isThinVerticalBorderStrip = bounds.Width <= 6.0 && bounds.Height >= 15.0;

            if (isThinHorizontalBorderStrip || isThinVerticalBorderStrip)
            {
                hasDiagonal = false;
                hasCurve = false;
            }

            bool isAxisAlignedRect = !hasDiagonal && !hasCurve &&
                                     bounds.Width > 2.0 && bounds.Height > 2.0 &&
                                     segments.Count is >= 3 and <= 6 &&
                                     segments.All(s => s.IsHorizontal() || s.IsVertical() || s.Length <= 6.0);

            result.Add(new ClassifiedPath(
                PathIndex: i,
                Bounds: bounds,
                IsFilled: pdfPath.IsFilled,
                IsStroked: pdfPath.IsStroked,
                FillColorHex: pdfPath.IsFilled ? PdfColorHelper.ToHex(pdfPath.FillColor) : null,
                StrokeColorHex: pdfPath.IsStroked ? PdfColorHelper.ToHex(pdfPath.StrokeColor) : null,
                LineWidth: (double)pdfPath.LineWidth,
                HasDiagonal: hasDiagonal,
                HasCurve: hasCurve,
                IsAxisAlignedRectangle: isAxisAlignedRect,
                Segments: segments));
        }

        return result;
    }
}
