using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfToWord.Cli.Rendering;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace PdfToWord.Cli.OpenXml;

/// <summary>
/// Insere imagens de diagramas rasterizados em EMUs (English Metric Units) no documento OpenXML (.docx).
/// </summary>
public static class DocxImageInserter
{
    private const long EmusPerInch = 914400L;
    private const long EmusPerPoint = EmusPerInch / 72L; // 12.700 EMUs por ponto

    private static uint _nextDrawingId = 1;

    /// <summary>
    /// Converte as dimensões da imagem de pixels para EMUs:
    /// EMU = Pixels * (914400 / DPI)
    /// e insere em um parágrafo centralizado via wp:inline, respeitando a largura útil da página.
    /// </summary>
    public static Paragraph CreateCenteredDiagramParagraph(
        MainDocumentPart mainPart,
        RenderedDiagram diagram,
        double usablePageWidthPt = 480.0,
        string diagramName = "Diagrama")
    {
        // 1. Adicionar ImagePart (image/png) ao pacote OpenXML
        ImagePart imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(diagram.PngBytes))
        {
            imagePart.FeedData(stream);
        }

        string relationshipId = mainPart.GetIdOfPart(imagePart);

        // 2. Converter Pixels para EMUs:
        //    EMU = Pixels * (914400 / DPI)
        long widthEmu = (long)Math.Round(diagram.WidthPx * ((double)EmusPerInch / diagram.Dpi));
        long heightEmu = (long)Math.Round(diagram.HeightPx * ((double)EmusPerInch / diagram.Dpi));

        // 3. Respeitar a largura útil da página (subtraindo margens) sem distorcer o aspect ratio
        long maxUsableWidthEmu = (long)Math.Round(usablePageWidthPt * EmusPerPoint);
        if (widthEmu > maxUsableWidthEmu && widthEmu > 0)
        {
            double scaleFactor = (double)maxUsableWidthEmu / widthEmu;
            widthEmu = maxUsableWidthEmu;
            heightEmu = (long)Math.Round(heightEmu * scaleFactor);
        }

        uint docPrId = Interlocked.Increment(ref _nextDrawingId);
        string imageFileName = $"{diagramName}_{docPrId}.png";

        // 4. Construir o elemento Drawing (wp:inline)
        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = docPrId, Name = diagramName, Description = imageFileName },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = docPrId, Name = imageFileName },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip
                                {
                                    Embed = relationshipId,
                                    CompressionState = A.BlipCompressionValues.Print
                                },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle
                                }))
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            )
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });

        return new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "120", After = "160" }),
            new Run(drawing));
    }
}
