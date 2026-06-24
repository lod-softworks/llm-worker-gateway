using Lod.LlmGateway.Contracts;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Lod.LlmGateway.Contracts.Models.LMStudio;

namespace Lod.LlmGateway.WorkerHost.Clients;

public sealed class LMStudioClient(
    ILogger<LMStudioClient> logger,
    IConfiguration configuration,
    HttpClient httpClient,
    IMemoryCache memoryCache)
    : StreamingChatClientBase(httpClient, memoryCache)
{
    static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);
    readonly string? preferredModel = GetPreferredModel(configuration);

    public async Task<LMStudioChatResponse> CreateChatResponseAsync(
        LMStudioChatRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            string? resolvedModel = preferredModel ?? request.Model;

            object body = new
            {
                model = resolvedModel,
                system_prompt = request.SystemPrompt,
                input = request.Input,
                integrations = request.Integrations,
                stream = false,
                temperature = request.Temperature,
                top_p = request.TopP,
                top_k = request.TopK,
                min_p = request.MinP,
                repeat_penalty = request.RepeatPenalty,
                max_output_tokens = request.MaxOutputTokens,
                reasoning = request.Reasoning,
                context_length = request.ContextLength,
                store = request.Store,
                previous_response_id = request.PreviousResponseId
            };

            return await ReadJsonAsync<LMStudioChatResponse>(
                "api/v1/chat",
                body,
                serializerOptions,
                "LM Studio HTTP request error.",
                cancellationToken);
        }
        catch (HttpRequestException httpException)
        {
            logger.LogWarning(httpException, "Failed to call LM Studio chat API. The service may be unavailable.");
            throw;
        }
    }

    public async IAsyncEnumerable<string> CreateChatResponseStreamAsync(
        LMStudioChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? resolvedModel = preferredModel ?? request.Model;

        object body = new
        {
            model = resolvedModel,
            system_prompt = request.SystemPrompt,
            input = request.Input,
            integrations = request.Integrations,
            stream = true,
            temperature = request.Temperature,
            top_p = request.TopP,
            top_k = request.TopK,
            min_p = request.MinP,
            repeat_penalty = request.RepeatPenalty,
            max_output_tokens = request.MaxOutputTokens,
            reasoning = request.Reasoning,
            context_length = request.ContextLength,
            store = request.Store,
            previous_response_id = request.PreviousResponseId
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/v1/chat")
        {
            Content = CreateJsonContent(body, serializerOptions)
        };

        await foreach (var payload in ReadSseStreamAsync(
            requestMessage,
            "LM Studio chat completions request failed.",
            cancellationToken))
        {
            yield return payload;
        }
    }

    static string? GetPreferredModel(IConfiguration configuration)
    {
        string? configuredModel = configuration["LMStudioRest:PreferredModel"];
        return string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel;
    }
}
