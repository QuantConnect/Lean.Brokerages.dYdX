using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public class SubscribeRequestSchema : BaseRequestSchema
{
    public override string Type => "subscribe";
    [JsonProperty("batched")] public bool Batched { get; set; }
}