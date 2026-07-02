using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Data;
using Lod.LlmGateway.Gateway.Jobs;
using Lod.LlmGateway.Contracts.Models.LMStudio;
using System.Text;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Handlers;

public sealed class LMStudioChatHandler(
    ILogger<LMStudioChatHandler> logger,
    JobRouter jobRouter,
    LMStudioChatTelemetryWriter telemetryWriter)
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<IResult> HandleAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        DateTimeOffset requestReceivedUtc = DateTimeOffset.UtcNow;

        string? clientName = httpContext.User.Identity?.Name;

        httpContext.Request.EnableBuffering();
        using StreamReader requestReader = new(httpContext.Request.Body, leaveOpen: true);
        string requestText = await requestReader.ReadToEndAsync(cancellationToken);
        httpContext.Request.Body.Seek(0, SeekOrigin.Begin);

        LMStudioChatRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LMStudioChatRequest>(requestText);
        }
        catch (JsonException)
        {
            return GatewayResults.LMStudioError(StatusCodes.Status400BadRequest, "Invalid chat request.");
        }

        if (request is null)
        {
            return GatewayResults.LMStudioError(StatusCodes.Status400BadRequest, "Invalid chat request.");
        }
        else if (request.Input.ValueKind == JsonValueKind.Undefined || request.Input.ValueKind == JsonValueKind.Null)
        {
            return GatewayResults.LMStudioError(StatusCodes.Status400BadRequest, "No input received in the request.");
        }
        else
        {
            logger.LogTrace("Received LM Studio chat request ({Id}). Request: {Request}", requestId, requestText);
        }

        return request.Stream
            ? await HandleStreamResponse(requestId, clientName, request, requestReceivedUtc, cancellationToken)
            : await HandleNonStreamResponseAsync(requestId, clientName, request, requestReceivedUtc, cancellationToken);
    }

    private async Task<IResult> HandleStreamResponse(
        Guid requestId,
        string? clientName,
        LMStudioChatRequest request,
        DateTimeOffset requestReceivedUtc,
        CancellationToken cancellationToken)
    {
        logger.LogTrace("Streaming LM Studio response ({Id}).", requestId);

        IAsyncEnumerator<StreamChunk> enumerator;
        try
        {
            enumerator = jobRouter.EnqueueLMStudioAndStreamAsync(
                request,
                DefaultTimeout,
                cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (NoWorkerAvailableException ex)
        {
            return GatewayResults.LMStudioError(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }

        bool moved;
        try
        {
            moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status504GatewayTimeout, "The request timed out.");
        }
        catch (NoWorkerAvailableException ex)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (JobFailedException ex)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status502BadGateway, ex.Message);
        }

        if (moved is false)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status502BadGateway, "All connected workers failed to complete the request.");
        }

        if (enumerator.Current is { IsDone: true, Error: { Length: > 0 } workerError })
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return GatewayResults.LMStudioError(StatusCodes.Status502BadGateway, workerError);
        }

        return Results.Stream(
            async (outputStream) =>
            {
                DateTimeOffset requestSentUtc = DateTimeOffset.UtcNow;
                string? streamError = null;

                try
                {
                    bool hasMore = true;
                    StreamChunk currentChunk = enumerator.Current;

                    while (hasMore)
                    {
                        string line;
                        if (currentChunk.IsDone)
                        {
                            if (!string.IsNullOrEmpty(currentChunk.Error))
                            {
                                streamError = currentChunk.Error;
                                string errorPayload = JsonSerializer.Serialize(new
                                {
                                    error = new
                                    {
                                        message = currentChunk.Error,
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
                            line = $"data: {currentChunk.Data}\n\n";
                        }

                        byte[] bytes = Encoding.UTF8.GetBytes(line);
                        await outputStream.WriteAsync(bytes, cancellationToken);
                        await outputStream.FlushAsync(cancellationToken);

                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace("Response chunk for LM Studio chat request ({Id}): {Chunk}", requestId, line);
                        }

                        if (currentChunk.IsDone)
                        {
                            break;
                        }

                        try
                        {
                            hasMore = await enumerator.MoveNextAsync().ConfigureAwait(false);
                            if (hasMore)
                            {
                                currentChunk = enumerator.Current;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "An exception occurred while streaming LM Studio chat completions for request ({Id}).", requestId);
                            streamError = ex.Message;
                            break;
                        }
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
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

    private async Task<IResult> HandleNonStreamResponseAsync(
        Guid requestId,
        string? clientName,
        LMStudioChatRequest request,
        DateTimeOffset requestReceivedUtc,
        CancellationToken cancellationToken)
    {
        try
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

            return GatewayResults.LMStudioError(StatusCodes.Status503ServiceUnavailable, "No workers are currently connected.");
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

            return GatewayResults.LMStudioError(StatusCodes.Status504GatewayTimeout, "The request timed out.");
        }
    }
}
