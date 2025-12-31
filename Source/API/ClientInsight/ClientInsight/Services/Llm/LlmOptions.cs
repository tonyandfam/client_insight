namespace ClientInsightAPI.Services.Llm;

public sealed class LlmOptions
{
    public bool Enabled { get; set; } = true;

    // Cheap + good default for this task (change anytime)
    public string Model { get; set; } = "gpt-4o-mini";

    // Dev protection: don’t blow your budget during iteration
    public int BatchSize { get; set; } = 5;
    public int MaxOutputTokens { get; set; } = 450;

    // Limit input text size (characters) to reduce token spend
    public int MaxInputChars { get; set; } = 14000; // e.g. ~2k-3k tokens depending on content
    public int MinExtractedTextChars { get; set; } = 500;

    // Worker behavior
    public int PollDelayMs { get; set; } = 2500;
    public int ProcessingStaleMinutes { get; set; } = 20;
    public int MaxAttempts { get; set; } = 3;

    // Optional metadata
    public string PromptVersion { get; set; } = "v1";
}
