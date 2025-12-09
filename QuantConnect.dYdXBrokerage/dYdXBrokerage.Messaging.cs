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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Brokerages.dYdX.Models.WebSockets;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    /// <summary>
    /// Wss message handler
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    protected override void OnMessage(object sender, WebSocketMessage e)
    {
        _messageHandler.HandleNewMessage(e);
    }

    /// <summary>
    /// Processes WSS messages from the private user data streams
    /// </summary>
    /// <param name="webSocketMessage">The message to process</param>
    private void OnUserMessage(WebSocketMessage webSocketMessage)
    {
        var e = (WebSocketClientWrapper.TextMessage)webSocketMessage.Data;
        try
        {
            if (Log.DebuggingEnabled)
            {
                Log.Debug($"{nameof(dYdXBrokerage)}.{nameof(OnUserMessage)}(): {e.Message}");
            }

            var jObj = JObject.Parse(e.Message);
            var topic = jObj.Value<string>("type");
            if (topic.Equals("connected", StringComparison.InvariantCultureIgnoreCase))
            {
                OnConnected(jObj.ToObject<ConnectedResponseSchema>());
                return;
            }

            // TODO: send "pong" response

            var channel = jObj.Value<string>("channel");
            switch (channel)
            {
                case "v4_markets":
                    OnMarketUpdate(jObj);
                    break;
                case "v4_subaccounts":
                    OnSubaccountUpdate(jObj);
                    break;
            }
        }
        catch (Exception exception)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                $"Parsing wss message failed. Data: {e.Message} Exception: {exception}"));
            throw;
        }
    }

    private void OnConnected(ConnectedResponseSchema _)
    {
        _connectionConfirmedEvent.Set();
    }

    /// <summary>
    /// Handles market updates from v4_markets channel.
    /// Supports both 'subscribed' (snapshot) and 'channel_batch_data' (updates) types.
    /// </summary>
    private void OnMarketUpdate(JObject jObj)
    {
        var contents = jObj["contents"];
        if (contents == null) return;

        // 'contents' can be an Object (in 'subscribed') or an Array (in 'channel_batch_data')
        switch (jObj.Value<string>("type"))
        {
            case "subscribed":
                var initialData = jObj.ToObject<DataResponseSchema<ExchangeInfo>>();
                if (initialData?.Contents != null)
                {
                    RefreshMarkets(initialData.Contents);
                }

                break;

            case "channel_batch_data":
                var oraclePrices = jObj.ToObject<BatchDataResponseSchema<OraclePricesMarketUpdate>>();
                if (oraclePrices?.Contents != null)
                {
                    UpdateOraclePrice(oraclePrices.Contents);
                }

                break;
        }
    }

    private void OnSubaccountUpdate(JObject jObj)
    {
        var contents = jObj["contents"];
        if (contents == null) return;

        // 'contents' can be an Object (in 'subscribed') or an Array (in 'channel_batch_data')
        switch (jObj.Value<string>("type"))
        {
            case "subscribed":
                // do nothing as we fetched subaccount initial data
                break;

            case "channel_data":
                var subaccountUpdate = jObj.ToObject<DataResponseSchema<SubaccountsUpdateMessage>>();
                if (subaccountUpdate.Contents?.BlockHeight > 0)
                {
                    UpdateBlockHeight(subaccountUpdate.Contents.BlockHeight.Value);
                }

                if (subaccountUpdate.Contents?.Orders?.Any() == true)
                {
                    HandleOrders(subaccountUpdate.Contents);
                }

                break;
        }
    }

    private void HandleOrders(SubaccountsUpdateMessage contents)
    {
        var contentsOrders = contents.Orders;
        foreach (var dydxOrder in contentsOrders)
        {
            switch (dydxOrder.Status)
            {
                case "BEST_EFFORT_OPENED":
                    if (_pendingOrders.TryRemove(dydxOrder.ClientId, out var tuple))
                    {
                        var (resetEvent, leanOpenOrder) = tuple;
                        leanOpenOrder.BrokerId.Add(dydxOrder.Id);
                        _orderBrokerIdToClientIdMap.TryAdd(dydxOrder.Id, dydxOrder.ClientId);
                        OnOrderEvent(new OrderEvent(leanOpenOrder, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event")
                        {
                            Status = OrderStatus.Submitted
                        });
                        resetEvent.Set();
                    }

                    break;

                case "CANCELED":
                case "BEST_EFFORT_CANCELED":
                    var leanCancelOrder =
                        _algorithm.Transactions.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();
                    if (leanCancelOrder != null)
                    {
                        OnOrderEvent(new OrderEvent(leanCancelOrder, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event")
                        {
                            Status = OrderStatus.Canceled
                        });
                    }

                    break;

                case "FILLED":
                    var orderFills = contents.Fills?
                        .Where(x => x.OrderId == dydxOrder.Id)
                        .ToList();
                    HandleFills(dydxOrder, orderFills);
                    break;

                default:
                    Log.Error($"dYdXBrokerage.HandleOrders(): order status not handled: {dydxOrder.Status}");
                    break;
            }
        }
    }

    private void HandleFills(OrderSubaccountMessage order, List<FillSubaccountMessage> orderFills)
    {
        try
        {
            var leanOrder = _orderProvider.GetOrdersByBrokerageId(order.Id)?.SingleOrDefault();
            if (leanOrder == null)
            {
                // not our order, nothing else to do here
                Log.Error($"dYdXBrokerage.HandleFills(): order not found: {order.Id}");
                return;
            }

            var finalOrderStatus = Domain.Market.ParseOrderStatus(order.Status);
            var fills = orderFills.ToList();

            for (int i = 0; i < fills.Count; i++)
            {
                var fill = fills[i];
                var fillPrice = fill.Price;
                var fillQuantity = fill.Side == OrderDirection.Sell
                    ? -fill.QuoteAmount
                    : fill.QuoteAmount;
                var updTime = Time.ParseDate(fill.CreatedAt);
                var orderFee = OrderFee.Zero;
                if (fill.Fee.HasValue && fill.Fee.Value > 0)
                {
                    var symbol = _symbolMapper.GetLeanSymbol(fill.Ticker, SecurityType.CryptoFuture, MarketName);
                    var symbolProps = SymbolPropertiesDatabase.GetSymbolProperties(MarketName, symbol,
                        SecurityType.CryptoFuture, Currencies.USD);

                    // TODO: fee in not in docs, but present in response. Not sure about fee currency
                    // see ref. https://docs.dydx.xyz/types/fill_subaccount_message
                    // might not be sent if zero fee
                    orderFee = new OrderFee(new CashAmount(fill.Fee.Value, symbolProps.QuoteCurrency));
                }

                // no current order status
                // TODO: check if we can get partially filled order status at all
                // their API does not contain it https://docs.dydx.xyz/types/order_status#orderstatus
                var status = i == fills.Count - 1 ? finalOrderStatus : OrderStatus.PartiallyFilled;
                var orderEvent = new OrderEvent
                (
                    leanOrder.Id, leanOrder.Symbol, updTime, status,
                    fill.Side, fillPrice, fillQuantity,
                    orderFee, $"dYdX Order Event {fill.Side}"
                );

                OnOrderEvent(orderEvent);
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
            throw;
        }
    }

    private void UpdateBlockHeight(uint blockHeight)
    {
        _market.UpdateBlockHeigh(blockHeight);
    }

    private void RefreshMarkets(ExchangeInfo exchangeInfo)
    {
        // Implementation to refresh markets using exchangeInfo.Markets
        if (exchangeInfo == null || !exchangeInfo.Symbols.Any())
        {
            return;
        }

        _market.RefreshMarkets(exchangeInfo.Symbols.Values);
    }

    private void UpdateOraclePrice(IEnumerable<OraclePricesMarketUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (update.OraclePrices == null || !update.OraclePrices.Any())
            {
                continue;
            }

            foreach (var priceKvp in update.OraclePrices)
            {
                _market.UpdateOraclePrice(priceKvp.Key, priceKvp.Value.OraclePrice);
            }
        }
    }
}