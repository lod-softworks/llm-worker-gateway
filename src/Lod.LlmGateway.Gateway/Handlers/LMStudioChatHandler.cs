using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Data;
using Lod.LlmGateway.Gateway.Jobs;
using Lod.LlmGateway.Contracts.HttpContracts.LMStudio;
using Lod.LlmGateway.Contracts.Models.LMStudio;
using System.Text;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Handlers;

public sealed class LMStudioChatHandler(
    ILogger<LMStudioChatHandler> logger,
    JobRouter jobRouter,
    ApiKeyAuthorizer apiKeyAuthorizer,
    LMStudioChatTelemetryWriter telemetryWriter)
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

            return GatewayResults.LMStudioError(
                StatusCodes.Status401Unauthorized,
                "Missing or invalid API key.");
        }

        string? clientName = apiKeyAuthorizer.GetAuthorizedClientName(httpContext);

        httpContext.Request.EnableBuffering();
        using StreamReader requestReader = new(httpContext.Request.Body, leaveOpen: true);
        string requestText = await requestReader.ReadToEndAsync(cancellationToken);
        httpContext.Request.Body.Seek(0, SeekOrigin.Begin);

        LMStudioChatRequestContract? apiContract;
        try
        {
            apiContract = JsonSerializer.Deserialize<LMStudioChatRequestContract>(requestText);
        }
        catch (JsonException)
        {
            return GatewayResults.LMStudioError(
                StatusCodes.Status400BadRequest,
                "Invalid chat request.");
        }

        if (apiContract is null)
        {
            return GatewayResults.LMStudioError(
                StatusCodes.Status400BadRequest,
                "Invalid chat request.");
        }

        LMStudioChatRequest request = MapToRequest(apiContract);
        if (request.Input.ValueKind == JsonValueKind.Undefined || request.Input.ValueKind == JsonValueKind.Null)
        {
            return GatewayResults.LMStudioError(
                StatusCodes.Status400BadRequest,
                "No input received in the request.");
        }
        else if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Received LM Studio chat request ({Id}). Request: {Request}",
                requestId, requestText);
        }

        try
        {
            if (request.Stream)
            {
                logger.LogTrace("Streaming LM Studio response ({Id}).", requestId);

                return Results.Stream(
                    async (outputStream) =>
                    {
                        DateTimeOffset requestSentUtc = DateTimeOffset.UtcNow;
                        string? streamError = null;

                        try
                        {
                            await foreach (StreamChunk chunk in jobRouter.EnqueueLMStudioAndStreamAsync(
                                request,
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
                                    line = $"data: {chunk.Data}\n\n";
                                }

                                byte[] bytes = Encoding.UTF8.GetBytes(line);
                                await outputStream.WriteAsync(bytes, cancellationToken);
                                await outputStream.FlushAsync(cancellationToken);

                                if (logger.IsEnabled(LogLevel.Trace))
                                {
                                    logger.LogTrace("Response chunk for LM Studio chat request ({Id}): {Chunk}", requestId, line);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (NoWorkerAvailableException)
                        {
                            streamError = "No workers are currently connected.";
                            string errorPayload = JsonSerializer.Serialize(new
                            {
                                error = new
                                {
                                    message = streamError,
                                    code = (string?)null
                                }
                            });
                            await outputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {errorPayload}\n\n"), cancellationToken);
                            await outputStream.FlushAsync(cancellationToken);
                        }
                        catch (JobFailedException ex)
                        {
                            streamError = ex.Message;
                            string errorPayload = JsonSerializer.Serialize(new
                            {
                                error = new
                                {
                                    message = streamError,
                                    code = (string?)null
                                }
                            });
                            await outputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {errorPayload}\n\n"), cancellationToken);
                            await outputStream.FlushAsync(cancellationToken);
                        }

                        await telemetryWriter.WriteAsync(
                            requestId.ToString("n"),
                            clientName,
                            requestReceivedUtc,
                            requestSentUtc,
                            DateTimeOffset.UtcNow,
                            request.Model,
                            response: null,
                            cloudFallbackUsed: false,
                            httpStatusCode: StatusCodes.Status200OK,
                            error: streamError,
                            cancellationToken);
                    },
                    "text/event-stream");
            }
            else
            {
                DateTimeOffset requestSentUtc = DateTimeOffset.UtcNow;

                LMStudioChatResponse response = await jobRouter.EnqueueLMStudioAndAwaitAsync(
                    request,
                    DefaultTimeout,
                    cancellationToken);

                string json = JsonSerializer.Serialize(response);

                logger.LogTrace("Finished LM Studio chat request ({Id}). Response: {Response}", requestId, json);

                await telemetryWriter.WriteAsync(
                    requestId.ToString("n"),
                    clientName,
                    requestReceivedUtc,
                    requestSentUtc,
                    DateTimeOffset.UtcNow,
                    request.Model,
                    response,
                    cloudFallbackUsed: false,
                    httpStatusCode: StatusCodes.Status200OK,
                    error: null,
                    cancellationToken);

                return Results.Content(json, "application/json", Encoding.UTF8);
            }
        }
        catch (NoWorkerAvailableException)
        {
            logger.LogTrace("No worker for LM Studio chat request ({Id}) is currently available.", requestId);

            await telemetryWriter.WriteAsync(
                requestId.ToString("n"),
                clientName,
                requestReceivedUtc,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                request.Model,
                response: null,
                cloudFallbackUsed: false,
                httpStatusCode: StatusCodes.Status503ServiceUnavailable,
                error: "No workers are currently connected.",
                cancellationToken);

            return GatewayResults.LMStudioError(
                StatusCodes.Status503ServiceUnavailable,
                "No workers are currently connected.");
        }
        catch (JobFailedException ex)
        {
            logger.LogTrace(ex, "Failure for LM Studio chat request ({Id}).", requestId);

            await telemetryWriter.WriteAsync(
                requestId.ToString("n"),
                clientName,
                requestReceivedUtc,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                request.Model,
                response: null,
                cloudFallbackUsed: false,
                httpStatusCode: StatusCodes.Status502BadGateway,
                error: ex.Message,
                cancellationToken);

            return GatewayResults.LMStudioError(
                StatusCodes.Status502BadGateway,
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            logger.LogTrace("Timeout for LM Studio chat request ({Id}).", requestId);

            await telemetryWriter.WriteAsync(
                requestId.ToString("n"),
                clientName,
                requestReceivedUtc,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                request.Model,
                response: null,
                cloudFallbackUsed: false,
                httpStatusCode: StatusCodes.Status504GatewayTimeout,
                error: "The request timed out.",
                cancellationToken);

            return GatewayResults.LMStudioError(
                StatusCodes.Status504GatewayTimeout,
                "The request timed out.");
        }
    }

    static LMStudioChatRequest MapToRequest(LMStudioChatRequestContract contract)
    {
        return new LMStudioChatRequest
        {
            Model = contract.Model,
            Input = contract.Input,
            SystemPrompt = contract.SystemPrompt,
            Integrations = contract.Integrations,
            Stream = contract.Stream,
            Temperature = contract.Temperature,
            TopP = contract.TopP,
            TopK = contract.TopK,
            MinP = contract.MinP,
            RepeatPenalty = contract.RepeatPenalty,
            MaxOutputTokens = contract.MaxOutputTokens,
            Reasoning = contract.Reasoning,
            ContextLength = contract.ContextLength,
            Store = contract.Store,
            PreviousResponseId = contract.PreviousResponseId,
            Metadata = contract.Metadata
        };
    }
}
