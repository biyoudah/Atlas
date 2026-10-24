<div align="center">

# ATLAS: Industrial OPC UA Telemetry Adapter & Semantic Discovery Engine

### One engine to discover, classify, and stream industrial machine address spaces — from raw PLC noise to production metrics in seconds.

[![Release v0.1.0](https://img.shields.io/badge/release-v0.1.0-blue?style=flat-square&logo=github)](https://github.com/atlas-industrial/atlas/releases)
[![Build & Tests](https://img.shields.io/badge/build-passing-brightgreen?style=flat-square&logo=dotnet)](https://github.com/atlas-industrial/atlas/actions)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![OPC UA Standard](https://img.shields.io/badge/OPC_UA-1.05_Compliant-005BA1?style=flat-square&logo=opcfoundation)](https://opcfoundation.org/)
[![Companion Models](https://img.shields.io/badge/Companion_Models-OPC_40501_%2F_40001-orange?style=flat-square)](https://umati.org/)
[![Metrics](https://img.shields.io/badge/Metrics-OpenMetrics_%2F_Grafana-E6522C?style=flat-square&logo=prometheus)](https://prometheus.io/)
[![License](https://img.shields.io/badge/license-Apache--2.0-yellow?style=flat-square)](LICENSE)

**[Interactive Cockpit](#-interactive-visual-cockpit)** · **[Quick Start](#-quick-start)** · **[The 95% Noise Problem](#-the-industrial-problem-why-raw-opc-ua-is-noise)** · **[How Atlas Works](#-how-atlas-works)** · **[Architecture](#-architecture--data-flow)** · **[Comparison](#-competitive-matrix)** · **[AI Agent MCP](#-ai-agent-integration-mcp)** · **[Configuration & Troubleshooting](#-troubleshooting--failure-modes)**

```bash
dotnet run --project src/Atlas -- ui
```

</div>

> [!IMPORTANT]
> **Production-Tested Against Live Industrial Reference Endpoints:**
> Atlas is validated against real-world open-source reference servers without synthetic shortcuts:
> - **VDW umati International MachineTool Testbench** (`opc.tcp://opcua.umati.app:4840`, OPC 40501 / 40001) in Germany.
> - **Eclipse Milo CTT Reference Server** (`opc.tcp://milo.digitalpetri.com:62541/milo`).
> - **Sterfive Multi-Namespace IoT Server** (`opc.tcp://opcuademo.sterfive.com:26543`).
> - **Industrial S7-1500 Controller & open62541 reference testbeds**.

---

## 🎯 The Industrial Problem: Why Raw OPC UA Is Noise

When connecting a standard SCADA, IoT gateway, or time-series database (Prometheus, InfluxDB) to an industrial CNC or PLC over OPC UA, engineers encounter a major stumbling block: **the vast majority of the exposed address space is uncurated noise.**

Our benchmark on the official international **umati MachineTool server (VDW)** revealed the reality:

```
[ umati OPC UA Server (opcua.umati.app:4840) — Depth 6 Crawl ]
┌─────────────────────────────────────────────────────────────────────────────┐
│ Total Discovered Nodes: 604                                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│ 🟩 Actionable Telemetry Signals:    30 nodes  ( 5.0%)  [Feed, Speed, State] │
│ 🟨 Empty Container Scaffolding:    287 nodes  (47.5%)  [Objects/Machines/...]│
│ 🟦 Type Dictionaries & Metadata:   286 nodes  (47.3%)  [Schemas & Tests]    │
│ 🟥 Defective / Corrupt Nodes:        1 node   ( 0.2%)  [OnlineAccess i=6095]│
└─────────────────────────────────────────────────────────────────────────────┘
  ➔ 95% OF THE RAW ADDRESS SPACE IS PURE NOISE.
```

If an engineer simply maps raw nodes into a database:
1. **Time-series explosion:** 95% of ingested tags are static folder descriptors or protocol metadata that generate zero production insight.
2. **Crash on corrupted nodes:** Nodes like `DeviceTopology/OnlineAccess (i=6095)` return `BadWaitingForInitialData` / `Null`, causing naive Prometheus scrapers or Telegraf agents to fail.
3. **Cryptic vendor registers:** Signals are named `ns=3;s="DB10"."Var_27"` instead of `feed_override` or `spindle_speed_rpm`.
4. **Shift counter resets:** Daily parts counters suddenly drop to zero when operators reset them at shift transitions, triggering artificial negative production spikes.

---

## ⚡ The Atlas Solution

**Atlas** acts as an intelligent discovery and normalization buffer between industrial controllers and modern observability stacks:

- **Deep Address-Space Discovery:** High-throughput batched BFS crawler that catalogs the hierarchy without overloading embedded PLC communication processors.
- **4-Tier Semantic Inference:** Automatically infers signal meaning (e.g. `feed_override`, `operating_mode`, `parts_produced_total`) using standard companion specifications (OPC 40501 MachineTool), vendor packs (Siemens, Fanuc, Mitsubishi), and heuristic confidence scoring.
- **QualityGuard Circuit Breaker:** Detects stale, disconnected, or defective nodes (such as `OnlineAccess`) and quarantines them instantly, preventing corrupt data from poisoning downstream time-series.
- **Turnkey OpenMetrics & Grafana:** Native Prometheus HTTP endpoint (`/metrics`) with automatic device/channel label injection and auto-provisioned Grafana v11 dashboards.
- **Live HTTP & WebSocket Streaming:** Serves `/api/live` snapshots and `/ws` multiplexed subscription streams for modern web dashboards.
- **On-Demand Address Space Scan API:** Crawl any reachable `opc.tcp://` endpoint dynamically on demand via `GET /api/scan?url=...&depth=6`.
- **Native AI Agent MCP Server:** Exposes read-only inspection tools over Model Context Protocol (stdio) for Claude Code, Cursor, Codex, and Gemini CLI.

---

## 🖥️ Interactive Visual Cockpit

Atlas ships with an embedded, zero-dependency modern dark-mode industrial cockpit. Run one command to launch it:

```bash
dotnet run --project src/Atlas -- ui
```

Or open `docs/index.html` directly in your browser.

```
┌─────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  ATLAS v0.1.0 Cockpit  [ 🔌 opc.tcp://opcua.umati.app:4840 ] [ ⚡ ANALYZE ]   ● Connected · 1000ms Polling │
├────────────────────────────────┬──────────────────────────────────┬─────────────────────────────────────┤
│  DISCOVERED NODES              │  ACTIONABLE TELEMETRY            │  QUALITYGUARD CIRCUIT               │
│  604 Nodes (Depth 6)           │  30 Signals (5.0%)               │  1 Anomaly Quarantined              │
├────────────────────────────────┴──────────────────────────────────┴─────────────────────────────────────┤
│  📡 ADDRESS SPACE TOPOLOGY: [■■■ Telemetry 5%][■■■■■■■■■ Folders 48%][■■■■■■■■ Dictionaries 45%][■ Stale 2%] │
├─────────────────────────────────────────────────┬───────────────────────────────────────────────────────┤
│  ADDRESS SPACE BROWSER (UaExpert Hierarchy)     │  NODE INSPECTOR & SEMANTIC INTELLIGENCE               │
│  ├── Objects                                    │  NodeId: nsu=http://example.com/BasicMachineTool/;i=58904
│  │   ├── Machines                               │  BrowsePath: .../Channel 1/FeedOverride               │
│  │   │   ├── BasicMachineTool                   │  Live Value: 26.95 % (Scraped in 2ms)                 │
│  │   │   │   └── Monitoring                     │  Inferred: ➔ feed_override (Confidence: 98%)         │
│  │   │   │       ├── 🟢 FeedOverride (26.9%)    │  Tier: Tier 1 StandardCompanionSpec (OPC 40501)       │
│  │   │   │       └── 🟢 OperatingMode (AUTO)    │  Evidence: EURange 0..150%, Path matches MachineTool  │
│  │   │   ├── FullMachineTool (67.5%)            ├───────────────────────────────────────────────────────┤
│  │   │   └── Hexagon PMM Gold (100.0%)          │  LIVE OSCILLOSCOPE (1s Interval)   [ ◀ 1–4 / 30 ▶ ]   │
│  │   └── DeviceTopology                         │  [~~~~~~~~~~~~~~~~~~~~/\~~~~~~~~~~~~~~~~~~~~~~~~~~~~] │
│  │       └── 🔴 OnlineAccess (Quarantined)      │  BasicMachineTool: 26.9%  ·  FullMachineTool: 67.5%   │
└─────────────────────────────────────────────────┴───────────────────────────────────────────────────────┘
```

### Features of the Cockpit:
- **Dynamic Endpoint Crawling:** Type any `opc.tcp://` endpoint URL and click `ANALYZE ADDRESS SPACE` to discover nodes in real time.
- **Reference Target Presets:** Test against validated public reference servers (umati, Milo, Sterfive, Siemens S7-1500) with instant fallback.
- **Uncovered Nodes Radar:** See exactly what percentage of the address space is actionable vs noise.
- **Interactive Tree Browser:** Click any node to inspect data types, live values, and semantic inference evidence.
- **Paginated Fleet Gauges:** Seamlessly browse all 30 actionable signals without cluttering the screen (`◀ 1–4 / 30 ▶`).
- **Live 1-Second Oscilloscope:** Real-time waveform canvas streaming feed overrides, speeds, and states directly from `/api/live`.
- **Direct Downstream Integration:** Instant links to `/metrics` (Prometheus), `/catalog` (JSON), `/api/live` (JSON), and Grafana.

---

## 🏗️ Architecture & Data Flow

```mermaid
flowchart TD
    subgraph OT["Industrial Shop Floor (OT Layer)"]
        CNC1["umati MachineTool<br/>(OPC 40501 / 40001)"]
        CNC2["Siemens S7-1500 / 840D<br/>(Proprietary DBs)"]
        CNC3["Fanuc / Mitsubishi<br/>(Industrial Controllers)"]
    end

    subgraph Atlas["Atlas Telemetry & Semantic Engine (:9090)"]
        Crawler["AtlasCrawler<br/>(BFS Depth & Batched Reads)"]
        Classifier["SemanticLabeler<br/>(4-Tier Inference Engine)"]
        Quality["QualityGuard<br/>(Hysteresis & Circuit Breaker)"]
        Exporter["PrometheusExporter<br/>(Device & Channel Labeling)"]
        WebUI["Embedded Web Cockpit<br/>(Interactive Visual Explorer)"]
        LiveAPI["/api/live & /api/scan<br/>(REST & WebSocket Gateway)"]
    end

    subgraph IT["Enterprise & Observability (IT Layer)"]
        Prom["Prometheus v3<br/>(:9091)"]
        Graf["Grafana v11<br/>(:3000)"]
        MES["MES / ISO 22400 TRS<br/>(/reconciliation)"]
        AI["AI Coding Agents<br/>(Claude, Cursor, Codex via MCP)"]
    end

    CNC1 -->|opc.tcp WAN| Crawler
    CNC2 -->|opc.tcp LAN| Crawler
    CNC3 -->|opc.tcp LAN| Crawler

    Crawler --> Classifier
    Classifier --> Quality
    Quality -->|Prunes 95% Noise| Exporter
    Quality -->|Quarantines Bad Nodes| WebUI
    Quality -->|Live State Updates| LiveAPI

    Exporter -->|Scrape 1s (2ms latency)| Prom
    Prom --> Graf
    Atlas -->|JSON| MES
    Atlas -->|stdio| AI
```

---

## 📊 Competitive Matrix

| Feature | Standard UaExpert | Kepware / ThingWorx | Telegraf (opcua input) | **Atlas Engine** |
| :--- | :---: | :---: | :---: | :---: |
| **Address Space Discovery** | Manual browsing | Manual tagging | Manual NodeId configuration | **Automated Deep Crawl (BFS)** |
| **Noise Filtering (95% Prune)** | ❌ None | ❌ None | ❌ None | **✅ Automatic Uncovered Radar** |
| **Semantic Inference** | ❌ None | ❌ None | ❌ None | **✅ 4-Tier (OPC 40501 + Heuristics)** |
| **Corrupt Node Circuit Breaker** | ❌ Displays Red Box | ❌ Unreliable | ❌ Scrape Fails / Drops | **✅ QualityGuard Quarantine** |
| **Prometheus Exporter** | ❌ None | ⚠️ Requires add-on | ⚠️ Raw numeric dump | **✅ Native OpenMetrics + Labels** |
| **Turnkey Grafana Dashboard** | ❌ None | ❌ Manual build | ❌ Manual build | **✅ Hot-Reloadable Provisions** |
| **Interactive Web UI** | ❌ Desktop only | ⚠️ Heavy Web Admin | ❌ None | **✅ Zero-Dependency Embedded Cockpit** |
| **Live REST / WebSocket API** | ❌ None | ⚠️ Proprietary SDK | ❌ None | **✅ `/api/live` & `/ws` Out of Box** |
| **AI Agent MCP Integration** | ❌ None | ❌ None | ❌ None | **✅ Native Model Context Protocol** |
| **Shift Counter Reset Handling** | ❌ None | ❌ None | ❌ Negative rate spikes | **✅ Accumulator Reconciliation** |
| **Footprint & Licensing** | Proprietary GUI | Commercial (\$\$\$\$) | Go Daemon | **Open-Source Apache-2.0 (.NET 8)** |

---

## 🚀 Quick Start

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.
- (Optional) [Docker & Docker Compose](https://www.docker.com/) for the full Prometheus + Grafana stack.

### 1. Launch Visual Cockpit (Browser)
```bash
# Automatically starts Atlas and opens the interactive UI
dotnet run --project src/Atlas -- ui

# Or specify a custom endpoint
dotnet run --project src/Atlas -- ui opc.tcp://opcua.umati.app:4840
```

### 2. Crawl & Catalog an Address Space
```bash
# Export discovered nodes with semantic tags to JSON
dotnet run --project src/Atlas -- crawl opc.tcp://opcua.umati.app:4840 --max-depth 6 -o catalog.json
```

### 3. Stream Telemetry to Prometheus
```bash
# Serve live metrics on port 9090
dotnet run --project src/Atlas -- serve opc.tcp://opcua.umati.app:4840 --catalog catalog.json --port 9090
```

Access endpoints:
- **Interactive Web Cockpit:** [http://localhost:9090/](http://localhost:9090/)
- **Live Telemetry Snapshot:** [http://localhost:9090/api/live](http://localhost:9090/api/live)
- **On-Demand Crawl API:** [http://localhost:9090/api/scan?url=opc.tcp://opcua.umati.app:4840](http://localhost:9090/api/scan?url=opc.tcp://opcua.umati.app:4840)
- **Prometheus Metrics:** [http://localhost:9090/metrics](http://localhost:9090/metrics)
- **JSON Address Space Catalog:** [http://localhost:9090/catalog](http://localhost:9090/catalog)
- **Health & Diagnostics:** [http://localhost:9090/health](http://localhost:9090/health)
- **MES / ISO 22400 OEE:** [http://localhost:9090/reconciliation](http://localhost:9090/reconciliation)
- **Multiplexed WebSocket Stream:** `ws://localhost:9090/ws`

### 4. Run with Docker Compose
To launch the full turnkey stack (Atlas + umati Reference Server + Prometheus v3 + Grafana v11):
```bash
docker compose up -d
```
Open Grafana at [http://localhost:3000](http://localhost:3000) (anonymous Admin enabled).

---

## 🤖 AI Agent Integration (MCP)

Atlas exposes a native **Model Context Protocol (MCP)** server over `stdio` so AI coding agents (Claude Code, Cursor, Codex, Gemini CLI) can safely query industrial telemetry without write-risk:

```json
{
  "mcpServers": {
    "atlas": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/Atlas", "--", "mcp", "--catalog", "catalog.json"]
    }
  }
}
```

Available MCP Tools:
- `opcua_get_catalog`: Browse discovered machine nodes and inferred semantics.
- `opcua_read_telemetry`: Read current live values and QualityGuard health states.
- `opcua_query_metrics`: Query Prometheus metric names and MTConnect classifications.
- `opcua_get_quarantine_log`: Inspect nodes isolated by QualityGuard.

---

## 📋 CLI Command Reference

```bash
atlas <command> [options]

COMMANDS:
  ui [endpoint-url]                 Launch interactive visual cockpit in default browser
  crawl <endpoint-url>              Crawl address space and generate catalog
  diff <base.json> <target.json>    Compare two catalogs to detect hidden tags / elevations
  diff-live <endpoint-url>          Compare anonymous vs authenticated view on live PLC
  dashboard <catalog.json>          Generate turnkey Grafana v10/v11 JSON dashboard
  serve <endpoint-url>              Serve Prometheus metrics (/metrics), Web UI (/) & WebSocket gateway (/ws)
  mcp                               Run native Model Context Protocol (MCP) server over stdio

OPTIONS:
  -d, --max-depth <depth>           Maximum browse recursion depth (default: 6 for WAN, 30 for local)
  -b, --batch-size <size>           Batch size for attribute reads (default: 200)
  --port <port>                     HTTP serving port (default: 9090)
  --catalog <file.json>             Load pre-crawled catalog for instant startup
  --pack <pack-name>                Apply vendor mapping pack (e.g. siemens_s7, fanuc)
  -u, --user <username>             Username for authenticated PLC sessions
  -p, --password <password>         Password for authenticated PLC sessions
```

---

## 🔧 Troubleshooting & Failure Modes

| Symptom / Error | Root Cause | Solution |
| :--- | :--- | :--- |
| `BadCertificateUntrusted` | Target PLC uses self-signed or internal PKI certificate. | Atlas enables `autoAcceptUntrustedCertificates: true` by default in CLI/development. In production, place trusted CA certificates in `%USERPROFILE%/.opcfoundation/pki/trusted`. |
| `BadTimeout / Connect Failure` | Network latency, firewall blocking port 4840, or high recursion depth. | Use `--max-depth 6` for WAN endpoints like umati.app. Check port connectivity with `Test-NetConnection -Port 4840 <host>`. |
| `Port 9090 Already in Use` | Another service or Atlas background daemon is listening on port 9090. | Specify a custom port via `--port 9095`, or terminate the lingering process (`Stop-Process -Id <PID>`). |
| `400 Bad Request on /api/scan` | Invalid endpoint URL format. | Ensure the URL starts with `opc.tcp://` (e.g. `opc.tcp://127.0.0.1:4840`). HTTP/HTTPS schemes are rejected. |
| `Stale Quality / BadWaitingForData` | PLC node has uninitialized memory or sensor micro-cut. | QualityGuard automatically flags the signal as `Uncertain` or `Bad` without dropping the scrape. Check `/health` to see quarantined anomalies. |

---

## 🧪 Automated Testing

Atlas includes an automated test suite with **92 tests** (100% pass rate) covering:
- HTTP server routing, pre-flight CORS, `/api/live` snapshots, `/api/scan` dynamic crawls, and HEAD handling.
- OPC UA address-space tree traversal, continuation points, and batch decoding.
- MTConnect and OPC 40501 MachineTool semantic classification.
- QualityGuard hysteresis, micro-cut resilience, and counter reconciliations.
- Prometheus v3 OpenMetrics serialization and newline formatting.
- Grafana dashboard generation and device label transformations.

```bash
dotnet test Atlas.slnx
# Réussi!  - échec : 0, réussite : 92, durée : 4 s
```

---

## 📜 License

Atlas is licensed under the **Apache License 2.0**. See [LICENSE](LICENSE) for details.
Built on the official **OPC Foundation .NET Standard** stack.
