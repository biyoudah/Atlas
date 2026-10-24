---
name: Vendor Mapping Pack Proposal
about: Submit or propose a new vendor/machine mapping pack
title: '[PACK] '
labels: mapping-pack
assignees: ''
---

## Controller / Machine Details
- **Manufacturer:** (e.g. Siemens, Fanuc, Heidenhain, Haas)
- **Model / Family:** (e.g. Sinumerik 840D sl, Fanuc Series 31i-B)
- **OPC UA Server Version / Vendor:** (e.g. Internal Sinumerik Integrate, Kepware, IBHsoftec)

## Sample Tag Conventions
Provide examples of how registers or symbols are named on this controller:
- Spindle Speed: `...`
- Feed Rate Override: `...`
- Part Counter: `...`
- Machine Mode / State: `...`

## Proposed Rules (Draft JSON)
```json
{
  "$schema": "https://raw.githubusercontent.com/atlas-industrial/atlas/main/schema/mapping-pack.schema.json",
  "name": "vendor-model-name",
  "vendor": "Vendor",
  "model": "Model",
  "version": "1.0.0",
  "rules": [
    {
      "pattern": "...",
      "label": "spindle_speed",
      "unit": "rpm",
      "description": "..."
    }
  ]
}
```
