using Microsoft.EntityFrameworkCore;
using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.Contracts.Models.LMStudio;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Data;

public sealed class LMStudioChatTelemetryWriter(
    GatewayDbContext dbContext,
    ILogger<LMStudioChatTelemetryWriter> logger)
{
    public async Task WriteAsync(
        string gatewayRequestId,
        string? client,
        DateTimeOffset requestReceivedUtc,
        DateTimeOffset? requestSentUtc,
        DateTimeOffset responseSentUtc,
        string requestedModel,
        LMStudioChatResponse? response,
        bool cloudFallbackUsed,
        int httpStatusCode,
        string? error,
        CancellationToken cancellationToken)
    {
        bool localModelFallbackUsed = DetermineLocalFallbackUsage(requestedModel, response?.ModelInstanceId, cloudFallbackUsed);

        LMStudioChatRecord record = new()
        {
            GatewayRequestId = gatewayRequestId,
            Client = client,
            RequestReceivedUtc = requestReceivedUtc,
            RequestSentUtc = requestSentUtc,
            ResponseSentUtc = responseSentUtc,
            RequestedModel = requestedModel,
            ResponseModelInstanceId = response?.ModelInstanceId,
            UpstreamResponseId = response?.ResponseId,
            Provider = cloudFallbackUsed ? "cloud" : "local",
            LocalModelFallbackUsed = localModelFallbackUsed,
            CloudFallbackUsed = cloudFallbackUsed,
            InputTokens = response?.Stats?.InputTokens,
            TotalOutputTokens = response?.Stats?.TotalOutputTokens,
            ReasoningOutputTokens = response?.Stats?.ReasoningOutputTokens,
            TokensPerSecond = response?.Stats?.TokensPerSecond,
            TimeToFirstTokenSeconds = response?.Stats?.TimeToFirstTokenSeconds,
            ModelLoadTimeSeconds = response?.Stats?.ModelLoadTimeSeconds,
            RawStatsJson = response?.Stats is null ? null : JsonSerializer.Serialize(response.Stats),
            HttpStatusCode = httpStatusCode,
            Error = error
        };

        dbContext.Set<LMStudioChatRecord>().Add(record);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Failed to persist LM Studio telemetry for request {RequestId}.", gatewayRequestId);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Failed to persist LM Studio telemetry for request {RequestId}.", gatewayRequestId);
        }
    }

    static bool DetermineLocalFallbackUsage(string requestedModel, string? modelInstanceId, bool cloudFallbackUsed)
    {
        if (cloudFallbackUsed || string.IsNullOrWhiteSpace(requestedModel) || string.IsNullOrWhiteSpace(modelInstanceId))
        {
            return false;
        }

        return modelInstanceId.Contains(requestedModel, StringComparison.OrdinalIgnoreCase) is false
            && requestedModel.Contains(modelInstanceId, StringComparison.OrdinalIgnoreCase) is false;
    }
}
