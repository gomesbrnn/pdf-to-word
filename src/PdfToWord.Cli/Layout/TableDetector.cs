using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

/// <summary>
/// Detecta e reconstrói tabelas ortogonais (com grades completas, parciais ou células mescladas),
/// resolvendo GridSpan (colunas mescladas), VerticalMerge (linhas mescladas), cores de fundo (Shading)
/// e parágrafos internos de cada célula.
/// </summary>
public static class TableDetector
{
    private const double CoordinateClusterTolerancePt = 3.2;
    private const double MinColumnWidthPt = 16.0;
    private const double MinRowHeightPt = 11.0;

    public static IReadOnlyList<TableBlock> DetectTables(
        Page page,
        IReadOnlyList<Word> pageWords,
        IReadOnlyList<ClassifiedPath> classifiedPaths,
        IReadOnlyList<PdfRect> vectorBullets,
        ConsumptionTracker tracker)
    {
        // 1. Selecionar primitivas ortogonais ainda não consumidas
        var orthogonalPaths = classifiedPaths
            .Where(p => !tracker.IsPathConsumed(p.PathIndex) &&
                        !p.HasDiagonal &&
                        !p.HasCurve &&
                        p.Bounds.MaxY > 55.0)
            .ToList();

        if (orthogonalPaths.Count < 4)
            return Array.Empty<TableBlock>();

        // 2. Agrupar caminhos ortogonais que se tocam ou estão muito próximos (tolerância 4.5 pt)
        //    Cuidado para não unir duas tabelas separadas verticalmente (ex: Lista de Presença na Pág. 6 tem gap ~ 12pt)
        var clusters = ClusterOrthogonalPaths(orthogonalPaths, maxGapPt: 4.5);

        var tables = new List<TableBlock>();

        foreach (var cluster in clusters)
        {
            var tableBlock = TryBuildTableFromCluster(cluster, pageWords, vectorBullets, tracker);
            if (tableBlock != null)
            {
                tables.Add(tableBlock);
            }
        }

        return tables
            .OrderByDescending(t => t.Bounds.MaxY)
            .ToList();
    }

