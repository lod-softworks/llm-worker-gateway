using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Lod.LlmGateway.Gateway.Api;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyAuthorizer apiKeyAuthorizer)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? clientName = apiKeyAuthorizer.GetAuthorizedClientName(Context);
        if (clientName is null)
        {
            Logger.LogWarning("Client request connection rejected: missing or invalid API key header. Client provided key '{Provided}' does not match configured key '{Configured}'.",
                ApiKeyAuthorizer.ObfuscateKey(ApiKeyAuthorizer.GetApiKey(Context) ?? ""),
                string.Join(", ", apiKeyAuthorizer.ClientObfuscatedKeys));

            return AuthenticateResult.Fail("Missing or invalid API key.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.Name, clientName),
            new("client_name", clientName),
        ];
        ClaimsIdentity identity = new(claims, Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";

        await GatewayResults.WriteEndpointErrorAsync(Response.HttpContext, StatusCodes.Status401Unauthorized, "Missing or invalid API key.");
    }
}
