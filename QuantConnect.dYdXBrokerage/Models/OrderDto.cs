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

using System;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.dYdX.Models;

public class OrderDto
{
    public string Id { get; set; }
    public uint ClientId { get; set; }
    public OrderDirection Side { get; set; }
    public decimal Size { get; set; }
    public string TotalFilled { get; set; }
    public decimal Price { get; set; }
    public decimal TriggerPrice { get; set; }
    public string Type { get; set; }
    public string Status { get; set; }
    public string TimeInForce { get; set; }
    public bool ReduceOnly { get; set; }
    public uint OrderFlags { get; set; }
    public string GoodTilBlock { get; set; }
    public string GoodTilBlockTime { get; set; }
    public uint ClientMetadata { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool PostOnly { get; set; }
    public string Ticker { get; set; }
}