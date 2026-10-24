using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfToWord.Cli.Models;

namespace PdfToWord.Cli.OpenXml;

/// <summary>
/// Constrói elementos Table nativos do OpenXML a partir de TableBlock (com GridSpan, VerticalMerge e Shading)
/// e CalloutBlock (caixas de destaque de 1 célula com borda lateral espessa).
/// </summary>
public static class DocxTableWriter
{
    private const double TwipsPerPoint = 20.0; // 1 pt = 20 dxa (twips)

    public static Table BuildTable(TableBlock tableModel, double usablePageWidthPt)
    {
        double totalPdfWidthPt = Math.Max(1.0, tableModel.ColumnWidthsPt.Sum());
        double scale = totalPdfWidthPt > usablePageWidthPt
            ? usablePageWidthPt / totalPdfWidthPt
            : Math.Min(1.05, usablePageWidthPt / totalPdfWidthPt);

        int tableWidthTwips = (int)Math.Round(totalPdfWidthPt * scale * TwipsPerPoint);
        string borderHex = tableModel.BorderColorHex;

        var table = new Table();

        // 1. Propriedades da Tabela (TableProperties)
        var tblProps = new TableProperties(
            new TableWidth { Width = tableWidthTwips.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableJustification { Val = TableRowAlignmentValues.Center },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex },
                new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex },
                new LeftBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex },
                new RightBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 6, Space = 0, Color = borderHex }),
            new TableCellMarginDefault(
                new TopMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 130, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 130, Type = TableWidthValues.Dxa }));

        table.Append(tblProps);

        // 2. Definição das colunas canônicas (TableGrid)
        var tableGrid = new TableGrid();
        var colTwips = new List<int>(tableModel.ColumnWidthsPt.Count);
        foreach (double colWidthPt in tableModel.ColumnWidthsPt)
        {
            int wTwips = Math.Max(160, (int)Math.Round(colWidthPt * scale * TwipsPerPoint));
            colTwips.Add(wTwips);
            tableGrid.Append(new GridColumn { Width = wTwips.ToString() });
        }
        table.Append(tableGrid);

        // 3. Construção das linhas e células (incluindo GridSpan e VerticalMerge)
        for (int r = 0; r < tableModel.Rows.Count; r++)
        {
            var rowCells = tableModel.Rows[r];
            var tr = new TableRow();

            if (r < tableModel.RowHeightsPt.Count)
            {
                int rowHeightTwips = Math.Max(260, (int)Math.Round(tableModel.RowHeightsPt[r] * 0.85 * TwipsPerPoint));
                tr.Append(new TableRowProperties(
                    new TableRowHeight { Val = (uint)rowHeightTwips, HeightType = HeightRuleValues.AtLeast },
                    new CantSplit()));
            }

            foreach (var cellModel in rowCells)
            {
                int spanWidthTwips = 0;
                for (int k = 0; k < cellModel.GridSpan && cellModel.ColIndex + k < colTwips.Count; k++)
                {
                    spanWidthTwips += colTwips[cellModel.ColIndex + k];
                }

                var tcProps = new TableCellProperties(
                    new TableCellWidth { Width = spanWidthTwips.ToString(), Type = TableWidthUnitValues.Dxa });

                if (cellModel.GridSpan > 1)
                {
                    tcProps.Append(new GridSpan { Val = cellModel.GridSpan });
                }

                if (cellModel.IsVerticalMergeContinuation)
                {
                    tcProps.Append(new VerticalMerge());
                }
                else if (cellModel.RowSpan > 1)
                {
                    tcProps.Append(new VerticalMerge { Val = MergedCellValues.Restart });
                }

                if (!string.IsNullOrEmpty(cellModel.FillColorHex))
                {
                    tcProps.Append(new Shading
                    {
                        Val = ShadingPatternValues.Clear,
                        Color = "auto",
                        Fill = cellModel.FillColorHex
                    });
                }

                tcProps.Append(new TableCellVerticalAlignment
                {
                    Val = cellModel.VerticalCenter
                        ? TableVerticalAlignmentValues.Center
                        : TableVerticalAlignmentValues.Top
                });

                var tc = new TableCell(tcProps);

                if (cellModel.IsVerticalMergeContinuation || cellModel.Paragraphs.Count == 0)
                {
                    tc.Append(new Paragraph(
                        new ParagraphProperties(
                            new SpacingBetweenLines { Before = "0", After = "0", Line = "240" })));
                }
                else
                {
                    foreach (var paraModel in cellModel.Paragraphs)
                    {
                        tc.Append(DocxWriter.BuildParagraphElement(paraModel, insideTableOrCallout: true));
                    }
                }

                tr.Append(tc);
            }

            table.Append(tr);
        }

        return table;
    }

    /// <summary>
    /// Constrói uma Caixa de Destaque (Callout) como uma tabela estilizada de 1 célula,
    /// preservando a borda esquerda espessa, a cor de fundo e todo o texto editável.
    /// </summary>
    public static Table BuildCalloutBox(CalloutBlock callout, double usablePageWidthPt)
    {
        double widthPt = Math.Min(callout.Bounds.Width, usablePageWidthPt);
        int widthTwips = (int)Math.Round(widthPt * TwipsPerPoint);

        string outerBorderColor = callout.OuterBorderColorHex ?? callout.FillColorHex;
        BorderValues outerBorderStyle = callout.OuterBorderColorHex != null
            ? BorderValues.Single
            : BorderValues.None;

        var table = new Table(
            new TableProperties(
                new TableWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa },
                new TableJustification { Val = TableRowAlignmentValues.Center },
                new TableBorders(
                    new TopBorder { Val = outerBorderStyle, Size = 6, Space = 0, Color = outerBorderColor },
                    new BottomBorder { Val = outerBorderStyle, Size = 6, Space = 0, Color = outerBorderColor },
                    new RightBorder { Val = outerBorderStyle, Size = 6, Space = 0, Color = outerBorderColor },
                    new LeftBorder
                    {
                        Val = BorderValues.Single,
                        Size = 28, // 3.5 pt (borda esquerda de destaque)
                        Space = 0,
                        Color = callout.LeftBorderColorHex
                    }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "160", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "160", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 220, Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = 200, Type = TableWidthValues.Dxa })),
            new TableGrid(new GridColumn { Width = widthTwips.ToString() }));

        var tcProps = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa },
            new Shading
            {
                Val = ShadingPatternValues.Clear,
                Color = "auto",
                Fill = callout.FillColorHex
            });

        var tc = new TableCell(tcProps);

        if (callout.InnerParagraphs.Count == 0)
        {
            tc.Append(new Paragraph());
        }
        else
        {
            foreach (var p in callout.InnerParagraphs)
            {
                tc.Append(DocxWriter.BuildParagraphElement(p, insideTableOrCallout: true));
            }
        }

        table.Append(new TableRow(tc));
        return table;
    }
}
