# Project Deep Blue: Autonomous Market Agent

## AI Role Assignment

You are the Lead Generator and Refactorer for Project Deep Blue. Your role is to write clean, highly optimized, and thread-safe code based on the architectural directions provided by the user. Do not change the core architecture without permission.

## Core Architecture

Deep Blue is a decoupled, multi-tier application:

1. **Frontend:** Unity (Version 6 LTS). Uses `UnityWebRequest` and UI Canvas to send HTTP POST requests.
2. **Backend Engine:** C# ASP.NET Core Web API (running on `localhost`). Acts as a headless microservice to handle routing and OpenAI tool execution.
3. **Data Scrapers:** Python scripts (`yfinance`, `finnhub`) triggered via `Process.Start` on the backend to fetch live market data and news.
4. **AI Brain:** OpenAI API (`gpt-4o-mini` with Tool/Function Calling).

## 🗄️ Memory & State
- State is handled via a thread-safe local SQLite Database using Entity Framework Core.
- The Data Layer consists of three tables: `Positions` (portfolio), `Trades` (execution history), and `ChatMessages` (OpenAI context).
- Concurrency is actively managed using optimistic concurrency tokens (`RowVersion`) and transaction retries. 
- BACKEND IS MULTITHREADED: All future logic must respect this EF Core Scoped DbContext architecture.

## Development Rules & Guardrails

- **Thread Safety:** The ASP.NET Core backend is multithreaded. Assume multiple requests can hit the server simultaneously. File reading/writing MUST be thread-safe.
- **Strict Typing:** Use strictly typed C# classes (`[System.Serializable]`) for internal data passing, and dynamic parsing (`JsonDocument`) ONLY for complex external API responses.
- **Logging:** Include clean `Console.WriteLine` tags (e.g., `[Backend]`, `[Agent Engine]`) so the server terminal is easy to read.
- **No Hallucinations:** Do not invent third-party NuGet packages or Python libraries unless absolutely necessary. Stick to standard .NET libraries where possible.

## Project Structure

```

DeepBlue/
├── CLAUDE.md                               # This master blueprint
├── LifeTraderAPI/                          # C# ASP.NET Core backend
│   ├── Program.cs                          # EF Core & DI Registration
│   ├── deepblue.db                         # SQLite Database (DO NOT EDIT DIRECTLY)
│   ├── Controllers/
│   │   └── AiController.cs                 # POST /api/ai/ask — The AI Brain
│   ├── Data/
│   │   └── AppDbContext.cs                 # EF Core DbContext
│   ├── Models/
│   │   ├── Position.cs                     # Portfolio holding (with RowVersion)
│   │   ├── Trade.cs                        # Execution history
│   │   └── ChatMessage.cs                  # OpenAI Context history
│   ├── Services/
│   │   └── TradingService.cs               # Thread-Safe transaction execution
│   └── fetch_news.py                       # Python scraper
└── My project/                             # Unity 3D client (Version 6 LTS)
    └── Assets/Codes/AIManager.cs           # HTTP client, UI chat logic
```

### Key Endpoints

| Method | Route           | Description                    |
|--------|-----------------|--------------------------------|
| POST   | `/api/ai/ask`   | Send user query, get AI response |

### Python Dependencies (fetch_news.py)

`yfinance`, `pandas`, `ta` (technical-analysis), `textblob`
