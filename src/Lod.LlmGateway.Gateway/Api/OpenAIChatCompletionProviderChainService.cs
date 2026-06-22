using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Data;
using Lod.LlmGateway.Gateway.Jobs;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.CompilerServices;

namespace Lod.LlmGateway.Gateway.Api;

public sealed class OpenAIChatCompletionProviderChainService(
    ILogger<OpenAIChatCompletionProviderChainService> logger,
    IOptions<OpenAIChatCompletionOptions> options,
    OpenAIChatCompletionHttpExecutor httpExecutor,
    JobRouter jobRouter)
{
    public IReadOnlyList<OpenAIChatCompletionProvider> BuildMatchingChain(string? requestModel) =>
        OpenAIChatCompletionProviderMatcher.BuildMatchingChain(requestModel, options.Value.Providers);

    public async Task<OpenAIChatCompletionNonStreamResult> TryRunChainAsync(
        ChatCompletionRequest request,
        IReadOnlyList<OpenAIChatCompletionProvider> chain,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chain.Count, 1);

        List<OpenAIChatCompletionAttempt> attempts = [];
        for (int i = 0; i < chain.Count; i++)
        {
            OpenAIChatCompletionProvider step = chain[i];
            const string sourceWorker = "worker";
            const string sourceApi = "api";
            if (step.Source == OpenAIProviderSource.Worker)
            {
                try
                {
                    ChatCompletionResponse response = await jobRouter
                        .EnqueueAndAwaitAsync(request, timeout, cancellationToken)
                        .ConfigureAwait(false);
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, true, 200, null, sourceWorker));
                    return new OpenAIChatCompletionNonStreamResult(
                        true,
                        response,
                        OpenAIChatCompletionChainTelemetry.ForWinner(step.Name, i, attempts),
                        WinningSource: step.Source);
                }
                catch (NoWorkerAvailableException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 503, ex.Message, sourceWorker));
                    logger.LogWarning(ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): no worker available.", step.Name, i);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 504, ex.Message, sourceWorker));
                    logger.LogWarning(ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): cancelled or timed out.", step.Name, i);
                }
                catch (JobFailedException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 502, ex.Message, sourceWorker));
                    logger.LogWarning(ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): job failed.", step.Name, i);
                }
            }
            else
            {
                try
                {
                    ChatCompletionResponse response = await httpExecutor
                        .CreateChatCompletionAsync(step, request, cancellationToken)
                        .ConfigureAwait(false);
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, true, 200, null, sourceApi));
                    return new OpenAIChatCompletionNonStreamResult(
                        true,
                        response,
                        OpenAIChatCompletionChainTelemetry.ForWinner(step.Name, i, attempts),
                        WinningSource: step.Source);
                }
                catch (HttpRequestException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(
                        step.Name, i, false, (int?)ex.StatusCode, ex.Message, sourceApi));
                    logger.LogWarning(ex, "OpenAI provider chain step '{Name}' (Api, index {Index}) failed.", step.Name, i);
                    if (ex.StatusCode == HttpStatusCode.BadRequest)
                    {
                        return new OpenAIChatCompletionNonStreamResult(
                            false,
                            null,
                            OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts),
                            (int)HttpStatusCode.BadRequest,
                            ex.Message);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, null, ex.Message, sourceApi));
                    logger.LogWarning(ex, "OpenAI provider chain step '{Name}' (Api, index {Index}) failed.", step.Name, i);
                }
            }
        }
        return new OpenAIChatCompletionNonStreamResult(false, null, OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts));
    }

    public async IAsyncEnumerable<StreamChunk> StreamWithChainAsync(
        IReadOnlyList<OpenAIChatCompletionProvider> chain,
        ChatCompletionRequest request,
        OpenAIChatCompletionChainStreamTelemetryCapture capture,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chain.Count, 1);

        List<OpenAIChatCompletionAttempt> attempts = [];
        for (int i = 0; i < chain.Count; i++)
        {
            OpenAIChatCompletionProvider step = chain[i];
            if (step.Source == OpenAIProviderSource.Worker)
            {
                IAsyncEnumerable<StreamChunk> stream = jobRouter.EnqueueAndStreamAsync(
                    request,
                    timeout,
                    cancellationToken);
                await using IAsyncEnumerator<StreamChunk> wEnum = stream.GetAsyncEnumerator(cancellationToken);
                bool moved;
                try
                {
                    moved = await wEnum.MoveNextAsync();
                }
                catch (NoWorkerAvailableException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 503, ex.Message, "worker"));
                    logger.LogWarning(
                        ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): no worker available.", step.Name, i);
                    continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 504, ex.Message, "worker"));
                    logger.LogWarning(
                        ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): cancelled or timed out.", step.Name, i);
                    continue;
                }
                catch (JobFailedException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 502, ex.Message, "worker"));
                    logger.LogWarning(
                        ex, "OpenAI provider chain step '{Name}' (Worker, index {Index}): job failed.", step.Name, i);
                    continue;
                }

                if (moved is false)
                {
                    continue;
                }

                if (wEnum.Current is { IsDone: true, Error: { Length: > 0 } workerStreamError })
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, 502, workerStreamError, "worker"));
                    continue;
                }

                for (;;)
                {
                    yield return wEnum.Current;
                    if (await wEnum.MoveNextAsync() is false)
                    {
                        break;
                    }
                }
                attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, true, 200, null, "worker"));
                capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForWinner(step.Name, i, attempts);
                capture.WinningSource = OpenAIProviderSource.Worker;
                capture.ResponseModel = ResolveResponseModel(step);
                yield break;
            }
            else
            {
                IAsyncEnumerable<string> single = httpExecutor.CreateChatCompletionStreamAsync(
                    step,
                    request,
                    cancellationToken);
                await using IAsyncEnumerator<string> enumerator = single.GetAsyncEnumerator(cancellationToken);
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (HttpRequestException ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(
                        step.Name, i, false, (int?)ex.StatusCode, ex.Message, "api"));
                    logger.LogWarning(
                        ex, "OpenAI provider chain step '{Name}' (Api, index {Index}) failed while opening stream.", step.Name, i);
                    if (ex.StatusCode == HttpStatusCode.BadRequest)
                    {
                        capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts);
                        capture.TerminalHttpStatusCode = (int)HttpStatusCode.BadRequest;
                        capture.TerminalError = ex.Message;
                        yield break;
                    }

                    continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, false, null, ex.Message, "api"));
                    logger.LogWarning(
                        ex, "OpenAI provider chain step '{Name}' (Api, index {Index}) failed while opening stream.", step.Name, i);
                    continue;
                }

                if (moved is false)
                {
                    attempts.Add(new OpenAIChatCompletionAttempt(
                        step.Name, i, false, null, "Empty stream from provider.", "api"));
                    continue;
                }

                attempts.Add(new OpenAIChatCompletionAttempt(step.Name, i, true, 200, null, "api"));
                capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForWinner(step.Name, i, attempts);
                capture.WinningSource = OpenAIProviderSource.Api;
                capture.ResponseModel = ResolveResponseModel(step);

                yield return new StreamChunk(false, null, enumerator.Current);
                while (await enumerator.MoveNextAsync())
                {
                    yield return new StreamChunk(false, null, enumerator.Current);
                }

                yield break;
            }
        }
        capture.Telemetry = OpenAIChatCompletionChainTelemetry.ForAllFailed(attempts);
    }

    static string? ResolveResponseModel(OpenAIChatCompletionProvider provider) =>
        string.IsNullOrWhiteSpace(provider.Model) is false ? provider.Model : null;
}
