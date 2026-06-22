using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Data;

namespace Lod.LlmGateway.Gateway.Api;

public sealed record OpenAIChatCompletionNonStreamResult(
    bool Succeeded,
    ChatCompletionResponse? Response,
    OpenAIChatCompletionChainTelemetry ChainTelemetry,
    int? TerminalHttpStatusCode = null,
    string? TerminalError = null,
    OpenAIProviderSource? WinningSource = null);
