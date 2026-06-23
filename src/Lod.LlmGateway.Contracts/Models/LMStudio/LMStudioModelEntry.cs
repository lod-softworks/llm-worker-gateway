namespace Lod.LlmGateway.Contracts.Models.LMStudio;

/// <summary>
/// Runtime model metadata entry for LM Studio model-list responses.
/// </summary>
/// <remarks>
/// Official reference: https://lmstudio.ai/docs/developer/rest/endpoints/get-api-v1-models
/// </remarks>
public sealed record class LMStudioModelEntry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("key")]
    public string Key { get; init; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("loaded_instances")]
    public List<LMStudioModelInstanceEntry> LoadedInstances { get; init; } = [];
}
