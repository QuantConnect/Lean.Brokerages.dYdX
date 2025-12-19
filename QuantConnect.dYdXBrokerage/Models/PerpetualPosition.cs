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

public class PerpetualPosition
{
    [JsonProperty("market")]
    public string Symbol { get; set; }
    public Enums.PositionStatus Status { get; set; }
    public Enums.PositionSide Side { get; set; }

    public decimal Size { get; set; }
    public decimal MaxSize { get; set; }

    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }

    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }

    public string CreatedAt { get; set; }
    public long CreatedAtHeight { get; set; }
    public string ClosedAt { get; set; }

    public decimal SumOpen { get; set; }
    public decimal SumClose { get; set; }
    public decimal NetFunding { get; set; }

    public int SubaccountNumber { get; set; }

    [JsonIgnore]
    public decimal Quantity => Side switch
    {
        Enums.PositionSide.Short when Size > 0 => -Size,
        _ => Size
    };
}