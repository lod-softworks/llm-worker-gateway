using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.Contracts.Models.LMStudio;
using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Workers;

namespace Lod.LlmGateway.Gateway.Jobs;

public readonly record struct StreamChunk(bool IsDone, string? Error, string? Data);

public sealed class JobRouter(WorkerRegistry workerRegistry)
{
    readonly WorkerRegistry workerRegistry = workerRegistry;
    readonly ConcurrentDictionary<string, TaskCompletionSource<ChatCompletionResponse>> pending =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, TaskCompletionSource<OpenAIModelListResponse>> pendingModelList =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, TaskCompletionSource<LMStudioChatResponse>> pendingLMStudio =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (Channel<StreamChunk> Channel, WorkerSession Session)> streaming =
        new(StringComparer.Ordinal);

    public async Task<ChatCompletionResponse> EnqueueAndAwaitAsync(
        ChatCompletionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!workerRegistry.TryGetAvailableWorker(out var session) || session is null)
        {
            throw new NoWorkerAvailableException();
        }

        var requestId = Guid.NewGuid().ToString("n");
        var completionSource = new TaskCompletionSource<ChatCompletionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending[requestId] = completionSource;

        var message = new WorkerJobMessage(
            WorkerMessageTypes.ChatCompletionsJob,
            requestId,
            request);

        await workerRegistry.SendJobAsync(session, message, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);

        try
        {
            await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
            {
                return await completionSource.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            pending.TryRemove(requestId, out _);
            await session.MarkIdleAsync(cancellationToken);
        }
    }

    public void CompleteJob(
        string requestId,
        ChatCompletionResponse response)
    {
        if (pending.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetResult(response);
        }
    }

    public void FailJob(
        string requestId,
        string error)
    {
        if (pending.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetException(new JobFailedException(error));
        }
    }

    public async Task<OpenAIModelListResponse> EnqueueModelListAndAwaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!workerRegistry.TryGetAvailableWorker(out var session) || session is null)
        {
            throw new NoWorkerAvailableException();
        }

        var requestId = Guid.NewGuid().ToString("n");
        var completionSource = new TaskCompletionSource<OpenAIModelListResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingModelList[requestId] = completionSource;

        var message = new WorkerModelListJobMessage(
            WorkerMessageTypes.ModelListJob,
            requestId);

        await workerRegistry.SendJobAsync(session, message, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);

        try
        {
            await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
            {
                return await completionSource.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            pendingModelList.TryRemove(requestId, out _);
            await session.MarkIdleAsync(cancellationToken);
        }
    }

    public void CompleteModelListJob(
        string requestId,
        OpenAIModelListResponse response)
    {
        if (pendingModelList.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetResult(response);
        }
    }

    public void FailModelListJob(
        string requestId,
        string error)
    {
        if (pendingModelList.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetException(new JobFailedException(error));
        }
    }

    public async Task<LMStudioChatResponse> EnqueueLMStudioAndAwaitAsync(
        LMStudioChatRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!workerRegistry.TryGetAvailableWorker(out var session) || session is null)
        {
            throw new NoWorkerAvailableException();
        }

        var requestId = Guid.NewGuid().ToString("n");
        var completionSource = new TaskCompletionSource<LMStudioChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingLMStudio[requestId] = completionSource;

        var message = new WorkerLMStudioJobMessage(
            WorkerMessageTypes.LMStudioJob,
            requestId,
            request);

        await workerRegistry.SendJobAsync(session, message, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);

        try
        {
            await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
            {
                return await completionSource.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            pendingLMStudio.TryRemove(requestId, out _);
            await session.MarkIdleAsync(cancellationToken);
        }
    }

    public void CompleteLMStudioJob(
        string requestId,
        LMStudioChatResponse response)
    {
        if (pendingLMStudio.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetResult(response);
        }
    }

    public void FailLMStudioJob(
        string requestId,
        string error)
    {
        if (pendingLMStudio.TryGetValue(requestId, out var completionSource))
        {
            completionSource.TrySetException(new JobFailedException(error));
        }
    }

    public async IAsyncEnumerable<StreamChunk> EnqueueAndStreamAsync(
        ChatCompletionRequest request,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!workerRegistry.TryGetAvailableWorker(out var session) || session is null)
        {
            throw new NoWorkerAvailableException();
        }

        var requestId = Guid.NewGuid().ToString("n");
        var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        streaming[requestId] = (channel, session);

        var message = new WorkerJobMessage(
            WorkerMessageTypes.ChatCompletionsJob,
            requestId,
            request);

        await workerRegistry.SendJobAsync(session, message, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(linked.Token))
            {
                yield return chunk;
                if (chunk.IsDone)
                {
                    break;
                }
            }
        }
        finally
        {
            TryCompleteStreamChannel(requestId);
            streaming.TryRemove(requestId, out _);
        }
    }

    public async IAsyncEnumerable<StreamChunk> EnqueueLMStudioAndStreamAsync(
        LMStudioChatRequest request,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!workerRegistry.TryGetAvailableWorker(out var session) || session is null)
        {
            throw new NoWorkerAvailableException();
        }

        var requestId = Guid.NewGuid().ToString("n");
        var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        streaming[requestId] = (channel, session);

        var message = new WorkerLMStudioJobMessage(
            WorkerMessageTypes.LMStudioJob,
            requestId,
            request);

        await workerRegistry.SendJobAsync(session, message, cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(linked.Token))
            {
                yield return chunk;
                if (chunk.IsDone)
                {
                    break;
                }
            }
        }
        finally
        {
            TryCompleteStreamChannel(requestId);
            streaming.TryRemove(requestId, out _);
        }
    }

    public void PushStreamChunk(string requestId, string payload)
    {
        if (streaming.TryGetValue(requestId, out var entry))
        {
            entry.Channel.Writer.TryWrite(new StreamChunk(IsDone: false, Error: null, Data: payload));
        }
    }

    public void CompleteStream(string requestId, string? error)
    {
        if (streaming.TryRemove(requestId, out var entry))
        {
            if (!string.IsNullOrEmpty(error))
            {
                entry.Channel.Writer.TryWrite(new StreamChunk(IsDone: true, Error: error, Data: null));
            }
            else
            {
                entry.Channel.Writer.TryWrite(new StreamChunk(IsDone: true, Error: null, Data: null));
            }
            entry.Channel.Writer.Complete();
        }
    }

    void TryCompleteStreamChannel(string requestId)
    {
        if (streaming.TryRemove(requestId, out var entry))
        {
            entry.Channel.Writer.Complete();
        }
    }
}

public sealed class NoWorkerAvailableException : Exception
{
    public NoWorkerAvailableException()
        : base("No workers are currently connected.")
    {
    }
}

public sealed class JobFailedException : Exception
{
    public JobFailedException(string message)
        : base(message)
    {
    }
}
