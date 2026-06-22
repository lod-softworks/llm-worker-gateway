using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lod.LlmGateway.Contracts.HttpContracts.LMStudio;

/// <summary>
/// OpenAPI request contract for LM Studio native chat endpoint.
/// </summary>
/// <remarks>
/// Official documentation:
/// https://lmstudio.ai/docs/developer/rest/chat
/// </remarks>
public sealed record class LMStudioChatRequestContract
{
    [JsonPropertyName("model")]
    [DefaultValue("local-model")]
    public string Model { get; init; } = "local-model";

    // LM Studio input accepts multiple shapes (string | array/object payloads).
    [JsonPropertyName("input")]
    public JsonElement Input { get; init; } = JsonSerializer.SerializeToElement("Write a one-sentence summary about gateway proxies.");

    [JsonPropertyName("system_prompt")]
    [DefaultValue("You are a concise assistant.")]
    public string? SystemPrompt { get; init; } = "You are a concise assistant.";

    // Integrations payload shape is provider-extensible.
    [JsonPropertyName("integrations")]
    public JsonElement? Integrations { get; init; }

    [JsonPropertyName("stream")]
    [DefaultValue(false)]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    [DefaultValue(0.7)]
    public double? Temperature { get; init; } = 0.7;

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
    public bool Store { get; init; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Metadata { get; init; } = [];
}
