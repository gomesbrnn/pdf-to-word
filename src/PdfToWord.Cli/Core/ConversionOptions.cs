namespace PdfToWord.Cli.Core;

/// <summary>
/// Opções configuráveis para o pipeline de conversão PDF -> DOCX.
/// </summary>
public sealed class ConversionOptions
{
    /// <summary>
    /// Resolução em DPI para recorte e rasterização de diagramas vetoriais via PDFium (padrão: 300 DPI).
    /// </summary>
    public int Dpi { get; init; } = 300;

    /// <summary>
    /// Limiar de distância euclidiana (em pontos PDF, 1/72 pol) para agrupar primitivas vetoriais em um mesmo diagrama.
    /// </summary>
    public double ClusterDistanceThresholdPt { get; init; } = 18.0;

    /// <summary>
    /// Margem de busca (em pontos PDF) ao redor de vetores de diagrama para capturar textos e rótulos adjacentes.
    /// </summary>
    public double TextCaptureMarginPt { get; init; } = 10.0;

    /// <summary>
    /// Margem de respiro (padding em pontos PDF) aplicada ao redor da Bounding Box final do diagrama.
    /// </summary>
    public double DiagramPaddingPt { get; init; } = 7.0;

    /// <summary>
    /// Ativa o fallback de OCR (Tesseract) para páginas escaneadas (sem texto vetorial). Padrão: ativo.
    /// </summary>
    public bool EnableOcr { get; init; } = true;

    /// <summary>
    /// Exibe logs detalhados de diagnóstico geométrico durante o processamento. Padrão: ativo.
    /// </summary>
    public bool Verbose { get; init; } = true;
}
