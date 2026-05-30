namespace SentimentAnalysis.Api.Models;

public static class JobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static readonly HashSet<string> Valid = [Queued, Running, Completed, Failed];
}

public sealed class Job
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public string Status { get; set; } = JobStatuses.Queued;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public JobResult? Result { get; set; }
}
