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
        var job = await db.Jobs
            .Where(x => x.Status == JobStatuses.Queued)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null)
        {
            return false;
        }

        job.Status = JobStatuses.Running;
        job.StartedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var text = await pdfTextExtractor.ExtractTextAsync(job.StoredFilePath, cancellationToken);
            var feedback = feedbackParser.Parse(text);
            if (feedback.Count == 0)
            {
                throw new InvalidOperationException("No readable feedback text found in the PDF.");
            }

            var analysis = await sentimentAnalyzer.AnalyzeAsync(feedback, cancellationToken);
            job.Result = BuildResult(job.Id, analysis);
            job.Status = JobStatuses.Completed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = null;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job {JobId} failed", job.Id);
            job.Status = JobStatuses.Failed;
            job.CompletedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
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
