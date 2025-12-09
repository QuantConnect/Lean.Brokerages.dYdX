using System;
using Newtonsoft.Json;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.dYdX.Models;

public class FillSubaccountMessage
{
    [JsonProperty("orderId")] public string OrderId { get; set; }
    [JsonProperty("side")] public OrderDirection Side { get; set; }
    [JsonProperty("quoteAmount")] public decimal QuoteAmount { get; set; }
    [JsonProperty("price")] public decimal Price { get; set; }
    [JsonProperty("createdAt")] public string CreatedAt { get; set; }
    [JsonProperty("fee")] public decimal? Fee { get; set; }
    [JsonProperty("ticker")] public string Ticker { get; set; }
}