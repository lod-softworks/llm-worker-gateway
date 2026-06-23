namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Runtime statistics payload returned by LM Studio chat responses.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/chat
/// </remarks>
public sealed record class LMStudioChatStats
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Metadata { get; init; } = [];

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("total_output_tokens")]
    public int TotalOutputTokens { get; init; }

    [JsonPropertyName("reasoning_output_tokens")]
    public int ReasoningOutputTokens { get; init; }

    [JsonPropertyName("tokens_per_second")]
    public double TokensPerSecond { get; init; }

    [JsonPropertyName("time_to_first_token_seconds")]
    public double TimeToFirstTokenSeconds { get; init; }

    [JsonPropertyName("model_load_time_seconds")]
    public double? ModelLoadTimeSeconds { get; init; }
}
