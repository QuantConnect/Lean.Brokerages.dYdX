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

namespace QuantConnect.Brokerages.dYdX.Models;

public class Candle
{
    public string StartedAt { get; set; }

    public string Ticker { get; set; }

    public string Resolution { get; set; }

    public decimal Low { get; set; }

    public decimal High { get; set; }

    public decimal Open { get; set; }

    public decimal Close { get; set; }

    public decimal BaseTokenVolume { get; set; }

    public decimal UsdVolume { get; set; }
}