using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lod.LlmGateway.Gateway.Tests;

public sealed class OpenAIChatCompletionTelemetryWriterTests
{
    [Fact]
    public async Task WriteNonStream_UsesConfiguredModel_WhenWorkerEchoesRequestedModel()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<GatewayDbContext> options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .Options;

        await using GatewayDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();
        OpenAIChatCompletionTelemetryWriter writer = new(dbContext, NullLogger<OpenAIChatCompletionTelemetryWriter>.Instance);

        await writer.WriteNonStream(
            new OpenAIChatCompletionRequestTelemetry(
                GatewayRequestId: "request-id",
                Client: null,
                RequestReceivedUtc: DateTimeOffset.UtcNow,
                RequestSentUtc: DateTimeOffset.UtcNow,
                ResponseSentUtc: DateTimeOffset.UtcNow,
                ConfiguredModel: "qwen3.6",
                RequestModel: "gpt-5-nano",
                ResponseFallbackModel: "gpt-5-nano",
                ResponseModel: null,
                Streamed: false,
                HttpStatusCode: 200,
                Error: null,
                ChainTelemetry: OpenAIChatCompletionChainTelemetry.ForWinner(
                    "Worker",
                    0,
                    [new OpenAIChatCompletionAttempt("Worker", 0, true, 200, null, "worker")])),
            new OpenAIChatCompletionNonStreamTelemetry(new ChatCompletionResponse
            {
                Id = "response-id",
                Model = "gpt-5-nano"
            }),
            CancellationToken.None);

        OpenAIChatCompletionRequestRecord record = await dbContext.OpenAIChatCompletionRequest.SingleAsync();
        Assert.Equal("qwen3.6", record.ResponseModel);
    }

    [Fact]
    public async Task WriteStream_UsesConfiguredModel_WhenWorkerStreamEchoesRequestedModel()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        DbContextOptions<GatewayDbContext> options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connection)
            .Options;

        await using GatewayDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();
        OpenAIChatCompletionTelemetryWriter writer = new(dbContext, NullLogger<OpenAIChatCompletionTelemetryWriter>.Instance);

        await writer.WriteStream(
            new OpenAIChatCompletionRequestTelemetry(
                GatewayRequestId: "request-id",
                Client: null,
                RequestReceivedUtc: DateTimeOffset.UtcNow,
                RequestSentUtc: DateTimeOffset.UtcNow,
                ResponseSentUtc: DateTimeOffset.UtcNow,
                ConfiguredModel: "qwen3.6",
                RequestModel: "gpt-5-nano",
                ResponseFallbackModel: "gpt-5-nano",
                ResponseModel: "gpt-5-nano",
                Streamed: true,
                HttpStatusCode: 200,
                Error: null,
                ChainTelemetry: OpenAIChatCompletionChainTelemetry.ForWinner(
                    "Worker",
                    0,
                    [new OpenAIChatCompletionAttempt("Worker", 0, true, 200, null, "worker")])),
            new OpenAIChatCompletionStreamTelemetry(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                1,
                Usage: null,
                RawUsageJson: null),
            CancellationToken.None);

        OpenAIChatCompletionRequestRecord record = await dbContext.OpenAIChatCompletionRequest.SingleAsync();
        Assert.Equal("qwen3.6", record.ResponseModel);
    }
}
