namespace Lod.LlmGateway.Gateway.Api;

public record class ApiKeyOptions
{
    public string? WorkerKey { get; init; }

    public Dictionary<string, string> Clients { get; init; } = [];
}
