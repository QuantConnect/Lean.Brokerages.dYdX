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

public class dYdXPerpetualPosition
{
    [JsonProperty("market")] public string Symbol { get; set; }
    [JsonProperty("status")] public Enums.PositionStatus Status { get; set; }
    [JsonProperty("side")] public Enums.PositionSide Side { get; set; }

    [JsonProperty("size")] public decimal Size { get; set; }
    [JsonProperty("maxSize")] public decimal MaxSize { get; set; }

    [JsonProperty("entryPrice")] public decimal EntryPrice { get; set; }
    [JsonProperty("exitPrice")] public decimal? ExitPrice { get; set; }

    [JsonProperty("realizedPnl")] public decimal RealizedPnl { get; set; }
    [JsonProperty("unrealizedPnl")] public decimal UnrealizedPnl { get; set; }

    [JsonProperty("createdAt")] public string CreatedAt { get; set; }
    [JsonProperty("createdAtHeight")] public long CreatedAtHeight { get; set; }
    [JsonProperty("closedAt")] public string ClosedAt { get; set; }

    [JsonProperty("sumOpen")] public decimal SumOpen { get; set; }
    [JsonProperty("sumClose")] public decimal SumClose { get; set; }
    [JsonProperty("netFunding")] public decimal NetFunding { get; set; }

    [JsonProperty("subaccountNumber")] public int SubaccountNumber { get; set; }

    [JsonIgnore]
    public decimal Quantity => Side switch
    {
        Enums.PositionSide.Short when Size > 0 => -Size,
        _ => Size
    };
}