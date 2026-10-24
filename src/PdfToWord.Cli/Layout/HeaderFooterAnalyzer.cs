using PdfToWord.Cli.Core;
using PdfToWord.Cli.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PdfToWord.Cli.Layout;

public sealed record HeaderFooterProfile(
    string? HeaderLeftText,
    string? HeaderRightText,
    string? FooterLeftText,
    bool FooterHasPageNumberRight,
    double HeaderThresholdY,
    double FooterThresholdY);

/// <summary>
/// Analisa todas as páginas do PDF para identificar padrões repetitivos nas margens superior e inferior
/// e direcioná-los às HeaderPart / FooterPart nativas do Word.
/// </summary>
public static class HeaderFooterAnalyzer
{
    private const double BottomZoneHeightPt = 58.0;
    private const double TopZoneHeightPt = 52.0;

    public static HeaderFooterProfile AnalyzeDocument(PdfDocument document)
    {
        if (document.NumberOfPages <= 1)
        {
            return new HeaderFooterProfile(null, null, null, false, double.MaxValue, 0);
        }

        var footerLeftCandidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int pageNumberRightMatches = 0;
        double samplePageHeight = 841.89;

        for (int p = 1; p <= document.NumberOfPages; p++)
        {
            var page = document.GetPage(p);
            samplePageHeight = page.Height;
            var words = page.GetWords().ToList();

            var footerWords = words
                .Where(w => w.BoundingBox.Top <= BottomZoneHeightPt)
                .OrderBy(w => w.BoundingBox.Left)
                .ToList();

            if (footerWords.Count == 0)
                continue;

            double midX = page.Width * 0.55;
            var leftWords = footerWords.Where(w => w.BoundingBox.Right <= midX).ToList();
            var rightWords = footerWords.Where(w => w.BoundingBox.Left > midX).ToList();

            if (leftWords.Count > 0)
            {
                string leftText = string.Join(" ", leftWords.Select(w => w.Text)).Trim();
                if (!string.IsNullOrWhiteSpace(leftText))
                {
                    footerLeftCandidates[leftText] = footerLeftCandidates.GetValueOrDefault(leftText) + 1;
                }
            }

            if (rightWords.Count > 0)
            {
                string rightText = string.Join(" ", rightWords.Select(w => w.Text)).Trim();
                if (rightText.StartsWith("Página", StringComparison.OrdinalIgnoreCase) ||
                    rightText.StartsWith("Pagina", StringComparison.OrdinalIgnoreCase) ||
                    rightText.StartsWith("Pág", StringComparison.OrdinalIgnoreCase) ||
                    int.TryParse(rightText, out _))
                {
                    pageNumberRightMatches++;
                }
            }
        }

        string? canonicalFooterLeft = footerLeftCandidates
            .Where(kv => kv.Value >= Math.Max(2, document.NumberOfPages / 3))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        bool hasFooterPageNumber = pageNumberRightMatches >= Math.Max(2, document.NumberOfPages / 3);

        return new HeaderFooterProfile(
            HeaderLeftText: null,
            HeaderRightText: null,
            FooterLeftText: canonicalFooterLeft,
            FooterHasPageNumberRight: hasFooterPageNumber,
            HeaderThresholdY: samplePageHeight - TopZoneHeightPt,
            FooterThresholdY: (canonicalFooterLeft != null || hasFooterPageNumber) ? BottomZoneHeightPt : 0);
    }

    /// <summary>
    /// Marca como consumidas as palavras e linhas vetoriais pertencentes ao cabeçalho/rodapé da página.
    /// </summary>
    public static (string? FooterLeft, string? FooterRight) ConsumePageHeaderFooter(
        Page page,
        IReadOnlyList<Word> words,
        IReadOnlyList<ClassifiedPath> paths,
        HeaderFooterProfile profile,
        ConsumptionTracker tracker,
        bool isCoverPage)
    {
        if (isCoverPage || profile.FooterThresholdY <= 0)
            return (null, null);

        var leftParts = new List<string>();
        var rightParts = new List<string>();
        double midX = page.Width * 0.55;

        for (int i = 0; i < words.Count; i++)
        {
            if (tracker.IsWordConsumed(i))
                continue;

            var box = words[i].BoundingBox;
            if (box.Top <= profile.FooterThresholdY)
            {
                tracker.MarkWordConsumed(i);
                if (box.Right <= midX)
                    leftParts.Add(words[i].Text);
                else
                    rightParts.Add(words[i].Text);
            }
        }

        foreach (var path in paths)
        {
            if (!tracker.IsPathConsumed(path.PathIndex) && path.Bounds.MaxY <= profile.FooterThresholdY)
            {
                tracker.MarkPathConsumed(path.PathIndex);
            }
        }

        string? leftText = leftParts.Count > 0 ? string.Join(" ", leftParts) : profile.FooterLeftText;
        string? rightText = rightParts.Count > 0 ? string.Join(" ", rightParts) : null;

        return (leftText, rightText);
    }
}
