using System.ComponentModel;
using PdfToWord.Cli.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PdfToWord.Cli.Cli;

public sealed class BatchCommand : Command<BatchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT_FOLDER>")]
        [Description("Pasta contendo os arquivos PDF a serem convertidos.")]
        public string InputFolder { get; init; } = string.Empty;

        [CommandOption("-o|--output <OUTPUT_FOLDER>")]
        [Description("Pasta de destino para os arquivos Word (.docx) gerados (padrão: ./out).")]
        public string? OutputFolder { get; init; }

        [CommandOption("--ocr")]
        [Description("Ativa o fallback de OCR (Tesseract) para páginas escaneadas (padrão: ativo).")]
        [DefaultValue(true)]
        public bool EnableOcr { get; init; } = true;

        [CommandOption("--dpi <DPI>")]
        [Description("Resolução em DPI para recorte e rasterização de diagramas (padrão: 300).")]
        [DefaultValue(300)]
        public int Dpi { get; init; } = 300;

        [CommandOption("--verbose")]
        [Description("Exibe logs detalhados durante o processamento em lote (padrão: ativo).")]
        [DefaultValue(true)]
        public bool Verbose { get; init; } = true;
    }

    public override ValidationResult Validate(CommandContext context, Settings settings)
    {
        string normalizedFolder = ConvertCommand.NormalizeInputPath(settings.InputFolder);
        if (string.IsNullOrWhiteSpace(normalizedFolder) || !Directory.Exists(normalizedFolder))
            return ValidationResult.Error($"Diretório de entrada não encontrado: {settings.InputFolder}");

        if (settings.Dpi is < 72 or > 1200)
            return ValidationResult.Error("O valor de --dpi deve estar entre 72 e 1200.");

        return ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        string inputDir = Path.GetFullPath(ConvertCommand.NormalizeInputPath(settings.InputFolder));
        string outputDir = !string.IsNullOrWhiteSpace(settings.OutputFolder)
            ? Path.GetFullPath(ConvertCommand.NormalizeRawPathString(settings.OutputFolder))
            : ConvertCommand.ResolveDefaultOutDirectory();

        Directory.CreateDirectory(outputDir);

        var pdfFiles = Directory.GetFiles(inputDir, "*.pdf", SearchOption.TopDirectoryOnly);
        if (pdfFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Nenhum arquivo .pdf encontrado em:[/] {Markup.Escape(inputDir)}");
            return 0;
        }

        ConvertCommand.RenderBanner();

        var options = new ConversionOptions
        {
            Dpi = settings.Dpi,
            EnableOcr = settings.EnableOcr,
            Verbose = settings.Verbose
        };

        var converter = new PdfToWordConverter(options);

        int totalPages = 0;
        int totalDiagrams = 0;
        int totalTables = 0;

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var batchTask = ctx.AddTask($"[bold green]Lote ({pdfFiles.Length} arquivos)[/]", maxValue: pdfFiles.Length);

                foreach (string pdfFile in pdfFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(pdfFile);
                    string outDocx = Path.Combine(outputDir, $"{fileName}.docx");

                    var fileTask = ctx.AddTask($"[cyan]{Markup.Escape(Path.GetFileName(pdfFile))}[/]", maxValue: 1);

                    var stats = converter.ConvertFile(pdfFile, outDocx, s =>
                    {
                        if (s.TotalPages > 0)
                        {
                            fileTask.MaxValue = s.TotalPages;
                            fileTask.Value = s.ProcessedPages;
                            fileTask.Description =
                                $"[cyan]{Markup.Escape(Path.GetFileName(pdfFile))}[/] " +
                                $"(Págs: {s.ProcessedPages}/{s.TotalPages} | BBox: {s.DiagramsRasterized} | Tabelas: {s.TablesConstructed})";
                        }
                    });

                    totalPages += stats.ProcessedPages;
                    totalDiagrams += stats.DiagramsRasterized;
                    totalTables += stats.TablesConstructed;
                    batchTask.Increment(1);
                }
            });

        AnsiConsole.MarkupLine(
            $"\n[bold green]✔ Lote finalizado![/] Arquivos: [bold]{pdfFiles.Length}[/] | " +
            $"Páginas: [cyan]{totalPages}[/] | Diagramas BBox: [yellow]{totalDiagrams}[/] | Tabelas: [green]{totalTables}[/]\n" +
            $"[bold green]Pasta de saída:[/] [underline]{Markup.Escape(outputDir)}[/]");

        return 0;
    }
}
