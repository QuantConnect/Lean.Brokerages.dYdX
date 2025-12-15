using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OrderbookEntry
{
    public decimal Price { get; set; }
    public decimal Size { get; set; }
}