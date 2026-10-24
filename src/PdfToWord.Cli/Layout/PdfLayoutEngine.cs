using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using PdfToWord.Cli.Rendering;
using Spectre.Console;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

/// <summary>
/// Motor de análise de layout por página: integra detecção de rodapé, callouts,
/// pipeline de Bounding Box (300 DPI), reconstrução de tabelas e fluxo textual.
/// </summary>
public sealed class PdfLayoutEngine
{
    private readonly ConversionOptions _options;
    private readonly BoundingBoxDetector _boundingBoxDetector;
    private readonly OcrFallbackService _ocrFallbackService;

    public PdfLayoutEngine(ConversionOptions options)
    {
        _options = options;
        _boundingBoxDetector = new BoundingBoxDetector(options);
        _ocrFallbackService = new OcrFallbackService();
    }

    public ProcessedPage ProcessPage(
        Page page,
        HeaderFooterProfile headerFooterProfile,
        DiagramRasterizer rasterizer,
        ConversionProgressStats stats)
    {
        var tracker = new ConsumptionTracker();
        var words = page.GetWords().ToList();
        var classifiedPaths = VectorPathClassifier.ExtractClassifiedPaths(page);
        var vectorBullets = VectorPathClassifier.ExtractVectorBullets(page);

        bool isCoverPage = page.Number == 1 && IsLikelyCoverPage(page, words);

        // 1. Fallback de OCR caso a página seja escaneada (sem texto vetorial extraível)
        if (words.Count == 0 && _options.EnableOcr)
        {
            var fullPagePng = rasterizer.RenderFullPageToPng(
                page.Number,
                page.Width,
                page.Height,
                _options.Dpi);

            var ocrParagraphs = _ocrFallbackService.ExtractParagraphsFromImage(
                fullPagePng,
                page.Width,
                page.Height);

            stats.OcrPagesProcessed++;

            if (ocrParagraphs.Count > 0)
            {
                return new ProcessedPage(
                    PageNumber: page.Number,
                    WidthPt: page.Width,
                    HeightPt: page.Height,
                    IsCoverPage: isCoverPage,
                    Elements: ocrParagraphs,
                    HeaderLeftText: null,
                    HeaderRightText: null,
                    FooterLeftText: null,
                    FooterRightText: null);
            }
        }

        // 2. Segmentação e consumo de Cabeçalho e Rodapé
        var (footerLeft, footerRight) = HeaderFooterAnalyzer.ConsumePageHeaderFooter(
            page,
            words,
            classifiedPaths,
            headerFooterProfile,
            tracker,
            isCoverPage);

        var pageElements = new List<IDocumentElement>();

        // 3. Detecção de Caixas de Destaque (Callouts editáveis: Págs. 1, 5, 8 e 10)
        var callouts = CalloutDetector.DetectCallouts(page, words, classifiedPaths, vectorBullets, tracker);
        foreach (var callout in callouts)
        {
            pageElements.Add(callout);
            stats.CalloutsConstructed++;

            if (_options.Verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [blue][[Callout Pág. {page.Number}]][/] {callout.InnerParagraphs.Count} parágrafos | " +
                    $"Fundo=#{callout.FillColorHex} | BordaEsq=#{callout.LeftBorderColorHex} | Box={Markup.Escape(callout.Bounds.ToString())}");
            }
        }

        // 4. Extração de Diagramas e Gráficos via Pipeline de Bounding Box + Rasterização 300 DPI
        var diagramCandidates = _boundingBoxDetector.DetectDiagrams(
            page,
            words,
            classifiedPaths,
            tracker);

