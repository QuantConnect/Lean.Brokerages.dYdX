using Newtonsoft.Json;
using System;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OrderDto
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("clientId")] public string ClientId { get; set; }
    [JsonProperty("side")] public OrderDirection Side { get; set; }
    [JsonProperty("size")] public string Size { get; set; }
    [JsonProperty("totalFilled")] public string TotalFilled { get; set; }
    [JsonProperty("price")] public string Price { get; set; }
    [JsonProperty("triggerPrice")] public string TriggerPrice { get; set; }
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
    [JsonProperty("timeInForce")] public string TimeInForce { get; set; }
    [JsonProperty("reduceOnly")] public bool ReduceOnly { get; set; }
    [JsonProperty("orderFlags")] public uint OrderFlags { get; set; }
    [JsonProperty("goodTilBlock")] public string GoodTilBlock { get; set; }
    [JsonProperty("goodTilBlockTime")] public string GoodTilBlockTime { get; set; }
    [JsonProperty("clientMetadata")] public uint ClientMetadata { get; set; }
    [JsonProperty("updatedAt")] public DateTime UpdatedAt { get; set; }
    [JsonProperty("postOnly")] public bool PostOnly { get; set; }
    [JsonProperty("ticker")] public string Ticker { get; set; }
}