using PdfToWord.Cli.Models;
using Tesseract;

namespace PdfToWord.Cli.Rendering;

/// <summary>
/// Serviço de OCR Fallback baseado em Tesseract.NET para páginas escaneadas (sem texto vetorial).
/// </summary>
public sealed class OcrFallbackService
{
    private readonly string _tessDataPath;

    public OcrFallbackService(string? tessDataPath = null)
    {
        _tessDataPath = tessDataPath
            ?? Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
            ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public bool IsTessDataAvailable(out string language)
    {
        if (Directory.Exists(_tessDataPath))
        {
            if (File.Exists(Path.Combine(_tessDataPath, "por.traineddata")))
            {
                language = "por";
                return true;
            }

            if (File.Exists(Path.Combine(_tessDataPath, "eng.traineddata")))
            {
                language = "eng";
                return true;
            }
        }

        language = string.Empty;
        return false;
    }

    public IReadOnlyList<ParagraphBlock> ExtractParagraphsFromImage(
        RenderedDiagram pageImage,
        double pageWidthPt,
        double pageHeightPt)
    {
        if (!IsTessDataAvailable(out string lang))
            return Array.Empty<ParagraphBlock>();

        var paragraphs = new List<ParagraphBlock>();

        using var engine = new TesseractEngine(_tessDataPath, lang, EngineMode.Default);
        using var pix = Pix.LoadFromMemory(pageImage.PngBytes);
        using var page = engine.Process(pix, PageSegMode.Auto);

        using var iter = page.GetIterator();
        iter.Begin();

        double scaleX = pageWidthPt / Math.Max(1, pageImage.WidthPx);
        double scaleY = pageHeightPt / Math.Max(1, pageImage.HeightPx);

        do
        {
            if (iter.TryGetBoundingBox(PageIteratorLevel.Para, out var rect))
            {
                string? text = iter.GetText(PageIteratorLevel.Para)?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Converter de Top-Left pixels para Bottom-Left pontos PDF
                    double minX = rect.X1 * scaleX;
                    double maxX = rect.X2 * scaleX;
                    double maxY = pageHeightPt - (rect.Y1 * scaleY);
                    double minY = pageHeightPt - (rect.Y2 * scaleY);

                    var bounds = new PdfRect(minX, minY, maxX, maxY);
                    string normalized = string.Join(" ", text
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

                    var run = new StyledTextRun(
                        Text: normalized,
                        FontFamily: "Segoe UI",
                        FontSizePt: 10.5,
                        ColorHex: "1E293B",
                        IsBold: false,
                        IsItalic: false,
                        Bounds: bounds);

                    paragraphs.Add(new ParagraphBlock(
                        Bounds: bounds,
                        Runs: new[] { run },
                        Heading: HeadingLevel.None,
                        Alignment: TextAlignmentMode.Left));
                }
            }
        } while (iter.Next(PageIteratorLevel.Para));

        return paragraphs;
    }
}
