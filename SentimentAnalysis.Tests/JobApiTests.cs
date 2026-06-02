using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SentimentAnalysis.Api.Data;
using SentimentAnalysis.Api.Models;
using SentimentAnalysis.Api.Services;
using SentimentAnalysis.Shared;
using Xunit;

namespace SentimentAnalysis.Tests;

public sealed class JobApiTests
{
    [Fact]
    public void ParserExtractsValidFeedbackIdsAndComments()
    {
        var items = new FeedbackParser().Parse("""
            Feedback ID: fb_001
            Comment: Delivery was fast but packaging was damaged.

            Feedback ID: fb_002
            Comment: Support answered quickly.
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal("fb_001", items[0].FeedbackId);
        Assert.Equal("Delivery was fast but packaging was damaged.", items[0].Comment);
        Assert.Equal("fb_002", items[1].FeedbackId);
        Assert.Equal("Support answered quickly.", items[1].Comment);
    }

    [Fact]
    public void ParserSupportsMultilineComments()
    {
        var items = new FeedbackParser().Parse("""
            Feedback ID: fb_multi
            Comment: First line of the comment.
            Second line with more detail.
            Third line should stay with the same item.

            Feedback ID: fb_next
            Comment: Next item.
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal("fb_multi", items[0].FeedbackId);
        Assert.Contains("Second line with more detail.", items[0].Comment);
        Assert.Contains("Third line should stay with the same item.", items[0].Comment);
        Assert.DoesNotContain("Feedback ID: fb_next", items[0].Comment);
    }

    [Fact]
    public void ParserSupportsFlattenedPdfExtractorText()
    {
        var items = new FeedbackParser().Parse("Sample Consumer Feedback Responses Feedback ID: fb_001 Comment: The delivery was fast and arrived earlier than expected. Feedback ID: fb_002 Comment: Customer support responded quickly, but my refund took longer than expected.");

        Assert.Equal(2, items.Count);
        Assert.Equal("fb_001", items[0].FeedbackId);
        Assert.Equal("The delivery was fast and arrived earlier than expected.", items[0].Comment);
        Assert.Equal("fb_002", items[1].FeedbackId);
        Assert.Equal("Customer support responded quickly, but my refund took longer than expected.", items[1].Comment);
    }

    [Fact]
    public void ParserSupportsPdfExtractorTextWithMissingSeparatingWhitespace()
    {
        var items = new FeedbackParser().Parse("FeedbackID:fb_001Comment:The delivery was fast.FeedbackID:fb_002Comment:Support was quick.");

        Assert.Equal(2, items.Count);
        Assert.Equal("fb_001", items[0].FeedbackId);
        Assert.Equal("The delivery was fast.", items[0].Comment);
        Assert.Equal("fb_002", items[1].FeedbackId);
        Assert.Equal("Support was quick.", items[1].Comment);
    }

    [Fact]
    public void ParserRejectsEmptyFeedbackText()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new FeedbackParser().Parse("   "));

