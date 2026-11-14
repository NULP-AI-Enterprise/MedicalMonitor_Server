using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using MedicalMonitorServer.Contracts.Models;

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
    
    public ConcurrentDictionary<string, DeviceState> device_map = new();

    private readonly WatchDogOptions _options;

    private readonly string[] _targetIps;

    public WatchDogService(WatchDogOptions options)
    {
        _options = options;
        ValidateOptions(_options);

        _targetIps = Enumerable.Range(_options.StartIp, _options.EndIp - _options.StartIp + 1)
                               .Select(i => $"{_options.IpBase}{i}")
                               .ToArray();
    }

    public async Task StartWatchDog(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var tasks = _targetIps.Select(ip => CheckDeviceAsync(ip, cancellationToken));
            var results = await Task.WhenAll(tasks);

            foreach (var currentState in results)
            {
                device_map.AddOrUpdate(
                    currentState.IpAddress,
                    addValueFactory: (key) =>
                    {
                        OnDeviceStateChanged?.Invoke(currentState);
                        return currentState;
                    },
                    updateValueFactory: (key, existingState) =>
                    {
                        if (existingState.State != currentState.State)
                        {
                            existingState.State = currentState.State;
                            OnDeviceStateChanged?.Invoke(existingState);
                        }
                        return existingState;
                    });
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