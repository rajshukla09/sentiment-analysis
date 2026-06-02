using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Client.Pages;

public partial class Index : IDisposable
{

    private const int MaxFilesPerBatch = 10;
    private const int MaxUploadMb = 2;
    private const long MaxUploadBytes = MaxUploadMb * 1024 * 1024;

    private readonly List<IBrowserFile> selectedFiles = new();
    private readonly List<UploadRow> uploads = new();
    private readonly RefreshJobsForm refreshJobsForm = new();
    private List<JobSummaryResponse> recentJobs = new();
    private bool isUploading;
    private bool isLoadingJobs;
    private string? error;
    private string? jobListError;
    private PeriodicTimer? timer;
    private readonly CancellationTokenSource cts = new();

    protected override async Task OnInitializedAsync()
    {
        await LoadRecentJobsAsync();
        timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        _ = PollAsync();
    }

    private void OnFilesSelected(InputFileChangeEventArgs args)
    {
        error = null;
        selectedFiles.Clear();

        IReadOnlyList<IBrowserFile> files;
        try
        {
            files = args.GetMultipleFiles(MaxFilesPerBatch);
        }
        catch (InvalidOperationException)
        {
            error = $"Select {MaxFilesPerBatch} PDFs or fewer per batch.";
            return;
        }

        foreach (var file in files)
        {
            if (file.Size > MaxUploadBytes)
            {
                error = $"{file.Name} is too large. PDF uploads must be {MaxUploadMb} MB or smaller.";
                selectedFiles.Clear();
                return;
            }

            selectedFiles.Add(file);
        }
    }

    private void ViewStatus(Guid jobId) => Navigation.NavigateTo($"job/{jobId}");

    private async Task UploadAsync()
    {
        if (!selectedFiles.Any()) return;
        isUploading = true;
        error = null;

        var batchRows = selectedFiles
            .Select(file => new UploadRow(file.Name, "uploading", DateTime.UtcNow))
            .ToList();
        uploads.InsertRange(0, batchRows);

        try
        {
            using var form = new MultipartFormDataContent();
            var contents = new List<StreamContent>();
            try
            {
                foreach (var file in selectedFiles)
                {
                    var stream = file.OpenReadStream(MaxUploadBytes);
                    var content = new StreamContent(stream);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                    form.Add(content, "files", file.Name);
                    contents.Add(content);
                }

                var response = await Http.PostAsync("jobs/batch", form, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var apiError = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(cancellationToken: cts.Token);
                    MarkRowsFailed(batchRows, apiError?.Error ?? $"Upload failed with HTTP {(int)response.StatusCode}.");
                    return;
                }

                var created = await response.Content.ReadFromJsonAsync<CreateJobsResponse>(cancellationToken: cts.Token);
                if (created is null || created.Jobs.Count != batchRows.Count)
                {
                    MarkRowsFailed(batchRows, "The API returned an unexpected upload response.");
                    return;
                }

                for (var i = 0; i < created.Jobs.Count; i++)
                {
                    batchRows[i].JobId = created.Jobs[i].JobId;
                    batchRows[i].Status = created.Jobs[i].Status;
                    batchRows[i].UpdatedAt = DateTime.UtcNow;
                }
            }
            finally
            {
                foreach (var content in contents)
                {
                    content.Dispose();
                }
            }

            await LoadRecentJobsAsync();
        }
        catch (Exception ex)
        {
            MarkRowsFailed(batchRows, ex.Message);
        }
        finally
        {
            selectedFiles.Clear();
            isUploading = false;
        }
    }

    private static void MarkRowsFailed(IEnumerable<UploadRow> rows, string message)
    {
        foreach (var row in rows)
        {
            row.Status = "failed";
            row.ErrorMessage = message;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task PollAsync()
    {
        if (timer is null) return;
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                await LoadRecentJobsAsync();
                await RefreshUploadRowsAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // Component was disposed.
        }
    }

    private async Task LoadRecentJobsAsync()
    {
        isLoadingJobs = true;
        jobListError = null;
        try
        {
            recentJobs = await Http.GetFromJsonAsync<List<JobSummaryResponse>>("jobs", cts.Token) ?? new List<JobSummaryResponse>();
        }
        catch (Exception ex)
        {
            jobListError = ex.Message;
        }
        finally
        {
            isLoadingJobs = false;
        }
    }

    private async Task RefreshUploadRowsAsync()
    {
        foreach (var upload in uploads.Where(x => x.JobId is not null && (x.Status is "queued" or "running")).ToArray())
        {
            try
            {
                var status = await Http.GetFromJsonAsync<JobStatusResponse>($"jobs/{upload.JobId}", cts.Token);
                if (status is null) continue;
                upload.Status = status.Status;
                upload.ErrorMessage = status.ErrorMessage;
                upload.UpdatedAt = status.CompletedAtUtc ?? status.StartedAtUtc ?? DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                upload.ErrorMessage = ex.Message;
            }
        }
    }

    private static string FriendlyStatus(string status) => status switch
    {
        "uploading" => "Uploading",
        "queued" => "Queued",
        "running" => "Processing",
        "completed" => "Processed",
        "failed" => "Failed",
        _ => status
    };

    private static string FormatBytes(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:N1} KB"
        : $"{bytes / (1024d * 1024d):N1} MB";

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
        timer?.Dispose();
    }

    private sealed class RefreshJobsForm
    {
    }

    private sealed class UploadRow
    {
        public UploadRow(string fileName, string status, DateTime updatedAt)
        {
            FileName = fileName;
            Status = status;
            UpdatedAt = updatedAt;
        }

        public string FileName { get; }
        public Guid? JobId { get; set; }
        public string Status { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
