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
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX.Tests
{
    [TestFixture]
    public class dYdXBrokerageMessagingTests
    {
        private static readonly Symbol _btcusd = Symbol.Create("BTCUSD", SecurityType.CryptoFuture, Market.DYDX);
        private const string BrokerId = "d6eb5204-e072-594e-8471-6f5b7d3c8f89";

        private static readonly MethodInfo _onSubaccountUpdate = typeof(dYdXBrokerage)
            .GetMethod("OnSubaccountUpdate", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _orderProviderField = typeof(dYdXBrokerage)
            .GetField("_orderProvider", BindingFlags.NonPublic | BindingFlags.Instance);

        private dYdXBrokerage _brokerage;
        private List<OrderEvent> _orderEvents;

        [SetUp]
        public void Setup()
        {
            _brokerage = new dYdXBrokerage();

            // an already submitted order known to LEAN (its broker id was assigned on the OPEN ack)
            var leanOrder = new MarketOrder(_btcusd, 0.0048m, new DateTime(2026, 06, 14, 18, 01, 00, DateTimeKind.Utc));
            leanOrder.BrokerId.Add(BrokerId);

            var orderProvider = new OrderProvider();
            orderProvider.Add(leanOrder);
            _orderProviderField.SetValue(_brokerage, orderProvider);

            _orderEvents = [];
            _brokerage.OrdersStatusChanged += (_, events) => _orderEvents.AddRange(events);
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage.DisposeSafely();
        }

        // Routes a raw v4_subaccounts "channel_data" payload through the real deserialization + dispatch path.
        private void DispatchSubaccountUpdate(string contents)
        {
            var payload = $$"""
            {
                "type": "channel_data",
                "connection_id": "e2a3a4b6-1d2c-4f5e-8a9b-0c1d2e3f4a5b",
                "message_id": 2,
                "id": "dydx1067v37ykkf7muydw77lzp3m8j05ewpe3p33fuc/0",
                "channel": "v4_subaccounts",
                "version": "2.4.0",
                "contents": {{contents}}
            }
            """;
            _onSubaccountUpdate.Invoke(_brokerage, [JObject.Parse(payload)]);
        }

        [Test]
        public void EmitsPartiallyFilledEventWhenFillArrivesWithOpenStatus()
        {
            // dYdX v4 has no PARTIALLY_FILLED status: an intermediate partial fill is reported while the
            // order is still OPEN, bundled in the same SubaccountsUpdateMessage.
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "subaccountId": "dydx1067v37ykkf7muydw77lzp3m8j05ewpe3p33fuc/0",
                    "clientId": "287469",
                    "side": "BUY",
                    "size": "0.0048",
                    "ticker": "BTC-USD",
                    "price": "64819",
                    "type": "MARKET",
                    "status": "OPEN",
                    "totalFilled": "0.001",
                    "updatedAt": "2026-06-14T19:00:00.000Z"
                }],
                "fills": [{
                    "id": "8f3b6a1c-0e2d-4c5b-9a8f-1b2c3d4e5f60",
                    "subaccountId": "dydx1067v37ykkf7muydw77lzp3m8j05ewpe3p33fuc/0",
                    "orderId": "{{BrokerId}}",
                    "side": "BUY",
                    "size": "0.001",
                    "price": "64000",
                    "quoteAmount": "64.0",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-06-14T19:00:00.000Z"
                }]
            }
            """);

            Assert.AreEqual(1, _orderEvents.Count);
            var orderEvent = _orderEvents[0];
            Assert.AreEqual(OrderStatus.PartiallyFilled, orderEvent.Status);
            Assert.AreEqual(OrderDirection.Buy, orderEvent.Direction);
            Assert.AreEqual(0.001m, orderEvent.FillQuantity);
            Assert.AreEqual(64000m, orderEvent.FillPrice);
        }

        [Test]
        public void DoesNotEmitFillEventWhenOpenStatusHasNoFills()
        {
            // a plain OPEN acknowledgement for an already submitted order must not produce a fill event
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "status": "OPEN"
                }]
            }
            """);

            Assert.IsEmpty(_orderEvents);
        }

        [Test]
        public void HandlesStopMarketPartialFillSameAsAnyOtherOrder()
        {
            // The customer's case: a conditional (stop-market) order. The fill message is keyed purely by
            // orderId and carries no order-type information, and the status switch only branches on Status,
            // so a stop order behaves identically. The only lifecycle difference is the UNTRIGGERED stage,
            // which precedes the trigger and carries no fills.
            var stopOrder = new StopMarketOrder(_btcusd, 0.0048m, 64819m,
                new DateTime(2026, 06, 14, 18, 01, 00, DateTimeKind.Utc));
            stopOrder.BrokerId.Add(BrokerId);

            var orderProvider = new OrderProvider();
            orderProvider.Add(stopOrder);
            _orderProviderField.SetValue(_brokerage, orderProvider);

            // before the oracle price crosses the trigger: UNTRIGGERED, no fills -> nothing emitted
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "triggerPrice": "64819",
                    "status": "UNTRIGGERED"
                }]
            }
            """);

            Assert.IsEmpty(_orderEvents);

            // once triggered the order is OPEN and begins to fill, exactly like a non-conditional order
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "status": "OPEN",
                    "totalFilled": "0.001"
                }],
                "fills": [{
                    "orderId": "{{BrokerId}}",
                    "side": "BUY",
                    "size": "0.001",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-06-14T19:00:00.000Z"
                }]
            }
            """);

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[0].Status);
            Assert.AreEqual(0.001m, _orderEvents[0].FillQuantity);
        }

        [Test]
        public void EmitsBundledFillBeforeCancellationWhenOrderIsCanceledWithAFill()
        {
            // dYdX may report a final partial fill in the same message that cancels the remaining quantity.
            // The fill must be emitted (PartiallyFilled) before the Canceled event, not dropped.
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "status": "CANCELED",
                    "totalFilled": "0.001"
                }],
                "fills": [{
                    "orderId": "{{BrokerId}}",
                    "side": "BUY",
                    "size": "0.001",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-06-14T19:00:00.000Z"
                }]
            }
            """);

            Assert.AreEqual(2, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[0].Status);
            Assert.AreEqual(0.001m, _orderEvents[0].FillQuantity);
            Assert.AreEqual(OrderStatus.Canceled, _orderEvents[1].Status);
        }

        [Test]
        public void MarksLastFillAsFilledOnlyWhenOrderStatusIsFilled()
        {
            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "status": "FILLED",
                    "totalFilled": "0.0048"
                }],
                "fills": [
                    {
                        "orderId": "{{BrokerId}}",
                        "side": "BUY",
                        "size": "0.002",
                        "price": "64000",
                        "ticker": "BTC-USD",
                        "createdAt": "2026-06-14T19:00:00.000Z"
                    },
                    {
                        "orderId": "{{BrokerId}}",
                        "side": "BUY",
                        "size": "0.0028",
                        "price": "64010",
                        "ticker": "BTC-USD",
                        "createdAt": "2026-06-14T19:00:01.000Z"
                    }
                ]
            }
            """);

            Assert.AreEqual(2, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Filled, _orderEvents[1].Status);
        }
    }
}
