using Lod.LlmGateway.Contracts.Models.OpenAI;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lod.LlmGateway.WorkerHost.Clients;

public sealed class OpenAIChatCompletionClient(
    ILogger<OpenAIChatCompletionClient> logger,
    IConfiguration configuration,
    HttpClient httpClient,
    IMemoryCache memoryCache)
    : StreamingChatClientBase(httpClient, memoryCache)
{
    static readonly JsonSerializerOptions serializerOptions = ChatCompletionRequestPayloadBuilder.OutboundSerializerOptions;
    readonly string? preferredModel = GetPreferredModel(configuration);
    readonly bool copyMaxCompletionTokensToMaxTokens = GetCopyMaxCompletionTokensToMaxTokens(configuration);

    public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        string resolvedModel = ResolveModel(request.Model);

        try
        {
            JsonObject body = ChatCompletionRequestPayloadBuilder.BuildOutboundPayload(
                request,
                resolvedModel,
                stream: false,
                copyMaxCompletionTokensToMaxTokens: copyMaxCompletionTokensToMaxTokens);
            return await ReadJsonAsync<ChatCompletionResponse>(
                "v1/chat/completions",
                body,
                serializerOptions,
                "OpenAI chat completions request failed.",
                cancellationToken);
        }
        catch (HttpRequestException httpException)
        {
            logger.LogWarning(httpException, "Failed to call OpenAI chat completions API. The service may be unavailable.");
            throw;
        }
    }

    public async Task<JsonElement> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await HttpClient.GetAsync("v1/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    !string.IsNullOrWhiteSpace(responseText) ? responseText : "OpenAI model list request failed.",
                    null,
                    response.StatusCode);
            }

            JsonElement? result = await response.Content.ReadFromJsonAsync<JsonElement>(serializerOptions, cancellationToken);
            return result ?? throw new InvalidOperationException("OpenAI model list returned an empty response.");
        }
        catch (HttpRequestException httpException)
        {
            logger.LogWarning(httpException, "Failed to call OpenAI model list API. The service may be unavailable.");
            throw;
        }
    }

    public async IAsyncEnumerable<string> CreateChatCompletionStreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string resolvedModel = ResolveModel(request.Model);

        await foreach (string payload in CreateChatCompletionStreamInternalAsync(request, resolvedModel, cancellationToken))
        {
            yield return payload;
        }
    }

    async IAsyncEnumerable<string> CreateChatCompletionStreamInternalAsync(
        ChatCompletionRequest request,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        JsonObject body = ChatCompletionRequestPayloadBuilder.BuildOutboundPayload(
            request,
            model,
            stream: true,
            copyMaxCompletionTokensToMaxTokens: copyMaxCompletionTokensToMaxTokens);
        using HttpRequestMessage requestMessage = new(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(serializerOptions), Encoding.UTF8, "application/json")
        };

        await foreach (string payload in ReadSseStreamAsync(
            requestMessage,
            "OpenAI chat completions request failed.",
            cancellationToken))
        {
            yield return payload;
        }
    }

    string ResolveModel(string model)
    {
        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            return preferredModel;
        }

        return model;
    }

    static string? GetPreferredModel(IConfiguration configuration)
    {
        string? configuredModel = configuration["OpenAIChatCompletions:PreferredModel"];
        return !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : null;
    }

    static bool GetCopyMaxCompletionTokensToMaxTokens(IConfiguration configuration)
    {
        bool? configured = configuration.GetValue<bool?>("OpenAIChatCompletions:CopyMaxCompletionTokensToMaxTokens");
        return configured ?? true;
    }
}
