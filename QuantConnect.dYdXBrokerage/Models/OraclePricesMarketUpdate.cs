using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OraclePricesMarketUpdate
{
    [JsonProperty("oraclePrices")]
    public Dictionary<string, OraclePriceDto> OraclePrices { get; set; }
}