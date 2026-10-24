# Changelog

All notable changes to the Atlas project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.2.0-alpha] - 2026-10-24

### Added
- **OPC 40501 MachineTool & OPC 40001 Machinery (umati) First-Class Integration:**
  - Native recognition of official companion specification namespaces (`http://opcfoundation.org/UA/MachineTool/`, `http://opcfoundation.org/UA/Machinery/`, `http://opcfoundation.org/UA/DI/`, `http://opcfoundation.org/UA/IA/`).
  - Standard machine tool state model parser mapping integer/multi-state discrete values to canonical `MachineToolState` (`Ready`, `Active`, `Interrupted`, `FeedHold`, `Moving`, `Warmup`, `ToolChange`, `Fault`).
  - Standardized node browsing and canonical naming for spindle kinetics, tool IDs, and parts counters.
  - Added official umati Machine Tool sample server (`ghcr.io/umati/sample-server:latest`) to `docker-compose.yml` on port 4841.
- **3-Tier Precedence Hierarchy for Semantic Provenance:**
  - **Tier 1 (Standard Companion Spec):** 100% confidence for OPC 40501 MachineTool & OPC 40001 Machinery signals.
  - **Tier 2 (Vendor Mapping Packs):** 100% confidence for explicit curated vendor profiles (Siemens, Fanuc, Mitsubishi).
  - **Tier 3 (Multi-Factor Heuristics):** 50% - 90% confidence for automated inference based on browse paths, units, and ranges.
  - Added `SignalProvenance` tracking on all inferred catalog items.
- **Canonical Signal Model (`CanonicalSignal`):**
  - Unified 18-attribute telemetry structure preserving raw value, numeric value, data type, status code, quality flag, source/server/received timestamps, staleness, sampling interval, semantic label, engineering unit, provenance, confidence, category, raw state, and standardized state.
  - Integrated into `SubscriptionMultiplexer` with atomic real-time updates and snapshot queries (`GetCanonicalSignal`, `GetCanonicalSignals`).
- **Deterministic Scenario-Driven Test Harness (`Atlas.FakeCnc`):**
  - Upgraded simulation engine to support deterministic, repeatable timeline scenarios triggered via `--scenario <name>` and `--seed <int>`.
  - Implemented 6 industrial verification scenarios:
    - `NormalProduction`: Steady nominal machining cycles.
    - `CounterReset`: Abrupt counter rollback simulating PLC memory reset or operator manual clear.
    - `QualityFault`: Temperature spike triggering severe QualityGuard warnings.
    - `ProductionDisruption`: E-stop fault state with feed hold and spindle zeroing.
    - `NetworkInterruption`: Stale timestamps and communication blackout testing client recovery.
    - `SchemaVariation`: Alternate node structures mimicking legacy PLC variations.
- **OEE & Production Analytics Edge-Case Hardening:**
  - Protected against zero-division errors when nominal cycle time or target quantities are unconfigured.
  - Integrated `MachineToolStandards.ParseStandardState` into Availability calculations to differentiate planned pauses from unscheduled downtime.
  - Added resilience against scrap counter anomalies and counter resets.
- **Test Suite Expansion:**
  - Expanded automated test suite from 69 to 82 passing tests, covering companion spec parsing, deterministic scenarios, canonical signal serialization, and OEE edge cases.

### Changed
- **Endpoint URL Preservation:**
  - Fixed client discovery endpoint rewriting to preserve requested target host/port across NAT, Docker, and WSL bridges.

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
