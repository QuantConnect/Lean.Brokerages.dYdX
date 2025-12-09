using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class SubaccountsUpdateMessage
{
    [JsonProperty("blockHeight")] public uint? BlockHeight { get; set; }
    [JsonProperty("orders")] public List<OrderSubaccountMessage> Orders { get; set; }
    [JsonProperty("fills")] public List<FillSubaccountMessage> Fills { get; set; }
}