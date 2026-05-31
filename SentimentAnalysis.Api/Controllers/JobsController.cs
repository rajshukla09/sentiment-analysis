using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentimentAnalysis.Api.Data;
using SentimentAnalysis.Api.Models;
using SentimentAnalysis.Api.Services;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Controllers;

[ApiController]
[Route("jobs")]
[Produces("application/json")]
public sealed class JobsController(AppDbContext db, IFileStorageService storage) : ControllerBase
{
    private const long MaxUploadBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Uploads a PDF and queues a background sentiment analysis job.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(CreateJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateJobResponse>> CreateJob(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiErrorResponse("A non-empty PDF file is required."));
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ApiErrorResponse("Only PDF uploads are accepted."));
        }

        if (file.Length > MaxUploadBytes)
        {
            return BadRequest(new ApiErrorResponse("PDF uploads must be 2 MB or smaller."));
        }

        var jobId = Guid.NewGuid();
        var saved = await storage.SaveUploadAsync(file, jobId, cancellationToken);
        var job = new Job
        {
            Id = jobId,
            FileName = saved.OriginalFileName,
            StoredFilePath = saved.StoredPath,
            Status = JobStatuses.Queued,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        return AcceptedAtAction(nameof(GetJob), new { id = jobId }, new CreateJobResponse(jobId, job.Status));
    }

    /// <summary>
    /// Gets the current status and timestamps for a queued analysis job.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobStatusResponse>> GetJob(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiErrorResponse("Job not found."));
        }

        return Ok(new JobStatusResponse(job.Id, job.Status, job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.ErrorMessage));
    }

    /// <summary>
    /// Gets the readable structured result for a completed analysis job.
    /// </summary>
    [HttpGet("{id:guid}/result")]
    [ProducesResponseType(typeof(JobResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JobResultResponse>> GetResult(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.Include(x => x.Result).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (job is null)
        {
            return NotFound(new ApiErrorResponse("Job not found."));
        }

        if (job.Status == JobStatuses.Failed)
        {
            return BadRequest(new ApiErrorResponse("Job failed.", job.ErrorMessage));
        }

        if (job.Status != JobStatuses.Completed || job.Result is null)
        {
            Response.Headers.Location = Url.ActionLink(nameof(GetJob), values: new { id }) ?? $"/jobs/{id}";
            return StatusCode(StatusCodes.Status202Accepted, new ApiErrorResponse("Result is not available until the job is completed."));
        }

        var themes = JsonSerializer.Deserialize<List<ThemeDto>>(job.Result.ThemesJson, JsonOptions) ?? [];
        var actions = JsonSerializer.Deserialize<List<RecommendedActionDto>>(job.Result.RecommendedActionsJson, JsonOptions) ?? [];
        return Ok(new JobResultResponse(
            job.Id,
            job.Result.OverallSummary,
            job.Result.OverallSentiment,
            themes,
            actions,
            job.Result.UncertaintyNote,
            job.Result.CreatedAtUtc));
    }
}
