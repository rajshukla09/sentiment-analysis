using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace SentimentAnalysis.Api.Services;

public sealed class RealPdfTextExtractor : IPdfTextExtractor
{
    public Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder();

        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine(ExtractPageText(page));
        }

        return Task.FromResult(builder.ToString());
    }

    private static string ExtractPageText(Page page)
    {
        var text = ContentOrderTextExtractor.GetText(page, addDoubleNewline: true);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (!string.IsNullOrWhiteSpace(page.Text))
        {
            return page.Text;
        }

        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).Select(word => word.Text);
        return string.Join(' ', words);
    }
}
