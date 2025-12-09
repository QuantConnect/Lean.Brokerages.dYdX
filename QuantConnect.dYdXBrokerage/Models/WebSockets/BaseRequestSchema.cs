using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public abstract class BaseRequestSchema
{
    [JsonProperty("type")] public abstract string Type { get; }
    [JsonProperty("channel")] public string Channel { get; set; }

    [JsonProperty("id", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string Id { get; set; }
}