    private static TableBlock? TryBuildTableFromCluster(
        List<ClassifiedPath> cluster,
        IReadOnlyList<Word> pageWords,
        IReadOnlyList<PdfRect> vectorBullets,
        ConsumptionTracker tracker)
    {
        PdfRect envelope = cluster[0].Bounds;
        for (int i = 1; i < cluster.Count; i++)
            envelope = envelope.Union(cluster[i].Bounds);

        // Uma tabela válida deve ter largura e altura compatíveis com uma estrutura tabular
        if (envelope.Width < 160.0 || envelope.Height < 28.0)
            return null;

        // 1. Extrair todos os segmentos horizontais e verticais das primitivas do cluster
        var horizSegments = new List<LineSegment2D>();
        var vertSegments = new List<LineSegment2D>();

        foreach (var path in cluster)
        {
            foreach (var seg in path.Segments)
            {
                if (seg.IsHorizontal() && seg.Length >= MinColumnWidthPt * 0.8)
                    horizSegments.Add(seg);
                else if (seg.IsVertical() && seg.Length >= MinRowHeightPt * 0.8)
                    vertSegments.Add(seg);
            }

            // Garantir que as 4 bordas de retângulos preenchidos/traçados também contribuam para a grade
            if (path.Bounds.Width >= MinColumnWidthPt && path.Bounds.Height >= MinRowHeightPt)
            {
                var b = path.Bounds;
                horizSegments.Add(new LineSegment2D(b.MinX, b.MaxY, b.MaxX, b.MaxY));
                horizSegments.Add(new LineSegment2D(b.MinX, b.MinY, b.MaxX, b.MinY));
                vertSegments.Add(new LineSegment2D(b.MinX, b.MinY, b.MinX, b.MaxY));
                vertSegments.Add(new LineSegment2D(b.MaxX, b.MinY, b.MaxX, b.MaxY));
            }
        }

        if (horizSegments.Count < 2 || vertSegments.Count < 2)
            return null;

        // 2. Determinar coordenadas X das colunas (ordem crescente: esquerda -> direita)
        var rawXs = new List<double>();
        foreach (var v in vertSegments)
        {
            rawXs.Add((v.X1 + v.X2) * 0.5);
        }

        List<double> colXs = ClusterCoordinates(rawXs, CoordinateClusterTolerancePt, ascending: true);
        colXs = FilterTooCloseStops(colXs, MinColumnWidthPt, ascending: true);

        // 3. Determinar coordenadas Y das linhas (ordem decrescente: topo -> base no PDF)
        var rawYs = new List<double>();
        foreach (var h in horizSegments)
        {
            rawYs.Add((h.Y1 + h.Y2) * 0.5);
        }

        List<double> rowYs = ClusterCoordinates(rawYs, CoordinateClusterTolerancePt, ascending: false);
        rowYs = FilterTooCloseStops(rowYs, MinRowHeightPt, ascending: false);

        int numCols = colXs.Count - 1;
        int numRows = rowYs.Count - 1;

        if (numCols < 1 || numRows < 1 || (numCols == 1 && numRows == 1))
            return null;

        // 4. Construir matriz de mesclagem (GridSpan horizontal e VerticalMerge vertical)
        bool[,] visited = new bool[numRows, numCols];
        var rowsList = new List<IReadOnlyList<TableCellModel>>(numRows);

        for (int r = 0; r < numRows; r++)
        {
            var currentRowCells = new List<TableCellModel>();

            for (int c = 0; c < numCols; c++)
            {
                if (visited[r, c])
                    continue;

                // Expandir horizontalmente (GridSpan) enquanto NÃO houver borda vertical entre c_end e c_end + 1 na linha r
                int colSpan = 1;
                while (c + colSpan < numCols)
                {
                    double dividerX = colXs[c + colSpan];
                    double rowTopY = rowYs[r];
                    double rowBottomY = rowYs[r + 1];

                    if (HasVerticalDividerAt(vertSegments, dividerX, rowBottomY, rowTopY))
                        break;

                    colSpan++;
                }

                // Expandir verticalmente (RowSpan / VerticalMerge) enquanto NÃO houver borda horizontal entre r_end e r_end + 1
                int rowSpan = 1;
                while (r + rowSpan < numRows)
                {
                    double dividerY = rowYs[r + rowSpan];
                    double cellLeftX = colXs[c];
                    double cellRightX = colXs[c + colSpan];

                    if (HasHorizontalDividerAt(horizSegments, dividerY, cellLeftX, cellRightX))
                        break;

                    rowSpan++;
                }

                // Marcar células elementares cobertas como visitadas
                for (int dr = 0; dr < rowSpan; dr++)
                {
                    for (int dc = 0; dc < colSpan; dc++)
                    {
                        visited[r + dr, c + dc] = true;
                    }
                }

                var cellBounds = new PdfRect(
                    MinX: colXs[c],
                    MinY: rowYs[r + rowSpan],
                    MaxX: colXs[c + colSpan],
                    MaxY: rowYs[r]);

                string? fillHex = ResolveCellBackgroundFill(cluster, cellBounds);

                // Extrair palavras contidas nesta célula física
                var cellWords = new List<TextFlowReconstructor.WordWithIndex>();
                for (int wIdx = 0; wIdx < pageWords.Count; wIdx++)
                {
                    if (tracker.IsWordConsumed(wIdx))
                        continue;

                    var wRect = PdfRect.FromPdfPig(pageWords[wIdx].BoundingBox);
                    if (cellBounds.ContainsPoint(wRect.CenterX, wRect.CenterY, tolerance: 2.0))
                    {
                        cellWords.Add(new TextFlowReconstructor.WordWithIndex(wIdx, pageWords[wIdx], wRect));
                        tracker.MarkWordConsumed(wIdx);
                    }
                }

                bool isHeaderOrEmphasizedCell =
                    r == 0 ||
                    (cellWords.Count > 0 && cellWords.All(w => w.Word.Text.EndsWith(':'))) ||
                    (fillHex != null && !string.Equals(fillHex, "F8FAFC", StringComparison.OrdinalIgnoreCase)) ||
                    (colSpan == numCols && numCols > 1);

                TextAlignmentMode alignment = InferCellTextAlignment(cellBounds, cellWords, vectorBullets);
                var cellParagraphs = TextFlowReconstructor.ReconstructCellOrBoxParagraphs(
                    cellWords,
                    cellBounds.Width,
                    alignment,
                    vectorBullets,
                    forceBoldCell: isHeaderOrEmphasizedCell);

                currentRowCells.Add(new TableCellModel(
                    RowIndex: r,
                    ColIndex: c,
                    GridSpan: colSpan,
                    RowSpan: rowSpan,
                    IsVerticalMergeContinuation: false,
                    Bounds: cellBounds,
                    FillColorHex: fillHex,
                    Paragraphs: cellParagraphs,
                    DefaultAlignment: alignment,
                    VerticalCenter: true));
            }

            rowsList.Add(currentRowCells);
        }

        // Inserir células de continuação de VerticalMerge (<w:vMerge/>) nas linhas inferiores quando RowSpan > 1
        var normalizedRows = InsertVerticalMergeContinuations(rowsList, numRows, colXs, rowYs);

        // Marcar todos os caminhos do cluster como consumidos
        foreach (var path in cluster)
        {
            tracker.MarkPathConsumed(path.PathIndex);
        }

        var colWidths = new List<double>(numCols);
        for (int c = 0; c < numCols; c++)
            colWidths.Add(colXs[c + 1] - colXs[c]);

        var rowHeights = new List<double>(numRows);
        for (int r = 0; r < numRows; r++)
            rowHeights.Add(rowYs[r] - rowYs[r + 1]);

        string borderColor = cluster
            .Select(p => p.StrokeColorHex)
            .FirstOrDefault(hex => !string.IsNullOrEmpty(hex) && !PdfColorHelper.IsNearWhite(hex))
            ?? "CBD5E1";

        var tableBounds = new PdfRect(colXs[0], rowYs[^1], colXs[^1], rowYs[0]);

        return new TableBlock(
            Bounds: tableBounds,
            ColumnWidthsPt: colWidths,
            RowHeightsPt: rowHeights,
            Rows: normalizedRows,
            BorderColorHex: borderColor);
    }

