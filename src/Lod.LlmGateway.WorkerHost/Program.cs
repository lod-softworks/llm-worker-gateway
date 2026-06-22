using Lod.LlmGateway.WorkerHost;
using Lod.LlmGateway.WorkerHost.Clients;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Lod LLM Gateway Worker Host";
});

builder.Services.AddMemoryCache();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHttpClient<OpenAIChatCompletionClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["OpenAIChatCompletions:BaseUrl"] ?? throw new InvalidOperationException("OpenAIChatCompletions:BaseUrl is not configured."));
    if (builder.Configuration["OpenAIChatCompletions:AuthToken"] is string authToken && !string.IsNullOrWhiteSpace(authToken))
    {
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");
    }
});
builder.Services.AddHttpClient<LMStudioClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["LMStudioRest:BaseUrl"] ?? throw new InvalidOperationException("LMStudioRest:BaseUrl is not configured."));
    if (builder.Configuration["LMStudioRest:AuthToken"] is string authToken && !string.IsNullOrWhiteSpace(authToken))
    {
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {authToken}");
    }
});

var host = builder.Build();

await host.RunAsync();
