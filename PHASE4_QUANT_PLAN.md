# Project Aleph: Phase 4 Roadmap & Architecture

## Core Architecture: The Triad
We have evolved into a rigid, Domain-Driven Design (DDD) named **Project Aleph**. The system relies on three sovereign sectors:

1. **Axiom (Sector_Core):** The Brain Stem. Handles databases, web scrapers, API controllers, and physical execution.
2. **Arbiter (Sector_AI):** The Mind. Handles cognitive routing, LLM agents, and playbook execution.
3. **Aether (Sector_Quant):** The Math. A dark-box environment for Bayesian math and Python probability matrices.

**The Evolution Loop Concept:**
Arbiter is designed to evolve Aether. Arbiter analyzes market narratives, and if the data shifts, Arbiter can trigger an update to Aether's probability weights. *Strict Boundary Rule: Arbiter cannot touch Aether directly. It must pass an `EvolutionRequest` to Axiom, and Axiom physically updates Aether.*

## 🚀 CURRENT PHASE: Phase 4 (The Triad & The Aether Matrix)

### Pillar 1: The Master Scripts (Immediate Focus)
We must lock in the C# boundaries before writing more logic.
* [ ] Create `IAxiom` / `Axiom.cs` (The Central Hub).
* [ ] Create `IArbiter` / `Arbiter.cs` (Injects IAxiom).
* [ ] Create `IAether` / `Aether.cs` (Injects IAxiom).
* [ ] Wire them into `Program.cs`.

### Pillar 2: Asynchronous Data Lakes (Upcoming)
Replace obsolete live-fetch Python scripts with robust Background Data Lakes.
* Design SQLite-backed data vaults for News Extraction and SEC filings.
* Update Arbiter's MCP tools to query these local databases instantly, ensuring the Agent never waits on fragile web-scrapers.

### Pillar 3: The Quant Engine & Evolution
* Establish the Python environment within the `Aether` sector.
* Build the Bayesian probability matrix scripts that consume the Data Lake.
* Implement the Arbiter -> Axiom -> Aether Evolution Loop.

## Execution Policies
* **Python Environment:** Backend MUST run Python using `PythonPathResolver`.
* **Process Calls:** MUST use `ProcessRunner.RunAsync` with `ArgumentList`. No string concatenated arguments.
* **Observability:** All logging via `ILogger<T>` (Serilog). Standard prefixes: `[Axiom]`, `[Arbiter]`, `[Aether]`, `[Python]`.