using Microsoft.EntityFrameworkCore;
using Lod.LlmGateway.Contracts.Models.OpenAI;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Data;

public sealed record class OpenAIChatCompletionRequestTelemetry(
    string GatewayRequestId,
    string? Client,
    DateTimeOffset RequestReceivedUtc,
    DateTimeOffset? RequestSentUtc,
    DateTimeOffset ResponseSentUtc,
    string? ConfiguredModel,
    string? RequestModel,
    string? ResponseFallbackModel,
    string? ResponseModel,
    bool Streamed,
    int HttpStatusCode,
    string? Error,
    OpenAIChatCompletionChainTelemetry? ChainTelemetry);

public sealed record class OpenAIChatCompletionNonStreamTelemetry(ChatCompletionResponse Response);

public sealed record class OpenAIChatCompletionStreamTelemetry(
    DateTimeOffset? FirstChunkSentUtc,
    DateTimeOffset? FinalChunkSentUtc,
    int ChunkCount,
    JsonElement? Usage,
    string? RawUsageJson);

public sealed class OpenAIChatCompletionTelemetryWriter(
    GatewayDbContext dbContext,
    ILogger<OpenAIChatCompletionTelemetryWriter> logger)
{
    public async Task WriteRequest(
        OpenAIChatCompletionRequestTelemetry requestTelemetry,
        CancellationToken cancellationToken)
    {
        OpenAIChatCompletionRequestRecord request = BuildRequestRecord(requestTelemetry, responseModel: null);
        dbContext.OpenAIChatCompletionRequest.Add(request);
        await SaveChanges(requestTelemetry.GatewayRequestId, cancellationToken);
    }

    public async Task WriteNonStream(
        OpenAIChatCompletionRequestTelemetry requestTelemetry,
        OpenAIChatCompletionNonStreamTelemetry nonStreamTelemetry,
        CancellationToken cancellationToken)
    {
        ChatCompletionResponse response = nonStreamTelemetry.Response;
        OpenAIChatCompletionRequestRecord request = BuildRequestRecord(requestTelemetry, response.Model);
        double? durationSeconds = GetElapsedSeconds(requestTelemetry.RequestSentUtc, requestTelemetry.ResponseSentUtc);
        dbContext.OpenAIChatCompletionRequest.Add(request);
        dbContext.OpenAIChatCompletionNonStream.Add(new OpenAIChatCompletionNonStreamRecord
        {
            Request = request,
            UpstreamResponseId = response.Id,
            PromptTokens = TryGetInt32(response.Usage, "prompt_tokens"),
            CompletionTokens = TryGetInt32(response.Usage, "completion_tokens"),
            TotalTokens = TryGetInt32(response.Usage, "total_tokens"),
            DurationSeconds = durationSeconds,
            TokensPerSecond = CalculateTokensPerSecond(TryGetInt32(response.Usage, "completion_tokens"), durationSeconds),
            PromptCost = TryGetDecimal(response.Usage, "prompt_cost"),
            CompletionCost = TryGetDecimal(response.Usage, "completion_cost"),
            TotalCost = TryGetDecimal(response.Usage, "total_cost")
                ?? TryGetDecimalFromDict(response.AdditionalProperties, "cost")
                ?? TryGetDecimalFromDict(response.AdditionalProperties, "total_cost"),
            RawUsageJson = response.Usage is null ? null : JsonSerializer.Serialize(response.Usage)
        });

        await SaveChanges(requestTelemetry.GatewayRequestId, cancellationToken);
    }

    public async Task WriteStream(
        OpenAIChatCompletionRequestTelemetry requestTelemetry,
        OpenAIChatCompletionStreamTelemetry streamTelemetry,
        CancellationToken cancellationToken)
    {
        OpenAIChatCompletionRequestRecord request = BuildRequestRecord(requestTelemetry, responseModel: null);
        double? durationSeconds = GetElapsedSeconds(requestTelemetry.RequestSentUtc, requestTelemetry.ResponseSentUtc);
        double? timeToFirstChunkSeconds = GetElapsedSeconds(requestTelemetry.RequestSentUtc, streamTelemetry.FirstChunkSentUtc);
        dbContext.OpenAIChatCompletionRequest.Add(request);
        dbContext.OpenAIChatCompletionStream.Add(new OpenAIChatCompletionStreamRecord
        {
            Request = request,
            FirstChunkSentUtc = streamTelemetry.FirstChunkSentUtc,
            FinalChunkSentUtc = streamTelemetry.FinalChunkSentUtc,
            ChunkCount = streamTelemetry.ChunkCount,
            PromptTokens = TryGetInt32(streamTelemetry.Usage, "prompt_tokens"),
            CompletionTokens = TryGetInt32(streamTelemetry.Usage, "completion_tokens"),
            TotalTokens = TryGetInt32(streamTelemetry.Usage, "total_tokens"),
            DurationSeconds = durationSeconds,
            TimeToFirstChunkSeconds = timeToFirstChunkSeconds,
            TokensPerSecond = CalculateTokensPerSecond(TryGetInt32(streamTelemetry.Usage, "completion_tokens"), durationSeconds),
            RawUsageJson = streamTelemetry.RawUsageJson
        });

        await SaveChanges(requestTelemetry.GatewayRequestId, cancellationToken);
    }

    OpenAIChatCompletionRequestRecord BuildRequestRecord(
        OpenAIChatCompletionRequestTelemetry telemetry,
        string? responseModel)
    {
        string? resolvedResponseModel = OpenAIChatCompletionModelResolution.ResolveForTelemetry(
            null,
            null,
            telemetry.ConfiguredModel,
            telemetry.ResponseFallbackModel);
        string requestedModel = string.IsNullOrWhiteSpace(telemetry.RequestModel)
            ? resolvedResponseModel ?? ""
            : telemetry.RequestModel.Trim();

        return new OpenAIChatCompletionRequestRecord
        {
            GatewayRequestId = telemetry.GatewayRequestId,
            Client = telemetry.Client,
            Streamed = telemetry.Streamed,
            RequestReceivedUtc = telemetry.RequestReceivedUtc,
            RequestSentUtc = telemetry.RequestSentUtc,
            ResponseSentUtc = telemetry.ResponseSentUtc,
            RequestedModel = requestedModel,
            ResponseModel = resolvedResponseModel,
            Provider = "local",
            LocalModelFallbackUsed = DetermineLocalFallbackUsage(resolvedResponseModel, telemetry.ConfiguredModel),
            CloudFallbackUsed = false,
            CloudFallbackTierName = telemetry.ChainTelemetry?.TierName,
            CloudFallbackWinnerIndex = telemetry.ChainTelemetry?.WinnerIndex,
            CloudFallbackAttemptsJson = telemetry.ChainTelemetry?.AttemptsJson,
            HttpStatusCode = telemetry.HttpStatusCode,
            Error = telemetry.Error
        };
    }

    async Task SaveChanges(string gatewayRequestId, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Failed to persist chat completion telemetry for request {RequestId}.", gatewayRequestId);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Failed to persist chat completion telemetry for request {RequestId}.", gatewayRequestId);
        }
    }

    static bool DetermineLocalFallbackUsage(string? responseModel, string? configuredModel)
    {
        if (string.IsNullOrWhiteSpace(responseModel) || string.IsNullOrWhiteSpace(configuredModel))
        {
            return false;
        }

        return !string.Equals(responseModel, configuredModel, StringComparison.OrdinalIgnoreCase);
    }

    static double? GetElapsedSeconds(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (!start.HasValue || !end.HasValue || end.Value <= start.Value)
        {
            return null;
        }

        return (end.Value - start.Value).TotalSeconds;
    }

    static double? CalculateTokensPerSecond(int? completionTokens, double? durationSeconds)
    {
        if (!completionTokens.HasValue || completionTokens.Value <= 0 || !durationSeconds.HasValue || durationSeconds.Value <= 0)
        {
            return null;
        }

        return completionTokens.Value / durationSeconds.Value;
    }

    static decimal? TryGetDecimalFromDict(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || metadata.TryGetValue(key, out JsonElement value) is false)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out decimal number) => number,
            _ => null
        };
    }

    static decimal? TryGetDecimal(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out decimal number) => number,
            _ => null
        };
    }

    static int? TryGetInt32(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.Value.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result))
        {
            return result;
        }

        return null;
    }
}
