using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

/// <summary>
/// Detecta Caixas de Destaque (Callout Boxes) formadas por um retângulo de fundo isolado + barra lateral espessa
/// que contêm apenas fluxo textual/listas (sem sub-formas de diagrama e sem fazer parte de uma grade de tabela).
/// </summary>
public static class CalloutDetector
{
    public static IReadOnlyList<CalloutBlock> DetectCallouts(
        Page page,
        IReadOnlyList<Word> pageWords,
        IReadOnlyList<ClassifiedPath> paths,
        IReadOnlyList<PdfRect> vectorBullets,
        ConsumptionTracker tracker)
    {
        var callouts = new List<CalloutBlock>();

        // Procurar retângulos preenchidos grandes (containers de callout)
        var candidateContainers = paths
            .Where(p => !tracker.IsPathConsumed(p.PathIndex) &&
                        p.IsFilled &&
                        !p.HasDiagonal &&
                        p.Bounds.Width >= 140 &&
                        p.Bounds.Height >= 45 &&
                        p.Bounds.Width <= page.Width * 0.92)
            .OrderByDescending(p => p.Bounds.Area)
            .ToList();

        foreach (var container in candidateContainers)
        {
            if (tracker.IsPathConsumed(container.PathIndex))
                continue;

            // 1. Verificar se existe uma barra vertical ESPESSA de destaque (>= 1.8 pt) na borda esquerda do container
            var leftAccentBar = paths.FirstOrDefault(p =>
                p.PathIndex != container.PathIndex &&
                !tracker.IsPathConsumed(p.PathIndex) &&
                p.Bounds.Width is >= 1.8 and <= 8.5 &&
                Math.Abs(p.Bounds.MinX - container.Bounds.MinX) <= 5.0 &&
                Math.Abs(p.Bounds.MinY - container.Bounds.MinY) <= 5.0 &&
                Math.Abs(p.Bounds.MaxY - container.Bounds.MaxY) <= 5.0);

            if (leftAccentBar == null)
                continue;

            // 2. Garantir que o container é uma caixa isolada e NÃO uma linha/célula colada a uma tabela!
            bool touchesAdjacentTableCells = paths.Any(p =>
                p.PathIndex != container.PathIndex &&
                p.PathIndex != leftAccentBar.PathIndex &&
                !tracker.IsPathConsumed(p.PathIndex) &&
                !container.Bounds.Contains(p.Bounds, tolerance: 5.0) &&
                (p.Bounds.Width >= 20.0 || p.Bounds.Height >= 14.0) &&
                container.Bounds.EuclideanDistanceTo(p.Bounds) <= 4.0);

            if (touchesAdjacentTableCells)
                continue;

            // 3. Caminhos sobrepostos ao container
            var overlappingPaths = paths
                .Where(p => p.PathIndex != container.PathIndex &&
                            p.PathIndex != leftAccentBar.PathIndex &&
                            !tracker.IsPathConsumed(p.PathIndex) &&
                            container.Bounds.Intersects(p.Bounds, tolerance: 2.0))
                .ToList();

            // Separar caminhos que são apenas bordas perimetrais do próprio Callout
            // de verdadeiras formas internas (caixas internas, divisórias internas de tabela ou setas).
            bool hasInternalShapesOrGrid = false;
            var perimeterPaths = new List<ClassifiedPath>();

            foreach (var p in overlappingPaths)
            {
                if (IsPerimeterBorderOfContainer(p, container.Bounds))
                {
                    perimeterPaths.Add(p);
                    continue;
                }

                // Ignorar pequenos pontos/marcadores de lista (<= 7x7 pt)
                if (p.Bounds.Width <= 7.0 && p.Bounds.Height <= 7.0)
                {
                    perimeterPaths.Add(p);
                    continue;
                }

                if (container.Bounds.Contains(p.Bounds, tolerance: 4.0))
                {
                    hasInternalShapesOrGrid = true;
                    break;
                }
            }

            if (hasInternalShapesOrGrid)
                continue;

            // 4. Capturar todas as palavras cujo centro está estritamente dentro do Callout Box
            //    (evita absorver títulos de seção imediatamente acima da borda superior)
            var insideWords = new List<TextFlowReconstructor.WordWithIndex>();
            for (int wIdx = 0; wIdx < pageWords.Count; wIdx++)
            {
                if (tracker.IsWordConsumed(wIdx))
                    continue;

                var wRect = PdfRect.FromPdfPig(pageWords[wIdx].BoundingBox);
                if (container.Bounds.ContainsPoint(wRect.CenterX, wRect.CenterY, tolerance: 0.5))
                {
                    insideWords.Add(new TextFlowReconstructor.WordWithIndex(wIdx, pageWords[wIdx], wRect));
                }
            }

            if (insideWords.Count == 0)
                continue;

            // 5. Marcar o container, a barra lateral, todas as bordas perimetrais e as palavras como consumidos
            tracker.MarkPathConsumed(container.PathIndex);
            tracker.MarkPathConsumed(leftAccentBar.PathIndex);

            string? outerBorderColor = container.StrokeColorHex;
            foreach (var pp in perimeterPaths)
            {
                outerBorderColor ??= pp.StrokeColorHex ?? pp.FillColorHex;
                tracker.MarkPathConsumed(pp.PathIndex);
            }

            foreach (var w in insideWords)
            {
                tracker.MarkWordConsumed(w.Index);
            }

            var paragraphs = TextFlowReconstructor.ReconstructCellOrBoxParagraphs(
                insideWords,
                container.Bounds.Width,
                TextAlignmentMode.Left,
                vectorBullets);

            string fillHex = container.FillColorHex ?? "F8FAFC";
            string leftHex = leftAccentBar.FillColorHex ?? leftAccentBar.StrokeColorHex ?? "1E8449";

            callouts.Add(new CalloutBlock(
                Bounds: container.Bounds,
                FillColorHex: fillHex,
                LeftBorderColorHex: leftHex,
                OuterBorderColorHex: outerBorderColor,
                InnerParagraphs: paragraphs));
        }

        return callouts
            .OrderByDescending(c => c.Bounds.MaxY)
            .ToList();
    }

