using MedicalMonitorServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace MedicalMonitorServer.Tests;

/// <summary>
/// Stands in for the SignalR hub: records every broadcast instead of sending
/// it, so a test can assert what the stations would have been told.
/// </summary>
internal sealed class FakeHubContext : IHubContext<VitalsHub>
{
    private readonly RecordingClients _clients = new();

    public IHubClients Clients => _clients;

    public IGroupManager Groups => throw new NotSupportedException("The pipeline does not use groups.");

    /// <summary>Every broadcast so far, in order: the hub method and its single payload.</summary>
    public IReadOnlyList<(string Method, object? Payload)> Sent => _clients.Proxy.Sent;

    /// <summary>The payloads sent to one hub method, typed.</summary>
    public IReadOnlyList<T> SentTo<T>(string method) =>
        Sent.Where(s => s.Method == method).Select(s => (T)s.Payload!).ToList();

    private sealed class RecordingClients : IHubClients
    {
        public RecordingProxy Proxy { get; } = new();

        public IClientProxy All => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    internal sealed class RecordingProxy : IClientProxy
    {
        private readonly List<(string Method, object? Payload)> _sent = new();

        public IReadOnlyList<(string Method, object? Payload)> Sent
        {
            get { lock (_sent) { return _sent.ToArray(); } }
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            lock (_sent) { _sent.Add((method, args.Length > 0 ? args[0] : null)); }
            return Task.CompletedTask;
        }
    }
}
