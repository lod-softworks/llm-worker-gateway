using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Jobs;
using Microsoft.Extensions.Options;

namespace Lod.LlmGateway.Gateway.Api;

public sealed class OpenAIModelListProviderService(
    ILogger<OpenAIModelListProviderService> logger,
    IOptions<OpenAIChatCompletionOptions> options,
    OpenAIModelListHttpExecutor httpExecutor,
    JobRouter jobRouter)
{
    public async Task<OpenAIModelListResponse> ListModelsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenAIChatCompletionProvider> providers = options.Value.Providers;
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No OpenAI providers are configured.");
        }

        Exception? lastError = null;
        for (int i = 0; i < providers.Count; i++)
        {
            OpenAIChatCompletionProvider provider = providers[i];
            try
            {
                if (provider.Source == OpenAIProviderSource.Worker)
                {
                    return await jobRouter.EnqueueModelListAndAwaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                }

                return await httpExecutor.ListModelsAsync(provider, cancellationToken).ConfigureAwait(false);
            }
            catch (NoWorkerAvailableException ex)
            {
                logger.LogWarning(ex, "OpenAI model-list provider step '{Name}' (Worker, index {Index}): no worker available.", provider.Name, i);
                lastError = ex;
            }
            catch (JobFailedException ex)
            {
                logger.LogWarning(ex, "OpenAI model-list provider step '{Name}' (Worker, index {Index}) failed.", provider.Name, i);
                lastError = ex;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "OpenAI model-list provider step '{Name}' (Api, index {Index}) failed.", provider.Name, i);
                lastError = ex;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OpenAI model-list provider step '{Name}' (index {Index}) failed.", provider.Name, i);
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "All OpenAI model-list provider steps failed.",
            lastError);
    }
}
