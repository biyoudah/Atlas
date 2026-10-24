# Contributing to Atlas

Thank you for your interest in contributing to **Atlas**!

Atlas is an open-source, community-driven industrial telemetry adapter and address-space discovery engine. Our mission is to democratize machine-tool telemetry, eliminate vendor lock-in, and provide reliable, noise-free pipelines from industrial PLCs and CNCs to modern observability stacks.

---

## Code of Conduct

All contributors and participants are expected to adhere to our [Code of Conduct](CODE_OF_CONDUCT.md). Please read it before engaging with the project.

---

## Fast-Track Development Setup

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (v8.0.100 or later).
- Git.
- (Optional) Docker & Docker Compose for multi-container stack testing.

### 1. Clone & Build
```bash
git clone https://github.com/atlas-industrial/atlas.git
cd atlas

# Restore dependencies
dotnet restore Atlas.slnx

# Build the solution
dotnet build Atlas.slnx
```

### 2. Run All Tests
```bash
dotnet test Atlas.slnx
```

### 3. Verify Formatting & Quality
```bash
# Verify standard formatting
dotnet format Atlas.slnx --verify-no-changes

# Or run the one-step verification script
# On Windows (PowerShell):
./scripts/verify.ps1

# On Linux/macOS:
./scripts/verify.sh
```

---

## Repository Structure

```
atlas/
├── Atlas.slnx                       # Solution manifest
├── Directory.Build.props            # Shared compiler & package properties
├── docs/
│   ├── index.html                   # Embedded visual cockpit (single-file UI)
│   └── PACK_GUIDE.md                # Mapping pack authoring guide
├── schema/
│   └── mapping-pack.schema.json     # JSON Schema (Draft 2020-12) for packs
├── scripts/
│   ├── verify.ps1                   # Windows local verification runner
│   └── verify.sh                    # Linux/macOS local verification runner
├── simulation/
│   └── Atlas.FakeCnc/               # Deterministic loopback CNC server for tests
├── src/
│   └── Atlas/
│       ├── Client/                  # OPC UA session & endpoint management
│       ├── Crawler/                 # BFS crawler & batch attribute reader
│       ├── Decoding/                # ExtensionObject & array type decoder
│       ├── Exporters/               # Prometheus, JSON, CSV, NodeSet2 exporters
│       ├── Labeling/                # 4-tier semantic inference engine
│       ├── Mcp/                     # Model Context Protocol stdio server
│       ├── Mes/                     # ISO 22400 OEE & work order reconciliation
│       ├── Models/                  # CanonicalSignal, CatalogEntry, CatalogDiff
│       ├── Packs/                   # Built-in vendor mapping packs (JSON)
│       ├── Profiling/               # Passive live profiler & variance analyzer
│       ├── Serving/                 # HTTP server, WebSocket gateway, QualityGuard
│       └── Standard/                # OPC 40501 MachineTool companion spec
└── tests/
    └── Atlas.Tests/                 # NUnit 4 automated test suite
```

---

## Ways to Contribute

### 1. Community Vendor Mapping Packs (Highest Impact)
Mapping packs allow Atlas to instantly recognize proprietary PLC register conventions (e.g. Siemens DB blocks, Fanuc PMC addresses, Heidenhain markers):
1. Review [`docs/PACK_GUIDE.md`](docs/PACK_GUIDE.md) and [`schema/mapping-pack.schema.json`](schema/mapping-pack.schema.json).
2. Create a new pack in `src/Atlas/Packs/<vendor>-<model>.json`.
3. Add a unit test verifying rule matching in `tests/Atlas.Tests/MappingPackSchemaTests.cs`.
4. Submit a pull request with sample node browse paths.

### 2. Core Protocol & Traversal Enhancements
- Optimize continuation point handling and batch size auto-tuning for bandwidth-constrained OT switches.
- Expand complex `ExtensionObject` unwrapping in `src/Atlas/Decoding/TypeDecoder.cs`.
- Improve heuristic classification in `src/Atlas/Labeling/SemanticLabeler.cs`.

### 3. Exporters & Observability
- Add native telemetry exporters (e.g., InfluxDB line protocol, MQTT Sparkplug B).
- Enhance pre-configured Grafana dashboard templates in `dashboards/`.

---

## Coding Standards & Invariants

- **Language Level:** C# 12 (`<LangVersion>latest</LangVersion>` on .NET 8).
- **Null Safety:** Nullable reference types are strictly enabled (`<Nullable>enable</Nullable>`). Avoid suppressing warnings (`!`) unless backed by an explicit assertion.
- **Asynchronous Execution:** Always accept and propagate `CancellationToken`. Avoid blocking calls (`.Result`, `.Wait()`). Use `.ConfigureAwait(false)` in library and background service code.
- **Dependency Discipline:** Do not introduce heavy third-party dependencies unless strictly justified. Prefer the .NET runtime and standard BCL whenever possible.
- **Zero Identifying Leaks:** Never commit private hostnames, customer names, personal email addresses, internal IP subnets, or production credentials to tracked files.
- **Formatting:** Code formatting is automatically validated in CI using `dotnet format --verify-no-changes`. Run `dotnet format Atlas.slnx` prior to committing.

---

## Testing Guidelines

Every substantive bug fix or feature addition must include automated regression tests:
- **Unit Tests:** Pure functions, semantic labeling rules, OEE calculations, and Prometheus serialization.
- **Integration Tests:** Use the included loopback simulation server (`Atlas.FakeCnc`) in `tests/Atlas.Tests/AtlasServerIntegrationTests.cs`.
- **Determinism:** Tests must never depend on external WAN endpoints or unreliable public networks. Public server compatibility checks are isolated from default CI runs.

---

## Pull Request Process

1. **Create a topic branch:**
   ```bash
   git checkout -b feat/your-feature-name
   # or
   git checkout -b fix/your-bugfix-name
   ```
2. **Make your changes** following the coding and testing standards.
3. **Run local verification:**
   ```bash
   ./scripts/verify.ps1   # or ./scripts/verify.sh
   ```
4. **Commit with a descriptive message** following [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat(crawler): add operation limits auto-detection`
   - `fix(exporter): handle empty string states gracefully`
   - `docs(readme): clarify port 9090 configuration`
5. **Open a Pull Request** against the `main` branch. Fill in the provided PR template.

---

## Reporting Issues

- **Bug Reports:** Use our [Bug Report Template](.github/ISSUE_TEMPLATE/bug_report.md). Include reproduction steps, expected vs. actual behavior, and relevant logs.
- **Feature Requests:** Open an issue describing the industrial use case, target controllers, and proposed telemetry mapping.
- **Security Disclosures:** For security vulnerabilities, consult [SECURITY.md](SECURITY.md) and report privately.
