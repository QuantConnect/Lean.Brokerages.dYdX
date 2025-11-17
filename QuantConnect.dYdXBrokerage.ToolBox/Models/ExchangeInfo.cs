using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.ToolBox.Models;

/// <summary>
/// Represents dYdX market data response
/// https://docs.dydx.xyz/indexer-client/http/markets/get_perpetual_markets#get-perpetual-markets
/// </summary>
public class ExchangeInfo
{
    [JsonProperty("markets")] public Dictionary<string, Symbol> Symbols { get; set; }
}