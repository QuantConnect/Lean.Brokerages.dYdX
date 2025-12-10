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
using System;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OrderDto
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("clientId")] public string ClientId { get; set; }
    [JsonProperty("side")] public OrderDirection Side { get; set; }
    [JsonProperty("size")] public string Size { get; set; }
    [JsonProperty("totalFilled")] public string TotalFilled { get; set; }
    [JsonProperty("price")] public string Price { get; set; }
    [JsonProperty("triggerPrice")] public string TriggerPrice { get; set; }
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("status")] public string Status { get; set; }
    [JsonProperty("timeInForce")] public string TimeInForce { get; set; }
    [JsonProperty("reduceOnly")] public bool ReduceOnly { get; set; }
    [JsonProperty("orderFlags")] public uint OrderFlags { get; set; }
    [JsonProperty("goodTilBlock")] public string GoodTilBlock { get; set; }
    [JsonProperty("goodTilBlockTime")] public string GoodTilBlockTime { get; set; }
    [JsonProperty("clientMetadata")] public uint ClientMetadata { get; set; }
    [JsonProperty("updatedAt")] public DateTime UpdatedAt { get; set; }
    [JsonProperty("postOnly")] public bool PostOnly { get; set; }
    [JsonProperty("ticker")] public string Ticker { get; set; }
}