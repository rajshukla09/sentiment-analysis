using System.Text.RegularExpressions;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public sealed partial class FeedbackParser : IFeedbackParser
{
    private const int MaxFeedbackItems = 50;

    public IReadOnlyList<FeedbackItem> Parse(string extractedText)
    {
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            throw new InvalidOperationException("No readable feedback text found in the PDF.");
        }

        var normalized = extractedText.Replace("\r\n", "\n").Replace('\r', '\n');
        var matches = FeedbackBlockRegex().Matches(normalized);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException("No readable feedback text found in the PDF.");
        }

        var items = new List<FeedbackItem>();
        foreach (Match match in matches)
        {
            var feedbackId = match.Groups["id"].Value.Trim();
            var comment = match.Groups["comment"].Value.Trim();

            if (string.IsNullOrWhiteSpace(feedbackId))
            {
                throw new InvalidOperationException("A feedback item is missing its feedback ID.");
            }

            if (string.IsNullOrWhiteSpace(comment))
            {
                throw new InvalidOperationException($"Feedback item '{feedbackId}' is missing its comment text.");
            }

            items.Add(new FeedbackItem(feedbackId, comment));
        }

        if (items.Count > MaxFeedbackItems)
        {
            throw new InvalidOperationException("The PDF contains more than 50 feedback items. Please upload a smaller batch.");
        }

        return items;
    }

    [GeneratedRegex(@"Feedback\s*ID\s*:\s*(?<id>.*?)\s*Comment\s*:\s*(?<comment>.*?)(?=\s*Feedback\s*ID\s*:|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex FeedbackBlockRegex();
}
