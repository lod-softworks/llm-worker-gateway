using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lod.LlmGateway.Contracts;

namespace Lod.LlmGateway.Gateway.Workers;

public sealed class WorkerRegistry
{
    readonly ConcurrentDictionary<string, WorkerSession> sessions = new();
    readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    public bool HasAnyConnectedWorkers => !sessions.IsEmpty;

    public int RegisteredWorkerCount => sessions.Count;

    public IEnumerable<string> ConnectedWorkerIds => sessions.Keys;

    public Task<WorkerSession> AcceptWorkerAsync(
        string workerId,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var session = new WorkerSession(workerId, socket);
        sessions[workerId] = session;
        return Task.FromResult(session);
    }

    public bool TryGetAvailableWorker(out WorkerSession? session)
    {
        foreach (var candidate in sessions.Values)
        {
            if (candidate.IsAvailable)
            {
                session = candidate;
                return true;
            }
        }

        session = null;
        return false;
    }

    public bool TryGetById(string workerId, out WorkerSession? session)
    {
        return sessions.TryGetValue(workerId, out session);
    }

    public void Remove(string workerId)
    {
        sessions.TryRemove(workerId, out _);
    }

    public async Task SendJobAsync(
        WorkerSession session,
        WorkerJobMessage message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, serializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        await session.MarkBusyAsync(cancellationToken);

        await session.Socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    public async Task SendJobAsync(
        WorkerSession session,
        WorkerLMStudioJobMessage message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, serializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        await session.MarkBusyAsync(cancellationToken);

        await session.Socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    public async Task SendJobAsync(
        WorkerSession session,
        WorkerModelListJobMessage message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, serializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        await session.MarkBusyAsync(cancellationToken);

        await session.Socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }
}

public sealed class WorkerSession
{
    public WorkerSession(string workerId, WebSocket socket)
    {
        WorkerId = workerId;
        Socket = socket;
    }

    public string WorkerId { get; }

    public WebSocket Socket { get; }

    public DateTimeOffset LastHeartbeat { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsBusy { get; private set; }

    public bool IsAvailable => Socket.State == WebSocketState.Open && IsBusy == false;

    public void MarkHeartbeat()
    {
        LastHeartbeat = DateTimeOffset.UtcNow;
    }

    public Task MarkBusyAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        return Task.CompletedTask;
    }

    public Task MarkIdleAsync(CancellationToken cancellationToken)
    {
        IsBusy = false;
        return Task.CompletedTask;
    }
}
