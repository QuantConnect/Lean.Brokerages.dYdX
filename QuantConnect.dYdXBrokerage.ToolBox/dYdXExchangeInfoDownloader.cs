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
using QuantConnect.ToolBox;
using System.Collections.Generic;
using Newtonsoft.Json;
using QuantConnect.Brokerages.dYdX.ToolBox.Models;
using QuantConnect.Configuration;

namespace QuantConnect.Brokerages.dYdX.ToolBox
{
    /// <summary>
    /// dYdX Brokerage implementation of <see cref="IExchangeInfoDownloader"/>
    /// </summary>
    public class dYdXExchangeInfoDownloader : IExchangeInfoDownloader
    {
        /// <summary>
        /// Market
        /// </summary>
        public string Market => QuantConnect.Market.dYdX;

        /// <summary>
        /// Get exchange info coma-separated data
        /// </summary>
        /// <returns>Enumerable of exchange info for this market</returns>
        public IEnumerable<string> Get()
        {
            var baseUrl = Config.Get("dydx-indexer-url", "https://indexer.dydx.trade");

            var futureData = Extensions.DownloadData($"{baseUrl.TrimEnd('/')}/v4/perpetualMarkets");

            var symbols = JsonConvert.DeserializeObject<ExchangeInfo>(futureData).Symbols
                .Values;
            foreach (var symbol in symbols)
            {
                if (!symbol.Status.Equals("ACTIVE", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                var contractSize = 1;
                var assets = symbol.Ticker.Split("-");
                var baseAsset = assets[0].Split(",")[0];
                var quoteAsset = assets[1];
                var tickerCsvValue = symbol.Ticker.Contains(",") ? $"\"{symbol.Ticker}\"" : symbol.Ticker;

                yield return
                    $"{Market.ToLowerInvariant()},{baseAsset}{quoteAsset},cryptofuture,{tickerCsvValue},{quoteAsset},{contractSize},{symbol.TickSize},{symbol.StepSize},{tickerCsvValue}";
            }
        }
    }
}