    private static List<IReadOnlyList<TableCellModel>> InsertVerticalMergeContinuations(
        List<IReadOnlyList<TableCellModel>> initialRows,
        int numRows,
        List<double> colXs,
        List<double> rowYs)
    {
        var perRowLists = initialRows.Select(r => r.ToList()).ToList();

        for (int r = 0; r < numRows; r++)
        {
            foreach (var cell in initialRows[r])
            {
                if (cell.RowSpan > 1 && !cell.IsVerticalMergeContinuation)
                {
                    for (int dr = 1; dr < cell.RowSpan; dr++)
                    {
                        int targetRow = r + dr;
                        var contBounds = new PdfRect(
                            colXs[cell.ColIndex],
                            rowYs[targetRow + 1],
                            colXs[cell.ColIndex + cell.GridSpan],
                            rowYs[targetRow]);

                        perRowLists[targetRow].Add(new TableCellModel(
                            RowIndex: targetRow,
                            ColIndex: cell.ColIndex,
                            GridSpan: cell.GridSpan,
                            RowSpan: 1,
                            IsVerticalMergeContinuation: true,
                            Bounds: contBounds,
                            FillColorHex: cell.FillColorHex,
                            Paragraphs: Array.Empty<ParagraphBlock>(),
                            DefaultAlignment: cell.DefaultAlignment,
                            VerticalCenter: true));
                    }
                }
            }
        }

        return perRowLists
            .Select(r => (IReadOnlyList<TableCellModel>)r.OrderBy(c => c.ColIndex).ToList())
            .ToList();
    }

    private static bool HasVerticalDividerAt(
        List<LineSegment2D> vertSegments,
        double targetX,
        double minY,
        double maxY)
    {
        double rowHeight = maxY - minY;
        if (rowHeight <= 0)
            return false;

        foreach (var seg in vertSegments)
        {
            double segX = (seg.X1 + seg.X2) * 0.5;
            if (Math.Abs(segX - targetX) <= CoordinateClusterTolerancePt + 0.8)
            {
                double segMinY = Math.Min(seg.Y1, seg.Y2);
                double segMaxY = Math.Max(seg.Y1, seg.Y2);
                double overlap = Math.Min(maxY, segMaxY) - Math.Max(minY, segMinY);
                if (overlap >= rowHeight * 0.45)
                    return true;
            }
        }

        return false;
    }

    private static bool HasHorizontalDividerAt(
        List<LineSegment2D> horizSegments,
        double targetY,
        double minX,
        double maxX)
    {
        double colWidth = maxX - minX;
        if (colWidth <= 0)
            return false;

        foreach (var seg in horizSegments)
        {
            double segY = (seg.Y1 + seg.Y2) * 0.5;
            if (Math.Abs(segY - targetY) <= CoordinateClusterTolerancePt + 0.8)
            {
                double segMinX = Math.Min(seg.X1, seg.X2);
                double segMaxX = Math.Max(seg.X1, seg.X2);
                double overlap = Math.Min(maxX, segMaxX) - Math.Max(minX, segMinX);
                if (overlap >= colWidth * 0.45)
                    return true;
            }
        }

        return false;
    }

