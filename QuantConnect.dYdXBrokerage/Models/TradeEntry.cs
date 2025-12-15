using System.Collections.Generic;
using Newtonsoft.Json;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.dYdX.Models;

public class TradeEntry
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("size")] public string Size { get; set; }
    [JsonProperty("side")] public OrderDirection Side { get; set; }
    [JsonProperty("price")] public string Price { get; set; }
    [JsonProperty("createdAt")] public string CreatedAt { get; set; }
    public decimal Quantity => Side == OrderDirection.Buy ? Size.ToDecimal() : -Size.ToDecimal();
}