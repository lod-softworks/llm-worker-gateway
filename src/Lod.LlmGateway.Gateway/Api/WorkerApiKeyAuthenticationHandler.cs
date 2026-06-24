using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Lod.LlmGateway.Gateway.Api;

public sealed class WorkerApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyAuthorizer apiKeyAuthorizer)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!apiKeyAuthorizer.IsWorkerAuthorized(Context))
        {
            Logger.LogWarning("Worker WebSocket connection rejected: missing or invalid X-Api-Key. Worker provided key '{Provided}' does not match configured key '{Configured}'.",
                ApiKeyAuthorizer.ObfuscateKey(ApiKeyAuthorizer.GetApiKey(Context) ?? ""),
                apiKeyAuthorizer.WorkerObfuscatedKey);

            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid Worker API key."));
        }

        Claim[] claims = [
            new Claim(ClaimTypes.Name, "Worker"),
            new Claim("client_name", "Worker")
        ];
        ClaimsIdentity identity = new(claims, Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
