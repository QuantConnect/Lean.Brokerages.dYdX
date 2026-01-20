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
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Tests;

namespace QuantConnect.Brokerages.dYdX.Tests
{
    [TestFixture]
    public class dYdXBrokerageHistoryProviderTests
    {
        private static readonly Symbol _btcusd = Symbol.Create("BTCUSD", SecurityType.CryptoFuture, Market.DYDX);

        private static IEnumerable<TestCaseData> TestParameters
        {
            get
            {
                TestGlobals.Initialize();

                return
                [
                    new(_btcusd, Resolution.Minute, TimeSpan.FromMinutes(10), TickType.Trade,
                        typeof(TradeBar), false),
                    new(_btcusd, Resolution.Hour, TimeSpan.FromHours(10), TickType.Trade,
                        typeof(TradeBar), false),
                    new(_btcusd, Resolution.Daily, TimeSpan.FromDays(10), TickType.Trade,
                        typeof(TradeBar), false),

                    new(_btcusd, Resolution.Minute, TimeSpan.FromMinutes(10),
                        TickType.OpenInterest,
                        typeof(OpenInterest), false),
                    new(_btcusd, Resolution.Hour, TimeSpan.FromHours(10), TickType.OpenInterest,
                        typeof(OpenInterest), false),
                    new(_btcusd, Resolution.Daily, TimeSpan.FromDays(10), TickType.OpenInterest,
                        typeof(OpenInterest), false),

                    // invalid parameter, return null if TickType.Quote
                    new(_btcusd, Resolution.Daily, TimeSpan.FromDays(10), TickType.Quote,
                        typeof(QuoteBar), true),

                    // invalid parameter, validate SecurityType more accurate
                    new(Symbols.SPY, Resolution.Hour, TimeSpan.FromHours(14), TickType.Quote,
                        typeof(QuoteBar), true),

                    // Symbol was delisted form Brokerage (can return history data or not) <see cref="Slice.Delistings"/>
                    new(Symbol.Create("MATICUSD", SecurityType.CryptoFuture, Market.DYDX),
                        Resolution.Daily,
                        TimeSpan.FromDays(14), TickType.Trade, typeof(TradeBar), true)
                ];
            }
        }

        [Test, TestCaseSource(nameof(TestParameters))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TimeSpan period, TickType tickType, Type dataType, bool invalidRequest)
        {
            var brokerage = CreateBrokerage();

            var historyProvider = new BrokerageHistoryProvider();
            historyProvider.SetBrokerage(brokerage);
            historyProvider.Initialize(new HistoryProviderInitializeParameters(null, null, null,
                null, null, null, null,
                false, null, null, new AlgorithmSettings()));

            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var now = DateTime.UtcNow;
            var requests = new[]
            {
                new HistoryRequest(now.Add(-period),
                    now,
                    dataType,
                    symbol,
                    resolution,
                    marketHoursDatabase.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType),
                    marketHoursDatabase.GetDataTimeZone(symbol.ID.Market, symbol, symbol.SecurityType),
                    resolution,
                    false,
                    false,
                    DataNormalizationMode.Adjusted,
                    tickType)
            };

            var historyArray = historyProvider.GetHistory(requests, TimeZones.Utc)?.ToArray();
            if (invalidRequest)
            {
                Assert.Null(historyArray);
                return;
            }

            Assert.NotNull(historyArray);
            foreach (var slice in historyArray)
            {
                if (resolution == Resolution.Tick)
                {
                    foreach (var tick in slice.Ticks[symbol])
                    {
                        Log.Debug($"{tick}");
                    }
                }
                else if (slice.QuoteBars.TryGetValue(symbol, out var quoteBar))
                {
                    Log.Debug($"{quoteBar}");
                }
                else if (slice.Bars.TryGetValue(symbol, out var tradeBar))
                {
                    Log.Debug($"{tradeBar}");
                }
            }

            if (historyProvider.DataPointCount > 0)
            {
                // Ordered by time
                Assert.That(historyArray, Is.Ordered.By("Time"));

                // No repeating bars
                var timesArray = historyArray.Select(x => x.Time).ToArray();
                Assert.AreEqual(timesArray.Length, timesArray.Distinct().Count());
            }

            Log.Trace("Data points retrieved: " + historyProvider.DataPointCount);

            Brokerage CreateBrokerage()
            {
                var privateKey = Config.Get("dydx-private-key-hex");
                var address = Config.Get("dydx-address");
                var subaccountNumber = checked((uint)Config.GetInt("dydx-subaccount-number"));
                var nodeUrlGrpc = Config.Get("dydx-node-api-grpc", "https://test-dydx-grpc.kingnodes.com:443");
                var indexerUrlRest = Config.Get("dydx-indexer-api-rest", "https://indexer.v4testnet.dydx.exchange/v4");
                var indexerUrlWss = Config.Get("dydx-indexer-api-wss", "wss://indexer.v4testnet.dydx.exchange/v4/ws");
                var chainId = Config.Get("dydx-chain-id", "dydx-testnet-4");

                return new dYdXBrokerage(
                    privateKey,
                    address,
                    chainId,
                    subaccountNumber,
                    nodeUrlGrpc,
                    indexerUrlRest,
                    indexerUrlWss,
                    null,
                    null,
                    null
                );
            }
        }
    }
}