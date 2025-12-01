using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public abstract class BaseResponseSchema
{
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("connection_id")] public string ConnectionId { get; set; }
    [JsonProperty("message_id")] public int MessageId { get; set; }
}