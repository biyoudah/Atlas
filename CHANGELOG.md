# Changelog

All notable changes to the Atlas project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.1.0-alpha] - 2026-10-24

### Added
- **Core OPC UA Crawler & Profiler:**
  - Fast breadth-first address space traversal with batch attribute reads (`Value`, `DataType`, `AccessLevel`, `UserAccessLevel`, `Description`).
  - Hierarchical property decoding (extracts `EURange` and `EngineeringUnits` from standard OPC UA companion specs).
  - Dynamic profile analysis classifying continuous vs discrete vs slow-trend vs static signals with noise floor and update cadence profiling.
- **Semantic Inference Engine & Mapping Packs:**
  - Multi-heuristic scoring engine combining regex patterns, engineering units, value ranges, and browse paths.
  - Built-in vendor mapping packs for Siemens S7-1500 / Sinumerik, Fanuc 31i, and Mitsubishi M800 CNCs.
  - JSON Schema validation (`schema/mapping-pack.schema.json`) based on Draft 2020-12 and MTConnect taxonomies.
  - Comprehensive pack authoring guide in `docs/PACK_GUIDE.md`.
- **Telemetry Serving & Turnkey Dashboards:**
  - Zero-allocation Prometheus metrics exporter with optional cardinality reduction (`--low-cardinality` / `--omit-node-id`).
  - High-frequency streaming WebSocket gateway (`/ws`) delivering real-time telemetry frames with delta compression.
  - Automated Grafana dashboard generator outputting ready-to-import JSON models with gauge, stat, and time-series panels.
- **Industrial Standards & MES Metrics:**
  - Synthetic metrics engine calculating real-time spindle thermal deviation, vibration alert bands, and feed vs spindle speed efficiency.
  - ISO 22400-compliant OEE (Overall Equipment Effectiveness) computation: Availability $\times$ Performance $\times$ Quality.
  - Shift-based Work Order context tracking (`--of-id`, `--target-parts`, `--ideal-cycle-sec`).
- **Security & Quality Safeguards:**
  - OPC UA client security profiles supporting `Sign` and `SignAndEncrypt` with X.509 certificate validation and trust-store isolation.
  - Safe credential management accepting passwords via environment variables (`ATLAS_OPCUA_PASSWORD`) or secrets file (`--password-file`) to prevent process table sniffing.
  - QualityGuard filter verifying OPC UA status codes (`StatusCode.IsGood`) and preventing stale or invalid values from contaminating downstream pipelines.
  - Explicit write protection whitelist with auditable execution logs on all mutation attempts.
- **Model Context Protocol (MCP) Server:**
  - Integrated stdio MCP server exposing industrial tools to AI agents: `crawl_address_space`, `read_node`, `write_node`, `get_node_profile`, `get_catalog`, and `inspect_synthetic_metrics`.
- **Simulation & Testing Infrastructure:**
  - Realistic 5-axis CNC Machining Center simulation server (`Atlas.FakeCnc`) simulating real physics, thermal drift, tool wear, axis kinematics, and cutting cycles.
  - Integration with official C-based reference `open62541` OPC UA server in `docker-compose.yml`.
  - Comprehensive automated test suite with 69 passing unit and end-to-end integration tests.
  - CI pipelines: GitHub Actions build and test suite, automated Dependabot dependency updates, and CodeQL SAST security scanning.

### Changed
- **Decoupled Architecture & Trademark Protection:**
  - Refactored all project namespaces and assembly identifiers from `Opc.Ua.*` to `Atlas.*` to ensure clean separation from the official OPC Foundation .NET Standard library.
  - Renamed simulation server to `Atlas.FakeCnc` and test suite to `Atlas.Tests`.
  - Anonymized sample endpoint URIs to standard test domain placeholders (`urn:example:cnc:milling01`).
- **Metrics Normalization:**
  - Corrected `atlas_collector_uptime_seconds` metric type from `counter` to `gauge`.
  - Normalized metric names and labels to conform strictly to Prometheus PromQL conventions.
