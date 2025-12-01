using Newtonsoft.Json;

namespace QuantConnect.Brokerages.dYdX.Models;

public class Symbol
{
    [JsonProperty("clobPairId")] public uint ClobPairId { get; set; }

    [JsonProperty("oraclePrice")] public decimal OraclePrice { get; set; }

    [JsonProperty("ticker")] public string Ticker { get; set; }

    [JsonProperty("status")] public string Status { get; set; }

    [JsonProperty("tickSize")] public string TickSize { get; set; }

    [JsonProperty("stepSize")] public string StepSize { get; set; }

    [JsonProperty("marketType")] public string MarketType { get; set; }
    [JsonProperty("stepBaseQuantums")] public ulong StepBaseQuantums { get; set; }
    [JsonProperty("atomicResolution")] public int AtomicResolution { get; set; }
    [JsonProperty("quantumConversionExponent")] public int QuantumConversionExponent { get; set; }
    [JsonProperty("subticksPerTick")] public uint SubticksPerTick { get; set; }
}