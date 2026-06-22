namespace Lod.LlmGateway.Gateway.Api;

public record class OpenAIChatCompletionOptions
{
    public List<OpenAIChatCompletionProvider> Providers { get; init; } = [];
}
