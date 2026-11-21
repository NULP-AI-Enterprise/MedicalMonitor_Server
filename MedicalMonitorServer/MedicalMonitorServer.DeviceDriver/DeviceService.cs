using MedicalMonitorServer.Contracts.Models;
using MedicalMonitorServer.DeviceDriver.NihonKohden;
using MedicalMonitorServer.DeviceDriver.NihonKohden.LsNet;
using MedicalMonitorServer.DeviceDriver.Umec;
using Microsoft.Extensions.Logging;

namespace MedicalMonitorServer.DeviceDriver;

public class DeviceService : IDeviceService, IMonitorAdmissions
{
    public event Action<UmecMessage>? OnMessageReceived;

    private static readonly Task<MonitorAdmissionResult> NotSupported =
        Task.FromResult(new MonitorAdmissionResult(MonitorAdmissionStatus.NotSupported));

    private readonly NihonKohdenDeviceDataProvider? _nihonKohden;

    /// <summary>
    /// Guards <see cref="_deviceStates"/>. It is reached from the watchdog's scan
    /// (a monitor went away), from every link thread (a stream opened or ended)
    /// and from HTTP requests (<see cref="GetAllDevice"/>), so each access takes
    /// this lock. Nothing is called out to while it is held.
    /// </summary>
    private readonly object _gate = new();

    private readonly Dictionary<IDeviceDataProvider, List<DeviceState>> _deviceStates = new();
    private readonly IWatchDogService _watchDogService;
    private readonly List<IDeviceDataProvider> _deviceDataProviders = new();

    /// <param name="linkOptions">Link timings handed to the uMec provider; defaults match a real monitor.</param>
    /// <param name="providers">Substituted by tests; by default the uMec provider alone.</param>
    /// <param name="nihonKohden">Nihon Kohden monitors to read, added to the providers above; none when omitted or empty.</param>
    public DeviceService(
        IWatchDogService watchDogService,
        ILoggerFactory? loggerFactory = null,
        UmecLinkOptions? linkOptions = null,
        IEnumerable<IDeviceDataProvider>? providers = null,
        NihonKohdenOptions? nihonKohden = null)
    {
        _watchDogService = watchDogService;

        // Null-or-empty, not just null: Microsoft DI resolves IEnumerable<T> to an
        // EMPTY ARRAY when no T is registered, so a plain ?? would leave this
        // service with no providers at all — no link, no data, and an ownership
        // check that always answers false.
        var supplied = providers?.Where(provider => provider is not null).ToArray();
        _deviceDataProviders.AddRange(supplied is { Length: > 0 }
            ? supplied
            : new IDeviceDataProvider[]
            {
                new UmecDeviceDataProvider(
                    loggerFactory?.CreateLogger<UmecDeviceDataProvider>(), linkOptions),
            });

        // A Nihon Kohden monitor speaks HL7 or LS-NET, whichever is selected on it; each has its own driver.
        NihonKohdenDeviceDataProvider? nihonKohdenProvider = null;
        NihonKohdenLsNetProvider? lsNetProvider = null;
        if (nihonKohden is { Monitors.Count: > 0 })
        {
            if (nihonKohden.Monitors.Any(m => !m.IsLsNet))
            {
                var hl7 = new NihonKohdenOptions
                {
                    Monitors = nihonKohden.Monitors.Where(m => !m.IsLsNet).ToList(),
                    ReconnectDelayMs = nihonKohden.ReconnectDelayMs,
                    ReadTimeoutSeconds = nihonKohden.ReadTimeoutSeconds,
                };
                nihonKohdenProvider = new(hl7, loggerFactory?.CreateLogger<NihonKohdenDeviceDataProvider>());
                _deviceDataProviders.Add(nihonKohdenProvider);
            }

            if (nihonKohden.Monitors.Any(m => m.IsLsNet))
            {
                lsNetProvider = new(nihonKohden.LsNet, nihonKohden.Monitors, loggerFactory?.CreateLogger<NihonKohdenLsNetProvider>());
                _deviceDataProviders.Add(lsNetProvider);
            }
        }

        foreach (var provider in _deviceDataProviders){
            provider.OnDeviceConnected += OnDeviceConnected;
            provider.OnDeviceDisconnected += OnDeviceDisconnected;
            provider.OnMessageReceived += OnProviderMessageReceived;
        }

        // Wired last, so a scan can never see a half-built service: with the
        // provider list still empty a Connected edge would be swallowed, and the
        // watchdog reports only *changes*, so that monitor would stay offline
        // until it happened to flap.
        _watchDogService.OnDeviceStateChanged += OnDeviceStateChanged;
        _watchDogService.IsLinkOwned = IsLinkOwned;

        // Dialled from configuration, not found by the watchdog; only once every handler is in place.
        _nihonKohden = nihonKohdenProvider;
        nihonKohdenProvider?.Start();
        lsNetProvider?.Start();
    }

