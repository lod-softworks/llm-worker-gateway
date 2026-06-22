using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Minimal runtime response model for LM Studio chat processing.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/chat
/// </remarks>
public sealed record class LMStudioChatResponse
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Metadata { get; init; } = [];

    [JsonPropertyName("model_instance_id")]
    public string ModelInstanceId { get; init; } = "";

    // Output items can vary by provider mode.
    [JsonPropertyName("output")]
    public List<JsonElement> Output { get; init; } = [];

    [JsonPropertyName("stats")]
    public LMStudioChatStats? Stats { get; init; }

    [JsonPropertyName("response_id")]
    public string? ResponseId { get; init; }
}
