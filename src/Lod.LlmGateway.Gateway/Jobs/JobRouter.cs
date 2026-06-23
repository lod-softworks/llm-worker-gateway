using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lod.LlmGateway.Contracts;
using Lod.LlmGateway.Contracts.Models.LMStudio;
using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Workers;
using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Data;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Jobs;

public readonly record struct StreamChunk(bool IsDone, string? Error, string? Data);

public sealed class JobRouter(
    WorkerRegistry workerRegistry,
    ILogger<JobRouter> logger)
{
    readonly WorkerRegistry workerRegistry = workerRegistry;
    readonly ILogger<JobRouter> logger = logger;
    readonly ConcurrentDictionary<string, TaskCompletionSource<ChatCompletionResponse>> pending =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingModelList =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, TaskCompletionSource<LMStudioChatResponse>> pendingLMStudio =
        new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (Channel<StreamChunk> Channel, WorkerSession Session)> streaming =
        new(StringComparer.Ordinal);

    public async Task<OpenAIChatCompletionNonStreamResult> EnqueueAndAwaitAsync(
        ChatCompletionRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var workers = workerRegistry.GetAvailableWorkers();
        if (workers.Count == 0)
        {
            return new OpenAIChatCompletionNonStreamResult(
                false,
                null,
                OpenAIChatCompletionChainTelemetry.None,
                StatusCodes.Status503ServiceUnavailable,
                "No workers are currently connected.");
        }

        List<OpenAIChatCompletionAttempt> attempts = [];
        for (int i = 0; i < workers.Count; i++)
        {
            var session = workers[i];
            var requestId = Guid.NewGuid().ToString("n");
            var completionSource = new TaskCompletionSource<ChatCompletionResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending[requestId] = completionSource;

            var message = new WorkerJobMessage(
                WorkerMessageTypes.ChatCompletionsJob,
                requestId,
                request);

            try
            {
                await workerRegistry.SendJobAsync(session, message, cancellationToken).ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutSource.Token,
                    cancellationToken);

                ChatCompletionResponse response;
                await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
                {
                    response = await completionSource.Task.ConfigureAwait(false);
                }

                attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, true, 200, null, "worker"));
                return new OpenAIChatCompletionNonStreamResult(
                    true,
                    response,
                    OpenAIChatCompletionChainTelemetry.ForWinner(session.WorkerId, i, attempts));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 504, ex.Message, "worker"));
                logger.LogWarning(ex, "Worker completion attempt '{WorkerId}' (index {Index}) timed out.", session.WorkerId, i);
            }
            catch (JobFailedException ex)
            {
                attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 502, ex.Message, "worker"));
                logger.LogWarning(ex, "Worker completion attempt '{WorkerId}' (index {Index}) job failed: {Message}", session.WorkerId, i, ex.Message);
            }
            catch (Exception ex)
            {
                attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 500, ex.Message, "worker"));
                logger.LogWarning(ex, "Worker completion attempt '{WorkerId}' (index {Index}) failed.", session.WorkerId, i);
            }
            finally
            {
                pending.TryRemove(requestId, out _);
                await session.MarkIdleAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return new OpenAIChatCompletionNonStreamResult(
            false,
            null,
            OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts),
            StatusCodes.Status502BadGateway,
            "All connected workers failed to complete the request.");
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

    public async Task<JsonElement> EnqueueModelListAndAwaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var workers = workerRegistry.GetAvailableWorkers();
        if (workers.Count == 0)
        {
            throw new NoWorkerAvailableException();
        }

        List<Exception> errors = [];
        for (int i = 0; i < workers.Count; i++)
        {
            var session = workers[i];
            var requestId = Guid.NewGuid().ToString("n");
            var completionSource = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingModelList[requestId] = completionSource;

            var message = new WorkerModelListJobMessage(
                WorkerMessageTypes.ModelListJob,
                requestId);

            try
            {
                await workerRegistry.SendJobAsync(session, message, cancellationToken).ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutSource.Token,
                    cancellationToken);

                await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
                {
                    return await completionSource.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                logger.LogWarning(ex, "Worker model list attempt '{WorkerId}' (index {Index}) failed.", session.WorkerId, i);
            }
            finally
            {
                pendingModelList.TryRemove(requestId, out _);
                await session.MarkIdleAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AggregateException("All workers failed to list models.", errors);
    }

    public void CompleteModelListJob(
        string requestId,
        JsonElement response)
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
        var workers = workerRegistry.GetAvailableWorkers();
        if (workers.Count == 0)
        {
            throw new NoWorkerAvailableException();
        }

        List<Exception> errors = [];
        for (int i = 0; i < workers.Count; i++)
        {
            var session = workers[i];
            var requestId = Guid.NewGuid().ToString("n");
            var completionSource = new TaskCompletionSource<LMStudioChatResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingLMStudio[requestId] = completionSource;

            var message = new WorkerLMStudioJobMessage(
                WorkerMessageTypes.LMStudioJob,
                requestId,
                request);

            try
            {
                await workerRegistry.SendJobAsync(session, message, cancellationToken).ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutSource.Token,
                    cancellationToken);

                await using (linked.Token.Register(() => completionSource.TrySetCanceled(linked.Token)))
                {
                    return await completionSource.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                logger.LogWarning(ex, "Worker LM Studio attempt '{WorkerId}' (index {Index}) failed.", session.WorkerId, i);
            }
            finally
            {
                pendingLMStudio.TryRemove(requestId, out _);
                await session.MarkIdleAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AggregateException("All workers failed to execute LM Studio completion.", errors);
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
        OpenAIChatCompletionChainStreamTelemetryCapture capture,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workers = workerRegistry.GetAvailableWorkers();
        if (workers.Count == 0)
        {
            capture.TerminalHttpStatusCode = StatusCodes.Status503ServiceUnavailable;
            capture.TerminalError = "No workers are currently connected.";
            yield break;
        }

        List<OpenAIChatCompletionAttempt> attempts = [];
        for (int i = 0; i < workers.Count; i++)
        {
            var session = workers[i];
            var requestId = Guid.NewGuid().ToString("n");
            var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            streaming[requestId] = (channel, session);

            var message = new WorkerJobMessage(
                WorkerMessageTypes.ChatCompletionsJob,
                requestId,
                request);

            bool success = false;
            IAsyncEnumerator<StreamChunk>? wEnum = null;
            try
            {
                await workerRegistry.SendJobAsync(session, message, cancellationToken).ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

                var stream = channel.Reader.ReadAllAsync(linked.Token);
                wEnum = stream.GetAsyncEnumerator(linked.Token);

                bool moved;
                try
                {
                    moved = await wEnum.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 504, ex.Message, "worker"));
                    logger.LogWarning(ex, "Worker stream attempt '{WorkerId}' (index {Index}) timed out during startup.", session.WorkerId, i);
                    continue;
                }
                catch (JobFailedException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 502, ex.Message, "worker"));
                    logger.LogWarning(ex, "Worker stream attempt '{WorkerId}' (index {Index}) job failed to start.", session.WorkerId, i);
                    continue;
                }
                catch (Exception ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 500, ex.Message, "worker"));
                    logger.LogWarning(ex, "Worker stream attempt '{WorkerId}' (index {Index}) failed to start.", session.WorkerId, i);
                    continue;
                }

                if (moved is false)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 502, "Empty stream from worker.", "worker"));
                    continue;
                }

                if (wEnum.Current is { IsDone: true, Error: { Length: > 0 } workerStreamError })
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, false, 502, workerStreamError, "worker"));
                    continue;
                }

                attempts.Add(new OpenAIChatCompletionAttempt(session.WorkerId, i, true, 200, null, "worker"));
                capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForWinner(session.WorkerId, i, attempts);
                capture.ResponseModel = string.IsNullOrWhiteSpace(request.Model) is false ? request.Model : null;
                success = true;

                yield return wEnum.Current;
                while (await wEnum.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return wEnum.Current;
                }
                yield break;
            }
            finally
            {
                if (wEnum is not null)
                {
                    await wEnum.DisposeAsync().ConfigureAwait(false);
                }
                TryCompleteStreamChannel(requestId);
                streaming.TryRemove(requestId, out _);
                if (!success)
                {
                    await session.MarkIdleAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts);
    }

    public async IAsyncEnumerable<StreamChunk> EnqueueLMStudioAndStreamAsync(
        LMStudioChatRequest request,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workers = workerRegistry.GetAvailableWorkers();
        if (workers.Count == 0)
        {
            throw new NoWorkerAvailableException();
        }

        for (int i = 0; i < workers.Count; i++)
        {
            var session = workers[i];
            var requestId = Guid.NewGuid().ToString("n");
            var channel = Channel.CreateUnbounded<StreamChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            streaming[requestId] = (channel, session);

            var message = new WorkerLMStudioJobMessage(
                WorkerMessageTypes.LMStudioJob,
                requestId,
                request);

            bool success = false;
            IAsyncEnumerator<StreamChunk>? wEnum = null;
            try
            {
                await workerRegistry.SendJobAsync(session, message, cancellationToken).ConfigureAwait(false);

                using var timeoutSource = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

                var stream = channel.Reader.ReadAllAsync(linked.Token);
                wEnum = stream.GetAsyncEnumerator(linked.Token);

                bool moved;
                try
                {
                    moved = await wEnum.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Worker LM Studio stream attempt '{WorkerId}' (index {Index}) failed to start.", session.WorkerId, i);
                    continue;
                }

                if (moved is false)
                {
                    continue;
                }

                if (wEnum.Current is { IsDone: true, Error: { Length: > 0 } })
                {
                    continue;
                }

                success = true;
                yield return wEnum.Current;
                while (await wEnum.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return wEnum.Current;
                }
                yield break;
            }
            finally
            {
                if (wEnum is not null)
                {
                    await wEnum.DisposeAsync().ConfigureAwait(false);
                }
                TryCompleteStreamChannel(requestId);
                streaming.TryRemove(requestId, out _);
                if (!success)
                {
                    await session.MarkIdleAsync(cancellationToken).ConfigureAwait(false);
                }
            }
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