        int diagramIdx = 1;
        foreach (var candidate in diagramCandidates)
        {
            RenderedDiagram rendered = rasterizer.RenderCropToPng(
                page.Number,
                candidate.BoundingBox,
                page.Width,
                page.Height,
                _options.Dpi);

            string diagName = $"Diagrama_P{page.Number}_{diagramIdx++}";
            pageElements.Add(new DiagramBlock(
                Bounds: candidate.BoundingBox,
                RenderedImage: rendered,
                Name: diagName));

            stats.DiagramsRasterized++;

            if (_options.Verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [cyan][[BBox Pág. {page.Number}]][/] {Markup.Escape(diagName)} | " +
                    $"Box={Markup.Escape(candidate.BoundingBox.ToString())} | " +
                    $"Vetores={candidate.VectorPrimitiveCount} | Palavras absorvidas={candidate.AbsorbedWordCount} | " +
                    $"PNG={rendered.WidthPx}x{rendered.HeightPx}px ({rendered.Dpi} DPI)");
            }
        }

        // 4b. Extrair imagens bitmap embutidas diretamente no PDF (se houver e não sobrepostas a BBox)
        ExtractEmbeddedPdfImages(page, rasterizer, pageElements, diagramCandidates, stats);

        // 5. Detecção e Construção de Tabelas Ortogonais (com GridSpan, VerticalMerge e Shading)
        var tables = TableDetector.DetectTables(page, words, classifiedPaths, vectorBullets, tracker);
        foreach (var table in tables)
        {
            pageElements.Add(table);
            stats.TablesConstructed++;

            if (_options.Verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [green][[Tabela Pág. {page.Number}]][/] {table.Rows.Count} linhas x {table.ColumnWidthsPt.Count} colunas | " +
                    $"Box={Markup.Escape(table.Bounds.ToString())}");
            }
        }

        // 6. Reconstrução do Fluxo Textual para todas as palavras restantes (Títulos, Parágrafos, Listas)
        var textParagraphs = TextFlowReconstructor.ReconstructPageTextFlow(
            page,
            words,
            classifiedPaths,
            vectorBullets,
            tracker,
            isCoverPage);

        pageElements.AddRange(textParagraphs);

        // 7. Ordenar todos os elementos da página de cima para baixo (Top-to-Bottom -> maior MaxY primeiro)
        var orderedElements = pageElements
            .OrderByDescending(e => e.SortTopY)
            .ToList();

        return new ProcessedPage(
            PageNumber: page.Number,
            WidthPt: page.Width,
            HeightPt: page.Height,
            IsCoverPage: isCoverPage,
            Elements: orderedElements,
            HeaderLeftText: headerFooterProfile.HeaderLeftText,
            HeaderRightText: headerFooterProfile.HeaderRightText,
            FooterLeftText: footerLeft ?? headerFooterProfile.FooterLeftText,
            FooterRightText: footerRight);
    }

    private void ExtractEmbeddedPdfImages(
        Page page,
        DiagramRasterizer rasterizer,
        List<IDocumentElement> pageElements,
        IReadOnlyList<DiagramCandidate> detectedDiagrams,
        ConversionProgressStats stats)
    {
        try
        {
            var pdfImages = page.GetImages().ToList();
            int imgIndex = 1;
            foreach (var img in pdfImages)
            {
                var imgBounds = PdfRect.FromPdfPig(img.Bounds);
                if (imgBounds.Width < 24 || imgBounds.Height < 24)
                    continue;

                // Ignorar se já estiver contida dentro de um Diagrama BBox
                if (detectedDiagrams.Any(d => d.BoundingBox.Intersects(imgBounds)))
                    continue;

                var rendered = rasterizer.RenderCropToPng(
                    page.Number,
                    imgBounds,
                    page.Width,
                    page.Height,
                    _options.Dpi);

                pageElements.Add(new DiagramBlock(
                    Bounds: imgBounds,
                    RenderedImage: rendered,
                    Name: $"Imagem_P{page.Number}_{imgIndex++}"));

                stats.DiagramsRasterized++;
            }
        }
        catch
        {
            // Ignorar imagens embutidas malformadas sem interromper o pipeline
        }
    }

    private static bool IsLikelyCoverPage(Page page, List<Word> words)
    {
        if (words.Count == 0 || words.Count > 120)
            return false;

        // Verificar se não há numeração "Página 1" no rodapé e se existe título grande (> 18 pt)
        bool hasFooterPage = words.Any(w =>
            w.BoundingBox.Top <= 55 &&
            w.Text.StartsWith("Página", StringComparison.OrdinalIgnoreCase));

        double maxFont = words.Max(TextFlowReconstructor.GetWordFontSize);
        return !hasFooterPage && maxFont >= 18.0;
    }
}
