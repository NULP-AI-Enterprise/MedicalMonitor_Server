using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using MedicalMonitorServer.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MedicalMonitorServer.DeviceDriver;

public sealed class WatchDogOptions
{
    public string IpBase { get; set; } = "192.168.2.";
    public int StartIp { get; set; } = 100;
    public int EndIp { get; set; } = 110;
    public int Port { get; set; } = 4601;
    public int ConnectTimeoutMs { get; set; } = 500;
    public int ScanIntervalMs { get; set; } = 1000;
}

public class WatchDogService : IWatchDogService
{
    public Action<DeviceState>? OnDeviceStateChanged { get; set; }

    /// <inheritdoc />
    public Func<string, bool>? IsLinkOwned { get; set; }

    public ConcurrentDictionary<string, DeviceState> device_map = new();

    private readonly WatchDogOptions _options;

    private readonly ILogger _logger;

    private readonly string[] _targetIps;

    /// <summary>Addresses already reported as skipped, so a held link is logged once, not every scan.</summary>
    private readonly HashSet<string> _loggedSkips = new(StringComparer.Ordinal);

    /// <summary>Addresses whose ownership check has already been reported as broken.</summary>
    private readonly HashSet<string> _loggedCheckFailures = new(StringComparer.Ordinal);

    /// <summary>
    /// One transition, captured by value. A <see cref="DeviceState"/> is shared with
    /// the driver and mutated from link threads, so holding the object until after
    /// the loop could log the opposite of what was actually observed.
    /// </summary>
    private readonly record struct Transition(string Ip, string Port, DeviceStateEnum State);

    /// <param name="logger">Logger for state transitions; omit to log nothing.</param>
    public WatchDogService(WatchDogOptions options, ILogger<WatchDogService>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<WatchDogService>.Instance;
        ValidateOptions(_options);

        _targetIps = Enumerable.Range(_options.StartIp, _options.EndIp - _options.StartIp + 1)
                               .Select(i => $"{_options.IpBase}{i}")
                               .ToArray();
    }

    public async Task StartWatchDog(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // An address whose HL7 link a driver already holds is left alone: its
            // recorded state stays as it is, so no transition is reported and
            // nothing tears the working link down.
            var toProbe = _targetIps.Where(ip => !SkipProbe(ip)).ToArray();

            var tasks = toProbe.Select(ip => CheckDeviceAsync(ip, cancellationToken));
            var results = await Task.WhenAll(tasks);

            // Transitions are logged after the loop: the factories below run under
            // the dictionary's bucket lock, which already carries OnDeviceStateChanged
            // (see item 9) and must not be given more work.
            var transitions = new List<Transition>();

            foreach (var currentState in results)
            {
                device_map.AddOrUpdate(
                    currentState.IpAddress,
                    addValueFactory: (key) =>
                    {
                        // First sighting: an address nobody answers on is not news,
                        // and the scanned range is mostly empty.
                        if (currentState.State == DeviceStateEnum.Connected)
                        {
                            transitions.Add(new Transition(
                                currentState.IpAddress, currentState.Port, currentState.State));
                        }

                        OnDeviceStateChanged?.Invoke(currentState);
                        return currentState;
                    },
                    updateValueFactory: (key, existingState) =>
                    {
                        if (existingState.State != currentState.State)
                        {
                            existingState.State = currentState.State;
                            transitions.Add(new Transition(
                                existingState.IpAddress, existingState.Port, currentState.State));
                            OnDeviceStateChanged?.Invoke(existingState);
                        }
                        return existingState;
                    });
            }

            foreach (var transition in transitions)
            {
                Report(transition);
            }

            try
            {
                await Task.Delay(_options.ScanIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>True when a driver holds this address's link, so it must not be probed.</summary>
    private bool SkipProbe(string ip)
    {
        bool owned;
        try
        {
            owned = IsLinkOwned?.Invoke(ip) == true;
        }
        catch (Exception ex)
        {
            // This runs inside the task returned by StartWatchDog, which
            // WatchDogHostedService hands straight back to the host. An escaping
            // exception would stop the whole server, so a broken check is logged
            // and we fall back to probing: a flap is recoverable and visible,
            // whereas suppressing an address forever is silent.
            // Reported once per address, because this would otherwise fire on
            // every scan — eleven times a second on the shipped range — and roll
            // the alarm history out of the log file. Returning early also keeps
            // us out of the release path below, which would claim the driver let
            // go of a link it may well still be holding.
            if (_loggedCheckFailures.Add(ip))
            {
                _logger.LogError(ex, "The link-ownership check for {Ip} failed; probing it anyway.", ip);
            }

            return false;
        }

        _loggedCheckFailures.Remove(ip);

        if (!owned)
        {
            if (_loggedSkips.Remove(ip))
            {
                _logger.LogInformation(
                    "Monitor {Ip}: the driver released its HL7 link, probing it again.", ip);
            }

            return false;
        }

        if (_loggedSkips.Add(ip))
        {
            _logger.LogInformation(
                "Monitor {Ip}: skipping the probe while the driver holds its HL7 link on port {Port}.",
                ip, _options.Port);
        }

        return true;
    }

    private void Report(Transition transition)
    {
        if (transition.State == DeviceStateEnum.Connected)
        {
            _logger.LogInformation("Monitor {Ip}:{Port} answered a probe.", transition.Ip, transition.Port);
        }
        else
        {
            _logger.LogWarning("Monitor {Ip}:{Port} did not answer a probe.", transition.Ip, transition.Port);
        }
    }

    private async Task<DeviceState> CheckDeviceAsync(string ip, CancellationToken cancellationToken)
    {
        var state = new DeviceState
        {
            IpAddress = ip,
            Port = _options.Port.ToString(),
            State = DeviceStateEnum.Disconnected
        };

        if (!IPAddress.TryParse(ip, out IPAddress? parsedIp))
        {
            return state;
        }

        using var client = new TcpClient();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(parsedIp, _options.Port, cts.Token);
            state.State = DeviceStateEnum.Connected;
        }
        catch (Exception)
        {
            
        }


        return state;
    }

    private static void ValidateOptions(WatchDogOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.IpBase) || !options.IpBase.EndsWith('.'))
        {
            throw new InvalidOperationException("WatchDog:IpBase must be a non-empty IPv4 prefix ending with '.'.");
        }

        if (options.StartIp is < 0 or > 255 || options.EndIp is < 0 or > 255 ||
            options.StartIp > options.EndIp)
        {
            throw new InvalidOperationException("WatchDog IP range must be between 0 and 255, with StartIp <= EndIp.");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("WatchDog:Port must be between 1 and 65535.");
        }

        if (options.ConnectTimeoutMs <= 0 || options.ScanIntervalMs <= 0)
        {
            throw new InvalidOperationException(
                "WatchDog:ConnectTimeoutMs and WatchDog:ScanIntervalMs must be greater than zero.");
        }
    }
}