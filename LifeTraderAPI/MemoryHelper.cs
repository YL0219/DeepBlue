using System.Text.Json;

namespace LifeTrader_AI;

public static class MemoryHelper
{
    public static List<object> LoadMemory(string filePath)
    {
        var history = new List<object>();

        // 1. SYSTEM PROMPT
        history.Add(new { 
            role = "system", 
            content = "You are 'MarketFlow', an elite hedge fund risk manager. " +
                      "Your goal is to protect capital, not chase gains. " +
                      "STYLE: Concise, cynical, military-grade brevity. " +
                      "CONSTRAINTS: 1. If the user gambles, insult them (mildly). " +
                      "FORMAT: End every response with '[Risk: LOW/MED/HIGH] | [Confidence: 0-100%]'" 
        });

        if (!File.Exists(filePath)) return history;

        try 
        {
            // 2. READ ALL LINES
            string[] lines = File.ReadAllLines(filePath);
            Console.WriteLine($"[System] Reading {lines.Length} lines from history...");

            // --- NEW: THE FILE CLEANER (Garbage Collection) ---
            // If the log file gets too big (over 50 lines), we cut the top half.
            if (lines.Length > 50)
            {
                // Keep only the last 20 lines
                var recentLines = lines.Skip(lines.Length - 20).ToArray();
                
                // Overwrite the file with the clean version
                File.WriteAllLines(filePath, recentLines);
                
                Console.WriteLine("[System] Log file was too large. Pruned old history.");
                
                // Update our local variable so we only parse the new short list
                lines = recentLines;
            }
            // --------------------------------------------------

            foreach (string line in lines)
            {
                // ATOMIC PARSER (Logic from previous session)
                int separatorIndex = line.LastIndexOf("| AI:");
                
                if (separatorIndex > 0)
                {
                    string userPart = line.Substring(0, separatorIndex);
                    string aiText = line.Substring(separatorIndex + 5).Trim(); 

                    int youIndex = userPart.IndexOf("You:");
                    
                    if (youIndex >= 0)
                    {
                        string userText = userPart.Substring(youIndex + 4).Trim();

                        history.Add(new { role = "user", content = userText });
                        history.Add(new { role = "assistant", content = aiText });
                    }
                }
            }

            // 3. MEMORY LIMIT (The Sync)
            // You wanted 5-6 items. 
            // 1 System + 6 Context = 7 Total Items.
            while (history.Count > 7) 
            {
                history.RemoveAt(1); // Remove the oldest message (after System Prompt)
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Memory Read Error: {ex.Message}");
        }

        return history;
    }
}