/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

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