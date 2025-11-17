using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.ToolBox.Models;

public class Symbol
{
    [JsonProperty("ticker")] public string Ticker { get; set; }

    [JsonProperty("status")] public string Status { get; set; }

    [JsonProperty("tickSize")] public string TickSize { get; set; }

    [JsonProperty("stepSize")] public string StepSize { get; set; }

    [JsonProperty("marketType")] public string MarketType { get; set; }
}