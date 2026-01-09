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
using QuantConnect.Brokerages.dYdX.Api;
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
            const int quoteQuantumsAtomicResolution = -6;
            var baseUrl = Config.Get("dydx-indexer-url", "https://indexer.dydx.trade/v4");

            var indexerApi = new dYdXIndexerClient(baseUrl);
            var symbols = indexerApi.GetExchangeInfo().Symbols.Values;
            foreach (var symbol in symbols)
            {
                if (!symbol.Status.Equals("ACTIVE", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                // TODO: handle ticker with comma, i.e. https://dydx.trade/trade/FARTCOIN,RAYDIUM,9BB6NFECJBCTNNLFKO2FQVQBQ8HHM13KCYYCDQBGPUMP-USD
                // For now, we skip them as SymbolPropertiesDatabase doesn't support csv values with ','
                if (symbol.Ticker.Contains(","))
                {
                    continue;
                }

                var contractSize = 1;
                var assets = symbol.Ticker.Split("-");
                var baseAsset = assets[0].Split(",")[0];
                var quoteAsset = assets[1];
                var tickerCsvValue = symbol.Ticker.Contains(",") ? $"\"{symbol.Ticker}\"" : symbol.Ticker;

                var strikeMultiplier = ((decimal)Math.Pow(10, -symbol.AtomicResolution)).ToStringInvariant();
                var minOrderSize = (symbol.StepBaseQuantums * (decimal)Math.Pow(10, symbol.AtomicResolution))
                    .ToStringInvariant();
                var exponent = symbol.AtomicResolution
                               - symbol.QuantumConversionExponent
                               - quoteQuantumsAtomicResolution;
                var priceMagnifier = ((decimal)Math.Pow(10, exponent)).ToStringInvariant();

                yield return
                    $"{Market.ToLowerInvariant()},{baseAsset}{quoteAsset},cryptofuture,{tickerCsvValue},{quoteAsset},{contractSize},{symbol.TickSize},{symbol.StepSize},{tickerCsvValue},{minOrderSize},{priceMagnifier},{strikeMultiplier}";
            }
        }
    }
}