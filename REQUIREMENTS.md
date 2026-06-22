# Product Requirements Document
## LLM Worker Gateway

## 1) Product Overview

LLM Worker Gateway is a brokered inference system made of an ASP.NET Core gateway API and a Windows worker service. The gateway exposes OpenAI-compatible and LM Studio-compatible HTTP endpoints, accepts outbound worker WebSocket connections, dispatches jobs to available workers, and records routing telemetry. The worker runs near local or remote model APIs and executes jobs received from the gateway.

The gateway may also route OpenAI-compatible requests to direct API providers as configured, but this repository is centered on the worker plus the gateway API that coordinates it.

## 2) Problem Statement

Teams want OpenAI/LM Studio-compatible endpoints while keeping model execution on private Windows hosts or private network segments. Directly exposing those hosts creates operational and security risk. This product keeps workers outbound-only: the gateway is public-facing, and workers initiate authenticated WebSocket connections to receive jobs.

## 3) Goals

- Provide OpenAI-compatible `POST /v1/chat/completions` behavior for non-streaming and streaming chat.
- Provide OpenAI-compatible `GET /v1/models`.
- Provide LM Studio-compatible `POST /api/v1/chat`.
- Host `/ws/worker` for authenticated worker WebSocket connections.
- Provide a Windows worker service that connects outbound to the gateway.
- Execute worker jobs against OpenAI-compatible and/or LM Studio REST endpoints.
- Route OpenAI chat requests through ordered `OpenAIChatCompletions:Providers` chains.
- Persist gateway telemetry for request outcomes, provider attempts, usage, and daily rollups.
- Use standard .NET and Azure configuration packages; do not depend on private `Lod.*` NuGet packages.
- Load Azure Key Vault configuration only when configured.

## 4) Non-Goals

- Durable queueing across gateway restarts.
- Multi-region orchestration or active-active gateway clustering.
- API parity beyond the currently implemented chat and model-list surfaces.
- Tenant billing, quotas, or advanced scheduling.
- Exposing inbound ports on worker machines.

## 5) Primary Users

- **API Client Integrator**: points OpenAI/LM Studio-compatible clients at the gateway.
- **Platform Operator**: deploys and configures gateway keys, provider chains, database, and Key Vault.
- **Worker Operator**: installs and runs the Windows worker service near model APIs.

## 6) Components

1. **Gateway API (`src/Lod.LlmGateway.Gateway`)**
   - Hosts client HTTP endpoints, worker WebSocket ingress, dashboard UI, provider routing, failover, and telemetry persistence.
2. **Windows Worker (`src/Lod.LlmGateway.WorkerHost`)**
   - Maintains outbound WebSocket connectivity to the gateway and executes model requests through local or remote upstream APIs.
3. **Shared Contracts (`src/Lod.LlmGateway.Contracts`)**
   - Contains OpenAI/LM Studio HTTP models and gateway/worker message contracts.
4. **Tests (`src/Lod.LlmGateway.Gateway.Tests`)**
   - Covers request payload construction, provider matching, gateway error results, telemetry helpers, and model resolution behavior.

## 7) Runtime Flow

1. Worker connects to `/ws/worker` with the configured worker API key.
2. Client sends a request with a configured client API key.
3. Gateway validates authorization and parses the request.
4. For OpenAI chat, gateway matches the requested model against `OpenAIChatCompletions:Providers`.
5. Worker-backed provider steps dispatch jobs to an available connected worker; direct `Api` steps call configured OpenAI-compatible HTTP providers.
6. Worker calls its configured upstream model API and returns a response or stream chunks.
7. Gateway returns the first successful response to the client and records telemetry for all attempts.

## 8) Functional Requirements

### FR-1: Client API Endpoints

The gateway must expose:

- `POST /v1/chat/completions`
- `GET /v1/models`
- `POST /api/v1/chat`

Chat endpoints must support non-streaming responses and streaming `text/event-stream` responses with terminal `[DONE]` frames.

### FR-2: Worker Connectivity

- Workers connect using outbound WebSocket to `/ws/worker`.
- The gateway validates `ApiKeys:WorkerKey` before accepting worker jobs.
- Workers send heartbeat messages; stale workers are removed by the health monitor.
- The worker reconnects with backoff when the gateway connection is interrupted.

### FR-3: Worker Execution

- The worker must support OpenAI-compatible chat completion jobs.
- The worker must support OpenAI-compatible model list jobs.
- The worker must support LM Studio native chat jobs.
- Worker preferred model settings override inbound request models when configured.
- Worker streaming jobs must forward chunk data and terminal completion or error messages to the gateway.

### FR-4: OpenAI Provider Routing

