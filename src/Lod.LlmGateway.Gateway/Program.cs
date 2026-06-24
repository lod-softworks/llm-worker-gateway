using Lod.LlmGateway.Gateway.Api;
using Lod.LlmGateway.Gateway.Data;
using Lod.LlmGateway.Gateway.Handlers;
using Lod.LlmGateway.Gateway.Jobs;
using Lod.LlmGateway.Gateway.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAzureKeyVault();

builder.Services.Configure<Lod.LlmGateway.Gateway.Api.ApiKeyOptions>(
    builder.Configuration.GetSection("ApiKeys"));

string databaseProvider = builder.Configuration["Database:Provider"] ?? "SqlServer";
builder.Services.AddDbContext<GatewayDbContext>(options =>
{
    string? connectionString = builder.Configuration.GetConnectionString("Gateway");
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
    }
    else if (databaseProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString, c => c.MigrationsHistoryTable("__EFMigrationsHistory"));
    }
    else
    {
        throw new InvalidOperationException($"Unsupported database provider '{databaseProvider}'. Supported values are 'SqlServer' and 'Sqlite'.");
    }
});
builder.Services.AddScoped<OpenAIChatCompletionTelemetryWriter>();
builder.Services.AddScoped<LMStudioChatTelemetryWriter>();
builder.Services.AddSingleton<WorkerRegistry>();
builder.Services.AddSingleton<JobRouter>();
builder.Services.AddSingleton<ApiKeyAuthorizer>();
builder.Services.AddSingleton<WorkerWebSocketHandler>();
builder.Services.AddScoped<OpenAIChatCompletionHandler>();
builder.Services.AddScoped<OpenAIModelListHandler>();
builder.Services.AddScoped<LMStudioChatHandler>();
builder.Services.AddHostedService<WorkerHealthMonitor>();
builder.Services.AddHostedService<OpenAIChatCompletionDailyRollupWorker>();

builder.Services.AddAuthentication("ApiKey")
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null)
    .AddScheme<AuthenticationSchemeOptions, WorkerApiKeyAuthenticationHandler>("WorkerApiKey", null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("ApiKeyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("ApiKey");
        policy.RequireAuthenticatedUser();
    })
    .AddPolicy("WorkerApiKeyPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("WorkerApiKey");
        policy.RequireAuthenticatedUser();
    });

builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        if (!GatewayResults.IsSupportedApiPath(context.Request.Path))
        {
            return;
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await GatewayResults.WriteEndpointErrorAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "An unexpected server error occurred.");
    });
});

app.UseStatusCodePages(async statusContext =>
{
    HttpContext context = statusContext.HttpContext;
    if (!GatewayResults.IsSupportedApiPath(context.Request.Path))
    {
        return;
    }

    if (context.Response.HasStarted ||
        !string.IsNullOrWhiteSpace(context.Response.ContentType) ||
        context.Response.ContentLength is > 0)
    {
        return;
    }

    await GatewayResults.WriteEndpointErrorAsync(context, context.Response.StatusCode);
});

app.UseStaticFiles();
app.UseWebSockets();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("LLM Worker Gateway");
    });
}

app.MapRazorPages();

RouteGroupBuilder openAiGroup = app.MapGroup("/v1")
    .RequireAuthorization("ApiKeyPolicy")
    .WithTags("OpenAI");

openAiGroup.MapPost("/chat/completions", async (HttpContext context, OpenAIChatCompletionHandler handler, CancellationToken cancellationToken) =>
        await handler.HandleAsync(context, cancellationToken))
    .WithDescription("""
                     OpenAI-compatible chat completions endpoint. The gateway dispatches the request to available connected WebSocket workers. If multiple workers are connected and available, the gateway attempts execution sequentially until one succeeds.

                     The gateway deserializes the request into a full OpenAI-style chat-completions object, preserves JSON extension properties, and forwards the merged body to the worker. Any unsupported fields on a given upstream are rejected by that server, not stripped by the gateway.

                     Official OpenAI API documentation:
                     - https://platform.openai.com/docs/api-reference/chat

                     Gateway note: additional request properties not listed in the OpenAPI contract are still accepted at runtime and passed through when serializing the outbound request.
                     """)
    .Accepts<Lod.LlmGateway.Contracts.Models.OpenAI.ChatCompletionRequest>("application/json")
    .Produces<Lod.LlmGateway.Contracts.Models.OpenAI.ChatCompletionResponse>(StatusCodes.Status200OK, "application/json");

openAiGroup.MapGet("/models", async (HttpContext context, OpenAIModelListHandler handler, CancellationToken cancellationToken) =>
        await handler.HandleAsync(context, cancellationToken))
    .WithDescription("""
                    OpenAI-compatible model listing endpoint. The gateway queries available connected WebSocket workers and returns the first successful `/v1/models` response.
                    """)
    .Produces<Lod.LlmGateway.Contracts.Models.OpenAI.ModelListResponse>(StatusCodes.Status200OK, "application/json");

RouteGroupBuilder lmStudioGroup = app.MapGroup("/api/v1")
    .RequireAuthorization("ApiKeyPolicy")
    .WithTags("LM Studio");

lmStudioGroup.MapPost("chat", async (HttpContext context, LMStudioChatHandler handler, CancellationToken cancellationToken) =>
        await handler.HandleAsync(context, cancellationToken))
    .WithDescription("""
                     LM Studio v1-compatible chat completions endpoint. It relays requests to available connected workers.

                     Official LM Studio documentation:
                     - https://lmstudio.ai/docs/developer/rest/chat
                     - https://lmstudio.ai/docs/api/endpoints/rest

                     Gateway note: runtime request parsing is passthrough-first and may accept additional properties beyond the documented schema.
                     """)
    .Accepts<Lod.LlmGateway.Contracts.Models.OpenAI.ChatCompletionRequest>("application/json")
    .Produces<Lod.LlmGateway.Contracts.Models.OpenAI.ChatCompletionResponse>(StatusCodes.Status200OK, "application/json");

app.Map("/ws/worker", async (HttpContext context, WorkerWebSocketHandler handler, CancellationToken cancellationToken) =>
    await handler.HandleAsync(context, cancellationToken))
    .RequireAuthorization("WorkerApiKeyPolicy");

using (IServiceScope scope = app.Services.CreateScope())
{
    DbContext dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

    if (dbContext.Database.IsSqlite())
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
}

await app.RunAsync();
