using Lod.LlmGateway.Contracts.Models.LMStudio;
using Lod.LlmGateway.Contracts.Models.OpenAI;

namespace Lod.LlmGateway.Contracts;

public static class WorkerMessageTypes
{
    public const string Hello = "hello";
    public const string Heartbeat = "heartbeat";
    public const string JobStreamChunk = "job_stream_chunk";
    public const string JobStreamEnd = "job_stream_end";
    public const string ChatCompletionsJob = "openai_chatcompletions_job";
    public const string ChatCompletionsJobResult = "openai_chatcompletions_job_result";
    public const string ModelListJob = "openai_models_job";
    public const string ModelListJobResult = "openai_models_job_result";

    public const string LMStudioJob = "lmstudio_rest_job";
    public const string LMStudioJobResult = "lmstudio_job_result";
    public const string LMStudioJobStreamChunk = "lmstudio_job_stream_chunk";
    public const string LMStudioJobStreamEnd = "lmstudio_job_stream_end";
}

public sealed record class WorkerHelloMessage(
    string Type,
    string WorkerId,
    string AuthToken);

public sealed record class WorkerHeartbeatMessage(
    string Type,
    string WorkerId,
    long UnixTimeSeconds);

public sealed record class WorkerJobMessage(
    string Type,
    string RequestId,
    ChatCompletionRequest Payload);

public sealed record class WorkerModelListJobMessage(
    string Type,
    string RequestId);

public sealed record class WorkerJobResultMessage(
    string Type,
    string RequestId,
    ChatCompletionResponse? Result,
    string? Error);

public sealed record class WorkerModelListJobResultMessage(
    string Type,
    string RequestId,
    OpenAIModelListResponse? Result,
    string? Error);

public sealed record class WorkerJobStreamChunkMessage(
    string Type,
    string RequestId,
    string Payload);

public sealed record class WorkerJobStreamEndMessage(
    string Type,
    string RequestId,
    string? Error);

public sealed record class WorkerLMStudioJobMessage(
    string Type,
    string RequestId,
    LMStudioChatRequest Payload);

public sealed record class WorkerLMStudioJobResultMessage(
    string Type,
    string RequestId,
    LMStudioChatResponse? Result,
    string? Error);

public sealed record class WorkerLMStudioJobStreamChunkMessage(
    string Type,
    string RequestId,
    string Payload);

public sealed record class WorkerLMStudioJobStreamEndMessage(
    string Type,
    string RequestId,
    string? Error);
