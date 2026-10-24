using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfToWord.Cli.Layout;
using PdfToWord.Cli.Models;

namespace PdfToWord.Cli.OpenXml;

/// <summary>
/// Compila a árvore final do documento Word (.docx) utilizando DocumentFormat.OpenXml,
/// configurando margens, cabeçalho/rodapé nativo, numeração de página e todos os elementos reconstruídos.
/// </summary>
public static class DocxWriter
{
    private const double TwipsPerPoint = 20.0;
    private const double PageMarginLeftRightPt = 54.0; // ~1.9 cm
    private const double PageMarginTopBottomPt = 48.0; // ~1.7 cm

    public static void WriteDocument(
        string outputDocxPath,
        IReadOnlyList<ProcessedPage> pages,
        HeaderFooterProfile headerFooterProfile)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputDocxPath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var wordDoc = WordprocessingDocument.Create(outputDocxPath, WordprocessingDocumentType.Document);
        MainDocumentPart mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        DocxStyleManager.EnsureStyles(mainPart);

        Body body = mainPart.Document.Body!;

        double pageWidthPt = pages.Count > 0 ? pages[0].WidthPt : 595.28;
        double pageHeightPt = pages.Count > 0 ? pages[0].HeightPt : 841.89;
        double usableWidthPt = pageWidthPt - (2.0 * PageMarginLeftRightPt);

        for (int pIdx = 0; pIdx < pages.Count; pIdx++)
        {
            var page = pages[pIdx];

            if (pIdx > 0)
            {
                // Inserir quebra de página explícita para preservar a paginação fiel ao documento original
                body.Append(new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
                    new Run(new Break { Type = BreakValues.Page })));
            }