        Assert.Equal("No readable feedback text found in the PDF.", error.Message);
    }

    [Fact]
    public void ParserRejectsMoreThanFiftyFeedbackRecords()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 51).Select(i => $"Feedback ID: fb_{i:000}\nComment: Comment {i}"));

        var error = Assert.Throws<InvalidOperationException>(() => new FeedbackParser().Parse(text));

        Assert.Equal("The PDF contains more than 50 feedback items. Please upload a smaller batch.", error.Message);
    }

    [Fact]
    public async Task ValidPdfUploadCreatesQueuedJob()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/jobs", PdfForm(SampleFeedback()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateJobResponse>();
        Assert.NotEqual(Guid.Empty, created!.JobId);

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}");
        Assert.Equal(JobStatuses.Queued, status!.Status);
    }

    [Fact]
    public async Task NonPdfFileIsRejected()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("not pdf"), "file", "feedback.txt");
        var response = await client.PostAsync("/jobs", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FileOverTwoMbIsRejected()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(new byte[(2 * 1024 * 1024) + 1]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", "large.pdf");
        var response = await client.PostAsync("/jobs", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EmptyNonReadablePdfMarksJobAsFailedWithClearError()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, "no feedback here");

        await factory.ProcessAllQueuedJobsAsync();

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}");
        Assert.Equal(JobStatuses.Failed, status!.Status);
        Assert.Equal("No readable feedback text found in the PDF.", status.ErrorMessage);
    }

    [Fact]
    public async Task MockLlmResultWithValidFeedbackIdsCompletesJob()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, SampleFeedback("fb_life"));

        await factory.ProcessAllQueuedJobsAsync();

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}");
        Assert.Equal(JobStatuses.Completed, status!.Status);
        var result = await client.GetFromJsonAsync<JobResultResponse>($"/jobs/{created.JobId}/result");
        Assert.Equal(created.JobId, result!.JobId);
        Assert.Contains("fb_life", result.TopThemes.Single().FeedbackIds);
    }

    [Fact]
    public async Task WorkerSendsParsedFeedbackItemsToAnalyzerNotPdfBytes()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, """
            Feedback ID: fb_parsed
            Comment: This comment should be sent without the surrounding PDF feedback labels.
            Additional detail stays in the parsed comment.
            """);

        await factory.ProcessAllQueuedJobsAsync();

        Assert.Equal(JobStatuses.Completed, (await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}"))!.Status);
        var sent = Assert.Single(factory.Analyzer.ReceivedFeedbackBatches);
        var item = Assert.Single(sent);
        Assert.Equal("fb_parsed", item.FeedbackId);
        Assert.Contains("Additional detail stays in the parsed comment.", item.Comment);
        Assert.DoesNotContain("Feedback ID:", item.Comment);
        Assert.DoesNotContain("Comment:", item.Comment);
    }

    [Fact]
    public async Task MockLlmResultWithInvalidFeedbackIdsIsHandledSafely()
    {
        await using var factory = new TestApplicationFactory(analyzerReturnsInvalidFeedbackId: true);
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, SampleFeedback("fb_valid"));

        await factory.ProcessAllQueuedJobsAsync();

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}");
        Assert.Equal(JobStatuses.Failed, status!.Status);
        Assert.Contains("not in the parsed input", status.ErrorMessage);
    }

    [Fact]
    public async Task MultipleJobsSubmittedCloseTogetherProduceSeparateResults()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();

        var created = await Task.WhenAll(
            CreateJobAsync(client, SampleFeedback("fb_101")),
            CreateJobAsync(client, SampleFeedback("fb_202")),
            CreateJobAsync(client, SampleFeedback("fb_303")));

        await factory.ProcessAllQueuedJobsAsync();

        foreach (var job in created)
        {
            var result = await client.GetFromJsonAsync<JobResultResponse>($"/jobs/{job.JobId}/result");
            Assert.Equal(job.JobId, result!.JobId);
        }

        var idsByJob = new HashSet<string>((await Task.WhenAll(created.Select(async j =>
        {
            var result = await client.GetFromJsonAsync<JobResultResponse>($"/jobs/{j.JobId}/result");
            return result!.TopThemes.Single().FeedbackIds.Single();
        }))));
        Assert.Equal(3, idsByJob.Count);
    }

    [Fact]
    public async Task MockLlmExceptionMarksJobAsFailed()
    {
        await using var factory = new TestApplicationFactory(analyzerThrows: true);
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, SampleFeedback());

        await factory.ProcessAllQueuedJobsAsync();

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/jobs/{created.JobId}");
        Assert.Equal(JobStatuses.Failed, status!.Status);
        Assert.Contains("Mock analyzer failure", status.ErrorMessage);
    }

    [Fact]
    public async Task SwaggerJsonIsAvailable()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var swagger = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sentiment Analysis API", swagger);
    }

    [Fact]
    public async Task ResultEndpointReturnsAcceptedWhenJobIsNotCompleted()
    {
        await using var factory = new TestApplicationFactory();
        var client = factory.CreateClient();
        var created = await CreateJobAsync(client, SampleFeedback());

        var response = await client.GetAsync($"/jobs/{created.JobId}/result");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<CreateJobResponse> CreateJobAsync(HttpClient client, string text)
    {
        var response = await client.PostAsync("/jobs", PdfForm(text));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateJobResponse>())!;
    }

    private static MultipartFormDataContent PdfForm(string text)
    {
        var form = new MultipartFormDataContent();
        var content = new StringContent(text);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(content, "file", "feedback.pdf");
        return form;
    }

    private static string SampleFeedback(string id = "fb_001") => $"""
        Feedback ID: {id}
        Comment: The checkout experience was fast, but support took too long to answer.
        """;
}

