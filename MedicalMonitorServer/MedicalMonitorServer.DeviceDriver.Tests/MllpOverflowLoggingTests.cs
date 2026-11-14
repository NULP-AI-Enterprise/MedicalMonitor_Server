using MedicalMonitorServer.DeviceDriver;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver.Tests;

/// <summary>
/// The frame reader drops its buffer when a frame never terminates. That is
/// data loss, so it must not happen quietly: these tests pin the warning and,
/// just as importantly, the silence on healthy traffic.
/// </summary>
public class MllpOverflowLoggingTests
{
    private const byte VT = 0x0B;

    /// <summary>Small enough that a test can reach the limit; the driver ships with 1 MiB.</summary>
    private const int TinyFrameLimit = 64;

    [Fact]
    public void Overflow_logs_a_warning_naming_the_dropped_bytes_and_the_limit()
    {
        var logger = new CapturingLogger();
        var service = new ComunicationService(logger) { MaxFrameSize = TinyFrameLimit };

        service.Feed(Unterminated(200));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("201", entry.Message);              // the VT plus 200 bytes
        Assert.Contains(TinyFrameLimit.ToString(), entry.Message);
        Assert.Contains("overflow", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Overflow_is_reported_once_per_drop_not_once_per_read()
    {
        var logger = new CapturingLogger();
        var service = new ComunicationService(logger) { MaxFrameSize = TinyFrameLimit };

        service.Feed(Unterminated(200));
        service.Feed(Unterminated(200));

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
    }

    [Fact]
    public void A_healthy_stream_logs_nothing()
    {
        var logger = new CapturingLogger();
        var service = new ComunicationService(logger) { MaxFrameSize = TinyFrameLimit };

        // Whole frames, a split frame, and noise between them: all routine.
        var wire = ComunicationService.Frame("first"u8);
        service.Feed(wire);
        service.Feed(wire.AsSpan(0, 3));
        service.Feed(wire.AsSpan(3));
        service.Feed("junk with no start byte"u8);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void A_frame_that_is_merely_large_is_not_reported()
    {
        var logger = new CapturingLogger();
        var service = new ComunicationService(logger) { MaxFrameSize = TinyFrameLimit };

        // Under the limit while pending, so it completes normally.
        service.Feed(ComunicationService.Frame(new byte[TinyFrameLimit - 1]));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Omitting_the_logger_is_still_allowed()
    {
        var service = new ComunicationService { MaxFrameSize = TinyFrameLimit };

        // NullLogger by default: must not throw on the overflow path.
        Assert.Empty(service.Feed(Unterminated(200)));
        Assert.Equal(0, service.Pending);
    }

    /// <summary>A start byte followed by <paramref name="length"/> bytes and no terminator.</summary>
    private static byte[] Unterminated(int length)
    {
        var data = new byte[length + 1];
        data[0] = VT;
        return data;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
