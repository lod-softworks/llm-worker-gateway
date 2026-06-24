using Lod.LlmGateway.Gateway.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Lod.LlmGateway.Gateway.Tests;

public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task ClientHandler_HandleAuthenticateAsync_ReturnsSuccess_WhenKeyIsValid()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Options.Create(new ApiKeyOptions
        {
            Clients = new Dictionary<string, string> { ["client-1"] = "valid-client-key" }
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Request.Headers["X-Api-Key"] = "valid-client-key";

        ApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("client-1", result.Principal.Identity?.Name);
        Assert.Equal("client-1", result.Principal.FindFirst("client_name")?.Value);
    }

    [Fact]
    public async Task ClientHandler_HandleAuthenticateAsync_ReturnsFailure_WhenKeyIsInvalid()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Options.Create(new ApiKeyOptions
        {
            Clients = new Dictionary<string, string> { ["client-1"] = "valid-client-key" }
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Request.Headers["X-Api-Key"] = "invalid-client-key";

        ApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("Missing or invalid API key.", result.Failure?.Message);
    }

    [Fact]
    public async Task ClientHandler_ChallengeAsync_WritesOpenAiErrorResponse()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Microsoft.Extensions.Options.Options.Create(new ApiKeyOptions
        {
            Clients = new Dictionary<string, string> { ["client-1"] = "valid-client-key" }
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Request.Path = "/v1/chat/completions";
        context.Response.Body = new MemoryStream();

        // Setup dependency injection for context (needed for WriteEndpointErrorAsync)
        ServiceCollection services = new();
        services.AddLogging();
        services.ConfigureHttpJsonOptions(_ => { });
        context.RequestServices = services.BuildServiceProvider();

        ApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using (StreamReader reader = new(context.Response.Body))
        {
            string body = await reader.ReadToEndAsync();
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                Assert.Equal("Missing or invalid API key.", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
                Assert.Equal("authentication_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
            }
        }
    }

    [Fact]
    public async Task WorkerHandler_HandleAuthenticateAsync_ReturnsSuccess_WhenKeyIsValid()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Microsoft.Extensions.Options.Options.Create(new ApiKeyOptions
        {
            WorkerKey = "valid-worker-key"
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Request.Headers["X-Api-Key"] = "valid-worker-key";

        WorkerApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("WorkerApiKey", "WorkerApiKey", typeof(WorkerApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("Worker", result.Principal.Identity?.Name);
    }

    [Fact]
    public async Task WorkerHandler_HandleAuthenticateAsync_ReturnsFailure_WhenKeyIsInvalid()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Microsoft.Extensions.Options.Options.Create(new ApiKeyOptions
        {
            WorkerKey = "valid-worker-key"
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Request.Headers["X-Api-Key"] = "invalid-worker-key";

        WorkerApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("WorkerApiKey", "WorkerApiKey", typeof(WorkerApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal("Missing or invalid Worker API key.", result.Failure?.Message);
    }

    [Fact]
    public async Task WorkerHandler_ChallengeAsync_SetsUnauthorizedStatusCode()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Microsoft.Extensions.Options.Options.Create(new ApiKeyOptions
        {
            WorkerKey = "valid-worker-key"
        });
        ApiKeyAuthorizer authorizer = new(options);

        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions = new MockOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        WorkerApiKeyAuthenticationHandler handler = new(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authorizer);

        AuthenticationScheme scheme = new("WorkerApiKey", "WorkerApiKey", typeof(WorkerApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        // Act
        await handler.ChallengeAsync(new AuthenticationProperties());

        // Assert
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("", "some-key")]
    [InlineData("   ", "some-key")]
    [InlineData("valid-key", "")]
    [InlineData("valid-key", "   ")]
    [InlineData("valid-key", null)]
    [InlineData("valid-key", "other-key")]
    public void Authorizer_IsAuthorized_ReturnsFalse_WhenKeysAreInvalidOrEmpty(string? configuredKey, string? providedKey)
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Options.Create(new ApiKeyOptions
        {
            WorkerKey = configuredKey,
            Clients = new Dictionary<string, string> { ["client-1"] = configuredKey ?? "" }
        });
        ApiKeyAuthorizer authorizer = new(options);

        DefaultHttpContext context = new();
        if (providedKey != null)
        {
            context.Request.Headers["X-Api-Key"] = providedKey;
        }

        // Act & Assert
        Assert.False(authorizer.IsWorkerAuthorized(context));
        Assert.False(authorizer.IsClientAuthorized(context));
    }

    [Fact]
    public void Authorizer_IsWorkerAuthorized_Throws_WhenWorkerKeyIsNull()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Options.Create(new ApiKeyOptions
        {
            WorkerKey = null
        });
        ApiKeyAuthorizer authorizer = new(options);
        DefaultHttpContext context = new();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => authorizer.IsWorkerAuthorized(context));
    }

    [Fact]
    public void Authorizer_IsAuthorized_ReturnsTrue_WhenKeysMatch()
    {
        // Arrange
        IOptions<ApiKeyOptions> options = Options.Create(new ApiKeyOptions
        {
            WorkerKey = "matching-key",
            Clients = new Dictionary<string, string> { ["client-1"] = "matching-key" }
        });
        ApiKeyAuthorizer authorizer = new(options);

        DefaultHttpContext context = new();
        context.Request.Headers["X-Api-Key"] = "matching-key";

        // Act & Assert
        Assert.True(authorizer.IsWorkerAuthorized(context));
        Assert.True(authorizer.IsClientAuthorized(context));
    }

    sealed class MockOptionsMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue { get; } = currentValue;
        public TOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<TOptions, string?> listener) => EmptyDisposable.Instance;
    }

    sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() {}
    }
}
