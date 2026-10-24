using System.ComponentModel;
using System.Text;
using PdfToWord.Cli.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PdfToWord.Cli.Cli;

public sealed class ConvertCommand : Command<ConvertCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[INPUT_PDF]")]
        [Description("Caminho do arquivo PDF de entrada (opcional: se omitido, permite arrastar o PDF no terminal).")]
        public string? InputPath { get; init; }

        [CommandOption("-o|--output <OUTPUT_DOCX>")]
        [Description("Caminho do arquivo Word (.docx) de saída (padrão: ./out/<nome_do_pdf>.docx).")]
        public string? OutputPath { get; init; }

        [CommandOption("--ocr")]
        [Description("Ativa o fallback de OCR (Tesseract) para páginas escaneadas (padrão: ativo).")]
        [DefaultValue(true)]
        public bool EnableOcr { get; init; } = true;

        [CommandOption("--dpi <DPI>")]
        [Description("Resolução em DPI para recorte e rasterização de diagramas via PDFium (padrão: 300).")]
        [DefaultValue(300)]
        public int Dpi { get; init; } = 300;

        [CommandOption("--verbose")]
        [Description("Exibe logs detalhados de Bounding Boxes, vetores e tabelas detectadas (padrão: ativo).")]
        [DefaultValue(true)]
        public bool Verbose { get; init; } = true;
    }

    public override ValidationResult Validate(CommandContext context, Settings settings)
    {
        if (settings.Dpi is < 72 or > 1200)
            return ValidationResult.Error("O valor de --dpi deve estar entre 72 e 1200.");

        if (!string.IsNullOrWhiteSpace(settings.InputPath))
        {
            string normalized = NormalizeInputPath(settings.InputPath);
            if (!File.Exists(normalized))
                return ValidationResult.Error($"Arquivo não encontrado: {normalized}");
        }

        return ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        RenderBanner();

        bool isInteractiveMode = string.IsNullOrWhiteSpace(settings.InputPath);
        string inputFullPath;

        if (isInteractiveMode)
        {
            var welcomePanel = new Panel(
                new Markup(
                    "[bold green]Modo de Alta Fidelidade Automático Ativado[/]\n" +
                    "[grey]Parâmetros otimizados aplicados automaticamente:[/] [yellow]DPI = 300[/] | [yellow]OCR Fallback = Ativo[/] | [yellow]Diagnóstico Verbose = Ativo[/] | [yellow]Saída = ./out/[/]\n\n" +
                    "[bold white]👉 Arraste e solte o arquivo PDF nesta janela do terminal[/] (ou digite/cole o caminho) e pressione [bold cyan][[Enter]][/]:"))
            {
                Header = new PanelHeader("[bold springgreen2] Captura Interativa de Documento PDF [/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.SpringGreen2)
            };
            AnsiConsole.Write(welcomePanel);

            string? capturedPath = PromptForPdfPath();
            if (string.IsNullOrWhiteSpace(capturedPath))
            {
                AnsiConsole.MarkupLine("[bold red]✖ Nenhum arquivo PDF válido foi informado. Operação cancelada.[/]");
                return -1;
            }

            inputFullPath = Path.GetFullPath(capturedPath);
            AnsiConsole.WriteLine();
        }
        else
        {
            inputFullPath = Path.GetFullPath(NormalizeInputPath(settings.InputPath!));
        }

        string outputFullPath = ResolveOutputDocxPath(inputFullPath, settings.OutputPath);

        var infoPanel = new Panel(
            new Markup(
                $"[bold]Entrada PDF:[/]  [cyan]{Markup.Escape(inputFullPath)}[/]\n" +
                $"[bold]Saída DOCX:[/]   [green]{Markup.Escape(outputFullPath)}[/]\n" +
                $"[bold]Configuração:[/] DPI = [yellow]{settings.Dpi}[/] | OCR Fallback = [yellow]{(settings.EnableOcr ? "Ativo" : "Inativo")}[/] | Verbose = [yellow]{settings.Verbose}[/]"))
        {
            Header = new PanelHeader("[bold white] Pipeline de Conversão PDF -> Word (.docx) [/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.SteelBlue1)
        };
        AnsiConsole.Write(infoPanel);
        AnsiConsole.WriteLine();

        var options = new ConversionOptions
        {
            Dpi = settings.Dpi,
            EnableOcr = settings.EnableOcr,
            Verbose = settings.Verbose
        };

        var converter = new PdfToWordConverter(options);
        ConversionProgressStats? finalStats = null;

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
                var pageTask = ctx.AddTask("[bold cyan]Páginas processadas[/]", maxValue: 1);

                finalStats = converter.ConvertFile(inputFullPath, outputFullPath, stats =>
                {
                    if (stats.TotalPages > 0)
                    {
                        pageTask.MaxValue = stats.TotalPages;
                        pageTask.Value = stats.ProcessedPages;
                        pageTask.Description =
                            $"[bold cyan]Páginas:[/] {stats.ProcessedPages}/{stats.TotalPages} | " +
                            $"[bold yellow]Diagramas (BBox):[/] {stats.DiagramsRasterized} | " +
                            $"[bold green]Tabelas:[/] {stats.TablesConstructed}";
                    }
                });
            });

        if (finalStats != null)
        {
            AnsiConsole.WriteLine();
            RenderSummaryTable(finalStats, outputFullPath);
        }

        return 0;
    }

    private static string? PromptForPdfPath()
    {
        while (true)
        {
            AnsiConsole.Markup("[bold cyan]📄 Caminho do PDF > [/]");
            string? rawLine = Console.ReadLine();

            if (rawLine == null)
            {
                // Fim da entrada (EOF em stdin redirecionado)
                return null;
            }

            string cleaned = NormalizeInputPath(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                if (Console.IsInputRedirected)
                    return null;

                AnsiConsole.MarkupLine("[yellow]⚠ Por favor, arraste um arquivo PDF para o terminal ou informe o caminho.[/]");
                continue;
            }

            if (!File.Exists(cleaned))
            {
                AnsiConsole.MarkupLine($"[red]✖ Arquivo não encontrado:[/] {Markup.Escape(cleaned)}");
                if (Console.IsInputRedirected)
                    return null;

                continue;
            }

            if (!cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]✖ O arquivo informado não é um documento .pdf:[/] {Markup.Escape(cleaned)}");
                if (Console.IsInputRedirected)
                    return null;

                continue;
            }

            return cleaned;
        }
    }

    /// <summary>
    /// Higieniza caminhos capturados via drag-and-drop no terminal (remove aspas simples/duplas,
    /// operador '&amp;' do PowerShell, prefixos file:/// e resolve diferenças de codificação de acentos).
    /// </summary>
    internal static string NormalizeInputPath(string? rawInput)
    {
        string cleaned = NormalizeRawPathString(rawInput);
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        if (File.Exists(cleaned) || Directory.Exists(cleaned))
            return cleaned;

        // Caso o terminal tenha escapado espaços/parênteses com crase (backtick do PowerShell)
        if (cleaned.Contains('`'))
        {
            string unescaped = cleaned.Replace("`", string.Empty, StringComparison.Ordinal);
            if (File.Exists(unescaped) || Directory.Exists(unescaped))
                return unescaped;
        }

        // Fallback para diferenças de codepage/encoding em nomes de arquivos acentuados no Windows Terminal / PowerShell
        try
        {
            string? dir = Path.GetDirectoryName(cleaned);
            string fileName = Path.GetFileName(cleaned);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !string.IsNullOrEmpty(fileName))
            {
                string targetSkeleton = ExtractAsciiSkeleton(fileName);
                if (targetSkeleton.Length >= 4)
                {
                    var matches = Directory.GetFiles(dir, "*.pdf")
                        .Where(f => string.Equals(ExtractAsciiSkeleton(Path.GetFileName(f)), targetSkeleton, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matches.Count == 1)
                        return matches[0];
                }
            }
        }
        catch
        {
            // Ignorar erros de acesso ao diretório durante tentativa de fallback
        }

        return cleaned;
    }

    internal static string NormalizeRawPathString(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return string.Empty;

        string s = rawInput.Trim();

        // Remover operador de chamada '&' adicionado pelo PowerShell ao arrastar arquivos com espaços
        if (s.StartsWith('&'))
        {
            s = s[1..].TrimStart();
        }

        // Remover pares de aspas duplas ou simples envolventes
        while (s.Length >= 2 &&
               ((s.StartsWith('"') && s.EndsWith('"')) ||
                (s.StartsWith('\'') && s.EndsWith('\''))))
        {
            s = s[1..^1].Trim();
        }

        // Remover aspas residuais no início ou fim
        s = s.Trim('"', '\'', ' ', '\t', '\r', '\n');

        // Converter URI file:/// caso arrastado de navegador ou webview
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(s, UriKind.Absolute, out Uri? fileUri) &&
            fileUri.IsFile)
        {
            s = fileUri.LocalPath;
        }

        return s;
    }

    private static string ExtractAsciiSkeleton(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.'))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve o diretório padrão './out' na raiz do projeto (não versionado pelo Git).
    /// </summary>
    internal static string ResolveDefaultOutDirectory()
    {
        string? projectRoot = FindProjectRoot(Directory.GetCurrentDirectory())
                              ?? FindProjectRoot(AppContext.BaseDirectory);

        string baseDir = projectRoot ?? Directory.GetCurrentDirectory();
        string outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(outDir);
        return outDir;
    }

    /// <summary>
    /// Resolve o caminho completo do arquivo .docx de saída, utilizando a pasta './out' por padrão.
    /// </summary>
    internal static string ResolveOutputDocxPath(string inputFullPath, string? customOutputPath)
    {
        string defaultOutDir = ResolveDefaultOutDirectory();
        string pdfFileNameWithoutExt = Path.GetFileNameWithoutExtension(inputFullPath);

        if (string.IsNullOrWhiteSpace(customOutputPath))
        {
            return Path.Combine(defaultOutDir, $"{pdfFileNameWithoutExt}.docx");
        }

        string cleanedOutput = NormalizeRawPathString(customOutputPath);

        if (Directory.Exists(cleanedOutput) ||
            cleanedOutput.EndsWith(Path.DirectorySeparatorChar) ||
            cleanedOutput.EndsWith(Path.AltDirectorySeparatorChar))
        {
            string targetDir = Path.GetFullPath(cleanedOutput);
            Directory.CreateDirectory(targetDir);
            return Path.Combine(targetDir, $"{pdfFileNameWithoutExt}.docx");
        }

        string? dirPart = Path.GetDirectoryName(cleanedOutput);
        if (string.IsNullOrEmpty(dirPart))
        {
            string fileName = Path.HasExtension(cleanedOutput) ? cleanedOutput : $"{cleanedOutput}.docx";
            return Path.Combine(defaultOutDir, fileName);
        }

        return Path.GetFullPath(cleanedOutput);
    }

    private static string? FindProjectRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "PdfToWord.csproj")) ||
                File.Exists(Path.Combine(dir.FullName, "PdfToWord.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    internal static void RenderBanner()
    {
        AnsiConsole.Write(
            new FigletText("pdf2word")
                .Color(Color.SpringGreen2));
    }

    internal static void RenderSummaryTable(ConversionProgressStats stats, string outputDocxPath)
    {
        var fileInfo = new FileInfo(outputDocxPath);
        double sizeKb = fileInfo.Exists ? fileInfo.Length / 1024.0 : 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold green]Resumo da Conversão Concluída[/]")
            .AddColumn(new TableColumn("[bold]Métrica[/]"))
            .AddColumn(new TableColumn("[bold]Resultado[/]").RightAligned());

        table.AddRow("Páginas Processadas", $"[cyan]{stats.ProcessedPages} / {stats.TotalPages}[/]");
        table.AddRow("Diagramas Detectados e Rasterizados (Bounding Box)", $"[yellow]{stats.DiagramsRasterized}[/]");
        table.AddRow("Tabelas Ortogonais Reconstruídas", $"[green]{stats.TablesConstructed}[/]");
        table.AddRow("Caixas de Destaque (Callouts Editáveis)", $"[blue]{stats.CalloutsConstructed}[/]");
        table.AddRow("Páginas com OCR Fallback", $"{stats.OcrPagesProcessed}");
        table.AddRow("Tamanho do Documento .docx Gerado", $"[bold]{sizeKb:F1} KB[/]");
        table.AddRow("Tempo Total de Execução", $"[bold]{stats.Elapsed.TotalSeconds:F2} s[/]");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[bold green]✔ Arquivo gerado com sucesso:[/] [underline]{Markup.Escape(outputDocxPath)}[/]");
    }
}
