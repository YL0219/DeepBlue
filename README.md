# 🌊 Deep Blue: Autonomous AI Market Agent

Deep Blue is a decoupled, multi-tier AI trading assistant. It allows users to execute trades, analyze market data, and view interactive financial charts entirely through natural language. 

## 🏗️ Architecture
The system is built on a 3-tier enterprise architecture to prevent context bloat and ensure thread-safe transaction execution:

1. **Frontend (Presentation):** Unity 3D (C#). Handles the UI Canvas and routes natural language prompts to the backend. It dynamically catches UI actions to render decoupled web views.
2. **Backend (Logic & State):** ASP.NET Core Web API (C#). Acts as the AI orchestrator. It manages the OpenAI `gpt-4o-mini` conversational loop, tool execution, and local SQLite database context via Entity Framework Core.
3. **Data Scrapers (Workers):** Python (`yfinance`, `textblob`). Invoked via hidden background processes to scrape live market data, calculate technical indicators, and fetch news sentiment.

## ✨ Key Features
* **Conversational Trading:** Ask the AI to buy or sell stock. The backend safely processes the transaction via a thread-safe `TradingService` and updates the SQLite database.
* **Interactive Charting:** Ask the AI to show a chart. Instead of hallucinating numbers, the backend generates an interactive TradingView candlestick chart (HTML/JS) and commands Unity to open it.
* **Persistent Memory:** Uses EF Core and SQLite to save the user's portfolio (`Positions`), trade receipts (`Trades`), and the AI's conversation history (`ChatMessages`).

## 🚀 How to Run Locally

### 1. Backend Setup
Because API keys are safely ignored by Git, you must create an `appsettings.json` file inside the `LifeTraderAPI` folder before booting the server:
```json
{
  "OpenAI": {
    "ApiKey": "YOUR_OPENAI_KEY_HERE"
  },
  "Finnhub": {
    "ApiKey": "YOUR_FINNHUB_KEY_HERE"
  }
}