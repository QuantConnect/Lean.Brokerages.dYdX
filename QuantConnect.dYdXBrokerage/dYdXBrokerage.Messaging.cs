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
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Brokerages.dYdX.Models.WebSockets;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    private readonly object _tickLocker = new();
    private readonly Dictionary<Symbol, DefaultOrderBook> _orderBooks = new();

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
            if (topic.Equals("error", StringComparison.InvariantCultureIgnoreCase))
            {
                var error = jObj.ToObject<ErrorResponseSchema>();
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, error.Message));
                return;
            }

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

    private void OnDataMessage(WebSocketMessage webSocketMessage)
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
            if (topic.Equals("error", StringComparison.InvariantCultureIgnoreCase))
            {
                var error = jObj.ToObject<ErrorResponseSchema>();
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, error.Message));
                return;
            }

            var channel = jObj.Value<string>("channel");
            switch (channel)
            {
                case "v4_orderbook":
                    OnOrderbookUpdate(jObj);
                    break;
                case "v4_trades":
                    OnTradeUpdate(jObj);
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
        Log.Trace($"{nameof(dYdXBrokerage)}.{nameof(OnConnected)}(): Connected to websocket");
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

    private void OnOrderbookUpdate(JObject jObj)
    {
        var contents = jObj["contents"];
        if (contents == null) return;

        var topic = jObj.Value<string>("type");
        switch (topic)
        {
            case "subscribed":
                HandleOrderBookSnapshot(jObj);
                break;

            case "channel_data":
                HandleOrderBookDelta(jObj);
                break;
            default:
                Log.Error($"{nameof(dYdXBrokerage)}.{nameof(OnOrderbookUpdate)}: unknown topic: {topic}");
                break;
        }
    }

    private void OnTradeUpdate(JObject jObj)
    {
        var contents = jObj["contents"];
        if (contents == null) return;

        var topic = jObj.Value<string>("type");
        switch (topic)
        {
            case "subscribed":
                // do nothing as we don't consume old trades
                break;

            case "channel_data":
                HandleTrades(jObj);
                break;
            default:
                Log.Error($"{nameof(dYdXBrokerage)}.{nameof(OnOrderbookUpdate)}: unknown topic: {topic}");
                break;
        }
    }

    private void HandleTrades(JObject jObj)
    {
        var trades = jObj.ToObject<DataResponseSchema<TradesMessage>>();
        var symbol = _symbolMapper.GetLeanSymbol(trades.Id, SecurityType.CryptoFuture, MarketName);
        foreach (var trade in trades.Contents.Trades)
        {
            // var tradeValue = trade.Side == OrderSide.Buy ? trade.Value : trade.Value * -1;
            EmitTradeTick(symbol,
                Time.ParseDate(trade.CreatedAt),
                trade.Price,
                trade.Quantity);
        }
    }

    private void EmitTradeTick(Symbol symbol, DateTime time, decimal tradePrice, decimal quantity)
    {
        var tick = new Tick
        {
            Symbol = symbol,
            Value = tradePrice,
            Quantity = quantity,
            Time = time,
            TickType = TickType.Trade
        };

        lock (_tickLocker)
        {
            _aggregator.Update(tick);
        }
    }

    private void HandleOrders(SubaccountsUpdateMessage contents)
    {
        var contentsOrders = contents.Orders;
        foreach (var dydxOrder in contentsOrders)
        {
            switch (dydxOrder.Status)
            {
                case "OPEN":
                case "BEST_EFFORT_OPENED":
                    if (_pendingOrders.TryRemove(dydxOrder.ClientId, out var tuple))
                    {
                        var (resetEvent, leanSubmittedOrder) = tuple;
                        leanSubmittedOrder.BrokerId.Add(dydxOrder.Id);
                        _orderBrokerIdToClientIdMap.TryAdd(dydxOrder.Id, dydxOrder.ClientId);
                        OnOrderEvent(new OrderEvent(leanSubmittedOrder, DateTime.UtcNow, OrderFee.Zero,
                            "dYdX Order Event")
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
                    HandleFills(dydxOrder, contents);
                    break;

                default:
                    Log.Error(
                        $"{nameof(dYdXBrokerage)}.{nameof(HandleOrders)}: order status not handled: {dydxOrder.Status}");
                    break;
            }
        }
    }

    private void HandleFills(OrderSubaccountMessage dydxOrder, SubaccountsUpdateMessage messageContents)
    {
        try
        {
            var leanOrder = _orderProvider.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();
            if (leanOrder == null)
            {
                // not our order, nothing else to do here
                Log.Error($"{nameof(dYdXBrokerage)}.{nameof(HandleFills)}: order not found: {dydxOrder.Id}");
                return;
            }

            var finalOrderStatus = Domain.Market.ParseOrderStatus(dydxOrder.Status);
            var orderFills = messageContents.Fills?
                .Where(x => x.OrderId == dydxOrder.Id)
                .ToList();

            if (orderFills == null)
            {
                throw new Exception($"No fills found for order {leanOrder.Id} (brokerage id: {dydxOrder.Id})");
            }

            for (int i = 0; i < orderFills.Count; i++)
            {
                var fill = orderFills[i];
                var fillPrice = fill.Price;
                var fillQuantity = fill.Side == OrderDirection.Sell
                    ? -fill.QuoteAmount
                    : fill.QuoteAmount;
                var updTime = Time.ParseDate(fill.CreatedAt);
                var orderFee = OrderFee.Zero;
                if (fill.Fee is > 0)
                {
                    // TODO: fee in not in docs, but present in response. Not sure about fee currency
                    // see ref. https://docs.dydx.xyz/types/fill_subaccount_message
                    // might not be sent if zero fee
                    orderFee = new OrderFee(new CashAmount(fill.Fee.Value, leanOrder.PriceCurrency));
                }

                // no current order status
                // TODO: check if we can get partially filled order status at all
                // their API does not contain it https://docs.dydx.xyz/types/order_status#orderstatus
                var status = i == orderFills.Count - 1 ? finalOrderStatus : OrderStatus.PartiallyFilled;
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
            if (update.OraclePrices == null)
            {
                continue;
            }

            foreach (var priceKvp in update.OraclePrices)
            {
                _market.UpdateOraclePrice(priceKvp.Key, priceKvp.Value.OraclePrice);
            }
        }
    }

    private void HandleOrderBookSnapshot(JObject jObj)
    {
        var orderbookSnapshot = jObj.ToObject<DataResponseSchema<Orderbook>>();
        var symbol = _symbolMapper.GetLeanSymbol(orderbookSnapshot.Id, SecurityType.CryptoFuture, MarketName);

        if (!_orderBooks.TryGetValue(symbol, out var orderBook))
        {
            orderBook = new DefaultOrderBook(symbol);
            _orderBooks[symbol] = orderBook;
        }
        else
        {
            orderBook.BestBidAskUpdated -= OnBestBidAskUpdated;
            orderBook.Clear();
        }

        UpdateOrderBook(orderBook, orderbookSnapshot.Contents);

        orderBook.BestBidAskUpdated += OnBestBidAskUpdated;
        if (orderBook.BestBidPrice == 0 && orderBook.BestAskPrice == 0)
        {
            // nothing to emit, can happen with illiquid assets
            return;
        }

        EmitQuoteTick(symbol, orderBook.BestBidPrice, orderBook.BestBidSize, orderBook.BestAskPrice,
            orderBook.BestAskSize);
    }

    private void HandleOrderBookDelta(JObject jObj)
    {
        var orderbookUpdate = jObj.ToObject<DataResponseSchema<Orderbook>>();
        var symbol = _symbolMapper.GetLeanSymbol(orderbookUpdate.Id, SecurityType.CryptoFuture, MarketName);

        if (!_orderBooks.TryGetValue(symbol, out var orderBook))
        {
            Log.Error($"Attempting to update a non existent order book for {symbol}");
            return;
        }

        UpdateOrderBook(orderBook, orderbookUpdate.Contents);
    }

    private void UpdateOrderBook(DefaultOrderBook orderBook, Orderbook orderbook)
    {
        if (orderbook.Bids != null)
        {
            foreach (var row in orderbook.Bids)
            {
                if (row.Size == 0)
                {
                    orderBook.RemoveBidRow(row.Price);
                }
                else
                {
                    orderBook.UpdateBidRow(row.Price, row.Size);
                }
            }
        }

        if (orderbook.Asks != null)
        {
            foreach (var row in orderbook.Asks)
            {
                if (row.Size == 0)
                {
                    orderBook.RemoveAskRow(row.Price);
                }
                else
                {
                    orderBook.UpdateAskRow(row.Price, row.Size);
                }
            }
        }
    }

    private void OnBestBidAskUpdated(object sender, BestBidAskUpdatedEventArgs e)
    {
        if (e.BestBidPrice < e.BestAskPrice)
        {
            EmitQuoteTick(e.Symbol, e.BestBidPrice, e.BestBidSize, e.BestAskPrice, e.BestAskSize);
        }

        // Orderbook got crossed, uncross it and then emit quote tick
        if (sender is DefaultOrderBook orderBook)
        {
            orderBook.BestBidAskUpdated -= OnBestBidAskUpdated;

            UncrossOrderBook(orderBook);

            orderBook.BestBidAskUpdated += OnBestBidAskUpdated;
            if (orderBook.BestBidPrice == 0 && orderBook.BestAskPrice == 0)
            {
                // nothing to emit, can happen with illiquid assets
                return;
            }

            EmitQuoteTick(e.Symbol,
                orderBook.BestBidPrice,
                orderBook.BestBidSize,
                orderBook.BestAskPrice,
                orderBook.BestAskSize);
        }
    }

    private void EmitQuoteTick(Symbol symbol, decimal bidPrice, decimal bidSize, decimal askPrice, decimal askSize)
    {
        var tick = new Tick
        {
            AskPrice = askPrice,
            BidPrice = bidPrice,
            Time = DateTime.UtcNow,
            Symbol = symbol,
            TickType = TickType.Quote,
            AskSize = askSize,
            BidSize = bidSize,
        };
        tick.SetValue();

        lock (_tickLocker)
        {
            _aggregator.Update(tick);
        }
    }

    /// <summary>
    /// Crossed prices where best bid > best ask may happen.
    /// This happens because the dydx network is decentralized, operated by 42 validators where the order book updates can be sent by any of the validators and therefore may arrive out of sequence to the full node/indexer
    /// see ref https://docs.dydx.xyz/interaction/data/watch-orderbook#uncrossing-the-orderbook
    /// </summary>
    /// <param name="orderBook">Order book to uncross</param>
    private void UncrossOrderBook(DefaultOrderBook orderBook)
    {
        // Get sorted lists: bids descending (highest first), asks ascending (lowest first)
        while (orderBook.BestBidPrice != 0
               && orderBook.BestAskPrice != 0
               && orderBook.BestBidPrice > orderBook.BestAskPrice)
        {
            var bidPrice = orderBook.BestBidPrice;
            var bidSize = orderBook.BestBidSize;
            var askPrice = orderBook.BestAskPrice;
            var askSize = orderBook.BestAskSize;

            if (bidSize > askSize)
            {
                orderBook.UpdateBidRow(bidPrice, bidSize - askSize);
                orderBook.RemoveAskRow(askPrice);
            }
            else if (bidSize < askSize)
            {
                orderBook.UpdateAskRow(askPrice, askSize - bidSize);
                orderBook.RemoveBidRow(bidPrice);
            }
            else
            {
                orderBook.RemoveAskRow(askPrice);
                orderBook.RemoveBidRow(bidPrice);
            }
        }
    }
}