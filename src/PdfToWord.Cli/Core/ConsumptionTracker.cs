namespace PdfToWord.Cli.Core;

/// <summary>
/// Controla quais elementos primitivos da página (palavras, caminhos vetoriais e imagens)
/// já foram consumidos por módulos anteriores (Rodapé/Cabeçalho, Diagramas BBox, Tabelas ou Callouts),
/// impedindo duplicação no fluxo textual normal.
/// </summary>
public sealed class ConsumptionTracker
{
    private readonly HashSet<int> _consumedWords = new();
    private readonly HashSet<int> _consumedPaths = new();
    private readonly HashSet<int> _consumedImages = new();

    public bool IsWordConsumed(int wordIndex) => _consumedWords.Contains(wordIndex);
    public void MarkWordConsumed(int wordIndex) => _consumedWords.Add(wordIndex);

    public bool IsPathConsumed(int pathIndex) => _consumedPaths.Contains(pathIndex);
    public void MarkPathConsumed(int pathIndex) => _consumedPaths.Add(pathIndex);

    public bool IsImageConsumed(int imageIndex) => _consumedImages.Contains(imageIndex);
    public void MarkImageConsumed(int imageIndex) => _consumedImages.Add(imageIndex);

    public int ConsumedWordCount => _consumedWords.Count;
    public int ConsumedPathCount => _consumedPaths.Count;
}
