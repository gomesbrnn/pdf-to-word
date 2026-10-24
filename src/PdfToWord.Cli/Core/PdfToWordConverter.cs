using System.Diagnostics;
using PdfToWord.Cli.Layout;
using PdfToWord.Cli.Models;
using PdfToWord.Cli.OpenXml;
using PdfToWord.Cli.Rendering;
using UglyToad.PdfPig;

namespace PdfToWord.Cli.Core;

/// <summary>
/// Orquestrador completo do pipeline de 6 etapas de conversão PDF -> Word (.docx).
/// </summary>
public sealed class PdfToWordConverter
{
    private readonly ConversionOptions _options;

    public PdfToWordConverter(ConversionOptions options)
    {
        _options = options;
    }

    public ConversionProgressStats ConvertFile(
        string inputPdfPath,
        string outputDocxPath,
        Action<ConversionProgressStats>? onPageProgress = null)
    {
        if (!File.Exists(inputPdfPath))
            throw new FileNotFoundException($"Arquivo PDF de entrada não encontrado: {inputPdfPath}", inputPdfPath);

        var sw = Stopwatch.StartNew();
        var stats = new ConversionProgressStats();

        using var pdfDocument = PdfDocument.Open(inputPdfPath);
        using var rasterizer = new DiagramRasterizer(inputPdfPath);

        stats.TotalPages = pdfDocument.NumberOfPages;
        onPageProgress?.Invoke(stats);

        // Etapa 1 & 2: Analisar padrões de cabeçalho e rodapé ao longo do documento
        var headerFooterProfile = HeaderFooterAnalyzer.AnalyzeDocument(pdfDocument);

        var layoutEngine = new PdfLayoutEngine(_options);
        var processedPages = new List<ProcessedPage>(pdfDocument.NumberOfPages);

        // Etapas 3, 4 e 5: Processar cada página (Callouts, Diagramas BBox 300 DPI, Tabelas e Fluxo Textual)
        for (int p = 1; p <= pdfDocument.NumberOfPages; p++)
        {
            var page = pdfDocument.GetPage(p);
            var processedPage = layoutEngine.ProcessPage(page, headerFooterProfile, rasterizer, stats);
            processedPages.Add(processedPage);

            stats.ProcessedPages = p;
            stats.Elapsed = sw.Elapsed;
            onPageProgress?.Invoke(stats);
        }

        // Etapa 6: Compilar o documento final .docx via DocumentFormat.OpenXml
        DocxWriter.WriteDocument(outputDocxPath, processedPages, headerFooterProfile);

        sw.Stop();
        stats.Elapsed = sw.Elapsed;
        onPageProgress?.Invoke(stats);

        return stats;
    }
}
