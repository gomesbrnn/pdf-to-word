using PdfToWord.Cli.Rendering;

namespace PdfToWord.Cli.Models;

public enum TextAlignmentMode
{
    Left,
    Center,
    Right,
    Justify
}

public enum HeadingLevel
{
    None = 0,
    Title = 1,
    Subtitle = 2,
    H1 = 3,
    H2 = 4,
    H3 = 5
}

/// <summary>
/// Elemento estrutural de página ordenado verticalmente (de cima para baixo, por TopY no PDF).
/// </summary>
public interface IDocumentElement
{
    PdfRect Bounds { get; }
    double SortTopY => Bounds.MaxY;
}

/// <summary>
/// Bloco de parágrafo ou título (H1, H2, H3, Normal, Item de Lista).
/// </summary>
public sealed record ParagraphBlock(
    PdfRect Bounds,
    IReadOnlyList<StyledTextRun> Runs,
    HeadingLevel Heading = HeadingLevel.None,
    TextAlignmentMode Alignment = TextAlignmentMode.Left,
    bool IsBulletItem = false,
    string? ListPrefix = null,
    string? BottomBorderColorHex = null,
    double SpacingBeforePt = 0,
    double SpacingAfterPt = 6) : IDocumentElement;

/// <summary>
/// Caixa de destaque (Callout) com cor de preenchimento, borda esquerda espessa e parágrafos internos editáveis.
/// </summary>
public sealed record CalloutBlock(
    PdfRect Bounds,
    string FillColorHex,
    string LeftBorderColorHex,
    string? OuterBorderColorHex,
    IReadOnlyList<ParagraphBlock> InnerParagraphs) : IDocumentElement;

/// <summary>
/// Diagrama ou gráfico vetorial complexo recortado e rasterizado em alta resolução (300 DPI) via Bounding Box.
/// </summary>
public sealed record DiagramBlock(
    PdfRect Bounds,
    RenderedDiagram RenderedImage,
    string Name) : IDocumentElement;

/// <summary>
/// Célula resolvida de uma tabela, com suporte a mesclagem horizontal (GridSpan), vertical (VerticalMerge) e preenchimento.
/// </summary>
public sealed record TableCellModel(
    int RowIndex,
    int ColIndex,
    int GridSpan,
    int RowSpan,
    bool IsVerticalMergeContinuation,
    PdfRect Bounds,
    string? FillColorHex,
    IReadOnlyList<ParagraphBlock> Paragraphs,
    TextAlignmentMode DefaultAlignment = TextAlignmentMode.Left,
    bool VerticalCenter = true);

/// <summary>
/// Tabela estruturada detectada por malha ortogonal.
/// </summary>
public sealed record TableBlock(
    PdfRect Bounds,
    IReadOnlyList<double> ColumnWidthsPt,
    IReadOnlyList<double> RowHeightsPt,
    IReadOnlyList<IReadOnlyList<TableCellModel>> Rows,
    string BorderColorHex = "CBD5E1") : IDocumentElement;

/// <summary>
/// Modelo completo de uma página processada pelo motor de layout.
/// </summary>
public sealed record ProcessedPage(
    int PageNumber,
    double WidthPt,
    double HeightPt,
    bool IsCoverPage,
    IReadOnlyList<IDocumentElement> Elements,
    string? HeaderLeftText,
    string? HeaderRightText,
    string? FooterLeftText,
    string? FooterRightText);
