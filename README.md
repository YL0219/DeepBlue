# 🌊 Deep Blue: Autonomous AI Quant Engine & Market Agent

Deep Blue is a decoupled, multi-tier AI trading assistant and Quant Data Engine. It allows users to execute trades, analyze market data, and view interactive financial charts entirely through natural language, backed by a robust asynchronous data lake.

## 🏗️ Enterprise Architecture
The system is built on a highly secure, thread-safe 3-tier architecture to prevent context bloat, race conditions, and OS-level deadlocks:

1. **Frontend (Presentation):** Unity 3D (C#). Handles the UI Canvas and routes natural language prompts to the backend. It dynamically catches UI actions to render decoupled web views.
2. **Backend Commander (Logic & State):** ASP.NET Core Web API (C#). Acts as the central orchestrator. It manages the OpenAI `gpt-4o-mini` conversational loop, isolated parallel tool execution (via EF Core DbContext scoping), and schedules asynchronous background services.
3. **Data Workers (The TET Pipeline):** Python (`openbb`, `yfinance`, `pandas`). Completely isolated in a local `.venv`. Invoked safely via a custom C# `ProcessRunner` (immune to command injection) to extract heavy market data, compress it into Parquet files, and return Pydantic-validated JSON contracts.

## ✨ Key Features
* **Asynchronous Data Lakes:** Heavy market data (OHLCV) is downloaded by background Python workers and stored locally in highly compressed `.parquet` files, while the metadata is indexed in SQLite.
* **Conversational Trading:** Ask the AI to buy or sell stock. The backend safely processes the transaction via a thread-safe `TradingService` utilizing optimistic concurrency.