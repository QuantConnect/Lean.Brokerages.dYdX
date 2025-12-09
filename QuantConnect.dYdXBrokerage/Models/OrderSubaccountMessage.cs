using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OrderSubaccountMessage
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("clientId")] public uint ClientId { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
}