using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public sealed record FeedbackRecord(string FeedbackId, string Comment);

public interface ISentimentAnalyzer
{
    Task<SentimentAnalysisDto> AnalyzeAsync(IReadOnlyList<FeedbackRecord> feedback, CancellationToken cancellationToken);
}

public interface IPdfTextExtractor
{
    Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken);
}

public interface IFeedbackParser
{
    IReadOnlyList<FeedbackRecord> Parse(string text);
}

public interface IJobProcessor
{
    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken);
}

public interface IFileStorageService
{
    Task<(string OriginalFileName, string StoredPath)> SaveUploadAsync(IFormFile file, Guid jobId, CancellationToken cancellationToken);
}
