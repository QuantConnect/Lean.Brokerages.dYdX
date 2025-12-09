using System;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OraclePriceDto
{
    [JsonProperty("oraclePrice")] public decimal OraclePrice { get; set; }

    [JsonProperty("marketId")] public uint MarketId { get; set; }
}