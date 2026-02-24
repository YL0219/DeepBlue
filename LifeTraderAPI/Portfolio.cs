using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace LifeTrader_AI // Make sure this matches your project's namespace
{
    // 1. THE BLUEPRINT: What does a single stock holding look like?
    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public int Shares { get; set; }
        public decimal AveragePrice { get; set; }
    }

    // 2. THE MANAGER: The guy who reads and writes the JSON file
    public class PortfolioManager
    {
        private readonly string _filePath = "portfolio.json";

        // Read the file and turn it into a list of Positions
        public List<Position> LoadPortfolio()
        {
            if (!File.Exists(_filePath))
            {
                return new List<Position>(); // Return an empty list if no file exists yet
            }

            string jsonString = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<Position>>(jsonString) ?? new List<Position>();
        }

        // Take a list of Positions and save it to the file
        public void SavePortfolio(List<Position> portfolio)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(portfolio, options);
            File.WriteAllText(_filePath, jsonString);
        }
    }
}