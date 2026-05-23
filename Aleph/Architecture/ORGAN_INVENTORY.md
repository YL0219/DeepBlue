# Aleph Organ Inventory — Phase 10

This is a **snapshot of the organs that already exist** in the repository, grouped by sector. It is not a wish-list. It is not a redesign. Every entry below corresponds to a directory or file that currently exists under `Aleph/`.

If you add a meaningful new organ, update this file in the same PR.

---

## Axiom — Reality Interface

Located under `Aleph/Axiom/`.

### Execution

- `Axiom/Execution/Trading/TradingService.cs` — real-money trade execution. **Frozen during Phase 10.**

### Infrastructure

- `Axiom/Infrastructure/ProcessRunner.cs`
- `Axiom/Infrastructure/PythonPathResolver.cs`
- `Axiom/Infrastructure/SymbolValidator.cs`
- `Axiom/Infrastructure/Mcp/` — MCP tool registry, invoker, schema adapter, and tool implementations (`McpMarketTools`, `McpExecutionTools`, `McpNewsTools`, `McpSkillTools`, `McpAetherTools`)
- `Axiom/Infrastructure/Skills/` — `FileSkillRegistry`, skill contracts
- `Axiom/Infrastructure/Python/` — Python dispatcher infrastructure

### Memory

- `Axiom/Memory/Data/AppDbContext.cs` — EF Core SQLite context
- `Axiom/Memory/Models/` — `Trade`, `Position`, `WatchlistItem`, `MarketDataAsset`, `IngestionReport`, `ChatMessage`, `ChatRequest`, `ToolRun`, `AutonomicEvent`
- `Axiom/Memory/AutonomicPersistenceService.cs` — kidney/persistence consumer for autonomic events

### Perception

- `Axiom/Perception/PerceptionSnapshotCache.cs`
- `Axiom/Perception/Ingestion/MarketIngestionOrchestrator.cs`
- `Axiom/Perception/Ingestion/MarketStressDetector.cs`
- `Axiom/Perception/Ingestion/ActiveSymbolSource.cs`
- `Axiom/Perception/Ingestion/PythonWorkerRunner.cs`
- `Axiom/Perception/Ingestion/IMarketStressDetector.cs`

### Python

- `Axiom/Python/python_router.py`
- `Axiom/Python/Workers/` — ingestion / news / web workers
- `Axiom/Python/Legacy/`
- `Axiom/Python/requirements.txt`, `setup_venv.ps1`

### Controller

- `Axiom/Controller/MarketController.cs`

### Sector Root

- `Axiom/Axiom.cs` — the `IAxiom` implementation; aggregates gateways: `Python`, `Market`, `MarketIngestion`, `Mcp`, `Trades`, `Chat`, `ToolRuns`, `Skills`, `Perception`

---

## Arbiter — Consciousness

Located under `Aleph/Arbiter/`.

**Status: thinnest sector. Phase 10.3 will deepen it.**

- `Arbiter/Arbiter.cs` — autonomous-agent loop with circuit breaker, parallel read-only tool execution, single state-changing tool per turn, MCP-mediated tool invocation
- `Arbiter/IArbiter.cs` — sector contract
- `Arbiter/Controller/AiController.cs`
- `Arbiter/Skills/` — markdown-based skill playbooks (`MacroNewsAnalysis.md` today)

Arbiter currently has **no dedicated memory, reasoning loop framework, or guardrail layer beyond the MCP turn-limits**. These are Phase 10.3 work.

---

## Aether — Quantitative Black Box

Located under `Aleph/Aether/`.

### Sector Root

- `Aether/Aether.cs` — `IAether` implementation; aggregates gateways: `Math`, `Ml`, `Sim`, `Macro`, `Regulation`
- `Aether/IAether.cs`, `Aether/IAlephOrgan.cs`

### Heart & Homeostasis

- `Aether/HeartbeatService.cs` — hosted background heartbeat
- `Aether/Homeostasis.cs`, `IHomeostasis.cs`, `IStressInjector.cs`
- `Aether/AutonomicEvent.cs`
- `Aether/PulseEnvelope.cs`

### Metabolism (Liver)

- `Aether/Metabolism/LiverService.cs` — hosted background metabolic digester
- `Aether/Metabolism/MetabolicArtifactWriter.cs`

### ML Cortex

- `Aether/MlCortex/MlCortexService.cs` — hosted background predictive consumer

### Sleep Cycle

- `Aether/SleepCycle/SleepCycleService.cs` — hosted offline learning orchestrator (kept; not aggressively expanded)

### Python

- `Aether/Python/aether_router.py`
- `Aether/Python/quant/`, `Aether/Python/macro/`, `Aether/Python/ml/`
- `Aether/Python/sim_manager.py`, `ml_manager.py`, `math_manager.py`, `macro_manager.py`
- `Aether/Python/cortex_dashboard.py`
- `Aether/Python/contracts/`

### Controller

- `Aether/Controller/DiagnosticsController.cs`

> Note: Aether's Python is currently dispatched through `Axiom.Python.RunJsonAsync(...)`. The Python *infrastructure* (dispatcher, path resolver, process runner) lives in Axiom; the Python *logic* lives in Aether. This is a known boundary compromise. See `CELL_RULES.md` for the rule of thumb.

---

## Circulation — Shared Bloodstream

Located under `Aleph/Circulation/` (stays at the root; do not move).

- `Circulation/AlephBus.cs`, `IAlephBus.cs`
- `Circulation/AlephEvent.cs`
- `Circulation/MetabolicEvent.cs`
- `Circulation/PredictionEvent.cs`
- `Circulation/BloodFilter.cs`
- `Circulation/Quarantine.cs`

Circulation has no controller, no Python, no DB. It is pure event plumbing.

---

## Sector Thickness Summary

| Sector       | Approx. surface area              | Phase 10 focus                                  |
|--------------|-----------------------------------|-------------------------------------------------|
| Axiom        | Largest — multi-organ, real-world | Boundary guard; do not expand trading           |
| Aether       | Large — heart + liver + cortex + sleep + python | Preserve; do not aggressively extend ML/Sleep |
| Arbiter      | Smallest — agent loop + 1 skill   | **Strengthen in 10.3**                          |
| Circulation  | Small but central                 | Keep at root; treat as stable contract          |

---

## What Is **Not** Here

These do not exist and should not be invented during Phase 10:

- a "Cognitive Labs" or fourth top-level domain
- a `CellRegistry` / `OrganManifest` system
- a hot-swap loader for Python bricks
- a separate `Simulation/` top-level directory
- a Unity frontend project in this repository

If a task tells you to create one of these, stop and report.
