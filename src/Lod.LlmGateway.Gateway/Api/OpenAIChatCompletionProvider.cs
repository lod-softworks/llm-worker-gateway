using Lod.LlmGateway.Contracts.Models.OpenAI;

namespace Lod.LlmGateway.Gateway.Api;

public record class OpenAIChatCompletionProvider
{
    public string Name { get; init; } = "";

    public OpenAIProviderSource Source { get; init; } = OpenAIProviderSource.Api;

    public string[] Models { get; init; } = [];

    public bool AcceptAnyModel { get; init; }

    public bool IgnoreProviderPrefix { get; init; }

    public string BaseUrl { get; init; } = "";

    /// <summary>When set, overrides the outbound <c>model</c> for Api <see cref="Source"/> only.</summary>
    public string Model { get; init; } = "";

    /// <summary>
    /// Mirrors <c>max_completion_tokens</c> to <c>max_tokens</c> when the latter is absent.
    /// Defaults to false for API providers to preserve cloud request semantics.
    /// </summary>
    public bool CopyMaxCompletionTokensToMaxTokens { get; init; }

    public string AuthToken { get; init; } = "";

    public Dictionary<string, string> Headers { get; init; } = [];
}
