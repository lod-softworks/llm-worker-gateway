using System.Text.Json.Serialization;

namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Runtime loaded-instance entry for LM Studio model-list responses.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/endpoints/get-api-v1-models
/// </remarks>
public sealed record class LMStudioModelInstanceEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";
}
