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
using NUnit.Framework;
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using DydxMarket = QuantConnect.Brokerages.dYdX.Domain.Market;
using DydxSymbol = QuantConnect.Brokerages.dYdX.Models.Symbol;

namespace QuantConnect.Brokerages.dYdX.Tests.Domain;

[TestFixture]
public class MarketTests
{
    private static readonly Symbol _ethusd = Symbol.Create("ETHUSD", SecurityType.CryptoFuture, Market.DYDX);
    private DydxMarket _market;
    private SymbolProperties _symbolProperties;
    private DydxSymbol _marketInfo;

    [SetUp]
    public void Setup()
    {
        var privateKeyHex = "0x933fd548827550f2a3560cf1ec0f30823ba7c9699a42ed6d8978726791ce8aef";
        var address = "dydx1067v37ykkf7muydw77lzp3m8j05ewpe3p33fuc";
        var subaccountNumber = 0u;
        var chainId = "dydx-testnet-4";

        var wallet = Wallet.FromAuthenticator(
            new dYdXApiClient("https://test-dydx-grpc.kingnodes.com:443", "https://indexer.v4testnet.dydx.exchange/v4"),
            privateKeyHex,
            address,
            chainId,
            subaccountNumber);

        var symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(QuantConnect.Market.DYDX);
        var symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
        _symbolProperties = symbolPropertiesDatabase.GetSymbolProperties(
            _ethusd.ID.Market,
            _ethusd,
            _ethusd.SecurityType,
            Currencies.USD);

        var marketTicker = symbolMapper.GetBrokerageSymbol(_ethusd);
        _marketInfo = new DydxSymbol
        {
            Ticker = marketTicker,
            ClobPairId = 123,
            OraclePrice = 2000m,
            StepBaseQuantums = 1,
            SubticksPerTick = 1000
        };

        _market = new DydxMarket(
            wallet,
            symbolMapper,
            symbolPropertiesDatabase,
            apiClient: null);

        _market.RefreshMarkets([_marketInfo]);
    }

    [Test]
    public void CreateOrder_LongTermLimitOrder_UsesLimitPriceForSubticks()
    {
        var symbol = Symbol.Create("ETHUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX);
        var limitPrice = 2500;

        var order = new LimitOrder(
            symbol,
            quantity: 0.1m,
            limitPrice,
            DateTime.UtcNow,
            properties: new dYdXOrderProperties
            {
                TimeInForce = new GoodTilDateTimeInForce(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            });

        var result = _market.CreateOrder(order, clientId: 42);

        Assert.AreEqual(2_500_000_000, result.Subticks);
    }

    [Test]
    public void CreateOrder_LongTermStopLimitOrder_UsesLimitPriceForSubticks()
    {
        var symbol = Symbol.Create("ETHUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX);
        var stopPrice = 2400m;
        var limitPrice = 2500;

        var order = new StopLimitOrder(
            symbol,
            quantity: 0.1m,
            stopPrice,
            limitPrice,
            DateTime.UtcNow,
            properties: new dYdXOrderProperties
            {
                TimeInForce = new GoodTilDateTimeInForce(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            });

        var result = _market.CreateOrder(order, clientId: 42);

        Assert.AreEqual(2_500_000_000, result.Subticks);
        Assert.AreEqual(2_400_000_000, result.ConditionalOrderTriggerSubticks);
    }

    [Test]
    public void CreateOrder_LongTermStopMarketOrder_UsesLimitPriceForSubticks()
    {
        var symbol = Symbol.Create("ETHUSD", SecurityType.CryptoFuture, QuantConnect.Market.DYDX);
        var stopPrice = 2400m;

        var order = new StopMarketOrder(
            symbol,
            quantity: 0.1m,
            stopPrice,
            DateTime.UtcNow,
            properties: new dYdXOrderProperties
            {
                TimeInForce = new GoodTilDateTimeInForce(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            });

        var result = _market.CreateOrder(order, clientId: 42);

        Assert.AreEqual(1_000, result.Subticks);
        Assert.AreEqual(2_400_000_000, result.ConditionalOrderTriggerSubticks);
    }
}