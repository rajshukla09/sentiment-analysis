using System.Text.RegularExpressions;

namespace SentimentAnalysis.Api.Services;

public sealed partial class FeedbackParser : IFeedbackParser
{
    private const int MaxFeedbackItems = 50;

    public IReadOnlyList<FeedbackRecord> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var matches = FeedbackBlockRegex().Matches(normalized);
        var records = new List<FeedbackRecord>();

        foreach (Match match in matches)
        {
            var id = match.Groups["id"].Value.Trim();
            var comment = match.Groups["comment"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(comment))
            {
                records.Add(new FeedbackRecord(id, comment));
            }
        }

        if (records.Count > MaxFeedbackItems)
        {
            throw new InvalidOperationException("The PDF contains more than 50 feedback items. Please upload a smaller batch.");
        }

        return records;
    }

    [GeneratedRegex(@"Feedback\s*ID\s*:\s*(?<id>[^\n]+)\n\s*Comment\s*:\s*(?<comment>.*?)(?=\n\s*Feedback\s*ID\s*:|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex FeedbackBlockRegex();
}
