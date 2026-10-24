using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

public sealed record DiagramCandidate(
    int PageNumber,
    PdfRect BoundingBox,
    int VectorPrimitiveCount,
    int AbsorbedWordCount);

/// <summary>
/// Módulo central da estratégia de Bounding Box para Diagramas e Gráficos Vetoriais:
/// 1. Diferencia vetores de diagramas/gráficos (setas, diagonais, curvas, gráficos de barras) de tabelas ortogonais.
/// 2. Agrupa primitivas próximas (distância euclidiana &lt; limiar configurável) em um Cluster Envelope.
/// 3. Expande a Bounding Box para incorporar textos/rótulos internos ou adjacentes e aplica padding (5–10 pt).
/// 4. Marca todos os vetores e palavras incorporados como "consumidos" para evitar duplicação.
/// </summary>
public sealed class BoundingBoxDetector
{
    private readonly ConversionOptions _options;

    public BoundingBoxDetector(ConversionOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<DiagramCandidate> DetectDiagrams(
        Page page,
        IReadOnlyList<Word> pageWords,
        IReadOnlyList<ClassifiedPath> classifiedPaths,
        ConsumptionTracker tracker)
    {
        // 1. Filtrar primitivas vetoriais ainda não consumidas (ignorando sublinhados simples de título)
        var candidates = classifiedPaths
            .Where(p => !tracker.IsPathConsumed(p.PathIndex) &&
                        !(p.Bounds.MaxY < 58 && p.Bounds.Height < 4))
            .ToList();

        if (candidates.Count == 0)
            return Array.Empty<DiagramCandidate>();

        // 2. Agrupar vetores espacialmente próximos via Union-Find (distância euclidiana <= ClusterDistanceThresholdPt)
        var clusters = ClusterPathsByDistance(candidates, _options.ClusterDistanceThresholdPt);

        var detectedDiagrams = new List<DiagramCandidate>();

        foreach (var cluster in clusters)
        {
            // Calcular o envelope bruto dos vetores do cluster
            PdfRect vectorEnvelope = cluster[0].Bounds;
            for (int i = 1; i < cluster.Count; i++)
            {
                vectorEnvelope = vectorEnvelope.Union(cluster[i].Bounds);
            }

            // Diferenciação: Verificar se o cluster é um Diagrama/Gráfico e NÃO uma Tabela Ortogonal
            if (!IsDiagramOrChartCluster(cluster, vectorEnvelope))
                continue;

            PdfRect aggregatedBox = vectorEnvelope;
            var consumedWordIndices = new List<int>();

            // 3. Expandir a Bounding Box para incorporar qualquer palavra ou rótulo de texto
            //    contido dentro de suas margens ou na margem de captura
            bool expanded;
            do
            {
                expanded = false;
                PdfRect searchBox = aggregatedBox.Inflate(
                    _options.TextCaptureMarginPt,
                    _options.TextCaptureMarginPt * 0.65,
                    page.Width,
                    page.Height);

                for (int wIdx = 0; wIdx < pageWords.Count; wIdx++)
                {
                    if (tracker.IsWordConsumed(wIdx) || consumedWordIndices.Contains(wIdx))
                        continue;

                    var wordRect = PdfRect.FromPdfPig(pageWords[wIdx].BoundingBox);
                    if (!searchBox.Intersects(wordRect))
                        continue;

                    // Evitar absorver parágrafos ou títulos externos acima/abaixo do container do diagrama
                    if (IsExternalParagraphOrHeading(pageWords[wIdx], wordRect, vectorEnvelope))
                        continue;

                    aggregatedBox = aggregatedBox.Union(wordRect);
                    consumedWordIndices.Add(wIdx);
                    expanded = true;
                }
            } while (expanded);

            // 4. Aplicar margem de respiro final (padding de 5 a 10 pt)
            PdfRect finalBoundingBox = aggregatedBox.Inflate(
                _options.DiagramPaddingPt,
                _options.DiagramPaddingPt,
                page.Width,
                page.Height);

            // 5. Marcação de Conteúdo Consumido (vetores e palavras)
            foreach (var path in cluster)
            {
                tracker.MarkPathConsumed(path.PathIndex);
            }

            foreach (int wIdx in consumedWordIndices)
            {
                tracker.MarkWordConsumed(wIdx);
            }

            // Garantir que nenhuma palavra estritamente dentro do envelope dos vetores escape
            for (int wIdx = 0; wIdx < pageWords.Count; wIdx++)
            {
                if (!tracker.IsWordConsumed(wIdx))
                {
                    var wordRect = PdfRect.FromPdfPig(pageWords[wIdx].BoundingBox);
                    if (vectorEnvelope.Inflate(3, 3, page.Width, page.Height).Intersects(wordRect) &&
                        !IsExternalParagraphOrHeading(pageWords[wIdx], wordRect, vectorEnvelope))
                    {
                        tracker.MarkWordConsumed(wIdx);
                        consumedWordIndices.Add(wIdx);
                    }
                }
            }

            detectedDiagrams.Add(new DiagramCandidate(
                PageNumber: page.Number,
                BoundingBox: finalBoundingBox,
                VectorPrimitiveCount: cluster.Count,
                AbsorbedWordCount: consumedWordIndices.Count));
        }

        return detectedDiagrams
            .OrderByDescending(d => d.BoundingBox.MaxY)
            .ToList();
    }

    private static List<List<ClassifiedPath>> ClusterPathsByDistance(
        List<ClassifiedPath> paths,
        double thresholdPt)
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
                if (paths[i].Bounds.EuclideanDistanceTo(paths[j].Bounds) <= thresholdPt)
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

    /// <summary>
    /// Determina se um agrupamento de vetores corresponde a um Diagrama/Gráfico (Ishikawa, Fluxograma, Gráfico de Barras)
    /// em vez de uma Tabela Ortogonal.
    /// </summary>
    private static bool IsDiagramOrChartCluster(
        List<ClassifiedPath> cluster,
        PdfRect envelope)
    {
        // Dimensões mínimas para ser considerado um diagrama/gráfico
        if (envelope.Width < 90 || envelope.Height < 45 || cluster.Count < 3)
            return false;

        int diagonalPaths = cluster.Count(p => p.HasDiagonal);
        int curvedPaths = cluster.Count(p => p.HasCurve);

        // Critério 1: Presença de linhas diagonais, pontas de seta ou curvas de Bézier (ex: Ishikawa, Fluxograma)
        if (diagonalPaths >= 1 || curvedPaths >= 1)
            return true;

        // Critério 2: Gráfico de Barras (Bar/Column Chart)
        // Possui >= 3 barras preenchidas coloridas alinhadas na mesma linha de base inferior (eixo X),
        // espaçadas horizontalmente entre si (não coladas borda-a-borda como células de tabela).
        var coloredRects = cluster
            .Where(p => p.IsFilled &&
                        !PdfColorHelper.IsNearWhite(p.FillColorHex) &&
                        p.Bounds.Width is >= 10 and <= 95 &&
                        p.Bounds.Height >= 20 &&
                        p.Bounds.Width < envelope.Width * 0.35)
            .OrderBy(p => p.Bounds.MinX)
            .ToList();

        if (coloredRects.Count >= 3)
        {
            // Agrupar por linha de base inferior (MinY semelhante)
            foreach (var baseCandidate in coloredRects)
            {
                var alignedOnBase = coloredRects
                    .Where(r => Math.Abs(r.Bounds.MinY - baseCandidate.Bounds.MinY) <= 4.0)
                    .OrderBy(r => r.Bounds.MinX)
                    .ToList();

                if (alignedOnBase.Count >= 3)
                {
                    bool hasGapsBetweenBars = true;
                    for (int i = 1; i < alignedOnBase.Count; i++)
                    {
                        double gap = alignedOnBase[i].Bounds.MinX - alignedOnBase[i - 1].Bounds.MaxX;
                        if (gap < 6.0)
                        {
                            hasGapsBetweenBars = false;
                            break;
                        }
                    }

                    if (hasGapsBetweenBars)
                        return true;
                }
            }
        }

        // Critério 3: Diagrama de blocos retangulares desconectados dentro de um quadro container
        // (onde as caixas internas são separadas por espaços vazios e cobrem apenas parte da área do container)
        var outerContainer = cluster
            .FirstOrDefault(p => Math.Abs(p.Bounds.Width - envelope.Width) <= 6.0 &&
                                 Math.Abs(p.Bounds.Height - envelope.Height) <= 6.0);

        if (outerContainer != null)
        {
            var innerBoxes = cluster
                .Where(p => p.PathIndex != outerContainer.PathIndex &&
                            p.Bounds.Width >= 25 &&
                            p.Bounds.Height >= 15 &&
                            p.Bounds.Width < envelope.Width * 0.45)
                .ToList();

            if (innerBoxes.Count >= 3)
            {
                // Verificar se as caixas internas possuem espaçamento horizontal/vertical entre si
                // (em uma tabela, as células tocam umas nas outras sem lacunas > 4pt)
                var distinctBoxes = DeduplicateCoincidentBoxes(innerBoxes);
                if (distinctBoxes.Count >= 3)
                {
                    int spacedPairs = 0;
                    var orderedByX = distinctBoxes.OrderBy(b => b.MinX).ToList();
                    for (int i = 1; i < orderedByX.Count; i++)
                    {
                        double gapX = orderedByX[i].MinX - orderedByX[i - 1].MaxX;
                        if (gapX >= 8.0 && orderedByX[i].VerticalOverlapRatio(orderedByX[i - 1]) > 0.5)
                        {
                            spacedPairs++;
                        }
                    }

                    if (spacedPairs >= 2)
                        return true;
                }
            }
        }

        return false;
    }

    private static List<PdfRect> DeduplicateCoincidentBoxes(List<ClassifiedPath> paths)
    {
        var result = new List<PdfRect>();
        foreach (var p in paths)
        {
            bool exists = result.Any(r =>
                Math.Abs(r.MinX - p.Bounds.MinX) <= 3.0 &&
                Math.Abs(r.MinY - p.Bounds.MinY) <= 3.0 &&
                Math.Abs(r.MaxX - p.Bounds.MaxX) <= 3.0 &&
                Math.Abs(r.MaxY - p.Bounds.MaxY) <= 3.0);

            if (!exists)
                result.Add(p.Bounds);
        }
        return result;
    }

    private static bool IsExternalParagraphOrHeading(Word word, PdfRect wordRect, PdfRect vectorEnvelope)
    {
        // Se a palavra está claramente acima ou abaixo do envelope vetorial do diagrama (> 3.5 pt fora),
        // pertence ao texto introdutório/conclusivo da página e não ao diagrama.
        if (wordRect.MinY > vectorEnvelope.MaxY + 3.5 || wordRect.MaxY < vectorEnvelope.MinY - 3.5)
            return true;

        return false;
    }
}
