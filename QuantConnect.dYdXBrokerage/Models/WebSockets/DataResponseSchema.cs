using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public class DataResponseSchema<T> : BaseResponseSchema
{
    [JsonProperty("contents")] public T Contents { get; set; }
}