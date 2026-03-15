# 🧬 Project Aleph: Autonomous Cybernetic Quant Engine

Project Aleph is an advanced, biologically-inspired AI trading agent powered by Quant Data Engine. Moving beyond rigid, hardcoded workflows, Aleph operates as an autonomous, homeostatic organism. It perceives the market, regulates its own internal stress levels, dynamically discovers its own capabilities, and executes trades via natural language.

## 🏗️ The Biological Architecture (Event-Driven)
The system has evolved from a standard 3-tier architecture into a decoupled, highly concurrent **Event-Driven Architecture (EDA)**, designed to mimic human biology:

* **The Heart & Endocrine System (Homeostasis):** A background cybernetic heartbeat that regulates internal states (Stress, Fatigue, Overload). It features reflexive panic triggers (e.g., reacting to VIX spikes) and autonomic decay.
* **The Bloodstream (AlephBus):** A high-performance, thread-safe, non-blocking fan-out message bus (`System.Threading.Channels`). Organs communicate entirely by publishing and consuming standardized "Blood Cells" (`AlephEvent`s).
* **The Conscious Mind (Arbiter & Dynamic MCP):** The AI brain (`gpt-4o-mini`). It uses a cutting-edge **Dynamic Reflection Registry** to scan its own assembly on startup, automatically wiring up tools (Model Context Protocol) without hardcoded routing. 
* **The Senses (Axiom Perception):** Uses a Hybrid Perception Model. It fetches years of historical data from local, highly-compressed `.parquet` data lakes, overlaid with live API quotes, allowing the AI to consciously focus on any symbol on demand.
* **The Kidneys & Liver (Metabolism & Memory):** Background consumers attached to the AlephBus. The "Kidneys" asynchronously persist autonomic vitals to SQLite. The "Liver" digests raw `MarketDataEvent`s, calculates technical indicators, and publishes clean `MetabolicEvent` energy for the brain and trading execution layers.

## 🛠️ The Tech Stack Pipeline
The system strictly enforces Domain-Driven Design (DDD) to prevent OS-level deadlocks and context bloat:

1. **Frontend (Presentation):** Unity 3D (C#). Handles the interactive UI Canvas, routing natural language prompts to the backend and rendering decoupled web views.
2. **Backend Commander (C# ASP.NET Core):** The central orchestrator managing the conversational loop, Dependency Injection, the `AlephBus` circulatory system, and optimistic concurrency for paper trading.
3. **Data Workers (Python):** Completely isolated in a local `.venv` using `openbb`, `yfinance`, and `pandas`. They are invoked safely via a custom C# `ProcessRunner` with SemaphoreSlim concurrency gates to prevent "Thundering Herd" API rate limits.

## ✨ Key Capabilities
* **Active Attention (Watchlist Escape):** The AI is not trapped by background watchlists. It can dynamically fetch, digest, and memorize data for any global ticker symbol upon user request.
* **Self-Awareness (Homeostatic Telemetry):** The AI can read its own vital signs. If market volatility spikes, the system automatically injects "Adrenaline," altering the heartbeat cycle and notifying the AI.
* **AGI-Ready Immune System:** Because tools and skills are registered via C# Reflection, the system is primed for future autonomous self-modification (writing and hot-swapping its own C# scripts).