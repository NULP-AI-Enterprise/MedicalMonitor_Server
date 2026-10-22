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

public class WatchDogService : IWatchDogService
{
    public Action<DeviceState>? OnDeviceStateChanged { get; set; }
    
    public ConcurrentDictionary<string, DeviceState> device_map = new();

    private readonly string _ipBase = "192.168.2.";
    private readonly int _startIp = 100;
    private readonly int _endIp = 110;
    private readonly int _port = 4601;
    private readonly int _connectTimeoutMs = 500;
    private readonly int _scanDelayMs = 1000;

    private readonly string[] _targetIps;

    public WatchDogService()
    {
        _targetIps = Enumerable.Range(_startIp, _endIp - _startIp + 1)
                               .Select(i => $"{_ipBase}{i}")
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
                await Task.Delay(_scanDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<DeviceState> CheckDeviceAsync(string ip, CancellationToken cancellationToken)
    {
        var state = new DeviceState { IpAddress = ip, State = DeviceStateEnum.Disconnected };

        if (!IPAddress.TryParse(ip, out IPAddress? parsedIp))
        {
            return state;
        }

        using var client = new TcpClient();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_connectTimeoutMs);

        try
        {
            await client.ConnectAsync(parsedIp, _port, cts.Token);
            state.State = DeviceStateEnum.Connected;
        }
        catch (Exception)
        {
            
        }


        return state;
    }
}