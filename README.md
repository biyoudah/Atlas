# 🌐 Atlas — Industrial OPC UA Discovery, Diagnostics & Turnkey Visualization Engine

[![Build & Test](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-Apache_2.0-green.svg)](LICENSE)
[![Status](https://img.shields.io/badge/Tests-60%2F60%20Passing-brightgreen.svg)]()
[![Standard](https://img.shields.io/badge/OPC_UA-v2.0-orange.svg)](https://opcfoundation.org/)
[![MTConnect](https://img.shields.io/badge/Taxonomy-MTConnect%20Tri--Fold-blueviolet.svg)]()
[![MCP](https://img.shields.io/badge/AI-Model%20Context%20Protocol%20(MCP)-blue.svg)]()

**Atlas** is an open-source discovery, profiling, industrial robustness, and visualization engine for OPC UA controllers. It crawls undocumented PLC/CNC address spaces, decodes complex dynamic structures, unmasks hidden registers, categorizes signals into ANSI/MTConnect taxonomy, reconciles IT MES work orders with OT signals to compute real-time OEE/TRS, guards against network micro-cuts and PLC counter resets, and serves turnkey Grafana dashboards, Prometheus metrics, and native Model Context Protocol (MCP) tool endpoints for AI assistants.

---

## ⚡ Why Atlas? (Breaking the Industrial Vendor Moat)

Industrial automation has a severe data accessibility and reliability problem:
* **The "Cryptic Tag" Lock-in:** Controllers (Siemens S7-1500/840D, Fanuc 31i, Mitsubishi) expose registers as `ns=3;s="DB10"."Var_27"`. Vendors push customers to buy expensive proprietary software or pay certified integrators just to know what a variable means.
* **The "Middleware Tax":** Industrial middleware gateways (Kepware, Matrikon, Siemens Industrial Edge) charge **$3,000 to $25,000+ per machine** in runtime licenses just to get data onto an open-source dashboard.
* **Hardware Session Bottlenecks:** Constrained PLC and CNC embedded CPUs crash or refuse connections after **2 to 5 concurrent OPC UA sessions**.
* **Industrial Network Micro-Cuts:** Transient shop-floor packet drops cause naive collectors to flap between `Good` and `Bad`, dropping metrics and triggering false alerts.
* **PLC Counter Resets:** Operators resetting daily counters at shift change create negative rate spikes in downstream time-series databases.
* **Gateway vs. Machine Disconnect Ambiguity:** When data stops arriving, operations cannot tell whether the CNC crashed or the edge network bridge went down.

**Atlas eliminates all these barriers:**
1. **Open Community Mapping Packs:** A community repository of regex rules that map raw PLC addresses to human-readable names (`spindle_speed`, `parts_produced`, `coolant_pressure`).
2. **One Session, Infinite Viewers:** Atlas maintains **one single persistent connection** to the machine controller and multiplexes data to hundreds of dashboards, Prometheus scrapers, and web clients simultaneously.
3. **QualityGuard & Robustness Hysteresis:** Network micro-cut smoothing (mark `Uncertain` before `Bad`), stale-frame detection (`opcua_data_stale` gauge), and monotonic counter protection against PLC resets.
4. **MTConnect-Style Tri-Fold Semantics:** Classifies every tag into `Sample` (analog kinetics), `Event` (discrete states/counters), or `Condition` (alarms/interlocks).
5. **MES Work Order & Real-Time OEE/TRS Engine:** Reconciles machine telemetry with declared production orders (`OF-2026-9481`) to compute live Availability, Performance, Quality, and Overall TRS.
6. **Logistics & Physics-Informed Synthetic Metrics:** Calculates Time-to-Starvation ($\text{Stock} \times \text{CycleTime}$), Spindle Thermal Drift ($\Delta^\circ\text{C}$), and Tool Wear Urgency ratio in real time.
7. **Collector Self-Supervision Diagnostics:** Exposes gateway health metrics (`atlas_collector_uptime_seconds`, `atlas_collector_scrape_latency_seconds`, `atlas_collector_read_errors_total`, `atlas_machine_connected`).
8. **Native Model Context Protocol (MCP) Server (`atlas mcp`):** Allows Claude, Cursor, and AI coding assistants to query the address space, inspect tags, calculate OEE, and author mapping packs in natural language.
9. **Turnkey Grafana Dashboards:** One CLI command auto-generates a ready-to-import Grafana v10 dashboard organized by MTConnect categories.

---

## 🏗️ Architecture & Pipeline Stages

```
┌────────────────────────────────────────────────────────────────────────┐
│                   ATLAS INDUSTRIAL DISCOVERY & SERVING PIPELINE        │
└────────────────────────────────────────────────────────────────────────┘
  [Stage 1] Exhaustive Crawl (Batched Reads + Stable URI IDs + XML/JSON/CSV)
      │
  [Stage 2] ExtensionObject Type Decoding (Complex Types -> Dotted Paths)
      │
  [Stage 3] Passive Live Profiler & Identity Differ (Signal Classification & Cross-Role Diff)
      │
  [Stage 4] Semantic Labeler & MTConnect Taxonomy (Sample / Event / Condition)
      │
  [Stage 5] Robustness Guard & Quality Hysteresis (QualityGuard: Micro-cuts, Staleness, Counter Reset)
      │
  [Stage 6] MES & Logistics Synthetic Metrics Engine (OEE/TRS + Time-To-Starvation + Thermal Drift)
      │
  [Stage 7] Multi-Channel Serving Layer (Prometheus /metrics + WebSocket /ws + MCP stdio + Grafana v10)
```

---

## 🚀 Quickstart Guide

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.

### 1. Build the Solution
```bash
git clone https://github.com/biyoudah/Atlas.git
cd Atlas
dotnet build Atlas.slnx
```

### 2. Run the Automated Tests (60 Tests)
```bash
dotnet test tests/Opc.Ua.Atlas.Tests/Opc.Ua.Atlas.Tests.csproj
```

---

## 💻 CLI Reference

### 1. Address Space Crawl & Export
```bash
# Crawl anonymously and export to JSON
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f json -o catalog.json

# Crawl with credentials and apply Siemens mapping pack
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 \
  -u operator -p operator123 \
  --pack siemens-s7-1500 \
  -f json -o catalog.json

# Export to CSV or NodeSet2 XML
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f csv -o catalog.csv
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://192.168.1.50:4840 -f nodeset2 -o nodeset2.xml
```

### 2. Unmasking Hidden Tags (`diff-live`)
```bash
dotnet run --project src/Opc.Ua.Atlas -- diff-live opc.tcp://192.168.1.50:4840 \
  --target-user admin --target-password admin123 \
  -o diff_report.md
```

### 3. Generate Turnkey Grafana Dashboard
```bash
dotnet run --project src/Opc.Ua.Atlas -- dashboard catalog.json \
  -o grafana_dashboard.json \
  -t "CNC Milling Center 01" \
  --machine "cnc_mill_01"
```

### 4. Run the Serving Layer (Prometheus + WebSocket + MES OEE)
```bash
dotnet run --project src/Opc.Ua.Atlas -- serve opc.tcp://192.168.1.50:4840 \
  --port 9090 \
  --machine "cnc_mill_01" \
  --pack siemens-s7-1500 \
  --of-id "OF-2026-9481" \
  --part-number "VALVE-BODY-50" \
  --target-parts 500 \
  --target-cycle-time 45.0 \
  --allow-write "nsu=http://opcfoundation.org/UA/CNC/;s=SpindleOverride"
```

### 5. Run Native Model Context Protocol (MCP) Server for AI Assistants
```bash
# Connect directly to Claude Desktop, Cursor, or Antigravity via stdio
dotnet run --project src/Opc.Ua.Atlas -- mcp --catalog catalog.json
```

---

## 🤖 AI Assistant Integration via Model Context Protocol (MCP)

Atlas implements a native Model Context Protocol (MCP) server over stdio (`atlas mcp`). Add Atlas to your Claude Desktop or Cursor configuration:

```json
{
  "mcpServers": {
    "atlas-opcua": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/Atlas/src/Opc.Ua.Atlas",
        "--",
        "mcp",
        "--catalog",
        "/path/to/catalog.json"
      ]
    }
  }
}
```

### Available MCP Tools:
- **`search_address_space`**: Search nodes by keyword, browse path, or MTConnect category (`Sample`, `Event`, `Condition`).
- **`read_node_value`**: Read current live telemetry, quality status (`Good`, `Uncertain`, `Bad`), and staleness.
- **`get_machine_health`**: Get edge gateway diagnostics, latency, read error counts, and active interlocks/alarms.
- **`calculate_oee`**: Reconcile live OT production counters with an MES work order to calculate real-time Availability, Performance, Quality, and TRS/OEE.
- **`generate_mapping_pack`**: Generate standardized Atlas JSON mapping packs using natural language instructions.

---

## 🧪 Simulation Testbed Included (5-Axis CNC)

Atlas ships with a high-fidelity 5-axis CNC Machining Center simulation server to test and showcase all features without hardware:

```bash
# Terminal 1: Launch the simulated CNC server on port 62541
dotnet run --project simulation/Opc.Ua.FakeCnc -- -p 62541

# Terminal 2: Run Atlas against it
dotnet run --project src/Opc.Ua.Atlas -- crawl opc.tcp://localhost:62541/FakeCncServer --pack siemens-s7-1500
```

---

## 📊 Live Metrics & Serving Endpoints

### Prometheus (`GET /metrics`)
```prometheus
# HELP opcua_spindle_speed_rpm spindle_speed telemetry reading
# TYPE opcua_spindle_speed_rpm gauge
opcua_spindle_speed_rpm{machine="cnc_mill_01",node_id="nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed"} 10002.7

# HELP opcua_parts_produced_total Total parts successfully machined in current shift
# TYPE opcua_parts_produced_total counter
opcua_parts_produced_total{machine="cnc_mill_01",node_id="nsu=http://opcfoundation.org/UA/CNC/;s=PartsProduced"} 142

# HELP opcua_spindle_thermal_drift_celsius Spindle thermal elongation drift relative to ambient baseline
# TYPE opcua_spindle_thermal_drift_celsius gauge
opcua_spindle_thermal_drift_celsius{machine="cnc_mill_01"} 16.5

# HELP opcua_time_to_starvation_seconds Time remaining until pallet stock starvation based on active cycle time
# TYPE opcua_time_to_starvation_seconds gauge
opcua_time_to_starvation_seconds{machine="cnc_mill_01"} 450.0

# HELP opcua_oee_overall_ratio Overall Equipment Effectiveness ratio (0.0 to 1.0)
# TYPE opcua_oee_overall_ratio gauge
opcua_oee_overall_ratio{machine="cnc_mill_01",order_id="OF-2026-9481",part_number="VALVE-BODY-50"} 0.7667

# HELP atlas_collector_uptime_seconds Atlas edge collector uptime in seconds
# TYPE atlas_collector_uptime_seconds counter
atlas_collector_uptime_seconds{machine="cnc_mill_01"} 3600.5

# HELP atlas_machine_connected Machine connectivity status (1=connected, 0=disconnected)
# TYPE atlas_machine_connected gauge
atlas_machine_connected{machine="cnc_mill_01"} 1
```

### Health & Self-Supervision (`GET /health`)
```json
{
  "status": "UP",
  "serverUri": "urn:rotarex:cnc:milling01",
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
  "connectedWsClients": 3,
  "readOnly": true
}
```

### MES Reconciliation (`GET /reconciliation`)
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

## 🛡️ Test Proof & Verification

| Test Suite | Tests | Result | Domain Capabilities Verified |
|---|---|---|---|
| `CrawlerAndCatalogTests` | 10 | **PASSED** | Batched reads, stable URI IDs, JSON/CSV/XML exports |
| `TypeDecodingAndFlatteningTests` | 7 | **PASSED** | ExtensionObject runtime decoding, struct flattening |
| `LiveProfilerTests` | 9 | **PASSED** | Passive signal classification, correlation matrix |
| `SemanticLabelerTests` | 7 | **PASSED** | Confidence provenance, regex mapping packs |
| `PrometheusAndDashboardTests` | 11 | **PASSED** | Prometheus exposition, state-sets, Grafana v10 schema |
| `QualityGuardTests` | 3 | **PASSED** | Micro-cut hysteresis (Uncertain before Bad), monotonic counter smoothing, staleness timeout |
| `SignalCategoryAndSemanticsTests` | 3 | **PASSED** | ANSI/MTConnect taxonomy (Sample, Event, Condition), mapping pack override, JSON serialization |
| `SyntheticMetricsTests` | 2 | **PASSED** | Spindle thermal drift ($\Delta^\circ\text{C}$), time-to-starvation, tool wear urgency |
| `OeeCalculatorTests` | 2 | **PASSED** | Reconciled Availability, Performance, Quality, TRS calculation, ETA estimation |
| `CollectorDiagnosticsTests` | 2 | **PASSED** | Self-supervision diagnostics, latency, read error counters, connectivity flag |
| `AtlasMcpServerTests` | 4 | **PASSED** | JSON-RPC 2.0 stdio protocol, tool discovery, address space search, live OEE, mapping pack authoring |
| **Total** | **60** | **100% PASS** | **End-to-End Industrial Reliability Verified** |

---

## 📄 License
Licensed under the [Apache License, Version 2.0](LICENSE).
OPC UA is a registered trademark of the OPC Foundation.
