using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SentimentAnalysis.Api.Options;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public sealed class OpenAISentimentAnalyzer(HttpClient httpClient, IOptions<OpenAIOptions> options, ILogger<OpenAISentimentAnalyzer> logger) : ISentimentAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedSentiments = ["positive", "neutral", "mixed", "negative"];

    public async Task<SentimentAnalysisDto> AnalyzeAsync(IReadOnlyList<FeedbackRecord> feedback, CancellationToken cancellationToken)
    {
        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured. Set OpenAI__ApiKey in the environment or user secrets.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(options.Value.Model) ? "gpt-4o-mini" : options.Value.Model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = "You analyze consumer feedback. Return strict JSON only with keys: overallSummary, overallSentiment, topThemes, recommendedActions, uncertaintyNote. overallSentiment and theme sentiment must be positive, neutral, mixed, or negative. topThemes must contain 3-7 items when evidence permits and include feedbackIds." },
                new { role = "user", content = BuildPrompt(feedback) }
            }
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OpenAI request failed with {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"OpenAI request failed with status {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAI returned an empty analysis response.");
        }

        return ParseAndValidate(content);
    }

    public static SentimentAnalysisDto ParseAndValidate(string rawJson)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<OpenAIAnalysisResponse>(rawJson, JsonOptions)
                ?? throw new JsonException("Analysis JSON was empty.");

            if (string.IsNullOrWhiteSpace(dto.OverallSummary)) throw new JsonException("overallSummary is required.");
            if (!AllowedSentiments.Contains(dto.OverallSentiment)) throw new JsonException("overallSentiment is invalid.");
            if (dto.TopThemes is null || dto.TopThemes.Count == 0) throw new JsonException("topThemes is required.");
            if (dto.TopThemes.Count > 7) throw new JsonException("topThemes cannot contain more than 7 items.");
            if (dto.RecommendedActions is null || dto.RecommendedActions.Count == 0) throw new JsonException("recommendedActions is required.");

            var themes = dto.TopThemes.Select(t =>
            {
                if (string.IsNullOrWhiteSpace(t.Theme) || string.IsNullOrWhiteSpace(t.Summary)) throw new JsonException("Each theme requires theme and summary.");
                if (!AllowedSentiments.Contains(t.Sentiment)) throw new JsonException("Theme sentiment is invalid.");
                if (t.FeedbackIds is null || t.FeedbackIds.Count == 0) throw new JsonException("Each theme requires evidence feedbackIds.");
                return new ThemeDto(t.Theme, t.Sentiment, t.Summary, t.FeedbackIds);
            }).ToArray();

            var actions = dto.RecommendedActions.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new RecommendedActionDto(a.Trim())).ToArray();
            if (actions.Length == 0) throw new JsonException("recommendedActions cannot be empty.");

            return new SentimentAnalysisDto(dto.OverallSummary, dto.OverallSentiment, themes, actions, dto.UncertaintyNote);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OpenAI returned invalid JSON: {ex.Message}", ex);
        }
    }

    private static string BuildPrompt(IReadOnlyList<FeedbackRecord> feedback)
    {
        var builder = new StringBuilder("Analyze these feedback records. Keep summary concise. Records:\n");
        foreach (var item in feedback)
        {
            builder.Append("- ").Append(item.FeedbackId).Append(": ").AppendLine(item.Comment.Length > 1200 ? item.Comment[..1200] : item.Comment);
        }
        return builder.ToString();
    }

    private sealed class OpenAIAnalysisResponse
    {
        public string OverallSummary { get; set; } = string.Empty;
        public string OverallSentiment { get; set; } = string.Empty;
        public List<ThemeItem>? TopThemes { get; set; }
        public List<string>? RecommendedActions { get; set; }
        public string? UncertaintyNote { get; set; }
    }

    private sealed class ThemeItem
    {
        public string Theme { get; set; } = string.Empty;
        public string Sentiment { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> FeedbackIds { get; set; } = [];
    }
}
