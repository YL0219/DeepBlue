# Project Aleph — Phase 10 Doctrine

This document is the source of truth for the **macro architecture** of Project Aleph during Phase 10. It is intentionally short. For execution rules, see `../../CLAUDE.md`. For organ inventory, see `ORGAN_INVENTORY.md`. For cell rules, see `CELL_RULES.md`.

---

## Phase 10 Purpose

Phase 10 — **Cellular Triad Architecture** — is not a feature-expansion phase. It is a governance phase.

Goals:

1. Clarify sector boundaries before they erode under feature pressure.
2. Inventory the organs that already exist; do not rebuild them.
3. Make cells independently testable so future hot-swap is possible.
4. Strengthen Arbiter, which is currently the thinnest sector.
5. Prepare perception expansion safely without leaking into other sectors.

Phase 10 is **defensive**. It exists so later phases can move quickly without re-litigating where things belong.

---

## Triad Architecture

Aleph is governed by three macro sectors plus a shared bloodstream:

```text
Axiom   = Reality Interface / Brainstem / Senses / Hands and Feet
Arbiter = Consciousness / Memory / Reasoning / Arbitration
Aether  = Quantitative Black Box / Simulation / Machine Learning / Strategy Research
Circulation = AlephBus / Shared Events / Bloodstream
```

Circulation is **not** a fourth intelligence domain. It is the shared event bloodstream that lets the three sectors communicate without direct coupling.

---

## Sector Responsibilities

### Axiom — Reality Interface

The only sector permitted to touch the real world. Owns:

- market, news, and web ingestion
- external API and broker gateways
- real account state and the real portfolio ledger
- real trade execution
- MCP / tool execution infrastructure
- hard safety guards
- database-backed reality records (`AppDbContext`)

Must **not** perform strategic reasoning, ML research, or sandbox simulation.

### Arbiter — Consciousness

Owns reasoning, memory, planning, decision review, skill selection, tool orchestration, multi-agent coordination, and final arbitration.

May request actions; **must not** call broker APIs, modify real portfolio state, or bypass Axiom safety guards.

Currently the thinnest sector. Phase 10.3 will deepen it.

### Aether — Quantitative Black Box

Owns quant algorithms, technical indicators, signal generation, sandbox simulation, strategy research, ML Cortex, Sleep Cycle, Python cognitive bricks, and prediction/grading/training workflows.

Must **never** touch broker APIs, real funds, real orders, or real portfolio state.
Must **never** depend on Axiom internals.

Simulation and ML live **inside Aether**. They are not a fourth top-level domain.

### Circulation — Shared Bloodstream

Lives at `Aleph/Circulation/` and stays there. Owns:

- `AlephBus`, `IAlephBus`
- `AlephEvent`, `MetabolicEvent`, `PredictionEvent`
- `BloodFilter`
- `Quarantine`

Cross-sector communication should prefer Circulation events to direct cross-sector calls.

---

## Forbidden Dependencies

| Source     | Forbidden target                                          |
|------------|-----------------------------------------------------------|
| Aether     | `AppDbContext` (Axiom Memory)                             |
| Aether     | `TradingService` (Axiom Execution)                        |
| Aether     | broker gateways, real portfolio repositories, real order executors |
| Aether     | Axiom concrete internal types (depend on `IAxiom` contract only, and only where unavoidable) |
| Arbiter    | `TradingService` (must go through Axiom MCP/contract)     |
| Arbiter    | direct broker API calls                                   |
| Arbiter    | direct portfolio mutation services                        |
| Axiom      | strategic reasoning, ML training math, sandbox simulation logic |

These are enforced manually for now; Phase 10.2 introduces reflection-based tests under `Aleph.Tests/`.

---

## Trading Freeze

Phase 10 is a **hard freeze on new trading behavior**.

While Phase 10 is in effect:

- Do not add new trading paths.
- Do not enable real order execution that is not already enabled.
- Do not modify broker execution behavior.
- Do not modify portfolio mutation behavior unless explicitly asked.
- Do not add autonomous trading paths.

If a task appears to require touching trading or real-money execution, stop and surface the risk to the user.

---

## Current Development Priority

In rough order:

1. Doctrine and inventory (this document set).
2. Boundary tests under `Aleph.Tests/`.
3. Physical DI isolation per sector (`*Bootstrapper.cs`).
4. Arbiter strengthening (memory, reasoning loop, guardrails).
5. Perception readiness (safe extension within Axiom).
6. Aether quant/cell boundary clarity.
7. Future hot-swap system — **deferred** until the above is stable.

If a proposed change does not advance one of these priorities, it does not belong in Phase 10.
