# 🌐 Atlas — Industrial OPC UA Telemetry Adapter & Address Space Engine

[![CI](https://github.com/biyoudah/Atlas/actions/workflows/ci.yml/badge.svg)](https://github.com/biyoudah/Atlas/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/License-Apache_2.0-green.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Standard](https://img.shields.io/badge/OPC_UA-v2.0-orange.svg)](https://opcfoundation.org/)
[![Status](https://img.shields.io/badge/Status-Alpha%20Prototype-yellow.svg)]()

**Atlas** is an open-source edge telemetry engine for industrial OPC UA controllers (Siemens S7-1500/840D, Fanuc 30i/31i, Mitsubishi M800, and standard OPC UA servers). It decouples operational technology (OT) machine data from proprietary software barriers by providing automated address space discovery, complex type flattening, ANSI/MTConnect semantic classification, network micro-cut hysteresis, ISO 22400-2 OEE calculation, turnkey Grafana v10 dashboard generation, Prometheus metrics export, and native Model Context Protocol (MCP) endpoints for AI assistants.

> **Project Status:** **Alpha / Active OT Prototype**. Currently verified against a high-fidelity 5-axis CNC simulation server (`Opc.Ua.FakeCnc`) with 67 automated unit and integration tests. In-situ plant pilots should follow the deployment and network guidelines in [SECURITY.md](SECURITY.md).

---

## 🎯 Technical Challenges Addressed

1. **Cryptic Vendor Registers:** PLCs and CNCs frequently expose variables as raw data-block addresses (e.g. `ns=3;s="DB10"."Var_27"`). Atlas applies regex-based community mapping packs to resolve them into standardized semantics (`spindle_speed`, `parts_produced`, `coolant_pressure`).
2. **Embedded Controller Session Constraints:** Embedded PLC/CNC communication processors typically support a limited number of concurrent OPC UA client sessions (often 2 to 5 concurrent sockets) before refusing connections. Atlas connects once and multiplexes telemetry across hundreds of downstream Prometheus scrapers, WebSocket consumers, and dashboards.
3. **Shop-Floor Network Jitter & Micro-Cuts:** Transient switch drops or packet loss in industrial environments cause naive collectors to flap between `Good` and `Bad`. Atlas implements configurable hysteresis: single dropped frames are marked `Uncertain` (retaining the last known value) before transitioning to `Bad` after $N$ consecutive failures.
4. **PLC Shift Counter Resets:** Operators routinely reset daily part counters (`M30` count or daily register) back to zero at shift transitions. QualityGuard detects counter drops and absorbs the offset into an internal accumulator, preventing negative rate spikes in downstream time-series databases.
5. **Gateway vs. Machine Disconnect Ambiguity:** When time-series stop updating, plant engineers need to determine whether the physical machine stopped or the network bridge crashed. Atlas exposes self-supervision diagnostics (`atlas_collector_scrape_latency_seconds`, `atlas_collector_read_errors_total`, `atlas_machine_connected`) to provide clear visibility.

---

## 🏗️ Architecture & Pipeline

```
┌────────────────────────────────────────────────────────────────────────┐
│                   ATLAS INDUSTRIAL DISCOVERY & SERVING PIPELINE        │
└────────────────────────────────────────────────────────────────────────┘
  [Stage 1] Exhaustive Crawl (Batched Reads + Stable URIs + XML/JSON/CSV)
      │
  [Stage 2] ExtensionObject Decoding (Complex Types -> Flattened Dotted Paths)
      │
  [Stage 3] Passive Profiler & Differ (Signal Classification & Cross-Role Diff)
      │
  [Stage 4] Semantic Labeler (ANSI/MTConnect Taxonomy: Sample / Event / Condition)
      │
  [Stage 5] QualityGuard Hysteresis (Micro-cut Tolerance, Staleness Timeout, Counter Offset)
      │
  [Stage 6] MES & OEE Engine (ISO 22400-2 TRS Reconciler + Buffer Depletion Horizon)
      │
  [Stage 7] Multi-Channel Serving (Prometheus /metrics + WebSocket /ws + MCP stdio)
```

---

## 🚀 Quickstart

### Option A: Run with Docker Compose (Single Command Demo)
Launch the entire stack (Simulated 5-axis CNC + Atlas Engine + Prometheus + Grafana) with one command:

```bash
docker compose up -d
```
* **Atlas HTTP API:** [http://localhost:9090](http://localhost:9090) (`/metrics`, `/catalog`, `/health`, `/reconciliation`)
* **Prometheus:** [http://localhost:9091](http://localhost:9091)
* **Grafana:** [http://localhost:3000](http://localhost:3000) (User: `admin`, Password: `admin`)
* **Simulated CNC Server:** `opc.tcp://localhost:62541/FakeCncServer`

### Option B: Build & Run from Source (.NET 8 SDK)
```bash
git clone https://github.com/biyoudah/Atlas.git
cd Atlas
dotnet build Atlas.slnx
dotnet test Atlas.slnx
```

---

## 💻 CLI Reference

### 1. Address Space Crawl & Export
```bash
# Crawl anonymously and export to JSON
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f json -o catalog.json

# Secure credential resolution via environment variable (avoids ps -ef exposure)
OPC_UA_PASSWORD="mySecretPassword" dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 \
  -u operator \
  --pack siemens-s7-1500 \
  -f json -o catalog.json

# Export to CSV or OPC UA Part 6 NodeSet2 XML
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f csv -o catalog.csv
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f nodeset2 -o nodeset2.xml
```

### 2. Unmasking Hidden Tags (`diff-live`)
Compares the accessible address space between an Anonymous role and an Authenticated role (e.g., Maintenance/Admin) to detect hidden registers or permission elevations:
```bash
dotnet run --project src/Opc.Ua.Atlas -- diff-live opc.tcp://192.168.1.50:4840 \
  --target-user admin --target-password-env ADMIN_PASSWORD \
  -o diff_report.md
```

### 3. Generate Turnkey Grafana Dashboard
Generates a production-ready Grafana v10 JSON dashboard organized by MTConnect categories (Conditions at top, Event KPI cards in middle, Sample process gauges at bottom):
```bash
dotnet run --project src/Opc.Ua.Atlas -- dashboard catalog.json \
  -o grafana_dashboard.json \
  -t "CNC Milling Center 01" \
  --machine "cnc_mill_01"
```

### 4. Run the Serving Layer (Prometheus + WebSocket + MES Reconciliation)
```bash
dotnet run --project src/Opc.Ua.Atlas -- serve opc.tcp://192.168.1.50:4840 \
  --port 9090 \
  --machine "cnc_mill_01" \
  --pack siemens-s7-1500 \
  --of-id "OF-2026-9481" \
  --part-number "VALVE-BODY-50" \
  --target-parts 500 \
  --target-cycle-time 45.0 \
  --low-cardinality
```

### 5. Native Model Context Protocol (MCP) Server for AI Assistants
Atlas provides an stdio JSON-RPC 2.0 MCP server. Connect Claude Desktop, Cursor, or local agents:
```bash
dotnet run --project src/Opc.Ua.Atlas -- mcp --catalog catalog.json
```

**Claude Desktop Configuration (`claude_desktop_config.json`):**
```json
{
  "mcpServers": {
    "atlas-opcua": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/Atlas/src/Opc.Ua.Atlas",
        "--",
        "mcp",
        "--catalog",
        "/absolute/path/to/catalog.json"
      ]
    }
  }
}
```

> **MCP Safety Invariant:** All tools exposed over MCP (`search_address_space`, `read_node_value`, `get_machine_health`, `calculate_oee`, `generate_mapping_pack`) are **strictly read-only and analytical**. Under no circumstances can an AI assistant issue an OPC UA `Write` or `MethodCall` through the MCP server.

---

## 📐 OEE / TRS Calculation Standard (ISO 22400-2)

Atlas computes real-time OEE / TRS conforming to **ISO 22400-2:2014** (Section 7.3):

* **Availability ($A$):**
  $$\text{Availability} = \frac{\text{Actual Operating Time}}{\text{Planned Production Time}}$$
  Where $\text{Planned Production Time} = \text{Total Shift Time} - \text{Planned Breaks/Downtime}$.
* **Performance ($P$):**
  $$\text{Performance} = \frac{\text{Total Parts Produced} \times \text{Nominal Cycle Time}}{\text{Actual Operating Time}}$$
* **Quality ($Q$):**
  $$\text{Quality} = \frac{\text{Total Parts Produced} - \text{Declared Scrap}}{\text{Total Parts Produced}}$$
* **Overall OEE:**
  $$\text{OEE} = A \times P \times Q$$

**Boundary Handling:**
* Division-by-zero protection when operating time or part count is zero.
* Automatic runtime reset upon work order transition (`OrderId` change).
* Operator scrap entry anomalies ($\text{Scrap} > \text{Total}$) are clamped to 0 good parts.

---

## 📊 Endpoints & Exposition Formats

### Prometheus (`GET /metrics`)
```prometheus
# HELP opcua_spindle_speed_rpm spindle_speed telemetry reading
# TYPE opcua_spindle_speed_rpm gauge
opcua_spindle_speed_rpm{machine="cnc_mill_01"} 10002.7

# HELP opcua_parts_produced_total parts_produced telemetry reading
# TYPE opcua_parts_produced_total counter
opcua_parts_produced_total{machine="cnc_mill_01"} 142

# HELP opcua_spindle_thermal_drift_celsius Spindle temperature delta relative to ambient reference baseline
# TYPE opcua_spindle_thermal_drift_celsius gauge
opcua_spindle_thermal_drift_celsius{machine="cnc_mill_01"} 16.5

# HELP opcua_time_to_starvation_seconds Time remaining until pallet stock starvation based on active cycle time
# TYPE opcua_time_to_starvation_seconds gauge
opcua_time_to_starvation_seconds{machine="cnc_mill_01"} 450.0

# HELP opcua_oee_overall_ratio Overall Equipment Effectiveness ratio (0.0 to 1.0)
# TYPE opcua_oee_overall_ratio gauge
opcua_oee_overall_ratio{machine="cnc_mill_01",order_id="OF-2026-9481",part_number="VALVE-BODY-50"} 0.7667

# HELP atlas_collector_uptime_seconds Atlas edge collector uptime in seconds
# TYPE atlas_collector_uptime_seconds gauge
atlas_collector_uptime_seconds{machine="cnc_mill_01"} 3600.5

# HELP atlas_machine_connected Machine connectivity status (1=connected, 0=disconnected)
# TYPE atlas_machine_connected gauge
atlas_machine_connected{machine="cnc_mill_01"} 1
```

### Self-Supervision Health Probe (`GET /health`)
```json
{
  "status": "UP",
  "serverUri": "urn:atlas:demo:cnc_mill_01",
  "collector": {
    "uptimeSeconds": 3600.5,
    "scrapeLatencyMs": 4.25,
    "readErrorsTotal": 0,
    "reconnectsTotal": 0,
    "monitoredItemsActive": 42
  },
  "machine": {
    "connected": true,
    "catalogEntries": 328,
    "staleSignalsCount": 0
  },
  "connectedWsClients": 2,
  "readOnly": true
}
```

### MES Work Order Reconciliation (`GET /reconciliation`)
```json
{
  "orderId": "OF-2026-9481",
  "partNumber": "VALVE-BODY-50",
  "availability": 0.8889,
  "performance": 0.875,
  "quality": 0.9857,
  "oeeOverall": 0.7667,
  "targetQuantity": 500,
  "totalPartsProduced": 350,
  "goodPartsProduced": 345,
  "scrapPartsProduced": 5,
  "completionPercent": 70.0,
  "estimatedTimeToCompleteSeconds": 9000.0
}
```

---

## ⚠️ Known Limitations & Boundaries

1. **Evaluation Scope:** All tests and benchmarks have been executed against simulated industrial servers (`Opc.Ua.FakeCnc`). In-situ physical machine pilots are planned in subsequent roadmap phases.
2. **Read-Only Telemetry Focus:** Atlas is optimized for data ingestion, discovery, and observability. It is **not** a deterministic soft-PLC or motion controller. While an audited write gateway exists for setpoints (e.g. override limits), physical safety interlocks must always remain on hardwired safety circuits or certified safety PLCs.
3. **Endpoint Authentication:** Built-in HTTP and WebSocket endpoints are unauthenticated by design and bind to `localhost` by default. Production deployments across network boundaries require a reverse proxy (e.g., NGINX, Envoy) with TLS and bearer tokens (see [SECURITY.md](SECURITY.md)).
4. **Label Cardinality:** By default, metric names identify signals cleanly (`opcua_spindle_speed_rpm{machine="..."}`). Attaching raw full `node_id` strings to labels can cause series cardinality explosion in Prometheus across large plants; use `--low-cardinality` to omit them.

---

## 🛡️ Automated Verification Suite (67 Tests)

| Test Fixture | Tests | Status | Domain Capabilities Verified |
|---|---|---|---|
| `AtlasCrawlerTests` | 10 | **PASS** | Recursive address space browse, continuation points, batched reads |
| `TypeDecoderTests` | 7 | **PASS** | `ExtensionObject` runtime decoding, struct field flattening |
| `ProfilerTests` | 9 | **PASS** | Passive signal classification, correlation matrices |
| `SemanticLabelerTests` | 7 | **PASS** | Multi-factor heuristic inference, mapping pack priority |
| `PrometheusAndDashboardTests` | 12 | **PASS** | Prometheus v0.0.4 exposition, state-sets, Grafana schema, low cardinality |
| `QualityGuardTests` | 5 | **PASS** | Micro-cut hysteresis (Uncertain $\rightarrow$ Bad), PLC reset smoothing, staleness timeout |
| `SignalCategoryAndSemanticsTests` | 3 | **PASS** | ANSI/MTConnect taxonomy (Sample, Event, Condition), pack overrides |
| `SyntheticMetricsTests` | 2 | **PASS** | Spindle thermal delta ($\Delta T$), time-to-starvation, tool wear ratio |
| `OeeCalculatorTests` | 4 | **PASS** | ISO 22400-2 TRS calculation, OF transitions, zero-divide edge cases |
| `CollectorDiagnosticsTests` | 2 | **PASS** | Gateway self-supervision, scrape latency, uptime gauge |
| `AtlasMcpServerTests` | 6 | **PASS** | JSON-RPC 2.0 stdio protocol, tool manifest, address space search, write rejection |
| **Total** | **67** | **100% PASS** | **End-to-End Suite Clean** |

---

## 📄 License
Licensed under the [Apache License, Version 2.0](LICENSE).
OPC UA is a registered trademark of the OPC Foundation.
