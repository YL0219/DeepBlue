# Aleph Cell Rules — Phase 10

This document defines the **micro-architecture** of Aleph: how things below the Triad are structured. It is intentionally lightweight. Phase 10 does not introduce manifests, registries, or hot-swap loaders.

For the macro architecture, see `PROJECT_DOCTRINE.md`. For what currently exists, see `ORGAN_INVENTORY.md`.

---

## The Hierarchy

```text
Sector → Organ → Cell → Brick
```

| Level   | Definition                                                          | Example                                                      |
|---------|---------------------------------------------------------------------|--------------------------------------------------------------|
| Sector  | One of Axiom / Arbiter / Aether / Circulation                       | `Aleph/Axiom/`                                               |
| Organ   | A coherent subsystem within a sector, usually a top-level subfolder | `Aleph/Axiom/Memory/`, `Aleph/Aether/MlCortex/`              |
| Cell    | The smallest independently testable functional unit                 | `MarketStressDetector`, `MetabolicArtifactWriter`, `LiverService` |
| Brick   | A leaf implementation detail under a cell                           | A regex, a parser, a Python script, a small helper           |

Cells are the unit of replacement. Bricks are not.

---

## What Is a Cell?

A type qualifies as a Cell if and only if it:

1. Has a single, narrow responsibility expressible in one sentence.
2. Has a stable input shape and a stable output shape.
3. Can be exercised in isolation by a unit or integration test.
4. Belongs cleanly to one organ in one sector.
5. Does not silently reach across sector boundaries (no Aether → broker, no Arbiter → DB writes, etc.).

If a type satisfies (1) through (5), name it like a cell, test it like a cell, and document its boundary.

If a type fails any of these, it is **not** a cell. It is either a brick (smaller, internal) or a candidate for splitting.

---

## What Is **Not** a Cell

- A `Controller` is not a cell. It is an HTTP edge adapter.
- A DTO / record / model is not a cell.
- A `Program.cs` registration block is not a cell.
- A sector-root aggregate like `Axiom.cs` or `Aether.cs` is **not** a single cell — it is an organ assembly that exposes gateways. Treat each gateway as its own cell.
- A static helper (`SymbolValidator`, `NormalizeSymbol`) is a brick, not a cell.

---

## Lightweight Cell Requirements

Phase 10 keeps cell metadata **in the code itself**, not in manifest files.

For each cell, the following should be discoverable by a reader of the source:

| Field         | Where it lives                                       |
|---------------|------------------------------------------------------|
| CellId        | The type's fully-qualified name                      |
| Sector        | The top-level folder (`Axiom`/`Arbiter`/`Aether`/`Circulation`) |
| Organ         | The immediate subfolder                              |
| Input         | The public method signatures or request records      |
| Output        | The return types or response records                 |
| Test fixture  | A corresponding test in `Aleph.Tests/`               |
| Boundary rule | The cell's constructor dependencies — they must match its sector's allowed dependencies (see `PROJECT_DOCTRINE.md` "Forbidden Dependencies") |

No XML attribute, no manifest YAML, no central registry. The folder structure and constructor signature **are** the manifest.

---

## God Cell Ban

A **God Cell** is any single type that:

- observes the real world, **and**
- reasons about it, **and**
- simulates over it, **and**
- executes against it, **and**
- persists the result.

Aleph's whole reason for the Triad split is to make God Cells syntactically impossible. If you find yourself writing a class that does more than one of {observe, reason, simulate, execute, persist}, split it before merging.

`Axiom.cs`, `Aether.cs`, and `Arbiter.cs` are **gateway aggregators**, not god cells — they expose gateways that each delegate to a single-responsibility implementation.

---

## Manifest Discipline

Phase 10 does **not** introduce a manifest or registry system.

Manifests are deferred until there is a real need:

- Python ML bricks that need versioning and rollback
- Strategy scripts that hot-swap
- Broker gateway adapters that need shadow-mode
- External data providers with multiple candidates
- Self-modification candidates

If and when manifests arrive, they will live next to the cell they describe (e.g. `Aether/MlCortex/MlCortexService.manifest.yaml`), **not** in a central registry. We will not build a `CellRegistry` service first and then look for cells to put in it.

---

## Cross-Sector Communication

Preferred, in order:

1. **Circulation events** (`AlephBus.PublishAsync(...)`) — fully decoupled.
2. **Sector contract interfaces** (`IAxiom`, `IArbiter`, `IAether`) — coupled to the public contract only.
3. **Direct concrete-type DI** — only inside a sector. Never across.

Known existing exception:

- `Aether` currently takes `IAxiom` in its constructor to dispatch Python through `axiom.Python.RunJsonAsync(...)`. The Python infrastructure (dispatcher, process runner) is an Axiom organ; the Python logic is an Aether organ. This is a deliberate boundary compromise documented here so it stops being re-debated.

If you find a new cross-sector concrete dependency, treat it as a bug and route it through a Circulation event or an `IAxiom`-style contract.

---

## Naming

- Cells: noun, ends in `Service`, `Detector`, `Writer`, `Orchestrator`, `Cache`, `Registry`, `Validator`, `Gateway`. Avoid `Manager` and `Helper`.
- Organs: PascalCase folder name singular (`MlCortex`, `Memory`, `Perception`).
- Bricks: anything that doesn't fit the cell test above — keep them small and private where possible.

---

## When in Doubt

> Label the organs. Protect the boundaries. Test the cells. Move surgically.

If a change touches more than one sector or more than one organ, it is no longer "small." Stop, split it, and ask.
