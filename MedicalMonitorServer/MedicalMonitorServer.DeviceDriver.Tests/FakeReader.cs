using System.Diagnostics;
using System.Net;
using System.Text;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// Stands in for <see cref="ComunicationService"/> so a link can be driven
/// without a monitor on the network. Each connection attempt runs the next
/// behaviour from the script; the last one repeats once the script runs out.
/// </summary>
/// <remarks>
/// <see cref="ReadTcpStreamAsync"/> deliberately mirrors the structure of the
/// real one — write side started alongside the read side, cancelled when
/// reading stops, both awaited — because that contract is what
/// <c>MonitorConnection</c> is written against. If the real implementation's
/// session lifecycle changes, this has to change with it.
/// </remarks>
internal sealed class FakeReader : IComunicationService
{
    private readonly Func<FakeSession, Task>[] _script;
    private readonly List<FakeSession> _sessions = new();
    private int _attempts;

    public FakeReader(params Func<FakeSession, Task>[] script)
    {
        if (script.Length == 0)
        {
            throw new ArgumentException("At least one session behaviour is required.", nameof(script));
        }

        _script = script;
    }

    /// <summary>Every connection attempt so far, in order.</summary>
    public IReadOnlyList<FakeSession> Sessions
    {
        get { lock (_sessions) { return _sessions.ToArray(); } }
    }

    /// <summary>How many times the connection has dialled the monitor.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public int MaxFrameSize => 1024 * 1024;

    public int Pending => 0;

    public List<byte[]> Feed(ReadOnlySpan<byte> data) => new();

    public void Reset() { }

    public async Task ReadTcpStreamAsync(
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        Func<IMonitorLink, CancellationToken, Task>? onConnected = null,
        CancellationToken cancellationToken = default)
    {
        int index = Interlocked.Increment(ref _attempts) - 1;
        var behaviour = _script[Math.Min(index, _script.Length - 1)];

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new FakeSession(index, ip, port, onMessageReceived, sessionCts.Token);
        lock (_sessions) { _sessions.Add(session); }

        Task reader;
        try
        {
            reader = behaviour(session);
        }
        catch (Exception ex)
        {
            reader = Task.FromException(ex);
        }

        var writer = onConnected?.Invoke(session.Link, sessionCts.Token);

        if (writer is null)
        {
            await reader;
            return;
        }

        await Task.WhenAny(reader, writer);
        sessionCts.Cancel();

        foreach (var side in new[] { reader, writer })
        {
            try
            {
                await side;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public Task ListenUdpBroadcastsAsync(
        int[] ports,
        Func<byte[], IPEndPoint, CancellationToken, Task> onDatagramReceived,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    // ------------------------------------------------------------ behaviours

    /// <summary>A stream that ends straight away without delivering anything.</summary>
    public static Func<FakeSession, Task> EndsImmediately => _ => Task.CompletedTask;

    /// <summary>A connection attempt that fails, as a refused or reset socket would.</summary>
    public static Func<FakeSession, Task> Fails =>
        _ => Task.FromException(new IOException("the monitor hung up"));

    /// <summary>A stream that stays open until the connection is cancelled.</summary>
    public static Func<FakeSession, Task> StaysOpen =>
        async session => await Task.Delay(Timeout.Infinite, session.Token);

    /// <summary>Delivers each message, then keeps the stream open.</summary>
    public static Func<FakeSession, Task> Delivers(params string[] messages) =>
        async session =>
        {
            foreach (var message in messages)
            {
                await session.DeliverAsync(message);
            }

            await Task.Delay(Timeout.Infinite, session.Token);
        };

    /// <summary>Delivers each message, then ends the stream as the monitor closing the link would.</summary>
    public static Func<FakeSession, Task> DeliversThenEnds(params string[] messages) =>
        async session =>
        {
            foreach (var message in messages)
            {
                await session.DeliverAsync(message);
            }
        };
}

/// <summary>One connection attempt: what was dialled, what was sent, and a way to deliver bytes back.</summary>
internal sealed class FakeSession
{
    private readonly Func<byte[], CancellationToken, Task> _onMessageReceived;

    public FakeSession(
        int index,
        string ip,
        int port,
        Func<byte[], CancellationToken, Task> onMessageReceived,
        CancellationToken token)
    {
        Index = index;
        Ip = ip;
        Port = port;
        _onMessageReceived = onMessageReceived;
        Token = token;
        Link = new RecordingLink();
    }

    /// <summary>Zero-based attempt number.</summary>
    public int Index { get; }

    public string Ip { get; }

    public int Port { get; }

    /// <summary>Cancelled when the reader stops, exactly as the real session token is.</summary>
    public CancellationToken Token { get; }

    /// <summary>Everything the connection sent during this session.</summary>
    public RecordingLink Link { get; }

    /// <summary>Hand one framed message's payload to the connection, as the reader would.</summary>
    public Task DeliverAsync(byte[] payload) => _onMessageReceived(payload, Token);

    public Task DeliverAsync(string payload) => DeliverAsync(Encoding.Latin1.GetBytes(payload));
}

/// <summary>Write side of a fake session; records instead of sending.</summary>
internal sealed class RecordingLink : IMonitorLink
{
    private readonly List<byte[]> _sent = new();

    public IReadOnlyList<byte[]> Sent
    {
        get { lock (_sent) { return _sent.ToArray(); } }
    }

    /// <summary>What was sent, decoded, so tests can compare against the HL7 text.</summary>
    public IReadOnlyList<string> SentText =>
        Sent.Select(payload => Encoding.Latin1.GetString(payload)).ToArray();

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sent) { _sent.Add(payload.ToArray()); }
        return Task.CompletedTask;
    }
}

/// <summary>Polls a condition so a test never waits longer than it has to, nor hangs if it never holds.</summary>
internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Waited {timeoutMs}ms for: {because}.");
    }
}
