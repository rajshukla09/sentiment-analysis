using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SentimentAnalysis.Api.Options;
using SentimentAnalysis.Shared;

namespace SentimentAnalysis.Api.Services;

public sealed class OpenAISentimentAnalyzer(HttpClient httpClient, IOptions<OpenAIOptions> options, ILogger<OpenAISentimentAnalyzer> logger) : ISentimentAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedSentiments = ["positive", "neutral", "mixed", "negative"];

    public async Task<SentimentAnalysisDto> AnalyzeAsync(IReadOnlyList<FeedbackItem> feedbackItems, CancellationToken cancellationToken)
    {
        if (feedbackItems.Count == 0)
        {
            throw new InvalidOperationException("At least one parsed feedback item is required before sentiment analysis.");
        }

        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured. Set OpenAI__ApiKey in the environment or user secrets.");
        }

        var compactFeedbackJson = JsonSerializer.Serialize(
            feedbackItems.Select(x => new { feedbackId = x.FeedbackId, comment = x.Comment }),
            JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = "gpt-4o-mini",
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "Return strict JSON only with shape {\"overallSummary\":string,\"overallSentiment\":\"positive|neutral|mixed|negative\",\"topThemes\":[{\"theme\":string,\"sentiment\":\"positive|neutral|mixed|negative\",\"summary\":string,\"feedbackIds\":[string]}],\"recommendedActions\":[string],\"uncertaintyNote\":string}. Use only the provided feedback items. Cite feedback IDs exactly from the input; do not invent IDs. Feedback items: " + compactFeedbackJson
                }
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

        return ParseAndValidate(content, feedbackItems.Select(x => x.FeedbackId));
    }

    public static SentimentAnalysisDto ParseAndValidate(string rawJson, IEnumerable<string> validFeedbackIds)
    {
        try
        {
            var validIds = validFeedbackIds.ToHashSet(StringComparer.Ordinal);
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

                var sanitizedIds = t.FeedbackIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var invalidIds = sanitizedIds.Where(id => !validIds.Contains(id)).ToArray();
                if (invalidIds.Length > 0)
                {
                    throw new JsonException($"Theme '{t.Theme}' cited feedback IDs that were not in the parsed input: {string.Join(", ", invalidIds)}.");
                }

                if (sanitizedIds.Length == 0) throw new JsonException("Each theme requires evidence feedbackIds.");
                return new ThemeDto(t.Theme.Trim(), t.Sentiment, t.Summary.Trim(), sanitizedIds);
            }).ToArray();

            var actions = dto.RecommendedActions.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new RecommendedActionDto(a.Trim())).ToArray();
            if (actions.Length == 0) throw new JsonException("recommendedActions cannot be empty.");

            return new SentimentAnalysisDto(dto.OverallSummary.Trim(), dto.OverallSentiment, themes, actions, dto.UncertaintyNote?.Trim());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OpenAI returned invalid JSON: {ex.Message}", ex);
        }
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