- `OpenAIChatCompletions:Providers` is an ordered provider array.
- Each provider includes `Name`, `Source` (`Api` or `Worker`), optional `Models`, optional `AcceptAnyModel`, optional `IgnoreProviderPrefix`, and optional `CopyMaxCompletionTokensToMaxTokens`.
- `Api` providers require `BaseUrl` plus either `AuthToken` or custom `Headers`.
- `Worker` providers require `Name` and either `AcceptAnyModel` or at least one model.
- Matching providers are attempted in configured order until one succeeds.
- A direct `Api` provider calls `{BaseUrl}/v1/chat/completions` or `{BaseUrl}/v1/models`.
- A `Worker` provider dispatches to an available connected worker.

### FR-5: Error and Timeout Behavior

- Unauthorized client calls return `401`.
- Invalid payloads return deterministic `400` JSON errors.
- If no provider matches an OpenAI chat model, the gateway returns `400`.
- If all matching providers fail, the gateway returns an endpoint-native error response.
- If no worker is available for a worker-backed request, the gateway returns a clear service-unavailable style error.
- Streaming errors are emitted as endpoint-native SSE `data:` error frames before stream completion.

### FR-6: Telemetry

- Gateway telemetry is persisted through EF Core.
- Required records include OpenAI chat requests, OpenAI non-stream results, OpenAI stream results, LM Studio chat results, provider-chain attempts, model resolution, and daily rollups.
- Telemetry must identify requested model, served model when available, winning provider, winning provider index, provider source, attempt success/failure, client identity, token usage, cost metadata when present, and timestamps.
- A hosted rollup worker aggregates OpenAI chat telemetry by UTC day and client.

### FR-7: Configuration

The gateway must support:

- `ApiKeys:WorkerKey`
- `ApiKeys:Clients`
- Client API key transport through `X-Api-Key`, `AuthToken`, `Authorization`, or `apiKey` query parameter
- `OpenAIChatCompletions:Providers`
- `Database:Provider` (`SqlServer` or `Sqlite`)
- `ConnectionStrings:Gateway`
- Optional `AzureKeyVault:VaultUri` or `KeyVault:VaultUri`

The worker must support:

- `Gateway:WorkerWebSocketUrl`
- `Gateway:WorkerApiKey`
- `OpenAIChatCompletions:BaseUrl`
- `OpenAIChatCompletions:AuthToken`
- Optional `OpenAIChatCompletions:PreferredModel`
- `LMStudioRest:BaseUrl`
- `LMStudioRest:AuthToken`
- Optional `LMStudioRest:PreferredModel`

When a gateway Key Vault URI is configured, the gateway must load Azure Key Vault secrets using `DefaultAzureCredential`. When no URI is configured, Key Vault must not be loaded.

### FR-8: Developer Experience

- Root `/` displays the dashboard and connected worker count.
- Development mode exposes OpenAPI JSON and Scalar API UI.
- Local gateway development defaults should run with SQLite.

## 9) Non-Functional Requirements

- **Security**: shared-key authentication for clients and workers; secrets may come from app configuration or Azure Key Vault.
- **Reliability**: provider-chain failover, worker reconnect behavior, and stale worker cleanup.
- **Compatibility**: preserve OpenAI and LM Studio request/response compatibility for implemented endpoints.
- **Operability**: persist telemetry and emit structured logs for routing, authorization, dispatch, worker execution, and failure paths.

## 10) Implementation Status

Implemented:

- Gateway API endpoints for OpenAI chat, OpenAI model list, and LM Studio chat.
- Worker WebSocket ingress, registry, health monitoring, and in-memory pending jobs.
- Windows worker host with upstream OpenAI-compatible and LM Studio REST clients.
- Provider-chain routing across worker and direct API providers.
- Streaming and non-streaming chat handling.
- EF Core telemetry and daily rollups.
- Optional Azure Key Vault configuration through standard Azure packages.
- Unit tests for core gateway behaviors.

Known gaps:

- No durable pending-job persistence.
- No tenant quotas or rate limiting.
- No distributed worker registry.
- No comprehensive external metrics backend.

## 11) Acceptance Criteria

1. `dotnet test` passes for the solution.
2. The solution includes gateway, Windows worker, shared contracts, and gateway tests.
3. No project references private `Lod.*` NuGet packages.
4. With a configured Key Vault URI, the gateway adds Azure Key Vault as a configuration source.
5. Without a configured Key Vault URI, the gateway runs without Azure Key Vault authentication.
6. Authorized OpenAI chat requests route through matching provider chains and fail over across failed attempts.
7. Unauthorized clients and workers are rejected.
8. Dashboard shows connected worker count.
9. A configured worker can connect outbound to the gateway and execute OpenAI-compatible or LM Studio jobs.

## 12) PRD Ownership

This document is the source of truth for worker gateway scope and behavior. Update it with any change to worker connectivity, endpoints, routing semantics, configuration, security, telemetry, or deployment.
