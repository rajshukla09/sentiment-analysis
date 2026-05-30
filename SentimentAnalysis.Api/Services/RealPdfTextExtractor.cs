using System.Text;
using UglyToad.PdfPig;

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
            builder.AppendLine(page.Text);
        }

        return Task.FromResult(builder.ToString());
    }
}
