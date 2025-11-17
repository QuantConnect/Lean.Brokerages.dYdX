using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

/// <summary>
/// Minimal models for dYdX Indexer v4 perpetual positions endpoint
/// </summary>
public class dYdXPerpetualPositionsResponse
{
    /// <summary>
    /// Collection of open perpetual positions
    /// </summary>
    [JsonProperty("positions")]
    public List<dYdXPerpetualPosition> Positions { get; set; } = new();
}