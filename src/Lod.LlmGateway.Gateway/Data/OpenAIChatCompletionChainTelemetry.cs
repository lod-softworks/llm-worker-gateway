using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lod.LlmGateway.Gateway.Data;

public sealed record OpenAIChatCompletionAttempt(
    string Name,
    int Index,
    bool Ok,
    int? HttpStatus,
    string? Error,
    string? Source = null);

public sealed record OpenAIChatCompletionChainTelemetry(
    string? TierName,
    int? WinnerIndex,
    string? AttemptsJson)
{
    static readonly JsonSerializerOptions AttemptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool CloudChainSucceeded => TierName is not null;

    public static OpenAIChatCompletionChainTelemetry None => new(null, null, null);

    public static OpenAIChatCompletionChainTelemetry ForWinner(
        string tierName,
        int index,
        IReadOnlyList<OpenAIChatCompletionAttempt> attempts) =>
        new(tierName, index, JsonSerializer.Serialize(attempts, AttemptJsonOptions));

    public static OpenAIChatCompletionChainTelemetry ForAllFailed(IReadOnlyList<OpenAIChatCompletionAttempt> attempts) =>
        new(null, null, JsonSerializer.Serialize(attempts, AttemptJsonOptions));
}

public sealed class OpenAIChatCompletionChainStreamTelemetryCapture
{
    public OpenAIChatCompletionChainTelemetry Telemetry { get; set; } = OpenAIChatCompletionChainTelemetry.None;

    public int? TerminalHttpStatusCode { get; set; }

    public string? TerminalError { get; set; }

    public string? ResponseModel { get; set; }
}