            foreach (var element in page.Elements)
            {
                switch (element)
                {
                    case ParagraphBlock paraBlock:
                        body.Append(BuildParagraphElement(paraBlock, insideTableOrCallout: false));
                        break;

                    case DiagramBlock diagramBlock:
                        body.Append(DocxImageInserter.CreateCenteredDiagramParagraph(
                            mainPart,
                            diagramBlock.RenderedImage,
                            usableWidthPt,
                            diagramBlock.Name));
                        break;

                    case TableBlock tableBlock:
                        body.Append(DocxTableWriter.BuildTable(tableBlock, usableWidthPt));
                        // Parágrafo espaçador após tabela (evita fusão de tabelas consecutivas no Word)
                        body.Append(new Paragraph(
                            new ParagraphProperties(
                                new SpacingBetweenLines { Before = "40", After = "80", Line = "120" })));
                        break;

                    case CalloutBlock calloutBlock:
                        body.Append(DocxTableWriter.BuildCalloutBox(calloutBlock, usableWidthPt));
                        body.Append(new Paragraph(
                            new ParagraphProperties(
                                new SpacingBetweenLines { Before = "40", After = "80", Line = "120" })));
                        break;
                }
            }
        }

        // Configurar SectionProperties (Tamanho da página, Margens, Rodapé e Capa sem rodapé)
        var sectionProps = BuildSectionProperties(
            mainPart,
            pageWidthPt,
            pageHeightPt,
            usableWidthPt,
            headerFooterProfile,
            hasCoverPage: pages.Count > 0 && pages[0].IsCoverPage);

        body.Append(sectionProps);
        mainPart.Document.Save();
    }

    public static Paragraph BuildParagraphElement(ParagraphBlock block, bool insideTableOrCallout)
    {
        var pPr = new ParagraphProperties();

        // Estilo semântico de título
        string? styleId = block.Heading switch
        {
            HeadingLevel.Title => "Title",
            HeadingLevel.Subtitle => "Subtitle",
            HeadingLevel.H1 => "Heading1",
            HeadingLevel.H2 => "Heading2",
            HeadingLevel.H3 => "Heading3",
            _ => null
        };

        if (styleId != null)
        {
            pPr.Append(new ParagraphStyleId { Val = styleId });
        }

        // Alinhamento
        JustificationValues justification = block.Alignment switch
        {
            TextAlignmentMode.Center => JustificationValues.Center,
            TextAlignmentMode.Right => JustificationValues.Right,
            TextAlignmentMode.Justify => JustificationValues.Both,
            _ => JustificationValues.Left
        };
        pPr.Append(new Justification { Val = justification });

        // Recuo para itens de lista com marcador (•)
        if (block.IsBulletItem)
        {
            pPr.Append(new Indentation
            {
                Left = insideTableOrCallout ? "260" : "360",
                Hanging = "200"
            });
        }

        // Borda inferior de título H1 (ex: linha verde sob "FASE 1: PLAN")
        if (!string.IsNullOrEmpty(block.BottomBorderColorHex))
        {
            pPr.Append(new ParagraphBorders(
                new BottomBorder
                {
                    Val = BorderValues.Single,
                    Size = 14, // ~1.75 pt
                    Space = 4,
                    Color = block.BottomBorderColorHex
                }));
        }

        int beforeTwips = (int)Math.Round((insideTableOrCallout ? Math.Min(block.SpacingBeforePt, 3.0) : block.SpacingBeforePt) * TwipsPerPoint);
        int afterTwips = (int)Math.Round((insideTableOrCallout ? Math.Min(block.SpacingAfterPt, 4.0) : block.SpacingAfterPt) * TwipsPerPoint);

        pPr.Append(new SpacingBetweenLines
        {
            Before = beforeTwips.ToString(),
            After = afterTwips.ToString(),
            Line = insideTableOrCallout ? "252" : "276",
            LineRule = LineSpacingRuleValues.Auto
        });

        var paragraph = new Paragraph(pPr);

        // Prefixo de marcador se for bullet item
        if (block.IsBulletItem && !string.IsNullOrEmpty(block.ListPrefix))
        {
            string bulletColor = block.Runs.Count > 0 ? block.Runs[0].ColorHex : "1E293B";
            double bulletSize = block.Runs.Count > 0 ? block.Runs[0].FontSizePt : 10.0;
            int halfPts = (int)Math.Round(bulletSize * 2.0);

            paragraph.Append(new Run(
                new RunProperties(
                    new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                    new FontSize { Val = halfPts.ToString() },
                    new FontSizeComplexScript { Val = halfPts.ToString() },
                    new Color { Val = bulletColor },
                    new Bold()),
                new Text($"{block.ListPrefix} ") { Space = SpaceProcessingModeValues.Preserve }));
        }

        foreach (var runModel in block.Runs)
        {
            int halfPointSize = Math.Max(14, (int)Math.Round(runModel.FontSizePt * 2.0));
            var rPr = new RunProperties(
                new RunFonts
                {
                    Ascii = runModel.FontFamily,
                    HighAnsi = runModel.FontFamily,
                    ComplexScript = runModel.FontFamily
                },
                new FontSize { Val = halfPointSize.ToString() },
                new FontSizeComplexScript { Val = halfPointSize.ToString() },
                new Color { Val = runModel.ColorHex });

            if (runModel.IsBold)
                rPr.Append(new Bold());

            if (runModel.IsItalic)
                rPr.Append(new Italic());

            paragraph.Append(new Run(
                rPr,
                new Text(runModel.Text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        return paragraph;
    }

    private static SectionProperties BuildSectionProperties(
        MainDocumentPart mainPart,
        double pageWidthPt,
        double pageHeightPt,
        double usableWidthPt,
        HeaderFooterProfile profile,
        bool hasCoverPage)
    {
        var sectionProps = new SectionProperties();

        // Configurar FooterPart se houver rodapé detectado
        if (!string.IsNullOrEmpty(profile.FooterLeftText) || profile.FooterHasPageNumberRight)
        {
            FooterPart footerPart = mainPart.AddNewPart<FooterPart>();
            string footerRelId = mainPart.GetIdOfPart(footerPart);

            int rightTabStopTwips = (int)Math.Round(usableWidthPt * TwipsPerPoint);

            var footerParaProps = new ParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = rightTabStopTwips }),
                new SpacingBetweenLines { Before = "0", After = "0" });

            var footerPara = new Paragraph(footerParaProps);

            var footerRunProps = () => new RunProperties(
                new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI" },
                new FontSize { Val = "17" }, // 8.5 pt
                new Color { Val = "64748B" });

            if (!string.IsNullOrEmpty(profile.FooterLeftText))
            {
                footerPara.Append(new Run(
                    footerRunProps(),
                    new Text(profile.FooterLeftText) { Space = SpaceProcessingModeValues.Preserve }));
            }

            if (profile.FooterHasPageNumberRight)
            {
                footerPara.Append(new Run(footerRunProps(), new TabChar()));
                footerPara.Append(new Run(
                    footerRunProps(),
                    new Text("Página ") { Space = SpaceProcessingModeValues.Preserve }));
                footerPara.Append(new SimpleField { Instruction = "PAGE" });
            }

            footerPart.Footer = new Footer(footerPara);
            footerPart.Footer.Save();

            sectionProps.Append(new FooterReference
            {
                Type = HeaderFooterValues.Default,
                Id = footerRelId
            });

            if (hasCoverPage)
            {
                // Página 1 (Capa) sem rodapé (TitlePage / DifferentFirstPage)
                FooterPart firstPageFooter = mainPart.AddNewPart<FooterPart>();
                firstPageFooter.Footer = new Footer(new Paragraph());
                firstPageFooter.Footer.Save();

                sectionProps.Append(new FooterReference
                {
                    Type = HeaderFooterValues.First,
                    Id = mainPart.GetIdOfPart(firstPageFooter)
                });
                sectionProps.Append(new TitlePage());
            }
        }

        uint widthTwips = (uint)Math.Round(pageWidthPt * TwipsPerPoint);
        uint heightTwips = (uint)Math.Round(pageHeightPt * TwipsPerPoint);
        int marginLrTwips = (int)Math.Round(PageMarginLeftRightPt * TwipsPerPoint);
        int marginTbTwips = (int)Math.Round(PageMarginTopBottomPt * TwipsPerPoint);

        sectionProps.Append(
            new PageSize { Width = widthTwips, Height = heightTwips },
            new PageMargin
            {
                Top = marginTbTwips,
                Bottom = marginTbTwips,
                Left = (uint)marginLrTwips,
                Right = (uint)marginLrTwips,
                Header = 560U,
                Footer = 560U,
                Gutter = 0U
            });

        return sectionProps;
    }
}
