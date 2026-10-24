# Atlas Mapping Pack Authoring & Contribution Guide

Atlas mapping packs provide semantic intelligence over proprietary or unstructured industrial PLC / CNC address spaces (such as Siemens Simatic S7-1200/1500, Sinumerik 840D/ONE, Fanuc FOCAS/31i, Heidenhain TNC, Mitsubishi M800, Beckhoff TwinCAT, and Rockwell ControlLogix).

By contributing or customizing a mapping pack, you allow Atlas to automatically discover raw tag names (like `DB100.DBD24` or `R1000.2`) and map them to standard MTConnect / ISO 22400 semantics and Prometheus metrics without altering PLC code.

---

## 1. Schema Validation

All mapping packs must conform to the JSON schema located at:
[`schema/mapping-pack.schema.json`](../schema/mapping-pack.schema.json)

You can validate your pack locally using `dotnet test --filter MappingPackSchemaTests` or any standard JSON Schema Draft 2020-12 validator.

### Minimal Valid Pack Example

```json
{
  "$schema": "https://raw.githubusercontent.com/biyoudah/Atlas/main/schema/mapping-pack.schema.json",
  "name": "siemens-s7-1500-sinumerik",
  "vendor": "Siemens",
  "model": "S7-1500 / Sinumerik ONE / 840D sl",
  "version": "1.0.0",
  "rules": [
    {
      "pattern": "(DB10\\.(Var_27|SpindleSpeed)|.*Spindle.*(Speed|ActualSpeed).*)",
      "label": "spindle_speed",
      "unit": "rpm",
      "category": "Sample",
      "scale": 1.0,
      "description": "Main machining spindle rotational speed"
    },
    {
      "pattern": "(DB100\\.(PartCounter|PartsProduced)|.*PartsProduced|.*PartCounter)",
      "label": "parts_produced",
      "unit": "count",
      "category": "Event",
      "description": "Total finished parts counter"
    }
  ]
}
```

---

## 2. Specification Fields

| Field | Type | Required | Description |
|---|---|---|---|
| `name` | string | Yes | Unique hyphenated identifier (e.g. `siemens-s7-1500`, `fanuc-31i-b`). |
| `vendor` | string | No | Controller manufacturer (Siemens, Fanuc, Beckhoff, etc.). |
| `model` | string | No | Controller family or NC model line. |
| `version` | string | No | SemVer version string (e.g. `1.0.0`). |
| `rules` | array | Yes | Ordered list of pattern-matching rule objects. |

### Rule Object Properties

| Property | Type | Required | Description |
|---|---|---|---|
| `pattern` | string (regex) | Yes | Regular expression evaluated against `NodeId`, `BrowseName`, or browse hierarchy path. |
| `label` | string | Yes | Normalized snake_case metric identifier (e.g. `spindle_speed`, `parts_produced`, `machine_state`). |
| `unit` | string | No | Physical engineering unit (`rpm`, `bar`, `°C`, `mm/min`, `s`, `count`, `%`). |
| `scale` | number | No | Numerical multiplier to normalize raw PLC integers (e.g., `0.1` for decicelsius, `0.001` for millimeters). |
| `category` | string | No | MTConnect taxonomy classification: `Sample`, `Event`, or `Condition`. |
| `description`| string | No | Clear operational explanation of the signal. |

---

## 3. MTConnect & ISO 22400 Standard Labels

Atlas standardizes on canonical operational labels:

| Canonical Label | Typical Units | MTConnect Category | Description |
|---|---|---|---|
| `spindle_speed` | `rpm` | `Sample` | Real-time main spindle rotational velocity. |
| `spindle_override` | `%` | `Sample` | Spindle speed override potentiometer (50-120%). |
| `spindle_load` | `%` | `Sample` | Main spindle motor load / torque percentage. |
| `spindle_temperature` | `°C` | `Sample` | Spindle front bearing or stator winding temperature. |
| `feed_rate` | `mm/min` | `Sample` | Programmed axis feed velocity. |
| `feed_override` | `%` | `Sample` | Axis feed override potentiometer (0-150%). |
| `parts_produced` | `count` | `Event` | Monotonically increasing good-part counter. |
| `parts_defective` | `count` | `Event` | Scrap / non-conforming part counter. |
| `machine_state` | string / enum | `Event` | Operating status (`RUNNING`, `STOPPED`, `FEED_HOLD`, `FAULT`). |
| `coolant_pressure` | `bar` | `Sample` | High-pressure through-spindle cutting coolant pump. |
| `vibration_rms` | `mm/s` | `Sample` | Bearing accelerometer root-mean-square vibration level. |
| `cycle_time` | `s` | `Sample` | Nominal or active part machining cycle duration. |

---

## 4. Testing Your Pack

1. Save your JSON pack into `src/Atlas/Packs/<your-pack-name>.json`.
2. Run automated validation:
   ```bash
   dotnet test --filter MappingPackSchemaTests
   ```
3. Test live against a simulator or real machine:
   ```bash
   dotnet run --project src/Atlas -- serve -s opc.tcp://localhost:4840 -m src/Atlas/Packs/<your-pack-name>.json
   ```
4. Verify Prometheus metric output at `http://localhost:9090/metrics`.

---

## 5. Contributing to the Upstream Repository

1. Fork `https://github.com/biyoudah/Atlas`.
2. Create a feature branch: `git checkout -b pack/<vendor>-<model>`.
3. Add your pack to `src/Atlas/Packs/`.
4. Ensure `dotnet test Atlas.slnx` passes all tests.
5. Submit a pull request with an explanation of the controller model and sample tags tested.
