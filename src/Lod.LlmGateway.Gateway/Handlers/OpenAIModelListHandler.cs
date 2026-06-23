using Lod.LlmGateway.Contracts.Models.OpenAI;
using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Jobs;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Handlers;

public sealed class OpenAIModelListHandler(
    JobRouter jobRouter,
    ApiKeyAuthorizer apiKeyAuthorizer)
{
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<IResult> HandleAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!apiKeyAuthorizer.IsClientAuthorized(httpContext))
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status401Unauthorized,
                "Missing or invalid API key.",
                type: "authentication_error");
        }

        try
        {
            JsonElement result = await jobRouter.EnqueueModelListAndAwaitAsync(
                DefaultTimeout,
                cancellationToken);
            return Results.Json(result, statusCode: StatusCodes.Status200OK, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            int statusCode = StatusCodes.Status502BadGateway;
            if (exception is NoWorkerAvailableException)
            {
                statusCode = StatusCodes.Status503ServiceUnavailable;
            }
            else if (exception is HttpRequestException { StatusCode: not null } httpException)
            {
                statusCode = (int)httpException.StatusCode!.Value;
            }

            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? "Unable to list models from any OpenAI provider."
                : exception.Message;

            return GatewayResults.OpenAIError(
                statusCode,
                message,
                type: statusCode >= 500 ? "server_error" : "gateway_error");
        }
    }
}
