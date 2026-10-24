using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PdfToWord.Cli.OpenXml;

/// <summary>
/// Cria e registra a folha de estilos (StyleDefinitionsPart) nativa do Word (.docx)
/// para permitir navegação hierárquica (H1, H2, H3, Title) e edição limpa.
/// </summary>
public static class DocxStyleManager
{
    public static void EnsureStyles(MainDocumentPart mainPart)
    {
        var stylePart = mainPart.StyleDefinitionsPart ?? mainPart.AddNewPart<StyleDefinitionsPart>();

        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts
                        {
                            Ascii = "Segoe UI",
                            HighAnsi = "Segoe UI",
                            ComplexScript = "Segoe UI"
                        },
                        new FontSize { Val = "20" }, // 10 pt
                        new FontSizeComplexScript { Val = "20" },
                        new Color { Val = "1E293B" },
                        new Languages { Val = "pt-BR" })),
                new ParagraphPropertiesDefault(
                    new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines
                        {
                            After = "120",
                            Line = "276",
                            LineRule = LineSpacingRuleValues.Auto
                        }))));

        styles.Append(
            CreateParagraphStyle("Normal", "Normal", isDefault: true, "Segoe UI", 20, "1E293B", bold: false),
            CreateParagraphStyle("Title", "Title", isDefault: false, "Georgia", 44, "1B5E20", bold: true, outlineLevel: null),
            CreateParagraphStyle("Subtitle", "Subtitle", isDefault: false, "Segoe UI", 28, "334155", bold: false, outlineLevel: null),
            CreateParagraphStyle("Heading1", "heading 1", isDefault: false, "Georgia", 32, "1B5E20", bold: true, outlineLevel: 0),
            CreateParagraphStyle("Heading2", "heading 2", isDefault: false, "Georgia", 25, "1E8449", bold: true, outlineLevel: 1),
            CreateParagraphStyle("Heading3", "heading 3", isDefault: false, "Segoe UI", 22, "1E293B", bold: true, outlineLevel: 2));

        stylePart.Styles = styles;
        stylePart.Styles.Save();
    }

    private static Style CreateParagraphStyle(
        string styleId,
        string styleName,
        bool isDefault,
        string fontFamily,
        int halfPoints,
        string colorHex,
        bool bold,
        int? outlineLevel = null)
    {
        var style = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId,
            Default = isDefault ? OnOffValue.FromBoolean(true) : null,
            CustomStyle = isDefault ? null : OnOffValue.FromBoolean(true)
        };

        style.Append(new StyleName { Val = styleName });
        style.Append(new PrimaryStyle());

        var pPr = new StyleParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "120", After = "80" });

        if (outlineLevel.HasValue)
        {
            pPr.Append(new OutlineLevel { Val = outlineLevel.Value });
        }

        var rPr = new StyleRunProperties(
            new RunFonts { Ascii = fontFamily, HighAnsi = fontFamily, ComplexScript = fontFamily },
            new FontSize { Val = halfPoints.ToString() },
            new FontSizeComplexScript { Val = halfPoints.ToString() },
            new Color { Val = colorHex });

        if (bold)
            rPr.Append(new Bold());

        style.Append(pPr, rPr);
        return style;
    }
}
