using System.Text;
using PdfToWord.Cli.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PdfToWord.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        var app = new CommandApp<ConvertCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("pdf2word");
            config.ValidateExamples();

            config.AddCommand<ConvertCommand>("convert")
                .WithDescription("Converte um arquivo PDF em um documento Word (.docx) editável de alta fidelidade.")
                .WithExample("convert", "relatorio.pdf", "--dpi", "300", "--ocr", "--verbose");

            config.AddCommand<BatchCommand>("batch")
                .WithDescription("Converte todos os arquivos PDF de uma pasta para documentos Word (.docx).")
                .WithExample("batch", "./pdfs", "-o", "./out", "--dpi", "300");

            config.SetExceptionHandler((ex, _) =>
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return -1;
            });
        });

        return app.Run(args);
    }
}
