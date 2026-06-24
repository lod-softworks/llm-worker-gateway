# Important Files

- `README.md`: human-focused project overview and local setup.
- `REQUIREMENTS.md`: product requirements and behavior source of truth.
- `Lod.LlmWorkerGateway.slnx`: solution structure for gateway, worker, contracts, and tests.
- `src/Lod.LlmGateway.Gateway/Program.cs`: application startup, authentication/authorization policy configuration, and endpoint registration.
- `src/Lod.LlmGateway.Gateway/Api/`: authentication handlers (`ApiKeyAuthenticationHandler.cs`, `WorkerApiKeyAuthenticationHandler.cs`), authorizer, and result helpers.
- `src/Lod.LlmGateway.Gateway/Data/`: Entity Framework Core database context, schemas for telemetry records, and daily rollup logic.
- `src/Lod.LlmGateway.Gateway/Handlers/`: request handlers for OpenAI chat completions, model lists, and LM Studio chat.
- `src/Lod.LlmGateway.Gateway/Jobs/`: job routing, streaming chunk management, and worker job completion/failover dispatcher.
- `src/Lod.LlmGateway.Gateway/Workers/`: worker registry, websocket connection handler, and worker registry health monitor.
- `src/Lod.LlmGateway.WorkerHost/`: Windows worker service and upstream REST client implementations.
- `src/Lod.LlmGateway.Contracts/`: shared contracts, JSON models, and message types exchanged between gateway and worker.
- `src/Lod.LlmGateway.Gateway.Tests/`: automated unit tests for authentication, payload builders, telemetry writers, and model resolution.
- `.github/workflows/pr-build-test.yml`: CI pipeline running restore, build, and test steps on pull request events.
