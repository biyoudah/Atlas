# Contributing to Atlas

Thank you for your interest in contributing to **Atlas**!

Atlas is an open-source initiative designed to break vendor lock-in, eliminate paywalled tag packs, and democratize access to industrial machinery telemetry.

---

## How You Can Contribute

1. **Community Mapping Packs (Highest Impact):**
   - Add mappings for specific machine models (Siemens S7-1200/1500, Fanuc 0i/31i, Heidenhain, Mitsubishi, Haas).
   - See `src/Opc.Ua.Atlas/Packs/` for existing examples.
2. **Core Discovery & Decoding:**
   - Enhance complex ExtensionObject decoding and array unwrapping.
   - Improve passive profiling algorithms and correlation heuristics.
3. **Exporters & Integrations:**
   - Add native exporters for other time-series backends (VictoriaMetrics, QuestDB, TimescaleDB, InfluxDB).
   - Expand Grafana dashboard templates for specialized machines (plastic injection, robotics, presses).

---

## Developing Locally

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

### Build & Run Tests
```bash
# Build the entire solution
dotnet build Atlas.slnx

# Run all automated tests
dotnet test tests/Opc.Ua.Atlas.Tests/Opc.Ua.Atlas.Tests.csproj
```

### Running the Included Simulation Testbed
Atlas includes a realistic 5-axis CNC machining center simulation server:
```bash
# Terminal 1: Start the Fake CNC server on port 62541
dotnet run --project simulation/Opc.Ua.FakeCnc -- -p 62541

# Terminal 2: Crawl and inspect
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://localhost:62541/FakeCncServer --pack siemens-s7-1500
```

---

## Pull Request Guidelines

1. Ensure all tests pass (`dotnet test`).
2. Include tests for any new features or bug fixes.
3. Keep mapping packs vendor-accurate and provide sample node paths in rule descriptions.
