using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lod.LlmGateway.Gateway.Data;

[Table("LMStudioChat", Schema = "llm_worker_gateway")]
public sealed record class LMStudioChatRecord
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; init; }

    [MaxLength(64)]
    public string GatewayRequestId { get; init; } = "";

    [MaxLength(128)]
    public string? Client { get; init; }

    [MaxLength(64)]
    public string Api { get; init; } = "lmstudio.chat";

    [MaxLength(128)]
    public string Endpoint { get; init; } = "/api/v1/chat";

    public DateTimeOffset RequestReceivedUtc { get; init; }

    public DateTimeOffset? RequestSentUtc { get; init; }

    public DateTimeOffset ResponseSentUtc { get; init; }

    [MaxLength(128)]
    public string RequestedModel { get; init; } = "";

    [MaxLength(128)]
    public string? ResponseModelInstanceId { get; init; }

    [MaxLength(128)]
    public string? UpstreamResponseId { get; init; }

    [MaxLength(128)]
    public string? Provider { get; init; }

    public bool LocalModelFallbackUsed { get; init; }

    public bool CloudFallbackUsed { get; init; }

    public int? InputTokens { get; init; }

    public int? TotalOutputTokens { get; init; }

    public int? ReasoningOutputTokens { get; init; }

    public double? TokensPerSecond { get; init; }

    public double? TimeToFirstTokenSeconds { get; init; }

    public double? ModelLoadTimeSeconds { get; init; }

    public string? RawStatsJson { get; init; }

    public int HttpStatusCode { get; init; }

    public string? Error { get; init; }
}
