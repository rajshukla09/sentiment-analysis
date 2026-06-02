namespace SentimentAnalysis.Shared;

public sealed record FeedbackItem(string FeedbackId, string Comment);

public sealed record CreateJobResponse(Guid JobId, string Status);

public sealed record CreateJobsResponse(IReadOnlyList<CreateJobItemResponse> Jobs);

public sealed record CreateJobItemResponse(Guid JobId, string FileName, string Status);

public sealed record JobSummaryResponse(
    Guid JobId,
    string FileName,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage);

public sealed record JobStatusResponse(
    Guid JobId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? ErrorMessage);

public sealed record JobResultResponse(
    Guid JobId,
    string OverallSummary,
    string OverallSentiment,
    IReadOnlyList<ThemeDto> TopThemes,
    IReadOnlyList<RecommendedActionDto> RecommendedActions,
    string? UncertaintyNote,
    DateTime CreatedAtUtc);

public sealed record SentimentAnalysisDto(
    string OverallSummary,
    string OverallSentiment,
    IReadOnlyList<ThemeDto> TopThemes,
    IReadOnlyList<RecommendedActionDto> RecommendedActions,
    string? UncertaintyNote);

public sealed record ThemeDto(
    string Theme,
    string Sentiment,
    string Summary,
    IReadOnlyList<string> FeedbackIds);

public sealed record RecommendedActionDto(string Action);

public sealed record ApiErrorResponse(string Error, string? Detail = null);
