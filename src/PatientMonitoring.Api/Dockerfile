FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app

# Copy csproj files for restore caching
COPY src/PatientMonitoring.Api/PatientMonitoring.Api.csproj src/PatientMonitoring.Api/
RUN dotnet restore src/PatientMonitoring.Api/PatientMonitoring.Api.csproj

# Copy source and publish
COPY src/PatientMonitoring.Api/ src/PatientMonitoring.Api/
WORKDIR /app/src/PatientMonitoring.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5225
EXPOSE 5225

ENTRYPOINT ["dotnet", "PatientMonitoring.Api.dll"]
