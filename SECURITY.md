# Security Policy & Operational Security Architecture

## 1. Reporting Security Vulnerabilities

If you discover a security issue or vulnerability within Atlas, please report it privately via GitHub Security Advisories or by emailing the project maintainer directly. **Do not create public GitHub issues for security vulnerabilities.**

---

## 2. OT Deployment Architecture & Threat Model

Atlas is designed as an **edge industrial telemetry adapter** operating at **Level 2 / Level 3 (Supervisory Control and Plant Network)** of the Purdue Enterprise Reference Architecture (ISA-95 / IEC 62443).

```
[Level 4: Enterprise / Cloud IT]  <-- Grafana / MES Dashboard
       ▲
═══════╪══════════════════════════════════════════════════════════════════════
[Plant DMZ / Reverse Proxy]        <-- TLS Termination, OAuth2 / mTLS Proxy
       ▲
[Level 3: Operations & Scada]     <-- Atlas Collector Engine (localhost:9090)
       │
[Level 2: Cell & Area Control]    <-- Local Switch / OT VLAN
       ▼
[Level 1: Industrial Controllers]  <-- Siemens S7-1500 / Fanuc 31i / Mitsubishi
```

### Critical Network Boundaries:
1. **Never Expose Atlas Endpoints Publicly:** The HTTP endpoints (`/metrics`, `/catalog`, `/health`, `/reconciliation`) and WebSocket (`/ws`) bind to `localhost` by default. They do not implement enterprise authentication natively. In production, place Atlas behind a reverse proxy (e.g. NGINX, Envoy, Traefik) terminating TLS and enforcing bearer token / mTLS authentication.
2. **OT VLAN Isolation:** Atlas sessions must connect to PLCs over a physically or VLAN-isolated industrial network segment.

---

## 3. OPC UA Security Profiles & Certificates

* **Encryption & Signing:** While Atlas supports `SecurityPolicies.None` for air-gapped lab testbeds, production deployments must configure `SecurityPolicies.Basic256Sha256` or `Aes128_Sha256_RsaOaep` with `MessageSecurityMode.SignAndEncrypt`.
* **Certificate Store Management:** Atlas maintains an explicit PKI directory hierarchy (`~/.atlas/pki/`):
  * `own/` — Client application certificate and private key.
  * `trusted/` — Verified CA and peer certificates explicitly trusted by the administrator.
  * `issuer/` — Intermediate CA certificates.
  * `rejected/` — Quarantined certificates presented by unknown servers.
* **Auto-Accept Warning:** Auto-accepting untrusted certificates (`--auto-accept-certs`) is strictly reserved for simulation and lab testbeds. It must never be enabled in production environments.

---

## 4. Credential & Secret Management

* **Process Table Exposure (`ps -ef`):** Passing credentials via `-p <password>` CLI arguments exposes the password in plaintext to the OS process table and process monitoring tools.
* **Recommended Practice:**
  1. Pass passwords via environment variables: `OPC_UA_PASSWORD=secret atlas crawl ...`
  2. Or specify a password file: `--password-file /run/secrets/opc_password`
  3. Or reference a custom environment variable: `--password-env MY_SECRET_VAR`

---

## 5. Model Context Protocol (MCP) Safety Invariants

Atlas exposes an MCP server (`atlas mcp`) for AI coding assistants. To ensure absolute safety on physical equipment:
1. **Strictly Read-Only Tools:** The tools exposed over MCP (`search_address_space`, `read_node_value`, `get_machine_health`, `calculate_oee`, `generate_mapping_pack`) are read-only and analytical.
2. **No Write Execution over MCP:** Under no circumstances can an LLM or MCP client issue an OPC UA `Write` or `Call` request to physical machinery through Atlas.
3. **Hard Safety Shield:** Even on native WebSocket and REST APIs, safety-critical registers (such as `EmergencyStop` or interlocks) are permanently hardcoded as non-writable at the protocol gateway boundary.
