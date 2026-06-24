using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Jobs;

namespace Lod.LlmGateway.Gateway.Workers;

public sealed class WorkerWebSocketHandler(
    WorkerRegistry workerRegistry,
    JobRouter jobRouter,
    ILogger<WorkerWebSocketHandler> logger)
{
    const int MaxMessageBytes = 1024 * 1024;
    readonly WorkerRegistry workerRegistry = workerRegistry;
    readonly JobRouter jobRouter = jobRouter;
    readonly ILogger<WorkerWebSocketHandler> logger = logger;
    readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

        string workerId = httpContext.Request.Query["workerId"].ToString();
        if (string.IsNullOrWhiteSpace(workerId))
        {
            workerId = Guid.NewGuid().ToString("n");
        }

        await workerRegistry.AcceptWorkerAsync(workerId, webSocket, cancellationToken);
        logger.LogInformation("Worker connected: {WorkerId}. Registered workers: {Count}", workerId, workerRegistry.RegisteredWorkerCount);

        var buffer = new byte[64 * 1024];

        try
        {
            while (webSocket.State == WebSocketState.Open && cancellationToken.IsCancellationRequested == false)
            {
                using MemoryStream messageStream = new();
                WebSocketReceiveResult received;
                do
                {
                    received = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);

                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (received.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    if (received.Count > 0)
                    {
                        messageStream.Write(buffer, 0, received.Count);
                        if (messageStream.Length > MaxMessageBytes)
                        {
                            logger.LogWarning("Dropping oversized worker message from {WorkerId} (>{MaxBytes} bytes).", workerId, MaxMessageBytes);
                            break;
                        }
                    }
                } while (!received.EndOfMessage && webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // Normal close from the remote side.
                    break;
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                if (messageStream.Length == 0 || messageStream.Length > MaxMessageBytes)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                await HandleMessageAsync(workerId, json, cancellationToken);
            }
        }
        catch (WebSocketException socketException)
        {
            // Remote closed without a clean close handshake or other transport issue.
            logger.LogInformation(
                socketException,
                "Worker WebSocket closed unexpectedly for {WorkerId}. Treating as normal disconnect.",
                workerId);
        }
        catch (OperationCanceledException)
        {
            // Server shutdown / request aborted; let the finally block clean up.
        }
        finally
        {
            workerRegistry.Remove(workerId);

            logger.LogInformation(
                "Worker disconnected: {WorkerId}. Registered workers after removal: {Count}",
                workerId,
                workerRegistry.RegisteredWorkerCount);
        }
    }

    async Task HandleMessageAsync(
        string workerId,
        string json,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ignoring malformed worker message from {WorkerId}.", workerId);
            return;
        }

        using (document)
        {
        if (!document.RootElement.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();
        if (string.Equals(type, WorkerMessageTypes.Heartbeat, StringComparison.Ordinal))
        {
            if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
            {
                session.MarkHeartbeat();
            }

            return;
        }

        if (string.Equals(type, WorkerMessageTypes.JobStreamChunk, StringComparison.Ordinal))
        {
            WorkerJobStreamChunkMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerJobStreamChunkMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed job stream chunk from {WorkerId}.", workerId);
                return;
            }

            if (message is not null)
            {
                jobRouter.PushStreamChunk(message.RequestId, message.Payload);
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.JobStreamEnd, StringComparison.Ordinal))
        {
            WorkerJobStreamEndMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerJobStreamEndMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed job stream end from {WorkerId}.", workerId);
                return;
            }

            if (message is not null)
            {
                jobRouter.CompleteStream(message.RequestId, message.Error);
                if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
                {
                    await session.MarkIdleAsync(cancellationToken);
                }
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.ChatCompletionsJobResult, StringComparison.Ordinal))
        {
            WorkerJobResultMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerJobResultMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed job result from {WorkerId}.", workerId);
                return;
            }

            if (message is null)
            {
                return;
            }

            if (message.Result is not null)
            {
                jobRouter.CompleteJob(message.RequestId, message.Result);
            }
            else if (!string.IsNullOrWhiteSpace(message.Error))
            {
                jobRouter.FailJob(message.RequestId, message.Error);
            }

            if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
            {
                await session.MarkIdleAsync(cancellationToken);
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.ModelListJobResult, StringComparison.Ordinal))
        {
            WorkerModelListJobResultMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerModelListJobResultMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed model-list result from {WorkerId}.", workerId);
                return;
            }

            if (message is null)
            {
                return;
            }

            if (message.Result is not null)
            {
                jobRouter.CompleteModelListJob(message.RequestId, message.Result.Value);
            }
            else if (!string.IsNullOrWhiteSpace(message.Error))
            {
                jobRouter.FailModelListJob(message.RequestId, message.Error);
            }

            if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
            {
                await session.MarkIdleAsync(cancellationToken);
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.LMStudioJobStreamChunk, StringComparison.Ordinal))
        {
            WorkerLMStudioJobStreamChunkMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerLMStudioJobStreamChunkMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed LM Studio stream chunk from {WorkerId}.", workerId);
                return;
            }

            if (message is not null)
            {
                jobRouter.PushStreamChunk(message.RequestId, message.Payload);
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.LMStudioJobStreamEnd, StringComparison.Ordinal))
        {
            WorkerLMStudioJobStreamEndMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerLMStudioJobStreamEndMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed LM Studio stream end from {WorkerId}.", workerId);
                return;
            }

            if (message is not null)
            {
                jobRouter.CompleteStream(message.RequestId, message.Error);
                if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
                {
                    await session.MarkIdleAsync(cancellationToken);
                }
            }
            return;
        }

        if (string.Equals(type, WorkerMessageTypes.LMStudioJobResult, StringComparison.Ordinal))
        {
            WorkerLMStudioJobResultMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WorkerLMStudioJobResultMessage>(json, serializerOptions);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed LM Studio job result from {WorkerId}.", workerId);
                return;
            }

            if (message is null)
            {
                return;
            }

            if (message.Result is not null)
            {
                jobRouter.CompleteLMStudioJob(message.RequestId, message.Result);
            }
            else if (!string.IsNullOrWhiteSpace(message.Error))
            {
                jobRouter.FailLMStudioJob(message.RequestId, message.Error);
            }

            if (workerRegistry.TryGetById(workerId, out var session) && session is not null)
            {
                await session.MarkIdleAsync(cancellationToken);
            }
            return;
        }
        }
    }
}
