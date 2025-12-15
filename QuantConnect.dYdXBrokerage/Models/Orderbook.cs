using System.Collections.Generic;
using Newtonsoft.Json;
using QuantConnect.Brokerages.dYdX.Models.Converters;

namespace QuantConnect.Brokerages.dYdX.Models;

public class Orderbook
{
    [JsonProperty("bids")]
    [JsonConverter(typeof(OrderbookEntryArrayConverter))]
    public List<OrderbookEntry> Bids { get; set; }

    [JsonProperty("asks")]
    [JsonConverter(typeof(OrderbookEntryArrayConverter))]
    public List<OrderbookEntry> Asks { get; set; }
}