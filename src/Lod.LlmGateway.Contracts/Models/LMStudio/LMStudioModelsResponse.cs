namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Runtime model-list response for LM Studio `/api/v1/models`.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/endpoints/get-api-v1-models
/// </remarks>
public sealed record class LMStudioModelsResponse
{
    [JsonPropertyName("models")]
    public List<LMStudioModelEntry> Models { get; init; } = [];
}
