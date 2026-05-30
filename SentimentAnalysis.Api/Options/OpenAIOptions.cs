namespace SentimentAnalysis.Api.Options;

public sealed class OpenAIOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4o-mini";
}
