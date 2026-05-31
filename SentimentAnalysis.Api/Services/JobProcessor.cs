using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentimentAnalysis.Api.Data;
using SentimentAnalysis.Api.Models;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public sealed class JobProcessor(
    AppDbContext db,
    IPdfTextExtractor pdfTextExtractor,
    IFeedbackParser feedbackParser,
    ISentimentAnalyzer sentimentAnalyzer,
    ILogger<JobProcessor> logger) : IJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken)
    {
        var queuedJob = await db.Jobs
            .AsNoTracking()
            .Where(x => x.Status == JobStatuses.Queued)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (queuedJob is null)
        {
            return false;
        }

        var startedAt = DateTime.UtcNow;
        var claimed = await db.Jobs
            .Where(x => x.Id == queuedJob.Id && x.Status == JobStatuses.Queued)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, JobStatuses.Running)
                .SetProperty(x => x.StartedAtUtc, startedAt), cancellationToken);

        if (claimed == 0)
        {
            return true;
        }

        try
        {
            var text = await pdfTextExtractor.ExtractTextAsync(queuedJob.StoredFilePath, cancellationToken);
            var feedback = feedbackParser.Parse(text);
            if (feedback.Count == 0)
            {
                throw new InvalidOperationException("No readable feedback text found in the PDF.");
            }

            var analysis = await sentimentAnalyzer.AnalyzeAsync(feedback, cancellationToken);
            db.JobResults.Add(BuildResult(queuedJob.Id, analysis));
            await db.SaveChangesAsync(cancellationToken);

            var completedAt = DateTime.UtcNow;
            await db.Jobs
                .Where(x => x.Id == queuedJob.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, JobStatuses.Completed)
                    .SetProperty(x => x.CompletedAtUtc, completedAt)
                    .SetProperty(x => x.ErrorMessage, (string?)null), cancellationToken);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job {JobId} failed", queuedJob.Id);
            db.ChangeTracker.Clear();

            var errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            var completedAt = DateTime.UtcNow;
            await db.Jobs
                .Where(x => x.Id == queuedJob.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, JobStatuses.Failed)
                    .SetProperty(x => x.CompletedAtUtc, completedAt)
                    .SetProperty(x => x.ErrorMessage, errorMessage), CancellationToken.None);

            return true;
        }
    }

    private static JobResult BuildResult(Guid jobId, SentimentAnalysisDto analysis)
    {
        var themesJson = JsonSerializer.Serialize(analysis.TopThemes, JsonOptions);
        var actionsJson = JsonSerializer.Serialize(analysis.RecommendedActions, JsonOptions);
        var rawJson = JsonSerializer.Serialize(new
        {
            analysis.OverallSummary,
            analysis.OverallSentiment,
            TopThemes = analysis.TopThemes,
            RecommendedActions = analysis.RecommendedActions,
            analysis.UncertaintyNote
        }, JsonOptions);

        return new JobResult
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            OverallSummary = analysis.OverallSummary,
            OverallSentiment = analysis.OverallSentiment,
            ThemesJson = themesJson,
            RecommendedActionsJson = actionsJson,
            UncertaintyNote = analysis.UncertaintyNote,
            RawStructuredJson = rawJson,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
