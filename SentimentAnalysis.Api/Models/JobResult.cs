namespace SentimentAnalysis.Api.Models;

public sealed class JobResult
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string OverallSummary { get; set; } = string.Empty;
    public string OverallSentiment { get; set; } = string.Empty;
    public string ThemesJson { get; set; } = "[]";
    public string RecommendedActionsJson { get; set; } = "[]";
    public string? UncertaintyNote { get; set; }
    public string RawStructuredJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public Job Job { get; set; } = default!;
}
