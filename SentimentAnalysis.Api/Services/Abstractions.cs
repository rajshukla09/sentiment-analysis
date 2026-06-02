using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public interface ISentimentAnalyzer
{
    Task<SentimentAnalysisDto> AnalyzeAsync(IReadOnlyList<FeedbackItem> feedbackItems, CancellationToken cancellationToken);
}

public interface IPdfTextExtractor
{
    Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken);
}

public interface IFeedbackParser
{
    IReadOnlyList<FeedbackItem> Parse(string extractedText);
}

public interface IJobProcessor
{
    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken);
}

public interface IFileStorageService
{
    Task<(string OriginalFileName, string StoredPath)> SaveUploadAsync(IFormFile file, Guid jobId, CancellationToken cancellationToken);
}
