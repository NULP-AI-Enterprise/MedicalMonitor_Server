FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy csproj files for restore caching
COPY src/PatientMonitoring.Api/PatientMonitoring.Api.csproj src/PatientMonitoring.Api/
COPY MedicalMonitor_Desktop/src/Monitoring.Shared/Monitoring.Shared.csproj MedicalMonitor_Desktop/src/Monitoring.Shared/
COPY MedicalMonitor_Desktop/src/Monitoring.BL/Monitoring.BL.csproj MedicalMonitor_Desktop/src/Monitoring.BL/
COPY MedicalMonitorServer/MedicalMonitorServer.Contracts/MedicalMonitorServer.Contracts.csproj MedicalMonitorServer/MedicalMonitorServer.Contracts/
COPY MedicalMonitorServer/MedicalMonitorServer.DeviceDriver/MedicalMonitorServer.DeviceDriver.csproj MedicalMonitorServer/MedicalMonitorServer.DeviceDriver/
RUN dotnet restore src/PatientMonitoring.Api/PatientMonitoring.Api.csproj

# Copy source and publish
COPY src/PatientMonitoring.Api/ src/PatientMonitoring.Api/
COPY MedicalMonitor_Desktop/src/ MedicalMonitor_Desktop/src/
COPY MedicalMonitorServer/ MedicalMonitorServer/
WORKDIR /app/src/PatientMonitoring.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000;http://+:5225
EXPOSE 5000
EXPOSE 5225

ENTRYPOINT ["dotnet", "PatientMonitoring.Api.dll"]
