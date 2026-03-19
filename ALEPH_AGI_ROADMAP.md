# Project Aleph: AGI Architecture & Roadmap

## Core Architecture: The Cybernetic Triad & Shared Kernel
Aleph is an autonomous, homeostatic organism utilizing Domain-Driven Design (DDD):
1. **Axiom (The Senses):** Data ingestion, external tool execution, perception.
2. **Arbiter (The Conscious Mind):** AI cognition, dynamic tool discovery (Reflection), capability hot-swapping.
3. **Aether (The Internal Body):** The dark-box environment. Hosts autonomic organs (Heart, Liver, Kidneys, ML Cortex) and runs the swappable Python cognitive bricks.
* **Circulation (Shared Kernel):** The `AlephBus`. A high-performance event bus circulating `MarketDataEvent`, `MetabolicEvent`, and `PredictionEvent` blood cells.

## 🚀 CURRENT PHASE: Phase 9 (The Sleep Cycle & Hot-Swappable Cortex)

### Pillar 1: The Circulatory & Metabolic Pipeline [✅ COMPLETED]
* Raw data is ingested by Axiom -> `MarketDataEvent`.
* The Liver digests data via canonical Python math -> `MetabolicEvent`.
* The ML Cortex creates cold-start predictions -> `PredictionEvent` + `pending.jsonl` memory.

### Pillar 2: The Sleep Cycle (Online Learning) [Current Focus]
* Build the `SleepCycleService` (an organ that runs daily).
* **Label Resolution:** Evaluate yesterday's `pending.jsonl` against today's actual market data.
* **Neuroplasticity:** Trigger `partial_fit` on the Python incremental models to continuously train the AI.

### Pillar 3: The Hot-Swappable "Domino Tower" Sandbox [Current Focus]
* Aether's Python environment must be strictly modular. Macro, Quant, and ML are isolated "bricks."
* C# communicates via strict JSON contracts. It is completely blind to the internal Python logic.
* **The Goal:** Prepare the system for Arbiter (the AI) to use MCP tools to write, evaluate, and overwrite these Python bricks, evolving its own intelligence without crashing the C# host.