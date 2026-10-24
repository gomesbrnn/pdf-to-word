using PdfToWord.Cli.Models;

namespace PdfToWord.Cli.Models;

/// <summary>
/// Representa um trecho de texto (Run) com estilo tipográfico uniforme.
/// </summary>
public sealed record StyledTextRun(
    string Text,
    string FontFamily,
    double FontSizePt,
    string ColorHex,
    bool IsBold,
    bool IsItalic,
    PdfRect Bounds);
