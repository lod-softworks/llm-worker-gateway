using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.WorkerHost.Clients;

namespace Lod.LlmGateway.WorkerHost;

public sealed class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    OpenAIChatCompletionClient openAiChatCompletionClient,
    LMStudioClient lmStudioClient)
    : BackgroundService
{
    static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    const int ReceiveBufferSize = 64 * 1024;
    const int MaxMessageBytes = 1024 * 1024; // 1 MB safety cap per message

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gatewayUrl = configuration["Gateway:WorkerWebSocketUrl"];
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            logger.LogWarning("Gateway:WorkerWebSocketUrl is not configured. Worker will not connect.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        // Use a stable workerId for the lifetime of this process; the gateway currently
        // assigns its own ID per connection, but this is useful for diagnostics.
        var workerId = Guid.NewGuid().ToString("n");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                using var client = new ClientWebSocket();

                var workerKey = configuration["Gateway:WorkerApiKey"];
                if (!string.IsNullOrWhiteSpace(workerKey))
                {
                    client.Options.SetRequestHeader("X-Api-Key", workerKey);
                }

                await client.ConnectAsync(new Uri(gatewayUrl), stoppingToken);
                logger.LogInformation("Connected to gateway at {Url}", gatewayUrl);

                // Start a lightweight heartbeat loop to keep the WebSocket from going idle
                // on platforms with TCP/WebSocket idle timeouts (for example, Azure App Service).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (client.State == WebSocketState.Open && stoppingToken.IsCancellationRequested == false)
                        {
                            var heartbeat = new WorkerHeartbeatMessage(
                                WorkerMessageTypes.Heartbeat,
                                workerId,
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                            var payload = JsonSerializer.Serialize(heartbeat, serializerOptions);
                            var buffer = Encoding.UTF8.GetBytes(payload);

                            await client.SendAsync(
                                new ArraySegment<byte>(buffer),
                                WebSocketMessageType.Text,
                                true,
                                stoppingToken);

                            await Task.Delay(HeartbeatInterval, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal shutdown.
                    }
                    catch (Exception ex)
                    {
                        // Heartbeat failures should not crash the worker loop; log and let the outer loop reconnect.
                        logger.LogDebug(ex, "Worker heartbeat loop ended due to an exception.");
                    }
                }, stoppingToken);

                var buffer = new byte[ReceiveBufferSize];
                while (client.State == WebSocketState.Open && stoppingToken.IsCancellationRequested == false)
                {
                    using var messageStream = new MemoryStream();
                    WebSocketReceiveResult? received = null;

                    // Read one logical WebSocket message, which may arrive in multiple frames.
                    do
                    {
                        received = await client.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            stoppingToken);

                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            // Let the outer loop break and reconnect.
                            break;
                        }

                        if (received.MessageType != WebSocketMessageType.Text)
                        {
                            // Ignore non-text messages.
                            continue;
                        }

                        if (received.Count > 0)
                        {
                            messageStream.Write(buffer, 0, received.Count);

                            if (messageStream.Length > MaxMessageBytes)
                            {
                                logger.LogWarning(
                                    "Dropping oversized message from gateway (>{MaxBytes} bytes).",
                                    MaxMessageBytes);
                                break;
                            }
                        }
                    } while (!received.EndOfMessage && client.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested);

                    if (received is { MessageType: WebSocketMessageType.Close })
                    {
                        break;
                    }

                    if (messageStream.Length == 0)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(messageStream.ToArray());
                    await HandleMessageAsync(client, json, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error in worker loop. Reconnecting in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    async Task HandleMessageAsync(
        ClientWebSocket socket,
        string json,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();

        if (string.Equals(type, WorkerMessageTypes.ChatCompletionsJob, StringComparison.Ordinal))
        {
            var message = JsonSerializer.Deserialize<WorkerJobMessage>(json, serializerOptions);
            if (message is null) return;

            logger.LogInformation("Processing OpenAI job {RequestId}", message.RequestId);

            if (message.Payload.Stream)
            {
                try
                {
                    await foreach (var payload in openAiChatCompletionClient.CreateChatCompletionStreamAsync(
                        message.Payload,
                        cancellationToken))
                    {
                        if (payload == "[DONE]")
                        {
                            break;
                        }

                        var chunkMessage = new WorkerJobStreamChunkMessage(
                            WorkerMessageTypes.JobStreamChunk,
                            message.RequestId,
                            payload);

                        var chunkJson = JsonSerializer.Serialize(chunkMessage, serializerOptions);
                        var chunkBuffer = Encoding.UTF8.GetBytes(chunkJson);

                        await socket.SendAsync(
                            new ArraySegment<byte>(chunkBuffer),
                            WebSocketMessageType.Text,
                            true,
                            cancellationToken);
                    }

                    var endMessage = new WorkerJobStreamEndMessage(
                        WorkerMessageTypes.JobStreamEnd,
                        message.RequestId,
                        null);

                    var endJson = JsonSerializer.Serialize(endMessage, serializerOptions);
                    var endBuffer = Encoding.UTF8.GetBytes(endJson);

                    await socket.SendAsync(
                        new ArraySegment<byte>(endBuffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "OpenAI Chat Completion Job {RequestId} stream failed.", message.RequestId);

                    var endMessage = new WorkerJobStreamEndMessage(
                        WorkerMessageTypes.JobStreamEnd,
                        message.RequestId,
                        exception.Message);

                    var endJson = JsonSerializer.Serialize(endMessage, serializerOptions);
                    var endBuffer = Encoding.UTF8.GetBytes(endJson);

                    await socket.SendAsync(
                        new ArraySegment<byte>(endBuffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
            }
            else
            {
                try
                {
                    var response = await openAiChatCompletionClient.CreateChatCompletionAsync(
                        message.Payload,
                        cancellationToken);

                    var result = new WorkerJobResultMessage(
                        WorkerMessageTypes.ChatCompletionsJobResult,
                        message.RequestId,
                        response,
                        null);

                    var payload = JsonSerializer.Serialize(result, serializerOptions);
                    var buffer = Encoding.UTF8.GetBytes(payload);

                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "OpenAI Chat Completion Job {RequestId} failed.", message.RequestId);

                    var error = new WorkerJobResultMessage(
                        WorkerMessageTypes.ChatCompletionsJobResult,
                        message.RequestId,
                        null,
                        exception.Message);

                    var payload = JsonSerializer.Serialize(error, serializerOptions);
                    var buffer = Encoding.UTF8.GetBytes(payload);

                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
            }
        }
        else if (string.Equals(type, WorkerMessageTypes.ModelListJob, StringComparison.Ordinal))
        {
            var message = JsonSerializer.Deserialize<WorkerModelListJobMessage>(json, serializerOptions);
            if (message is null) return;

            logger.LogInformation("Processing OpenAI model-list job {RequestId}", message.RequestId);

            try
            {
                var response = await openAiChatCompletionClient.ListModelsAsync(cancellationToken);
                var result = new WorkerModelListJobResultMessage(
                    WorkerMessageTypes.ModelListJobResult,
                    message.RequestId,
                    response,
                    null);

                var payload = JsonSerializer.Serialize(result, serializerOptions);
                var buffer = Encoding.UTF8.GetBytes(payload);

                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "OpenAI model-list job {RequestId} failed.", message.RequestId);

                var error = new WorkerModelListJobResultMessage(
                    WorkerMessageTypes.ModelListJobResult,
                    message.RequestId,
                    null,
                    exception.Message);

                var payload = JsonSerializer.Serialize(error, serializerOptions);
                var buffer = Encoding.UTF8.GetBytes(payload);

                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
        }
        else if (string.Equals(type, WorkerMessageTypes.LMStudioJob, StringComparison.Ordinal))
        {
            var message = JsonSerializer.Deserialize<WorkerLMStudioJobMessage>(json, serializerOptions);
            if (message is null) return;

            logger.LogInformation("Processing LM Studio job {RequestId}", message.RequestId);

            if (message.Payload.Stream)
            {
                try
                {
                    await foreach (var payload in lmStudioClient.CreateChatResponseStreamAsync(
                        message.Payload,
                        cancellationToken))
                    {
                        if (payload == "[DONE]")
                        {
                            break;
                        }

                        var chunkMessage = new WorkerLMStudioJobStreamChunkMessage(
                            WorkerMessageTypes.LMStudioJobStreamChunk,
                            message.RequestId,
                            payload);

                        var chunkJson = JsonSerializer.Serialize(chunkMessage, serializerOptions);
                        var chunkBuffer = Encoding.UTF8.GetBytes(chunkJson);

                        await socket.SendAsync(
                            new ArraySegment<byte>(chunkBuffer),
                            WebSocketMessageType.Text,
                            true,
                            cancellationToken);
                    }

                    var endMessage = new WorkerLMStudioJobStreamEndMessage(
                        WorkerMessageTypes.LMStudioJobStreamEnd,
                        message.RequestId,
                        null);

                    var endJson = JsonSerializer.Serialize(endMessage, serializerOptions);
                    var endBuffer = Encoding.UTF8.GetBytes(endJson);

                    await socket.SendAsync(
                        new ArraySegment<byte>(endBuffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "LM Studio Job {RequestId} stream failed.", message.RequestId);

                    var endMessage = new WorkerLMStudioJobStreamEndMessage(
                        WorkerMessageTypes.LMStudioJobStreamEnd,
                        message.RequestId,
                        exception.Message);

                    var endJson = JsonSerializer.Serialize(endMessage, serializerOptions);
                    var endBuffer = Encoding.UTF8.GetBytes(endJson);

                    await socket.SendAsync(
                        new ArraySegment<byte>(endBuffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
            }
            else
            {
                try
                {
                    var response = await lmStudioClient.CreateChatResponseAsync(
                        message.Payload,
                        cancellationToken);

                    var result = new WorkerLMStudioJobResultMessage(
                        WorkerMessageTypes.LMStudioJobResult,
                        message.RequestId,
                        response,
                        null);

                    var payload = JsonSerializer.Serialize(result, serializerOptions);
                    var buffer = Encoding.UTF8.GetBytes(payload);

                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "LM Studio Job {RequestId} failed.", message.RequestId);

                    var error = new WorkerLMStudioJobResultMessage(
                        WorkerMessageTypes.LMStudioJobResult,
                        message.RequestId,
                        null,
                        exception.Message);

                    var payload = JsonSerializer.Serialize(error, serializerOptions);
                    var buffer = Encoding.UTF8.GetBytes(payload);

                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
            }
        }
    }
}
