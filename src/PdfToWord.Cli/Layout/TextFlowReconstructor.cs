using System.Text.RegularExpressions;
using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

/// <summary>
/// Reconstrói o fluxo textual (linhas, parágrafos, títulos H1/H2/H3, listas com marcadores ou numeradas)
/// a partir de um conjunto de palavras do PDF, preservando negrito, itálico e cor em cada Run.
/// </summary>
public static partial class TextFlowReconstructor
{
    [GeneratedRegex(@"^FASE\s+\d+\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex FaseHeadingRegex();

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?\s+\S+")]
    private static partial Regex SectionNumberHeadingRegex();

    [GeneratedRegex(@"^(\d+\.)+\s*")]
    private static partial Regex NumberedListPrefixRegex();

    public sealed record WordWithIndex(int Index, Word Word, PdfRect Bounds);

    /// <summary>
    /// Reconstrói parágrafos estruturados para o corpo principal da página (excluindo palavras já consumidas).
    /// </summary>
    public static IReadOnlyList<ParagraphBlock> ReconstructPageTextFlow(
        Page page,
        IReadOnlyList<Word> allWords,
        IReadOnlyList<ClassifiedPath> allPaths,
        IReadOnlyList<PdfRect> vectorBullets,
        ConsumptionTracker tracker,
        bool isCoverPage)
    {
        var remaining = new List<WordWithIndex>();
        for (int i = 0; i < allWords.Count; i++)
        {
            if (!tracker.IsWordConsumed(i))
            {
                remaining.Add(new WordWithIndex(i, allWords[i], PdfRect.FromPdfPig(allWords[i].BoundingBox)));
            }
        }

        if (remaining.Count == 0)
            return Array.Empty<ParagraphBlock>();

        var lines = GroupWordsIntoLines(remaining, vectorBullets);
        var paragraphs = GroupLinesIntoParagraphs(lines, page.Width, isCoverPage, allPaths, tracker);

        foreach (var w in remaining)
        {
            tracker.MarkWordConsumed(w.Index);
        }

        return paragraphs;
    }

    /// <summary>
    /// Reconstrói parágrafos dentro de uma região delimitada (ex: célula de tabela ou Callout Box).
    /// </summary>
    public static IReadOnlyList<ParagraphBlock> ReconstructCellOrBoxParagraphs(
        IReadOnlyList<WordWithIndex> boxWords,
        double containerWidth,
        TextAlignmentMode defaultAlignment = TextAlignmentMode.Left,
        IReadOnlyList<PdfRect>? vectorBullets = null,
        bool forceBoldCell = false)
    {
        if (boxWords.Count == 0)
            return Array.Empty<ParagraphBlock>();

        var lines = GroupWordsIntoLines(boxWords, vectorBullets);
        if (lines.Count == 0)
            return Array.Empty<ParagraphBlock>();

        var result = new List<ParagraphBlock>();
        var currentParaLines = new List<TextLineModel> { lines[0] };

        for (int i = 1; i < lines.Count; i++)
        {
            var prev = lines[i - 1];
            var curr = lines[i];

            double verticalGap = prev.Bounds.MinY - curr.Bounds.MaxY;
            double avgFontSize = (prev.DominantFontSizePt + curr.DominantFontSizePt) * 0.5;

            bool startsNewBullet = curr.IsBulletLine || curr.IsNumberedListLine;
            bool startsBoldField = curr.HasColonLabelPrefix && prev.Bounds.Width < containerWidth * 0.85;
            bool largeVerticalGap = verticalGap > avgFontSize * 0.68;

            // Em containers largos (ex: Callout Box), se a linha anterior terminou curta (< 65% da largura),
            // houve quebra de linha explícita (ex: lista de nomes da Equipe Técnica ou campos de metadados).
            bool prevWasExplicitShortLine = containerWidth >= 175.0 &&
                                            !prev.IsBulletLine &&
                                            !prev.IsNumberedListLine &&
                                            prev.Bounds.Width < containerWidth * 0.65;

            if (startsNewBullet || startsBoldField || largeVerticalGap || prevWasExplicitShortLine)
            {
                result.Add(BuildParagraphFromLines(currentParaLines, defaultAlignment, forceBoldAllRuns: forceBoldCell));
                currentParaLines = new List<TextLineModel> { curr };
            }
            else
            {
                currentParaLines.Add(curr);
            }
        }

        if (currentParaLines.Count > 0)
        {
            result.Add(BuildParagraphFromLines(currentParaLines, defaultAlignment, forceBoldAllRuns: forceBoldCell));
        }

        return result;
    }

    private sealed record TextLineModel(
        PdfRect Bounds,
        IReadOnlyList<WordWithIndex> Words,
        double DominantFontSizePt,
        string DominantColorHex,
        bool IsBoldLine,
        bool IsBulletLine,
        bool HasTextBulletToken,
        bool IsNumberedListLine,
        bool HasColonLabelPrefix,
        string FullText);

    private static List<TextLineModel> GroupWordsIntoLines(
        IReadOnlyList<WordWithIndex> words,
        IReadOnlyList<PdfRect>? vectorBullets = null)
    {
        var sorted = words
            .OrderByDescending(w => w.Bounds.CenterY)
            .ThenBy(w => w.Bounds.MinX)
            .ToList();

        var rawLineGroups = new List<List<WordWithIndex>>();

        foreach (var item in sorted)
        {
            bool added = false;
            foreach (var lineGroup in rawLineGroups)
            {
                double lineCenterY = lineGroup.Average(w => w.Bounds.CenterY);
                double lineHeight = lineGroup.Average(w => w.Bounds.Height);
                double tolerance = Math.Max(3.2, lineHeight * 0.45);

                if (Math.Abs(item.Bounds.CenterY - lineCenterY) <= tolerance)
                {
                    lineGroup.Add(item);
                    added = true;
                    break;
                }
            }

            if (!added)
            {
                rawLineGroups.Add(new List<WordWithIndex> { item });
            }
        }

        var lines = new List<TextLineModel>(rawLineGroups.Count);

        foreach (var group in rawLineGroups)
        {
            var orderedWords = group.OrderBy(w => w.Bounds.MinX).ToList();

            // Remover numeração duplicada ("1. 1." -> "1.")
            if (orderedWords.Count >= 3 &&
                NumberedListPrefixRegex().IsMatch(orderedWords[0].Word.Text.Trim()) &&
                string.Equals(orderedWords[0].Word.Text.Trim(), orderedWords[1].Word.Text.Trim(), StringComparison.Ordinal))
            {
                orderedWords.RemoveAt(0);
            }

            PdfRect lineBounds = orderedWords[0].Bounds;
            for (int i = 1; i < orderedWords.Count; i++)
                lineBounds = lineBounds.Union(orderedWords[i].Bounds);

            double maxFontSize = orderedWords.Max(w => GetWordFontSize(w.Word));
            string firstColor = GetWordColorHex(orderedWords[0].Word);
            bool allBold = orderedWords.All(w => IsWordBold(w.Word));

            string firstText = orderedWords[0].Word.Text.Trim();
            bool hasTextBulletToken = firstText is "•" or "●" or "▪";

            // Em fontes Type3 do Chromium, o GlyphRectangle fica ligeiramente abaixo do topo,
            // de modo que o ponto vetorial (bullet) fica entre [lineBounds.MinY - 2.0, lineBounds.MaxY + 6.5]
            bool hasVectorBullet = !hasTextBulletToken &&
                                   vectorBullets != null &&
                                   vectorBullets.Any(b =>
                                       b.CenterY >= lineBounds.MinY - 2.0 &&
                                       b.CenterY <= lineBounds.MaxY + 6.5 &&
                                       orderedWords[0].Bounds.MinX - b.MaxX is >= 0.5 and <= 24.0);

            bool isBullet = hasTextBulletToken || hasVectorBullet;

            string fullText = string.Join(" ", orderedWords.Select(w => NormalizePdfText(w.Word.Text)));
            bool isNumbered = !isBullet &&
                              !SectionNumberHeadingRegex().IsMatch(fullText) &&
                              NumberedListPrefixRegex().IsMatch(firstText) &&
                              orderedWords.Count > 1;

            bool hasColonLabel = orderedWords.Count >= 1 &&
                                 orderedWords.Take(Math.Min(5, orderedWords.Count)).Any(w => w.Word.Text.EndsWith(':'));

            lines.Add(new TextLineModel(
                Bounds: lineBounds,
                Words: orderedWords,
                DominantFontSizePt: maxFontSize,
                DominantColorHex: firstColor,
                IsBoldLine: allBold,
                IsBulletLine: isBullet,
                HasTextBulletToken: hasTextBulletToken,
                IsNumberedListLine: isNumbered,
                HasColonLabelPrefix: hasColonLabel,
                FullText: fullText));
        }

        return lines
            .OrderByDescending(l => l.Bounds.CenterY)
            .ToList();
    }

    private static List<ParagraphBlock> GroupLinesIntoParagraphs(
        List<TextLineModel> lines,
        double pageWidth,
        bool isCoverPage,
        IReadOnlyList<ClassifiedPath> allPaths,
        ConsumptionTracker tracker)
    {
        var blocks = new List<ParagraphBlock>();
        int idx = 0;

        while (idx < lines.Count)
        {
            var line = lines[idx];

            // Caso 1: Capa (Página 1)
            if (isCoverPage)
            {
                // Na capa, apenas o Título Principal (>= 20pt) e o Subtítulo (15..19pt) quebram em 2 linhas;
                // cabeçalhos institucionais (14pt) e local/data (12pt) são linhas independentes.
                if (line.DominantFontSizePt >= 15.0)
                {
                    var coverGroup = new List<TextLineModel> { line };
                    idx++;
                    while (idx < lines.Count &&
                           Math.Abs(lines[idx].DominantFontSizePt - line.DominantFontSizePt) <= 1.0)
                    {
                        coverGroup.Add(lines[idx]);
                        idx++;
                    }

                    bool isMainTitle = line.DominantFontSizePt >= 20.0;
                    HeadingLevel coverHeading = isMainTitle ? HeadingLevel.Title : HeadingLevel.Subtitle;
                    double spaceBefore = isMainTitle ? 110 : 8;
                    double spaceAfter = isMainTitle ? 10 : 120;

                    blocks.Add(BuildParagraphFromLines(
                        coverGroup,
                        TextAlignmentMode.Center,
                        coverHeading,
                        bottomBorderColor: null,
                        spacingBeforePt: spaceBefore,
                        spacingAfterPt: spaceAfter,
                        forceBoldAllRuns: isMainTitle,
                        overrideFontFamily: isMainTitle ? "Georgia" : "Segoe UI"));
                }
                else
                {
                    blocks.Add(BuildParagraphFromLines(
                        new[] { line },
                        TextAlignmentMode.Center,
                        HeadingLevel.None,
                        bottomBorderColor: null,
                        spacingBeforePt: 2,
                        spacingAfterPt: 4,
                        forceBoldAllRuns: true));
                    idx++;
                }
                continue;
            }

            // Caso 2: Título H1 (ex: "FASE 1: PLAN (Planejamento)")
            if (line.DominantFontSizePt >= 13.8 || FaseHeadingRegex().IsMatch(line.FullText))
            {
                string? underlineColor = FindAndConsumeUnderlineBar(line.Bounds, allPaths, tracker);
                blocks.Add(BuildParagraphFromLines(
                    new[] { line },
                    TextAlignmentMode.Left,
                    HeadingLevel.H1,
                    bottomBorderColor: underlineColor ?? "2E7D32",
                    spacingBeforePt: 14,
                    spacingAfterPt: 10,
                    forceBoldAllRuns: true,
                    overrideFontFamily: "Georgia"));
                idx++;
                continue;
            }

            // Caso 3: Subtítulo H2 (ex: "1.1 Tempestade de Ideias...")
            if (SectionNumberHeadingRegex().IsMatch(line.FullText) ||
                (line.DominantFontSizePt >= 11.2 && line.DominantColorHex is "15803D" or "166534" or "1E8449"))
            {
                blocks.Add(BuildParagraphFromLines(
                    new[] { line },
                    TextAlignmentMode.Left,
                    HeadingLevel.H2,
                    bottomBorderColor: null,
                    spacingBeforePt: 12,
                    spacingAfterPt: 6,
                    forceBoldAllRuns: true,
                    overrideFontFamily: "Georgia"));
                idx++;
                continue;
            }

            // Caso 4: Item de Lista (• ou 1.)
            if (line.IsBulletLine || line.IsNumberedListLine)
            {
                var bulletLines = new List<TextLineModel> { line };
                idx++;
                while (idx < lines.Count)
                {
                    var next = lines[idx];
                    double gap = bulletLines[^1].Bounds.MinY - next.Bounds.MaxY;
                    if (!next.IsBulletLine &&
                        !next.IsNumberedListLine &&
                        !SectionNumberHeadingRegex().IsMatch(next.FullText) &&
                        !FaseHeadingRegex().IsMatch(next.FullText) &&
                        next.DominantFontSizePt < 11.4 &&
                        gap <= line.DominantFontSizePt * 0.90 &&
                        Math.Abs(next.Bounds.MinX - line.Bounds.MinX) <= 18.0)
                    {
                        bulletLines.Add(next);
                        idx++;
                    }
                    else
                    {
                        break;
                    }
                }

                blocks.Add(BuildParagraphFromLines(
                    bulletLines,
                    TextAlignmentMode.Left,
                    HeadingLevel.None,
                    bottomBorderColor: null,
                    spacingBeforePt: 2,
                    spacingAfterPt: 4));
                continue;
            }

            // Caso 5: Parágrafo de corpo normal (agrupa linhas contínuas do mesmo parágrafo)
            var paraLines = new List<TextLineModel> { line };
            idx++;
            while (idx < lines.Count)
            {
                var prev = paraLines[^1];
                var next = lines[idx];
                double gap = prev.Bounds.MinY - next.Bounds.MaxY;

                bool nextIsHeading = next.DominantFontSizePt >= 13.8 ||
                                     FaseHeadingRegex().IsMatch(next.FullText) ||
                                     SectionNumberHeadingRegex().IsMatch(next.FullText) ||
                                     (next.DominantFontSizePt >= 11.2 && next.DominantColorHex is "15803D" or "166534");
                bool nextIsBullet = next.IsBulletLine || next.IsNumberedListLine;
                bool startsNewParagraphByLabel = next.HasColonLabelPrefix && prev.Bounds.Width < pageWidth * 0.72;

                if (nextIsHeading || nextIsBullet || startsNewParagraphByLabel || gap > prev.DominantFontSizePt * 0.85)
                    break;

                paraLines.Add(next);
                idx++;
            }

            bool isStandaloneSubtitle = paraLines.Count == 1 &&
                                        paraLines[0].FullText.EndsWith(':') &&
                                        paraLines[0].Words.Count <= 8;

            blocks.Add(BuildParagraphFromLines(
                paraLines,
                TextAlignmentMode.Left,
                HeadingLevel.None,
                bottomBorderColor: null,
                spacingBeforePt: 4,
                spacingAfterPt: 6,
                forceBoldAllRuns: isStandaloneSubtitle));
        }

        return blocks;
    }

    private static string? FindAndConsumeUnderlineBar(
        PdfRect headingBounds,
        IReadOnlyList<ClassifiedPath> allPaths,
        ConsumptionTracker tracker)
    {
        foreach (var path in allPaths)
        {
            if (tracker.IsPathConsumed(path.PathIndex))
                continue;

            if (path.Bounds.Width >= 200 && path.Bounds.Height <= 4.5)
            {
                double dy = headingBounds.MinY - path.Bounds.MaxY;
                if (dy is >= -2.0 and <= 15.0)
                {
                    tracker.MarkPathConsumed(path.PathIndex);
                    return path.StrokeColorHex ?? path.FillColorHex ?? "2E7D32";
                }
            }
        }

        return null;
    }

    private static ParagraphBlock BuildParagraphFromLines(
        IReadOnlyList<TextLineModel> lines,
        TextAlignmentMode alignment,
        HeadingLevel heading = HeadingLevel.None,
        string? bottomBorderColor = null,
        double spacingBeforePt = 2,
        double spacingAfterPt = 4,
        bool forceBoldAllRuns = false,
        string? overrideFontFamily = null)
    {
        PdfRect totalBounds = lines[0].Bounds;
        for (int i = 1; i < lines.Count; i++)
            totalBounds = totalBounds.Union(lines[i].Bounds);

        bool isBullet = lines[0].IsBulletLine;
        bool hasTextBulletToken = lines[0].HasTextBulletToken;
        string? listPrefix = isBullet ? "•" : null;

        var allWords = new List<WordWithIndex>();
        for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
        {
            var lineWords = lines[lineIdx].Words;
            for (int wIdx = 0; wIdx < lineWords.Count; wIdx++)
            {
                if (lineIdx == 0 && wIdx == 0 && hasTextBulletToken)
                {
                    continue;
                }

                allWords.Add(lineWords[wIdx]);
            }
        }

        var runs = MergeWordsIntoStyledRuns(
            allWords,
            forceBoldAllRuns,
            overrideFontFamily,
            isNumberedQuestionLine: lines[0].IsNumberedListLine);

        return new ParagraphBlock(
            Bounds: totalBounds,
            Runs: runs,
            Heading: heading,
            Alignment: alignment,
            IsBulletItem: isBullet,
            ListPrefix: listPrefix,
            BottomBorderColorHex: bottomBorderColor,
            SpacingBeforePt: spacingBeforePt,
            SpacingAfterPt: spacingAfterPt);
    }

    private static IReadOnlyList<StyledTextRun> MergeWordsIntoStyledRuns(
        IReadOnlyList<WordWithIndex> words,
        bool forceBoldAllRuns = false,
        string? overrideFontFamily = null,
        bool isNumberedQuestionLine = false)
    {
        if (words.Count == 0)
            return Array.Empty<StyledTextRun>();

        // Determinar até qual índice de palavra vai o prefixo em negrito (ex: "Carga Horária:", "1. Por que...?")
        int boldPrefixEndIdx = -1;
        if (!forceBoldAllRuns)
        {
            // Se começa com rótulo terminado em ':' dentro das primeiras 5 palavras (ou linha toda se <= 6 palavras)
            for (int i = 0; i < Math.Min(6, words.Count); i++)
            {
                if (words[i].Word.Text.EndsWith(':'))
                {
                    boldPrefixEndIdx = (i == 2 && words[0].Word.Text.Equals("Ementa", StringComparison.OrdinalIgnoreCase))
                        ? words.Count - 1 // "Ementa do Curso: Conscientização..." é inteira em negrito
                        : i;
                    break;
                }
            }

            // Em listas numeradas de perguntas ("1. Por que as dores ocorrem? Porque..."), destacar até o '?'
            if (boldPrefixEndIdx < 0 && isNumberedQuestionLine)
            {
                for (int i = 0; i < Math.Min(12, words.Count); i++)
                {
                    if (words[i].Word.Text.EndsWith('?'))
                    {
                        boldPrefixEndIdx = i;
                        break;
                    }
                }
            }
        }

        var runs = new List<StyledTextRun>();

        bool GetEffectiveBold(int idx)
        {
            if (forceBoldAllRuns || idx <= boldPrefixEndIdx)
                return true;

            string color = GetWordColorHex(words[idx].Word);
            // Textos coloridos de status/destaque (verde Aprovado, amarelo Alerta, vermelho Causa Raiz) são negrito
            if (color is not "1E293B" and not "334155" and not "475569" and not "64748B")
                return true;

            return IsWordBold(words[idx].Word);
        }

        string currentFont = overrideFontFamily ?? GetCleanFontFamily(words[0].Word);
        double currentSize = GetWordFontSize(words[0].Word);
        string currentColor = GetWordColorHex(words[0].Word);
        bool currentBold = GetEffectiveBold(0);
        bool currentItalic = IsWordItalic(words[0].Word);
        PdfRect currentBounds = words[0].Bounds;
        var currentText = new List<string> { NormalizePdfText(words[0].Word.Text) };

        for (int i = 1; i < words.Count; i++)
        {
            var w = words[i].Word;
            string font = overrideFontFamily ?? GetCleanFontFamily(w);
            double size = GetWordFontSize(w);
            string color = GetWordColorHex(w);
            bool bold = GetEffectiveBold(i);
            bool italic = IsWordItalic(w);
            string normalizedText = NormalizePdfText(w.Text);

            if (bold == currentBold &&
                italic == currentItalic &&
                string.Equals(color, currentColor, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(size - currentSize) <= 0.6)
            {
                currentText.Add(normalizedText);
                currentBounds = currentBounds.Union(words[i].Bounds);
            }
            else
            {
                runs.Add(new StyledTextRun(
                    Text: string.Join(" ", currentText) + " ",
                    FontFamily: currentFont,
                    FontSizePt: currentSize,
                    ColorHex: currentColor,
                    IsBold: currentBold,
                    IsItalic: currentItalic,
                    Bounds: currentBounds));

                currentFont = font;
                currentSize = size;
                currentColor = color;
                currentBold = bold;
                currentItalic = italic;
                currentBounds = words[i].Bounds;
                currentText = new List<string> { normalizedText };
            }
        }

        runs.Add(new StyledTextRun(
            Text: string.Join(" ", currentText),
            FontFamily: currentFont,
            FontSizePt: currentSize,
            ColorHex: currentColor,
            IsBold: currentBold,
            IsItalic: currentItalic,
            Bounds: currentBounds));

        return runs;
    }

    /// <summary>
    /// Substitui ligaduras tipográficas do PDF (U+FB00..U+FB04) por caracteres padrão editáveis no Word.
    /// </summary>
    public static string NormalizePdfText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("\uFB00", "ff", StringComparison.Ordinal)
            .Replace("\uFB01", "fi", StringComparison.Ordinal)
            .Replace("\uFB02", "fl", StringComparison.Ordinal)
            .Replace("\uFB03", "ffi", StringComparison.Ordinal)
            .Replace("\uFB04", "ffl", StringComparison.Ordinal);
    }

    public static double GetWordFontSize(Word word)
    {
        if (word.Letters.Count == 0)
            return 10.5;

        double avgPointSize = word.Letters.Average(l => l.PointSize);
        if (avgPointSize is >= 5.0 and <= 48.0)
            return Math.Round(avgPointSize * 2.0) / 2.0;

        double height = word.BoundingBox.Height;
        return Math.Clamp(Math.Round(height * 2.0) / 2.0, 8.0, 36.0);
    }

    public static string GetWordColorHex(Word word)
    {
        foreach (var letter in word.Letters)
        {
            string? hex = PdfColorHelper.ToHex(letter.Color);
            if (!string.IsNullOrEmpty(hex))
                return hex;
        }

        return "1E293B";
    }

    public static bool IsWordBold(Word word)
    {
        string fontName = word.FontName ?? string.Empty;
        if (fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("-Bd", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("SemiBold", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Medium", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return word.Letters.Any(l =>
            l.FontName != null &&
            (l.FontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
             l.FontName.Contains("-Bd", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsWordItalic(Word word)
    {
        string fontName = word.FontName ?? string.Empty;
        if (fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ||
            fontName.Contains("-It", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return word.Letters.Any(l =>
            l.FontName != null &&
            (l.FontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
             l.FontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase)));
    }

    public static string GetCleanFontFamily(Word word)
    {
        string raw = word.FontName ?? string.Empty;
        if (raw.Contains("Georgia", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Times", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !raw.Contains("Sans", StringComparison.OrdinalIgnoreCase))
        {
            return "Georgia";
        }

        return "Segoe UI";
    }
}
