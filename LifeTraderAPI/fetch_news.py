import sys
import yfinance as yf
import pandas as pd
from ta.momentum import RSIIndicator
from textblob import TextBlob
import threading # <--- The Tool for Speed

# --- GLOBAL VARIABLES (To store results from threads) ---
# We use these containers so threads can drop off their data
result_rsi = -1.0
result_news = "No Data"
result_sentiment = 0.0

# --- HELPER FUNCTIONS ---
def get_sentiment_score(text):
    if not text: return 0
    return TextBlob(text).sentiment.polarity

def get_rsi_data(symbol):
    """ Worker 1: Calculates RSI """
    global result_rsi # Tell Python we are writing to the global variable
    try:
        # Create a local ticker object (Thread-safe)
        ticker = yf.Ticker(symbol)
        hist = ticker.history(period="1mo")
        
        if not hist.empty:
            rsi_indicator = RSIIndicator(close=hist['Close'], window=14)
            result_rsi = rsi_indicator.rsi().iloc[-1]
        else:
            result_rsi = -1.0
    except:
        result_rsi = -1.0

def get_news_data(symbol):
    """ Worker 2: Fetches and Filters News """
    global result_news, result_sentiment
    try:
        ticker = yf.Ticker(symbol)
        news_list = ticker.news
        headlines = []
        total_score = 0
        count = 0
        
        # Keywords to search
        keywords = [symbol, ticker.info.get('shortName', '').split()[0]]

        if news_list:
            for item in news_list:
                content = item.get('content', {})
                title = content.get('title', 'No Title')
                summary = content.get('summary', '')
                pub_date = content.get('pubDate', '')[:10]
                
                # Filter Logic
                combined_text = (title + " " + summary).upper()
                is_relevant = any(k.upper() in combined_text for k in keywords)

                if is_relevant:
                    score = get_sentiment_score(title)
                    total_score += score
                    count += 1
                    
                    label = "NEUTRAL"
                    if score > 0.1: label = "POSITIVE"
                    elif score < -0.1: label = "NEGATIVE"
                    
                    headlines.append(f"- [{pub_date}] {title} [{label}]")
                if count >= 5: break
        
        if not headlines:
            headlines.append(f"No specific news found for {symbol}.")
            
        result_news = "\n".join(headlines)
        result_sentiment = total_score / count if count > 0 else 0

    except Exception as e:
        result_news = f"Error: {str(e)}"

# --- MAIN EXECUTION ---
if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Error: No symbol provided.")
        sys.exit(1)

    symbol = sys.argv[1].upper()

    # 1. SETUP THE THREADS (The Waiters)
    # We tell Thread 1 to run 'get_rsi_data' with 'symbol' as input
    thread_rsi = threading.Thread(target=get_rsi_data, args=(symbol,))
    
    # We tell Thread 2 to run 'get_news_data'
    thread_news = threading.Thread(target=get_news_data, args=(symbol,))

    # 2. START THE RACE (Launch them!)
    thread_rsi.start()
    thread_news.start()

    # 3. WAIT FOR FINISH LINE (Join)
    # This pauses the MAIN program until both threads are done
    thread_rsi.join()
    thread_news.join()

    # 4. REPORT RESULTS
    # Interpret RSI
    rsi_status = "NEUTRAL"
    if result_rsi == -1.0: rsi_status = "DATA ERROR"
    elif result_rsi >= 70: rsi_status = "OVERBOUGHT (SELL RISK)"
    elif result_rsi <= 30: rsi_status = "OVERSOLD (BUY CHANCE)"

    # Interpret Sentiment
    sent_status = "NEUTRAL"
    if result_sentiment > 0.1: sent_status = "BULLISH"
    elif result_sentiment < -0.1: sent_status = "BEARISH"

    print(f"--- TECHNICAL REPORT ({symbol}) ---")
    print(f"RSI (14-day): {result_rsi:.2f} [{rsi_status}]")
    print(f"SENTIMENT:    {result_sentiment:.2f} [{sent_status}]")
    print("-" * 30)
    print("TOP NEWS HEADLINES:")
    print(result_news)