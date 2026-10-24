# ==============================================================================
# Multi-stage Dockerfile for Atlas (Engine & Simulation Testbed)
# ==============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for caching restore layer
COPY Atlas.slnx Directory.Build.props ./
COPY src/Atlas/Atlas.csproj src/Atlas/
COPY simulation/Atlas.FakeCnc/Atlas.FakeCnc.csproj simulation/Atlas.FakeCnc/
COPY tests/Atlas.Tests/Atlas.Tests.csproj tests/Atlas.Tests/

RUN dotnet restore Atlas.slnx

# Copy source code and build
COPY . .
RUN dotnet build Atlas.slnx -c Release --no-restore

# Publish Atlas CLI & Fake CNC server
RUN dotnet publish src/Atlas/Atlas.csproj -c Release -o /app/atlas --no-build
RUN dotnet publish simulation/Atlas.FakeCnc/Atlas.FakeCnc.csproj -c Release -o /app/fakecnc --no-build

# ==============================================================================
# Runtime Stage
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Non-root security best practice
RUN useradd -u 1000 -m -s /bin/bash appuser

COPY --from=build --chown=appuser:appuser /app/atlas /app/atlas
COPY --from=build --chown=appuser:appuser /app/fakecnc /app/fakecnc

USER appuser
ENV DOTNET_EnableDiagnostics=0

EXPOSE 9090
EXPOSE 62541

WORKDIR /app/atlas
ENTRYPOINT ["dotnet", "Atlas.dll"]
CMD ["serve", "opc.tcp://fake-cnc:62541/FakeCncServer", "--port", "9090", "--machine", "cnc_mill_01", "--pack", "siemens-s7-1500"]
