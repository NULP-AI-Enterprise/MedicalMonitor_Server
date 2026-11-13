using FastEndpoints;
using MedicalMonitorServer.Contracts.Dtos;
using MedicalMonitorServer.Contracts.Mappings;
using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Endpoints;

public sealed class GetDevicesEndpoint : EndpointWithoutRequest<List<DeviceStateDto>>
{
    private readonly DeviceService _deviceService;

    public GetDevicesEndpoint(DeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    public override void Configure()
    {
        Get("/api/v1/devices");
        AllowAnonymous();
        Summary(s => s.Summary = "Get all connected devices");
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var devices = _deviceService.GetAllDevice().Select(d => d.ToDto()).ToList();
        return Send.OkAsync(devices, ct);
    }
}
