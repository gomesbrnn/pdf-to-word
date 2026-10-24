using System.Runtime.InteropServices;
using PDFiumCore;
using PdfToWord.Cli.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace PdfToWord.Cli.Rendering;

/// <summary>
/// Motor de rasterização e recorte de alta fidelidade baseado no Google PDFium (PDFiumCore).
/// Converte coordenadas vetoriais do PDF (Bottom-Left, 72 DPI) para o espaço matricial (Top-Left, 300 DPI)
/// e exporta a região da Bounding Box como PNG sem perdas.
/// </summary>
public sealed class DiagramRasterizer : IDisposable
{
    private const double PdfBaseDpi = 72.0;
    private static readonly object PdfiumLock = new();
    private static bool _isLibraryInitialized;

    private readonly FpdfDocumentT _document;
    private bool _disposed;

    public DiagramRasterizer(string pdfFilePath)
    {
        lock (PdfiumLock)
        {
            if (!_isLibraryInitialized)
            {
                fpdfview.FPDF_InitLibrary();
                _isLibraryInitialized = true;
            }

            _document = fpdfview.FPDF_LoadDocument(pdfFilePath, null);
            if (_document == null)
            {
                throw new InvalidOperationException($"Falha ao abrir o PDF no motor PDFium: {pdfFilePath}");
            }
        }
    }

    /// <summary>
    /// Renderiza a região exata do Bounding Box diretamente da página original em alta resolução (300 DPI)
    /// e exporta como imagem PNG comprimida sem perdas (image/png).
    /// </summary>
    public RenderedDiagram RenderCropToPng(
        int pageNumberOneBased,
        PdfRect pdfBox,
        double pageWidthPt,
        double pageHeightPt,
        int targetDpi = 300)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. Escala de Pontos (72 DPI) para Pixels (DPI alvo, padrão = 300):
        //    Pixels = (Pontos / 72.0) * DPI
        double scale = targetDpi / PdfBaseDpi;

        int fullPageWidthPx = Math.Max(1, (int)Math.Round(pageWidthPt * scale));
        int fullPageHeightPx = Math.Max(1, (int)Math.Round(pageHeightPt * scale));

        // 2. Transformação do Sistema de Coordenadas:
        //    PDF (Bottom-Left origin): MinY é inferior, MaxY é superior.
        //    PDFium (Top-Left origin): Y = 0 é o topo da página.
        double topLeftXPt = pdfBox.MinX;
        double topLeftYPt = pageHeightPt - pdfBox.MaxY;

        int cropLeftPx = Math.Clamp((int)Math.Floor(topLeftXPt * scale), 0, fullPageWidthPx - 1);
        int cropTopPx = Math.Clamp((int)Math.Floor(topLeftYPt * scale), 0, fullPageHeightPx - 1);

        int cropRightPx = Math.Clamp((int)Math.Ceiling(pdfBox.MaxX * scale), cropLeftPx + 1, fullPageWidthPx);
        int cropBottomPx = Math.Clamp((int)Math.Ceiling((pageHeightPt - pdfBox.MinY) * scale), cropTopPx + 1, fullPageHeightPx);

        int cropWidthPx = Math.Max(1, cropRightPx - cropLeftPx);
        int cropHeightPx = Math.Max(1, cropBottomPx - cropTopPx);

        lock (PdfiumLock)
        {
            FpdfPageT page = fpdfview.FPDF_LoadPage(_document, pageNumberOneBased - 1);
            if (page == null)
                throw new InvalidOperationException($"Não foi possível carregar a página {pageNumberOneBased} no PDFium.");

            try
            {
                // 3. Alocar bitmap nativo exatamente com as dimensões do recorte (cropWidthPx x cropHeightPx)
                //    e deslocar o viewport (-cropLeftPx, -cropTopPx) para renderizar apenas a região necessária
                FpdfBitmapT bitmap = fpdfview.FPDFBitmapCreateEx(
                    cropWidthPx,
                    cropHeightPx,
                    (int)FPDFBitmapFormat.BGRA,
                    IntPtr.Zero,
                    0);

                if (bitmap == null)
                    throw new OutOfMemoryException("Falha ao alocar FpdfBitmapT no PDFium.");

                try
                {
                    // Preencher fundo com branco sólido (ARGB: 0xFFFFFFFF)
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, cropWidthPx, cropHeightPx, 0xFFFFFFFF);

                    // Flags: FPDF_ANNOT (0x01) | FPDF_PRINTING (0x800)
                    const int renderFlags = 0x01 | 0x800;

                    fpdfview.FPDF_RenderPageBitmap(
                        bitmap,
                        page,
                        start_x: -cropLeftPx,
                        start_y: -cropTopPx,
                        size_x: fullPageWidthPx,
                        size_y: fullPageHeightPx,
                        rotate: 0,
                        flags: renderFlags);

                    IntPtr scan0 = fpdfview.FPDFBitmapGetBuffer(bitmap);
                    int stride = fpdfview.FPDFBitmapGetStride(bitmap);
                    int bufferLength = stride * cropHeightPx;

                    byte[] rawBgra = new byte[bufferLength];
                    Marshal.Copy(scan0, rawBgra, 0, bufferLength);

                    // 4. Codificar como PNG sem perdas via SixLabors.ImageSharp
                    using var image = new Image<Bgra32>(cropWidthPx, cropHeightPx);
                    image.ProcessPixelRows(accessor =>
                    {
                        for (int y = 0; y < accessor.Height; y++)
                        {
                            Span<Bgra32> rowSpan = accessor.GetRowSpan(y);
                            ReadOnlySpan<byte> srcBytes = new(rawBgra, y * stride, cropWidthPx * 4);
                            MemoryMarshal.Cast<byte, Bgra32>(srcBytes).CopyTo(rowSpan);
                        }
                    });

                    using var ms = new MemoryStream();
                    var encoder = new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.BestCompression,
                        ColorType = PngColorType.RgbWithAlpha
                    };
                    image.SaveAsPng(ms, encoder);

                    return new RenderedDiagram(
                        PngBytes: ms.ToArray(),
                        WidthPx: cropWidthPx,
                        HeightPx: cropHeightPx,
                        Dpi: targetDpi);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Renderiza a página inteira em PNG (usado como entrada para o fallback de OCR quando a página é escaneada).
    /// </summary>
    public RenderedDiagram RenderFullPageToPng(
        int pageNumberOneBased,
        double pageWidthPt,
        double pageHeightPt,
        int targetDpi = 300)
    {
        var fullBox = new PdfRect(0, 0, pageWidthPt, pageHeightPt);
        return RenderCropToPng(pageNumberOneBased, fullBox, pageWidthPt, pageHeightPt, targetDpi);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (PdfiumLock)
        {
            if (_document != null)
            {
                fpdfview.FPDF_CloseDocument(_document);
            }
        }

        _disposed = true;
    }
}

public sealed record RenderedDiagram(
    byte[] PngBytes,
    int WidthPx,
    int HeightPx,
    int Dpi);
