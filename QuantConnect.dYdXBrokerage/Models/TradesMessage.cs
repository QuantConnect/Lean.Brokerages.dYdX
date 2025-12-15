using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class TradesMessage
{
    [JsonProperty("trades")] public List<TradeEntry> Trades { get; set; }
}