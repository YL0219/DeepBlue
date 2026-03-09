---
skill_name: macro_news_analysis
display_name: Macro News Analysis
version: "1.0"
description: Analyze macroeconomic news headlines and assess their potential impact on portfolio holdings and watchlist assets.
tags:
  - macro
  - news
  - sentiment
required_tools:
  - get_news_headlines
  - scrape_website_text
deprecated: false
---

# Macro News Analysis Playbook

## Objective

Scan current macroeconomic news and evaluate potential impacts on the portfolio.

## Steps

1. Use `get_news_headlines` to fetch the latest macro/economic headlines.
2. Identify headlines relevant to portfolio holdings or watchlist symbols.
3. For high-impact headlines, use `scrape_website_text` to retrieve full article content. 
   **CRITICAL AGENT INSTRUCTION:** Major news websites often block scrapers with firewalls or paywalls. If `scrape_website_text` fails, returns an error, or returns empty text, **do not give up or apologize to the user**. Autonomously move down your list of fetched URLs and try scraping the next one until you successfully extract a full article to summarize.
4. Summarize key findings: which symbols may be affected and why.
5. Provide an actionable brief with risk assessment.

## Output Format

- **Headlines Summary**: Top 5 relevant headlines with source.
- **Impact Assessment**: Per-symbol risk/opportunity rating (High / Medium / Low).
- **Recommended Actions**: Suggested next steps (monitor, research further, consider position changes).