    private static string? ResolveCellBackgroundFill(List<ClassifiedPath> cluster, PdfRect cellBounds)
    {
        double cx = cellBounds.CenterX;
        double cy = cellBounds.CenterY;

        // Selecionar o retângulo preenchido de menor área que contém o centro da célula
        var matchingFills = cluster
            .Where(p => p.IsFilled &&
                        !string.IsNullOrEmpty(p.FillColorHex) &&
                        p.Bounds.ContainsPoint(cx, cy, tolerance: 1.5))
            .OrderBy(p => p.Bounds.Area)
            .ToList();

        foreach (var fillPath in matchingFills)
        {
            if (!PdfColorHelper.IsNearWhite(fillPath.FillColorHex))
                return fillPath.FillColorHex;
        }

        return null;
    }

    private static TextAlignmentMode InferCellTextAlignment(
        PdfRect cellBounds,
        List<TextFlowReconstructor.WordWithIndex> cellWords,
        IReadOnlyList<PdfRect> vectorBullets)
    {
        if (cellWords.Count == 0)
            return TextAlignmentMode.Left;

        // Se a célula contém lista com marcadores (• textuais ou vetoriais), alinhar à esquerda
        if (cellWords.Any(w => w.Word.Text.Trim() is "•" or "●" or "▪") ||
            vectorBullets.Any(b => cellBounds.ContainsPoint(b.CenterX, b.CenterY, tolerance: 2.0)))
        {
            return TextAlignmentMode.Left;
        }

        double minWordX = cellWords.Min(w => w.Bounds.MinX);
        double maxWordX = cellWords.Max(w => w.Bounds.MaxX);
        double textSpanWidth = maxWordX - minWordX;

        double leftMargin = minWordX - cellBounds.MinX;
        double rightMargin = cellBounds.MaxX - maxWordX;

        // Quando as margens esquerda e direita são simétricas e maiores que o padding normal de célula (11 pt),
        // o texto está centralizado na célula.
        if (leftMargin >= 11.5 &&
            rightMargin >= 11.5 &&
            textSpanWidth <= cellBounds.Width * 0.86 &&
            Math.Abs(leftMargin - rightMargin) <= Math.Max(10.0, cellBounds.Width * 0.14))
        {
            return TextAlignmentMode.Center;
        }

        // Células estreitas com valor curto (ex: "1", "O", "S1", "45%")
        if (cellBounds.Width <= 65.0 && textSpanWidth <= cellBounds.Width * 0.65 && Math.Abs(leftMargin - rightMargin) <= 8.0)
        {
            return TextAlignmentMode.Center;
        }

        return TextAlignmentMode.Left;
    }

    private static List<List<ClassifiedPath>> ClusterOrthogonalPaths(
        List<ClassifiedPath> paths,
        double maxGapPt)
    {
        int n = paths.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (paths[i].Bounds.EuclideanDistanceTo(paths[j].Bounds) <= maxGapPt)
                {
                    Union(i, j);
                }
            }
        }

        var groups = new Dictionary<int, List<ClassifiedPath>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<ClassifiedPath>();
                groups[root] = list;
            }
            list.Add(paths[i]);
        }

        return groups.Values.ToList();
    }

    private static List<double> ClusterCoordinates(List<double> values, double tolerance, bool ascending)
    {
        if (values.Count == 0)
            return new List<double>();

        var sorted = ascending
            ? values.OrderBy(v => v).ToList()
            : values.OrderByDescending(v => v).ToList();

        var clusters = new List<List<double>> { new() { sorted[0] } };

        for (int i = 1; i < sorted.Count; i++)
        {
            double v = sorted[i];
            double currentAvg = clusters[^1].Average();
            if (Math.Abs(v - currentAvg) <= tolerance)
            {
                clusters[^1].Add(v);
            }
            else
            {
                clusters.Add(new List<double> { v });
            }
        }

        return clusters.Select(c => c.Average()).ToList();
    }

    private static List<double> FilterTooCloseStops(List<double> stops, double minGap, bool ascending)
    {
        if (stops.Count <= 2)
            return stops;

        var filtered = new List<double> { stops[0] };
        for (int i = 1; i < stops.Count; i++)
        {
            double diff = Math.Abs(stops[i] - filtered[^1]);
            if (diff >= minGap)
            {
                filtered.Add(stops[i]);
            }
        }

        return filtered;
    }
}