    private static bool IsPerimeterBorderOfContainer(ClassifiedPath path, PdfRect container)
    {
        var b = path.Bounds;

        // 1. Retângulo de contorno coincidente com o container
        if (Math.Abs(b.MinX - container.MinX) <= 5.0 &&
            Math.Abs(b.MaxX - container.MaxX) <= 5.0 &&
            Math.Abs(b.MinY - container.MinY) <= 5.0 &&
            Math.Abs(b.MaxY - container.MaxY) <= 5.0)
        {
            return true;
        }

        // 2. Borda superior (próxima a container.MaxY)
        if (b.Height <= 6.0 &&
            Math.Abs(b.MaxY - container.MaxY) <= 5.0 &&
            b.MinX >= container.MinX - 5.0 &&
            b.MaxX <= container.MaxX + 5.0)
        {
            return true;
        }

        // 3. Borda inferior (próxima a container.MinY)
        if (b.Height <= 6.0 &&
            Math.Abs(b.MinY - container.MinY) <= 5.0 &&
            b.MinX >= container.MinX - 5.0 &&
            b.MaxX <= container.MaxX + 5.0)
        {
            return true;
        }

        // 4. Borda esquerda (próxima a container.MinX)
        if (b.Width <= 8.5 &&
            Math.Abs(b.MinX - container.MinX) <= 5.0 &&
            b.MinY >= container.MinY - 5.0 &&
            b.MaxY <= container.MaxY + 5.0)
        {
            return true;
        }

        // 5. Borda direita (próxima a container.MaxX)
        if (b.Width <= 6.0 &&
            Math.Abs(b.MaxX - container.MaxX) <= 5.0 &&
            b.MinY >= container.MinY - 5.0 &&
            b.MaxY <= container.MaxY + 5.0)
        {
            return true;
        }

        return false;
    }
}
