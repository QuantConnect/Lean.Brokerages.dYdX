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
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using Moq;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Packets;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Tests.Common.Securities;

namespace QuantConnect.Brokerages.dYdX.Tests
{
    [TestFixture]
    public partial class dYdXBrokerageTests : BrokerageTests
    {
        private static readonly Symbol _ethusd = Symbol.Create("ETHUSD", SecurityType.CryptoFuture, Market.dYdX);

        protected override Symbol Symbol => _ethusd;
        protected override SecurityType SecurityType => SecurityType.CryptoFuture;

        protected override decimal GetDefaultQuantity() => 0.01m;

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var privateKey = Config.Get("dydx-private-key-hex");
            var mnemonic = Config.Get("dydx-mnemonic");
            var address = Config.Get("dydx-address");
            var subaccountNumber = checked((uint)Config.GetInt("dydx-subaccount-number"));
            var nodeUrlRest = Config.Get("dydx-node-api-rest");
            var nodeUrlGrpc = Config.Get("dydx-node-api-grpc");
            var indexerUrlRest = Config.Get("dydx-indexer-api-rest");
            var indexerUrlWss = Config.Get("dydx-indexer-api-wss");
            var chainId = Config.Get("dydx-chain-id");

            var securities = new SecurityManager(new TimeKeeper(DateTime.UtcNow, TimeZones.NewYork))
            {
                { Symbol, CreateSecurity(Symbol) }
            };
            var algorithmSettings = new AlgorithmSettings();
            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());

            var algorithm = new Mock<IAlgorithm>();
            algorithm.Setup(a => a.Transactions).Returns(transactions);
            algorithm.Setup(a => a.Securities).Returns(securities);
            algorithm.Setup(a => a.BrokerageModel).Returns(new dYdXBrokerageModel());
            algorithm.Setup(a => a.Portfolio)
                .Returns(new SecurityPortfolioManager(securities, transactions, algorithmSettings));

            return new dYdXBrokerage(
                privateKey,
                mnemonic,
                address,
                chainId,
                subaccountNumber,
                nodeUrlRest,
                nodeUrlGrpc,
                indexerUrlRest,
                indexerUrlWss,
                algorithm.Object,
                orderProvider,
                new AggregationManager(),
                new LiveNodePacket());
        }

        protected override bool IsAsync() => true;

        protected override decimal GetAskPrice(Symbol symbol)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Provides the data required to test each order type in various cases
        /// </summary>
        private static IEnumerable<TestCaseData> OrderParameters()
        {
            yield return new TestCaseData(new MarketOrderTestParameters(Symbols.BTCUSD));
            yield return new TestCaseData(new LimitOrderTestParameters(Symbols.BTCUSD, 10000m, 0.01m));
            yield return new TestCaseData(new StopMarketOrderTestParameters(Symbols.BTCUSD, 10000m, 0.01m));
            yield return new TestCaseData(new StopLimitOrderTestParameters(Symbols.BTCUSD, 10000m, 0.01m));
            yield return new TestCaseData(new LimitIfTouchedOrderTestParameters(Symbols.BTCUSD, 10000m, 0.01m));

            var optionSymbol = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American, OptionRight.Call,
                200m, new DateTime(2029, 12, 19));
            yield return new TestCaseData(new MarketOrderTestParameters(optionSymbol));
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters)), Ignore("Not implemented")]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        public static Security CreateSecurity(Symbol symbol)
        {
            var timezone = TimeZones.NewYork;

            var config = new SubscriptionDataConfig(
                typeof(TradeBar),
                symbol,
                Resolution.Hour,
                timezone,
                timezone,
                true,
                false,
                false);

            return new Security(
                SecurityExchangeHours.AlwaysOpen(timezone),
                config,
                new Cash(Currencies.USD, 0, 1),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache()
            );
        }
    }
}