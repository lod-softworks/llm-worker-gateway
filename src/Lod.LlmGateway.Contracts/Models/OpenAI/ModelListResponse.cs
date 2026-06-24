namespace Lod.LlmGateway.Contracts.Models.OpenAI;

/// <summary>
/// Runtime model-list payload from OpenAI-compatible providers.
/// </summary>
/// <remarks>
/// Official reference: https://platform.openai.com/docs/api-reference/models/list
/// </remarks>
public sealed class ModelListResponse
{
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "";
}