    /// <summary>Only Nihon Kohden monitors on HL7 take a patient over the network; any other is told nothing.</summary>
    public Task<MonitorAdmissionResult> AssignPatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default) =>
        _nihonKohden is { } provider && provider.Owns(monitorIp)
            ? provider.AssignPatientAsync(monitorIp, patientId, patientName, cancellationToken)
            : NotSupported;

    public Task<MonitorAdmissionResult> UpdatePatientAsync(
        string monitorIp, string patientId, string patientName, CancellationToken cancellationToken = default) =>
        _nihonKohden is { } provider && provider.Owns(monitorIp)
            ? provider.UpdatePatientAsync(monitorIp, patientId, patientName, cancellationToken)
            : NotSupported;

    /// <summary>
    /// Whether a driver already holds this monitor's HL7 link, so the watchdog must
    /// not probe the port that link is using.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="IDeviceDataProvider.ConnectedDevices"/>, a snapshot of the
    /// provider's own concurrent dictionary, which is populated from
    /// <c>Connect()</c> until the link gives up — i.e. across every reconnect
    /// attempt, not just while a session happens to be up.
    /// Deliberately NOT <see cref="_deviceStates"/> or <see cref="GetAllDevice"/>:
    /// those follow single sessions (a device leaves the list whenever its stream
    /// ends), which would release ownership during every reconnect pause. Only
    /// <see cref="DeviceState.IpAddress"/> is read — never <c>State</c>, which is
    /// the unsynchronised field.
    /// </remarks>
    private bool IsLinkOwned(string ipAddress) =>
        _deviceDataProviders.Any(provider =>
            provider.ConnectedDevices.Any(device => device.IpAddress == ipAddress));

    private void OnProviderMessageReceived(UmecMessage message)
    {
        OnMessageReceived?.Invoke(message);
    }
    private void OnDeviceStateChanged(DeviceState deviceState)
    {
        if (deviceState.State == DeviceStateEnum.Connected)
        {
            foreach (var deviceDataProvider in _deviceDataProviders)
            {
                deviceDataProvider.Connect(deviceState);
            }
        }
        else if (deviceState.State == DeviceStateEnum.Disconnected)
        {
            IDeviceDataProvider? owner = null;

            lock (_gate)
            {
                foreach (var pair in _deviceStates)
                {
                    var states = pair.Value;

                    var connectedDevice = states.FirstOrDefault(
                        state => state.IpAddress == deviceState.IpAddress);

                    if (connectedDevice is null)
                    {
                        continue;
                    }

                    states.Remove(connectedDevice);
                    owner = pair.Key;

                    break;
                }
            }

            // Outside the lock: closing a link cancels its run loop, and whatever
            // that runs inline may come straight back into OnDeviceDisconnected.
            owner?.Disconnect(deviceState);
        }
    }
    private void OnDeviceConnected(IDeviceDataProvider sender, DeviceState deviceState)
    {
        lock (_gate)
        {
            if (!_deviceStates.TryGetValue(sender, out var states))
            {
                states = new List<DeviceState>();
                _deviceStates[sender] = states;
            }

            if (states.All(state => state.IpAddress != deviceState.IpAddress))
            {
                states.Add(deviceState);
            }
        }
    }

    private void OnDeviceDisconnected(IDeviceDataProvider sender, DeviceState deviceState)
    {
        lock (_gate)
        {
            if (_deviceStates.TryGetValue(sender, out var states))
            {
                states.RemoveAll(state => state.IpAddress == deviceState.IpAddress);
            }
        }
    }

    /// <summary>A snapshot of the connected devices; safe to enumerate while links change.</summary>
    public List<DeviceState> GetAllDevice(){
        List<DeviceState> devices = new();
        lock (_gate)
        {
            foreach (var pair in _deviceStates){
                devices.AddRange(pair.Value);
            }
        }
        return devices;
    }
}