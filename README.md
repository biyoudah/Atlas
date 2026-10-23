# 🌐 Atlas — Industrial OPC UA Discovery & Turnkey Visualization Engine

[![Build & Test](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-Apache_2.0-green.svg)](LICENSE)
[![Status](https://img.shields.io/badge/Tests-44%2F44%20Passing-brightgreen.svg)]()
[![Standard](https://img.shields.io/badge/OPC_UA-v2.0-orange.svg)](https://opcfoundation.org/)

**Atlas** is an open-source discovery, profiling, and visualization engine for industrial OPC UA servers. It crawls undocumented PLC address spaces, unmasks hidden tags, decodes complex structures, resolves cryptic vendor registers (`DB10.Var_27`) into standardized semantics, and generates turnkey Grafana dashboards and Prometheus metrics in seconds.

---

## ⚡ Why Atlas? (Breaking the Industrial Vendor Moat)

Industrial automation has a data accessibility problem:
* **The "Cryptic Tag" Lock-in:** Programmable controllers (Siemens S7-1500, Fanuc, Mitsubishi) expose registers as `ns=3;s="DB10"."Var_27"`. Vendors push customers to buy expensive proprietary engineering tools (TIA Portal, GX Works, Fanuc CNC Screen) or hire certified integrators just to know what a variable means.
* **The "Middleware Tax":** Industrial middleware gateways (Kepware, Matricon, Siemens Industrial Edge) cost **$3,000 to $25,000+ per machine** in runtime licenses just to get data onto an open-source dashboard.
* **Hardware Session Limits:** PLCs and CNC controllers run on constrained embedded CPUs that often crash or refuse connections after **2 to 5 concurrent OPC UA sessions**.
* **Hidden Registers:** Plant owners frequently cannot see maintenance calibration data, cycle efficiency metrics, or vibration limits because integrators lock them behind password-protected blocks.

**Atlas eliminates these barriers completely:**
1. **Open Community Mapping Packs:** A community repository of regex rules that map raw PLC addresses to human-readable names (`spindle_speed`, `parts_produced`, `coolant_pressure`). Map a machine model once, and everyone benefits.
2. **One Session, Infinite Viewers:** Atlas maintains **one single persistent connection** to the machine controller and multiplexes data to hundreds of dashboards, Prometheus scrapers, and web clients simultaneously.
3. **Turnkey Grafana Dashboards:** One CLI command auto-generates a ready-to-import Grafana v10 dashboard with EURange gauges, stat cards, and state timelines.
4. **Cross-Identity Audit (`diff-live`):** Crawls the controller anonymously, crawls it with credentials, and generates an audit report exposing every hidden tag and elevated permission.
5. **Hard Industrial Safety Shield:** Network writes default to strict read-only. Writes to `EmergencyStop` or safety registers are **permanently blocked** at the protocol boundary.

---

## 🏗️ Architecture & The 5 Pipeline Stages

```
┌────────────────────────────────────────────────────────────────────────┐
│                   5-STAGE ATLAS DISCOVERY & SERVING PIPELINE           │
└────────────────────────────────────────────────────────────────────────┘
  [Stage 1] Exhaustive Crawl (Batched Reads + Stable URI IDs + XML/JSON/CSV)
      │
  [Stage 2] ExtensionObject Type Decoding (Complex Types -> Dotted Paths)
      │
  [Stage 3] Passive Live Profiler (Signal Classification + Correlations)
      │
  [Stage 4] Semantic Labeler & Mapping Packs (Confidence Scores & Provenance)
      │
  [Stage 5] Serving Layer (Prometheus /metrics + WebSocket /ws + Grafana v10)
```

### Stage 1: Exhaustive Discovery & Stable URIs
- Recursively browses hierarchical and non-hierarchical references (`HasTypeDefinition`, `Organizes`, `HasComponent`).
- Batches attribute reads (`BrowseName`, `DisplayName`, `DataType`, `ValueRank`, `AccessLevel`, `UserAccessLevel`, `EURange`, `EngineeringUnits`).
- Mints permanent URI-based identifiers (`nsu=http://vendor.com/plc/;s=DB10.Var_27`) instead of fragile numeric indices (`ns=3`), which change upon PLC reboot or code download.
- Exports to **JSON**, **CSV**, and standard OPC UA Part 6 `<UANodeSet>` XML.

### Stage 2: Complex Type Decoding & Flattening
- Resolves dynamic `ExtensionObject` structures at runtime without compiled C# types by reading `DataTypeDefinition` attributes.
- Flattens nested records into dot-notation paths (e.g. `Axis[0].ActualPosition`, `ToolData.Geometry.LengthOffset`).

### Stage 3: Passive Live Profiling & Cross-Identity Diffing
- Subscribes passively to live signals and categorizes them into:
  - `Counter`: Monotonically increasing numbers (part counters).
  - `Analog`: Continuously variable physical quantities (spindle speed, pressure, temperature).
  - `Discrete State`: String/enum execution indicators (`ACTIVE`, `STOPPED`, `FEED_HOLD`).
  - `Constant`: Read-only configuration constants.
- **`diff-live` Engine:** Compares the address space between Anonymous and Authenticated roles (e.g., Operator or Admin) to detect paywalled or hidden data blocks.

### Stage 4: Semantic Labeling & Community Mapping Packs
- Labels nodes with a 4-tier confidence score ($0.0$ to $1.0$) and transparent evidence provenance.
- Built-in community packs for:
  - `siemens-s7-1500-sinumerik` (Siemens S7-1500 & Sinumerik 840D)
  - `fanuc-31i` (Fanuc 30i/31i/32i Series CNCs)
  - `mitsubishi-m800` (Mitsubishi M800/M80 Series CNCs)
- Custom user packs can be dropped into the `Packs/` folder as JSON/YAML files.

### Stage 5: Serving Layer & Turnkey Dashboards
- **Prometheus Exporter (`/metrics`):**
  - Incorporates units directly into metric names: `_rpm`, `_bar`, `_celsius`, `_total`, `_seconds`.
  - State-set metric format: Emits string states as binary indicators (`opcua_actprog{state="..."} 1`), **preventing time-series churn**.
- **Multiplexed WebSocket (`/ws`):**
  - Push-based JSON streaming of live telemetry.
  - Subscriptions over messages (`{"action":"subscribe","nodes":[...]}`) instead of messy query strings.
- **Industrial Safety Gate:**
  - Strict read-only default.
  - Permanent block on Emergency Stop and safety-critical tags.
  - Explicit allowlisting with client ID audit logging.
- **Grafana v10 Dashboard Generator:** Auto-generates fully styled dark-mode dashboards with stat cards, EURange gauges, time-series graphs, and state timelines.

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

### 2. Run the Automated Tests (44 Tests)
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

### 4. Run the Serving Layer (Prometheus + WebSocket)
```bash
dotnet run --project src/Opc.Ua.Atlas -- serve opc.tcp://192.168.1.50:4840 \
  --port 9090 \
  --machine "cnc_mill_01" \
  --pack siemens-s7-1500 \
  --allow-write "nsu=http://opcfoundation.org/UA/CNC/;s=SpindleOverride"
```

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

## 📊 Live Metrics & WebSocket Protocol

### Prometheus (`GET /metrics`)
```prometheus
# HELP opcua_spindle_speed_rpm spindle_speed telemetry reading
# TYPE opcua_spindle_speed_rpm gauge
opcua_spindle_speed_rpm{machine="cnc_mill_01",node_id="nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed"} 10002.7

# HELP opcua_parts_produced_total Total parts successfully machined in current shift
# TYPE opcua_parts_produced_total counter
opcua_parts_produced_total{machine="cnc_mill_01",node_id="nsu=http://opcfoundation.org/UA/CNC/;s=PartsProduced"} 142

# HELP opcua_coolant_pressure_bar coolant_pressure telemetry reading
# TYPE opcua_coolant_pressure_bar gauge
opcua_coolant_pressure_bar{machine="cnc_mill_01",node_id="nsu=http://opcfoundation.org/UA/CNC/;s=CoolantPressure"} 22.85

# HELP opcua_actprog Discrete state indicator (1=active, 0=inactive)
# TYPE opcua_actprog gauge
opcua_actprog{machine="cnc_mill_01",node_id="nsu=http://siemens.com/simatic-s7-1500/cnc/;s=\"DB20_Production\".\"ActProg\"",state="O1024_TURBINE_BLADE_5AXIS.NC"} 1
```

### WebSocket Stream (`ws://localhost:9090/ws`)

#### 1. Subscribe to nodes:
```json
{"action":"subscribe","nodes":["nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed"]}
```
**Server Response & Stream:**
```json
{"event":"subscribed","count":1,"nodes":["nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed"]}
{"event":"value","id":"nsu=http://opcfoundation.org/UA/CNC/;s=ActualSpeed","browseName":"ActualSpeed","label":"spindle_speed","confidence":1,"value":10004.0,"unit":"rpm","timestamp":"2026-10-23T22:40:24Z"}
```

#### 2. Safety Interception (Attempting write to Emergency Stop):
```json
{"action":"write","node":"nsu=http://opcfoundation.org/UA/CNC/;s=EmergencyStop","value":true}
```
**Server Rejection:**
```json
{
  "event": "write_result",
  "node": "nsu=http://opcfoundation.org/UA/CNC/;s=EmergencyStop",
  "success": false,
  "message": "Rejected: Hard safety violation. Emergency stop and safety nodes cannot be written via network gateway."
}
```

---

## 🛠️ Community Mapping Pack Specification

Creating a mapping pack for your machine takes less than 20 lines of JSON/YAML:

```json
{
  "name": "my-custom-lathe-pack",
  "description": "Mapping profile for Okuma / Siemens lathe",
  "rules": [
    {
      "pattern": "DB10\\.Var_27|ActualSpindle",
      "label": "spindle_speed",
      "unit": "rpm",
      "description": "Main Lathe Spindle RPM"
    },
    {
      "pattern": "DB20\\.PartCounter",
      "label": "parts_produced",
      "unit": "total",
      "description": "Good parts counter"
    }
  ]
}
```

Drop the file into `src/Opc.Ua.Atlas/Packs/` and use it via `--pack my-custom-lathe-pack`.

---

## 🛡️ Test Proof & Verification

| Test Suite | Tests | Result | Coverage |
|---|---|---|---|
| `CrawlerAndCatalogTests` | 10 | **PASSED** | Batched reads, stable URI IDs, JSON/CSV/XML exports |
| `TypeDecodingAndFlatteningTests` | 7 | **PASSED** | ExtensionObject runtime decoding, struct flattening |
| `LiveProfilerTests` | 9 | **PASSED** | Passive signal classification, correlation matrix |
| `SemanticLabelerTests` | 7 | **PASSED** | Confidence provenance, regex mapping packs |
| `PrometheusAndDashboardTests` | 11 | **PASSED** | Prometheus exposition, state-sets, Grafana v10 schema |
| **Total** | **44** | **100% PASS** | End-to-end integration verified |

---

## 📄 License
Licensed under the [Apache License, Version 2.0](LICENSE).
OPC UA is a registered trademark of the OPC Foundation.
