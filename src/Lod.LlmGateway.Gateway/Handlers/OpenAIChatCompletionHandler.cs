using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Data;
using Lod.LlmGateway.Gateway.Jobs;
using System.Text;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Handlers;

public sealed class OpenAIChatCompletionHandler(
    ILogger<OpenAIChatCompletionHandler> logger,
    JobRouter jobRouter,
    ApiKeyAuthorizer apiKeyAuthorizer,
    OpenAIChatCompletionTelemetryWriter telemetryWriter)
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<IResult> HandleAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        DateTimeOffset requestReceivedUtc = DateTimeOffset.UtcNow;

        if (!apiKeyAuthorizer.IsClientAuthorized(httpContext))
        {
            logger.LogWarning("Client request connection rejected: missing or invalid API key header. Client provided key '{Provided}' does not match configured key '{Configured}'.",
                ApiKeyAuthorizer.ObfuscateKey(ApiKeyAuthorizer.GetApiKey(httpContext) ?? ""),
                string.Join(", ", apiKeyAuthorizer.ClientObfuscatedKeys));

            return GatewayResults.OpenAIError(
                StatusCodes.Status401Unauthorized,
                "Missing or invalid API key.",
                type: "authentication_error");
        }

        string? clientName = apiKeyAuthorizer.GetAuthorizedClientName(httpContext);

        httpContext.Request.EnableBuffering();
        using StreamReader requestReader = new(httpContext.Request.Body, leaveOpen: true);
        string requestText = await requestReader.ReadToEndAsync(cancellationToken);
        httpContext.Request.Body.Seek(0, SeekOrigin.Begin);

        ChatCompletionRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ChatCompletionRequest>(requestText);
        }
        catch (JsonException)
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status400BadRequest,
                "Invalid chat completion request.",
                type: "invalid_request_error");
        }

        if (request is null)
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status400BadRequest,
                "Invalid chat completion request.",
                type: "invalid_request_error");
        }
        else if (request.Messages.ValueKind != JsonValueKind.Array || request.Messages.GetArrayLength() == 0)
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status400BadRequest,
                "No messages received in the request.",
                type: "invalid_request_error");
        }
        else if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Received chat completion request ({Id}). Request: {Request}",
                requestId, requestText);
        }

        if (request.Stream)
        {
            return Results.Stream(
                async (outputStream) =>
                {
                    DateTimeOffset requestSentUtc = DateTimeOffset.UtcNow;
                    string? streamError = null;
                    DateTimeOffset? firstChunkSentUtc = null;
                    DateTimeOffset? finalChunkSentUtc = null;
                    int streamChunkCount = 0;
                    JsonElement? streamUsage = null;
                    string? rawStreamUsageJson = null;
                    OpenAIChatCompletionChainStreamTelemetryCapture capture = new();
                    try
                    {
                        await foreach (StreamChunk chunk in jobRouter.EnqueueAndStreamAsync(
                            request,
                            capture,
                            DefaultTimeout,
                            cancellationToken))
                        {
                            string line;
                            if (chunk.IsDone)
                            {
                                if (!string.IsNullOrEmpty(chunk.Error))
                                {
                                    streamError = chunk.Error;
                                    string errorPayload = JsonSerializer.Serialize(new
                                    {
                                        error = new
                                        {
                                            message = chunk.Error,
                                            type = "server_error",
                                            code = (string?)null
                                        }
                                    });
                                    await outputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {errorPayload}\n\n"), cancellationToken);
                                    await outputStream.FlushAsync(cancellationToken);
                                }

                                line = "data: [DONE]\n\n";
                            }
                            else
                            {
                                capture.ResponseModel = ResolveStreamResponseModel(chunk.Data, capture.ResponseModel);
                                StreamUsageCapture usageCapture = ResolveStreamUsage(chunk.Data, streamUsage, rawStreamUsageJson);
                                streamUsage = usageCapture.Usage;
                                rawStreamUsageJson = usageCapture.RawUsageJson;
                                if (!string.Equals(chunk.Data, "[DONE]", StringComparison.Ordinal))
                                {
                                    streamChunkCount++;
                                    firstChunkSentUtc ??= DateTimeOffset.UtcNow;
                                }

                                line = $"data: {chunk.Data}\n\n";
                            }

                            await outputStream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken);
                            await outputStream.FlushAsync(cancellationToken);
                            finalChunkSentUtc = DateTimeOffset.UtcNow;

                            if (logger.IsEnabled(LogLevel.Trace))
                            {
                                logger.LogTrace("Response chunk for completion request ({Id}): {Chunk}", requestId, line);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    if (capture.Telemetry.CloudChainSucceeded is false && string.IsNullOrEmpty(streamError))
                    {
                        string errorMessage = capture.TerminalHttpStatusCode == StatusCodes.Status400BadRequest
                            ? capture.TerminalError ?? "Provider rejected the request."
                            : "All connected workers failed to complete the request.";
                        string errorPayload = JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                message = errorMessage,
                                type = capture.TerminalHttpStatusCode == StatusCodes.Status400BadRequest
                                    ? "invalid_request_error"
                                    : "server_error",
                                code = (string?)null
                            }
                        });
                        await outputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {errorPayload}\n\n"), cancellationToken);
                        await outputStream.FlushAsync(cancellationToken);
                        finalChunkSentUtc = DateTimeOffset.UtcNow;
                        streamError = errorMessage;
                    }

                    bool streamSucceeded = streamError is null;
                    string? configuredModel = streamSucceeded ? request.Model : null;
                    string? responseFallbackModel = streamSucceeded ? request.Model : null;

                    DateTimeOffset responseSentUtc = DateTimeOffset.UtcNow;
                    await telemetryWriter.WriteStream(
                        new OpenAIChatCompletionRequestTelemetry(
                            requestId.ToString("n"),
                            clientName,
                            requestReceivedUtc,
                            requestSentUtc,
                            responseSentUtc,
                            ConfiguredModel: configuredModel,
                            RequestModel: request.Model,
                            ResponseFallbackModel: responseFallbackModel,
                            ResponseModel: streamSucceeded ? capture.ResponseModel : null,
                            Streamed: true,
                            HttpStatusCode: StatusCodes.Status200OK,
                            Error: streamError,
                            ChainTelemetry: capture.Telemetry),
                        new OpenAIChatCompletionStreamTelemetry(
                            firstChunkSentUtc,
                            finalChunkSentUtc,
                            streamChunkCount,
                            streamUsage,
                            rawStreamUsageJson),
                        cancellationToken);
                },
                "text/event-stream");
        }

        DateTimeOffset requestSentNonStreamUtc = DateTimeOffset.UtcNow;
        try
        {
            OpenAIChatCompletionNonStreamResult result = await jobRouter.EnqueueAndAwaitAsync(
                request,
                DefaultTimeout,
                cancellationToken);

            if (result is { Succeeded: true, Response: { } r })
            {
                string json = JsonSerializer.Serialize(r);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Finished completion request ({Id}). Response: {Response}", requestId, json);
                }

                await telemetryWriter.WriteNonStream(
                    new OpenAIChatCompletionRequestTelemetry(
                        requestId.ToString("n"),
                        clientName,
                        requestReceivedUtc,
                        requestSentNonStreamUtc,
                        DateTimeOffset.UtcNow,
                        ConfiguredModel: request.Model,
                        RequestModel: request.Model,
                        ResponseFallbackModel: request.Model,
                        ResponseModel: null,
                        Streamed: false,
                        HttpStatusCode: StatusCodes.Status200OK,
                        Error: null,
                        ChainTelemetry: result.ChainTelemetry),
                    new OpenAIChatCompletionNonStreamTelemetry(r),
                    cancellationToken);

                return Results.Content(json, "application/json", Encoding.UTF8);
            }

            int statusCode = result.TerminalHttpStatusCode ?? StatusCodes.Status502BadGateway;
            string errorMessage = string.IsNullOrWhiteSpace(result.TerminalError)
                ? "All connected workers failed to complete the request."
                : result.TerminalError!;

            logger.LogWarning("OpenAI provider chain did not return a success for request ({Id}).", requestId);

            await telemetryWriter.WriteRequest(
                new OpenAIChatCompletionRequestTelemetry(
                    requestId.ToString("n"),
                    clientName,
                    requestReceivedUtc,
                    requestSentNonStreamUtc,
                    DateTimeOffset.UtcNow,
                    ConfiguredModel: null,
                    RequestModel: request.Model,
                    ResponseFallbackModel: null,
                    ResponseModel: null,
                    Streamed: false,
                    HttpStatusCode: statusCode,
                    Error: errorMessage,
                    ChainTelemetry: result.ChainTelemetry),
                cancellationToken);

            if (result.TerminalHttpStatusCode is StatusCodes.Status400BadRequest)
            {
                return GatewayResults.OpenAIError(
                    StatusCodes.Status400BadRequest,
                    result.TerminalError ?? "Provider rejected the request.",
                    type: "invalid_request_error");
            }

            return GatewayResults.OpenAIError(
                statusCode,
                errorMessage,
                type: statusCode >= 500 ? "server_error" : "gateway_error");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    static string? ResolveStreamResponseModel(string? chunkData, string? fallbackModel)
    {
        if (string.IsNullOrWhiteSpace(chunkData) || string.Equals(chunkData, "[DONE]", StringComparison.Ordinal))
        {
            return fallbackModel;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(chunkData);
            if (document.RootElement.TryGetProperty("model", out JsonElement modelElement)
                && modelElement.ValueKind == JsonValueKind.String)
            {
                string? streamModel = modelElement.GetString();
                if (string.IsNullOrWhiteSpace(streamModel) is false)
                {
                    return streamModel;
                }
            }
        }
        catch (JsonException)
        {
            return fallbackModel;
        }

        return fallbackModel;
    }

    static StreamUsageCapture ResolveStreamUsage(string? chunkData, JsonElement? fallbackUsage, string? fallbackRawUsageJson)
    {
        if (string.IsNullOrWhiteSpace(chunkData) || string.Equals(chunkData, "[DONE]", StringComparison.Ordinal))
        {
            return new StreamUsageCapture(fallbackUsage, fallbackRawUsageJson);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(chunkData);
            if (!document.RootElement.TryGetProperty("usage", out JsonElement usageElement)
                || usageElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new StreamUsageCapture(fallbackUsage, fallbackRawUsageJson);
            }

            JsonElement? usage = JsonDocument.Parse(usageElement.GetRawText()).RootElement;
            return new StreamUsageCapture(usage ?? fallbackUsage, usageElement.GetRawText());
        }
        catch (JsonException)
        {
            return new StreamUsageCapture(fallbackUsage, fallbackRawUsageJson);
        }
    }

    sealed record class StreamUsageCapture(JsonElement? Usage, string? RawUsageJson);
}
