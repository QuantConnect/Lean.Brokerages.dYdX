using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class dYdXPerpetualPosition
{
    [JsonProperty("market")] public string Symbol { get; set; }
    [JsonProperty("status")] public Enums.PositionStatus Status { get; set; }
    [JsonProperty("side")] public Enums.PositionSide Side { get; set; }

    [JsonProperty("size")] public decimal Size { get; set; }
    [JsonProperty("maxSize")] public decimal MaxSize { get; set; }

    [JsonProperty("entryPrice")] public decimal EntryPrice { get; set; }
    [JsonProperty("exitPrice")] public decimal ExitPrice { get; set; }

    [JsonProperty("realizedPnl")] public decimal RealizedPnl { get; set; }
    [JsonProperty("unrealizedPnl")] public decimal UnrealizedPnl { get; set; }

    [JsonProperty("createdAt")] public System.DateTime? CreatedAt { get; set; }
    [JsonProperty("createdAtHeight")] public long? CreatedAtHeight { get; set; }
    [JsonProperty("closedAt")] public System.DateTime? ClosedAt { get; set; }

    [JsonProperty("sumOpen")] public decimal SumOpen { get; set; }
    [JsonProperty("sumClose")] public decimal SumClose { get; set; }
    [JsonProperty("netFunding")] public decimal NetFunding { get; set; }

    [JsonProperty("subaccountNumber")] public int SubaccountNumber { get; set; }

    [JsonIgnore]
    public decimal Quantity => Side switch
    {
        Enums.PositionSide.Short when Size > 0 => -Size,
        _ => Size
    };
}