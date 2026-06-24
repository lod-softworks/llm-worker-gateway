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

    public async Task<WorkerSession> AcceptWorkerAsync(
        string workerId,
        WebSocket socket,
        CancellationToken _ = default)
    {
        var session = new WorkerSession(workerId, socket);
        sessions[workerId] = session;

        return session;
    }

    public IReadOnlyList<WorkerSession> GetAvailableWorkers()
    {
        return sessions.Values.Where(s => s.IsAvailable).ToList();
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

public sealed class WorkerSession(string workerId, WebSocket socket)
{
    public string WorkerId { get; } = workerId;

    public WebSocket Socket { get; } = socket;

    public DateTimeOffset LastHeartbeat { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsBusy { get; private set; }

    public bool IsAvailable => Socket.State == WebSocketState.Open && IsBusy == false;

    public void MarkHeartbeat()
    {
        LastHeartbeat = DateTimeOffset.UtcNow;
    }

    public async Task MarkBusyAsync(CancellationToken _ = default)
    {
        IsBusy = true;
    }

    public async Task MarkIdleAsync(CancellationToken _ = default)
    {
        IsBusy = false;
    }
}
