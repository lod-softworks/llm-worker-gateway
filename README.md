# LLM Worker Gateway

LLM Worker Gateway contains the Windows worker service and the gateway API that coordinates connected workers. The gateway accepts OpenAI-compatible and LM Studio-compatible client requests and routes work to connected workers.

This repository owns:

- The ASP.NET Core worker gateway API.
- The Windows worker service.
- Shared gateway/worker message and HTTP contracts.
- Worker connection health, job dispatch, routing telemetry, and dashboard UI.

## What It Provides

- OpenAI-compatible `POST /v1/chat/completions`.
- OpenAI-compatible `GET /v1/models`.
- LM Studio-compatible `POST /api/v1/chat`.
- Worker WebSocket endpoint at `/ws/worker`.
- Windows worker host that connects outbound to the gateway and calls local or remote model APIs.
- Sequential job dispatch and failover across available connected workers.
- EF Core telemetry for request attempts, winning workers, usage, and daily rollups.
- Development OpenAPI and Scalar UI.

## Repository Layout

- `src/Lod.LlmGateway.Gateway` - ASP.NET Core gateway API, dashboard, WebSocket worker ingress, routing, failover, and telemetry.
- `src/Lod.LlmGateway.WorkerHost` - Windows worker service that connects to the gateway and executes model requests.
- `src/Lod.LlmGateway.Contracts` - shared HTTP models and gateway/worker message contracts.
- `src/Lod.LlmGateway.Gateway.Tests` - gateway unit tests.
- `REQUIREMENTS.md` - living product requirements and behavior source of truth.

## Configuration

Gateway settings:

- `ApiKeys:WorkerKey` - shared key accepted by worker WebSocket connections.
- `ApiKeys:Clients` - client key dictionary used for API request authorization.
- `Database:Provider` - `SqlServer` or `Sqlite`.
- `ConnectionStrings:Gateway` - telemetry database connection string.
- Optional `AzureKeyVault:VaultUri` or `KeyVault:VaultUri`.

Worker settings:

- `Gateway:WorkerWebSocketUrl`
- `Gateway:WorkerApiKey`
- `OpenAIChatCompletions:BaseUrl`
- `OpenAIChatCompletions:AuthToken`
- Optional `OpenAIChatCompletions:PreferredModel`
- `LMStudioRest:BaseUrl`
- `LMStudioRest:AuthToken`
- Optional `LMStudioRest:PreferredModel`

Azure Key Vault is optional for the gateway. When a vault URI is configured, the gateway extends configuration from that vault using `DefaultAzureCredential`.

## Local Development

Prerequisites:

- .NET 10 SDK
- SQL Server or SQLite for gateway telemetry persistence
- A local or remote OpenAI-compatible endpoint and/or LM Studio endpoint for worker execution

Run the gateway:

```bash
dotnet run --project src/Lod.LlmGateway.Gateway/Lod.LlmGateway.Gateway.csproj
```

Run the worker:

```bash
dotnet run --project src/Lod.LlmGateway.WorkerHost/Lod.LlmGateway.WorkerHost.csproj
```

Run tests:

```bash
dotnet test
```

Development gateway defaults use SQLite at `src/Lod.LlmGateway.Gateway/App_Data/Llm.Gateway.db` and routes client requests to available connected workers.

## Documentation Maintenance

Keep `README.md` high-level and human-focused. Update `REQUIREMENTS.md` whenever worker connectivity, endpoint contracts, configuration, routing, telemetry, deployment, or security behavior changes.