internal sealed class TestApplicationFactory(bool analyzerThrows = false, bool analyzerReturnsInvalidFeedbackId = false) : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"sentiment-tests-{Guid.NewGuid():N}.db");
    private readonly string uploadsPath = Path.Combine(Path.GetTempPath(), $"sentiment-uploads-{Guid.NewGuid():N}");

    public FakeSentimentAnalyzer Analyzer { get; } = new(analyzerThrows, analyzerReturnsInvalidFeedbackId);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={databasePath}");
        builder.UseSetting("Storage:UploadsPath", uploadsPath);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPdfTextExtractor>();
            services.RemoveAll<ISentimentAnalyzer>();
            services.AddScoped<IPdfTextExtractor, TextFilePdfExtractor>();
            services.AddSingleton<ISentimentAnalyzer>(Analyzer);
        });
    }

    public async Task ProcessAllQueuedJobsAsync()
    {
        while (true)
        {
            using var scope = Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();
            if (!await processor.ProcessNextQueuedJobAsync(CancellationToken.None))
            {
                return;
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();

        DeleteFileIfExists(databasePath);
        DeleteFileIfExists($"{databasePath}-shm");
        DeleteFileIfExists($"{databasePath}-wal");
        DeleteFileIfExists($"{databasePath}-journal");
        DeleteDirectoryIfExists(uploadsPath);
    }

    private static void DeleteFileIfExists(string path)
    {
        RetryFileSystemCleanup(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        });
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        RetryFileSystemCleanup(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        });
    }

    private static void RetryFileSystemCleanup(Action cleanup)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                cleanup();
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }
}

internal sealed class TextFilePdfExtractor : IPdfTextExtractor
{
    public Task<string> ExtractTextAsync(string filePath, CancellationToken cancellationToken)
    {
        return File.ReadAllTextAsync(filePath, cancellationToken);
    }
}

internal sealed class FakeSentimentAnalyzer(bool throws, bool returnsInvalidFeedbackId) : ISentimentAnalyzer
{
    public List<IReadOnlyList<FeedbackItem>> ReceivedFeedbackBatches { get; } = [];

    public Task<SentimentAnalysisDto> AnalyzeAsync(IReadOnlyList<FeedbackItem> feedbackItems, CancellationToken cancellationToken)
    {
        ReceivedFeedbackBatches.Add(feedbackItems.Select(x => new FeedbackItem(x.FeedbackId, x.Comment)).ToArray());

        if (throws)
        {
            throw new InvalidOperationException("Mock analyzer failure for test.");
        }

        var ids = returnsInvalidFeedbackId ? new[] { "fb_not_from_input" } : feedbackItems.Select(x => x.FeedbackId).ToArray();
        return Task.FromResult(new SentimentAnalysisDto(
            $"Analyzed {feedbackItems.Count} feedback item(s).",
            "mixed",
            [new ThemeDto("Speed vs support", "mixed", "Customers like speed but support may lag.", ids)],
            [new RecommendedActionDto("Improve support response time.")],
            "Mocked analysis for tests."));
    }
}
