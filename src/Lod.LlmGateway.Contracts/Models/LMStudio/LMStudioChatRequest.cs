using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Minimal runtime request model for LM Studio chat processing.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/chat
/// </remarks>
public sealed record class LMStudioChatRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Metadata { get; init; } = [];

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    // LM Studio accepts multiple input shapes.
    [JsonPropertyName("input")]
    public JsonElement Input { get; init; }

    [JsonPropertyName("system_prompt")]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("integrations")]
    public JsonElement? Integrations { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("min_p")]
    public double? MinP { get; init; }

    [JsonPropertyName("repeat_penalty")]
    public double? RepeatPenalty { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("store")]
    public bool? Store { get; init; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }
}
