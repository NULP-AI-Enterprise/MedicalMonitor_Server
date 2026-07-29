using MedicalMonitorServer.DeviceDriver;

namespace MedicalMonitorServer.Core
{
    public class MonitorService : IMonitorService
    {
        private readonly IDeviceService _deviceService;

        public MonitorService(IDeviceService deviceService)
        {
            _deviceService = deviceService;
        }

        
    }
}
