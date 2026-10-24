namespace PdfToWord.Cli.Core;

/// <summary>
/// Estatísticas acumuladas durante a conversão para exibição em tempo real no Spectre.Console.
/// </summary>
public sealed class ConversionProgressStats
{
    public int TotalPages { get; set; }
    public int ProcessedPages { get; set; }
    public int DiagramsRasterized { get; set; }
    public int TablesConstructed { get; set; }
    public int CalloutsConstructed { get; set; }
    public int OcrPagesProcessed { get; set; }
    public TimeSpan Elapsed { get; set; }
}
