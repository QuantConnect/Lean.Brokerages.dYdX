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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
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

        private static readonly FieldInfo _pendingOrdersField = typeof(dYdXBrokerage)
            .GetField("_pendingOrders", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo _onPlaceOrderTimeout = typeof(dYdXBrokerage)
            .GetMethod("OnPlaceOrderTimeout", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo _timedOutOrdersField = typeof(dYdXBrokerage)
            .GetField("_timedOutOrders", BindingFlags.NonPublic | BindingFlags.Instance);

        private dYdXBrokerage _brokerage;
        private Order _leanOrder;
        private List<OrderEvent> _orderEvents;

        [SetUp]
        public void Setup()
        {
            _brokerage = new dYdXBrokerage();

            // an already submitted order known to LEAN (its broker id was assigned on the OPEN ack)
            _leanOrder = new MarketOrder(_btcusd, 0.0048m, new DateTime(2026, 06, 14, 18, 01, 00, DateTimeKind.Utc));
            _leanOrder.BrokerId.Add(BrokerId);

            var orderProvider = new OrderProvider();
            orderProvider.Add(_leanOrder);
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

        [Test]
        public void DoesNotEmitTheSameFillTwice()
        {
            const string fillId = "8f3b6a1c-0e2d-4c5b-9a8f-1b2c3d4e5f60";

            // the same fill reported on two separate messages must only produce one OrderEvent,
            // otherwise holdings/cash are double counted
            for (var i = 0; i < 2; i++)
            {
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
                        "id": "{{fillId}}",
                        "orderId": "{{BrokerId}}",
                        "side": "BUY",
                        "size": "0.001",
                        "price": "64000",
                        "ticker": "BTC-USD",
                        "createdAt": "2026-06-14T19:00:00.000Z"
                    }]
                }
                """);
            }

            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[0].Status);
            Assert.AreEqual(0.001m, _orderEvents[0].FillQuantity);
        }

        [Test]
        public void EmitsTerminalEventWhenSubmissionConfirmationTimesOut()
        {
            // Issue #32: the REST submission succeeded (Code 0) but the WebSocket OPEN/Submitted confirmation
            // never arrived within the timeout. A terminal event must be emitted so the order leaves
            // OrderStatus.New, otherwise LEAN can never cancel it and it stays in GetOpenOrders() forever.
            const uint clientId = 287469u;
            var order = new StopMarketOrder(_btcusd, 0.0048m, 64819m,
                new DateTime(2026, 06, 14, 18, 01, 00, DateTimeKind.Utc));

            var pendingOrders = (ConcurrentDictionary<uint, Tuple<ManualResetEventSlim, Order>>)
                _pendingOrdersField.GetValue(_brokerage);
            using var resetEvent = new ManualResetEventSlim(false);
            pendingOrders[clientId] = Tuple.Create(resetEvent, (Order)order);

            _onPlaceOrderTimeout.Invoke(_brokerage, [clientId, order]);

            Assert.AreEqual(1, _orderEvents.Count);
            // Invalid, not Canceled: we never got an acknowledgement that the order became live, so we
            // cannot claim to have canceled it.
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            // the pending entry is consumed so PlaceOrder stops waiting on it...
            Assert.IsFalse(pendingOrders.ContainsKey(clientId));
            // ...but the order stays recoverable by client id: the timeout is only a guess and dYdX may
            // still report the order open or filled later (issue #35)
            Assert.IsTrue(GetTimedOutOrders().ContainsKey(clientId));
        }

        [Test]
        public void DoesNotEmitTerminalEventWhenConfirmationRacedInBeforeTimeout()
        {
            // If the WebSocket confirmation arrives just as we time out, TryHandleOpen has already removed the
            // pending entry and emitted Submitted. The timeout path must not override that with an Invalid event.
            const uint clientId = 287469u;
            var order = new StopMarketOrder(_btcusd, 0.0048m, 64819m,
                new DateTime(2026, 06, 14, 18, 01, 00, DateTimeKind.Utc));

            // no pending entry: the confirmation already consumed it
            _onPlaceOrderTimeout.Invoke(_brokerage, [clientId, order]);

            Assert.IsEmpty(_orderEvents);
            // and no recovery entry is left behind for an order that was confirmed normally
            Assert.IsFalse(GetTimedOutOrders().ContainsKey(clientId));
        }

        [Test]
        public void RecoversFillsWhenTimedOutOrderFillsAfterwards()
        {
            // Issue #35: the submission confirmation never arrived so LEAN marked the order Invalid, but the
            // order actually reached the chain and dYdX filled it. The fills must be recovered through the
            // client id, otherwise LEAN's portfolio silently diverges from the real broker position (which
            // has been observed to compound until full account liquidation).
            const uint clientId = 287469u;
            const string lateBrokerId = "821402df-7366-5cba-9e36-60b0c3be1b04";
            var order = RegisterTimedOutOrder(clientId);

            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{lateBrokerId}}",
                    "clientId": "{{clientId}}",
                    "side": "SELL",
                    "status": "FILLED",
                    "totalFilled": "0.0048"
                }],
                "fills": [{
                    "orderId": "{{lateBrokerId}}",
                    "side": "SELL",
                    "size": "0.0048",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-07-12T15:11:30.000Z"
                }]
            }
            """);

            // Invalid from the timeout, then the recovery: Submitted followed by the fill
            Assert.AreEqual(3, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Submitted, _orderEvents[1].Status);
            Assert.AreEqual(OrderStatus.Filled, _orderEvents[2].Status);
            Assert.AreEqual(-0.0048m, _orderEvents[2].FillQuantity);
            Assert.AreEqual(64000m, _orderEvents[2].FillPrice);
            // the broker id is registered so any further messages resolve through the order provider
            Assert.IsTrue(order.BrokerId.Contains(lateBrokerId));
            // the recovery entry is consumed
            Assert.IsFalse(GetTimedOutOrders().ContainsKey(clientId));
        }

        [Test]
        public void ResurrectsTimedOutOrderWhenOpenConfirmationArrivesLate()
        {
            // The OPEN acknowledgement arrives after the submission wait already timed out: the order is
            // live at dYdX, so LEAN must be told it is Submitted again instead of leaving a dead Invalid
            // order whose future fills would be unattributable.
            const uint clientId = 287469u;
            const string lateBrokerId = "6f67e7e1-6630-5373-b20b-004fc02a5e6f";
            var order = RegisterTimedOutOrder(clientId);

            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{lateBrokerId}}",
                    "clientId": "{{clientId}}",
                    "side": "SELL",
                    "status": "OPEN"
                }]
            }
            """);

            Assert.AreEqual(2, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Submitted, _orderEvents[1].Status);
            Assert.IsTrue(order.BrokerId.Contains(lateBrokerId));
            Assert.IsFalse(GetTimedOutOrders().ContainsKey(clientId));
        }

        [Test]
        public void RecoversBundledFillWhenTimedOutOrderIsCanceledWithAFill()
        {
            // A timed-out order can be partially filled and then canceled by the chain (e.g. good-til-block
            // expiry) without LEAN ever seeing it open: the bundled fill must still reach the portfolio.
            const uint clientId = 287469u;
            const string lateBrokerId = "e0eb4e6c-8f26-5ff9-bac4-60e85175b4c9";
            RegisterTimedOutOrder(clientId);

            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{lateBrokerId}}",
                    "clientId": "{{clientId}}",
                    "side": "SELL",
                    "status": "CANCELED",
                    "totalFilled": "0.001"
                }],
                "fills": [{
                    "orderId": "{{lateBrokerId}}",
                    "side": "SELL",
                    "size": "0.001",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-07-12T15:11:30.000Z"
                }]
            }
            """);

            Assert.AreEqual(4, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            Assert.AreEqual(OrderStatus.Submitted, _orderEvents[1].Status);
            Assert.AreEqual(OrderStatus.PartiallyFilled, _orderEvents[2].Status);
            Assert.AreEqual(-0.001m, _orderEvents[2].FillQuantity);
            Assert.AreEqual(OrderStatus.Canceled, _orderEvents[3].Status);
        }

        [Test]
        public void DropsRecoveryEntryWhenTimedOutOrderIsCanceledWithoutFills()
        {
            // dYdX confirmed the order is dead without ever filling it: LEAN's Invalid verdict was right
            // and the recovery entry is no longer needed.
            const uint clientId = 287469u;
            const string lateBrokerId = "e0eb4e6c-8f26-5ff9-bac4-60e85175b4c9";
            RegisterTimedOutOrder(clientId);

            DispatchSubaccountUpdate($$"""
            {
                "orders": [{
                    "id": "{{lateBrokerId}}",
                    "clientId": "{{clientId}}",
                    "side": "SELL",
                    "status": "CANCELED"
                }]
            }
            """);

            // only the Invalid event from the timeout: the order was never resurrected
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Invalid, _orderEvents[0].Status);
            Assert.IsFalse(GetTimedOutOrders().ContainsKey(clientId));
        }

        [Test]
        public void EmitsErrorMessageWhenFillCannotBeAttributed()
        {
            // A fill for an order LEAN knows nothing about means the portfolio no longer matches the broker
            // account. It must be surfaced as an Error (stopping the algorithm under the default brokerage
            // message handler) instead of being silently logged and dropped (issue #35).
            var brokerageMessages = new List<BrokerageMessageEvent>();
            _brokerage.Message += (_, message) => brokerageMessages.Add(message);

            DispatchSubaccountUpdate("""
            {
                "orders": [{
                    "id": "00000000-0000-0000-0000-000000000000",
                    "clientId": "999999",
                    "side": "SELL",
                    "status": "FILLED",
                    "totalFilled": "0.001"
                }],
                "fills": [{
                    "orderId": "00000000-0000-0000-0000-000000000000",
                    "side": "SELL",
                    "size": "0.001",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-07-12T15:11:30.000Z"
                }]
            }
            """);

            Assert.IsEmpty(_orderEvents);
            Assert.AreEqual(1, brokerageMessages.Count);
            Assert.AreEqual(BrokerageMessageType.Error, brokerageMessages[0].Type);
        }

        [Test]
        public void DoesNotReEmitFillsWhenFilledMessageIsRedelivered()
        {
            // Once the order reaches FILLED its fill dedup set is dropped, so a re-delivery of the same
            // message (e.g. the indexer resending events after a websocket reconnect) can no longer be
            // deduplicated fill-by-fill. A fully filled order cannot receive new fills, so re-deliveries
            // must be ignored wholesale or holdings/cash would be double counted.
            var fillMessage = $$"""
            {
                "orders": [{
                    "id": "{{BrokerId}}",
                    "clientId": "287469",
                    "side": "BUY",
                    "status": "FILLED",
                    "totalFilled": "0.0048"
                }],
                "fills": [{
                    "id": "8f3b6a1c-0e2d-4c5b-9a8f-1b2c3d4e5f60",
                    "orderId": "{{BrokerId}}",
                    "side": "BUY",
                    "size": "0.0048",
                    "price": "64000",
                    "ticker": "BTC-USD",
                    "createdAt": "2026-06-14T19:00:00.000Z"
                }]
            }
            """;

            DispatchSubaccountUpdate(fillMessage);
            Assert.AreEqual(1, _orderEvents.Count);
            Assert.AreEqual(OrderStatus.Filled, _orderEvents[0].Status);

            // the live transaction handler marks the LEAN order Filled once the event is processed
            _leanOrder.Status = OrderStatus.Filled;

            DispatchSubaccountUpdate(fillMessage);
            Assert.AreEqual(1, _orderEvents.Count);
        }

        // Places an order into the pending map and forces the submission-confirmation timeout, leaving the
        // order Invalid on the LEAN side but recoverable by client id, exactly as OnPlaceOrderTimeout does
        // when the OPEN acknowledgement never arrives in production.
        private Order RegisterTimedOutOrder(uint clientId)
        {
            var order = new LimitOrder(_btcusd, -0.0048m, 64000m,
                new DateTime(2026, 07, 12, 15, 11, 00, DateTimeKind.Utc));
            var orderProvider = (OrderProvider)_orderProviderField.GetValue(_brokerage);
            orderProvider.Add(order);

            var pendingOrders = (ConcurrentDictionary<uint, Tuple<ManualResetEventSlim, Order>>)
                _pendingOrdersField.GetValue(_brokerage);
            using var resetEvent = new ManualResetEventSlim(false);
            pendingOrders[clientId] = Tuple.Create(resetEvent, (Order)order);
            _onPlaceOrderTimeout.Invoke(_brokerage, [clientId, order]);

            return order;
        }

        private ConcurrentDictionary<uint, Order> GetTimedOutOrders()
        {
            return (ConcurrentDictionary<uint, Order>)_timedOutOrdersField.GetValue(_brokerage);
        }
    }
}
