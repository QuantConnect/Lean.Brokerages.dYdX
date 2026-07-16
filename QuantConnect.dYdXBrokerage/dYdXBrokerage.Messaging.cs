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
using QuantConnect.Brokerages.dYdX.Extensions;
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

    // dYdX can report the same fill on more than one message (e.g. a partial fill while OPEN and again with
    // the final status), so we track, per order, the ids of fills already turned into OrderEvents to avoid
    // double counting holdings/cash. The per-order set is dropped once the order is filled or canceled, so
    // memory stays bounded by the number of open orders rather than the lifetime fill count.
    private readonly Dictionary<string, HashSet<string>> _processedFillIdsByOrder = [];

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
                Log.Debug($"{nameof(dYdXBrokerage)}.{nameof(OnDataMessage)}(): {e.Message}");
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
            EmitTradeTick(symbol,
                Time.ParseDate(trade.CreatedAt),
                trade.Price,
                trade.Size);
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
                case "UNTRIGGERED":
                case "OPEN":
                case "BEST_EFFORT_OPENED":
                    TryHandleOpen(dydxOrder);
                    // dYdX v4 has no PARTIALLY_FILLED order status: intermediate partial fills
                    // are reported through the Fills array while the order is still OPEN.
                    // Process them here so the fills are not dropped, otherwise LEAN holdings/cash
                    // drift from the broker state until the order eventually reaches FILLED.
                    if (contents.Fills?.Any(x => x.OrderId == dydxOrder.Id) == true)
                    {
                        HandleFills(dydxOrder, contents);
                    }

                    break;

                case "CANCELED":
                case "BEST_EFFORT_CANCELED":
                    var hasFills = contents.Fills?.Any(x => x.OrderId == dydxOrder.Id) == true;
                    var leanCancelOrder =
                        _orderProvider.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();

                    // dYdX can partially fill an order LEAN gave up on (see OnPlaceOrderTimeout) and then
                    // cancel the remainder without LEAN ever seeing it open: recover the order so the
                    // bundled fills are not dropped
                    if (leanCancelOrder == null && hasFills && TryHandleOpen(dydxOrder))
                    {
                        leanCancelOrder = _orderProvider.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();
                    }

                    if (leanCancelOrder != null)
                    {
                        // dYdX can report a final partial fill in the same message that cancels the remainder.
                        // Emit those fills (as PartiallyFilled) before the cancellation,
                        // otherwise they are dropped the same way OPEN-status fills were.
                        if (hasFills)
                        {
                            HandleFills(dydxOrder, contents);
                        }

                        OnOrderEvent(new OrderEvent(leanCancelOrder, DateTime.UtcNow, OrderFee.Zero,
                            $"dYdX Order Event (cancel id: {dydxOrder.Id})")
                        {
                            Status = OrderStatus.Canceled
                        });
                    }
                    else if (hasFills)
                    {
                        // fills for an order we cannot attribute at all: let HandleFills surface the divergence
                        HandleFills(dydxOrder, contents);
                    }

                    // dYdX confirmed the order is dead: a timed-out order canceled by the chain (e.g. its
                    // good-til-block expired) needs no recovery anymore
                    _timedOutOrders.TryRemove(dydxOrder.ClientId, out _);

                    // terminal status: the order will receive no further fills, drop its dedup set
                    ForgetProcessedFills(dydxOrder.Id);
                    break;

                case "FILLED":
                    HandleFills(dydxOrder, contents);
                    // terminal status: the order will receive no further fills, drop its dedup set
                    ForgetProcessedFills(dydxOrder.Id);
                    break;

                default:
                    Log.Error(
                        $"{nameof(dYdXBrokerage)}.{nameof(HandleOrders)}: order status not handled: {dydxOrder.Status}");
                    break;
            }
        }
    }

    private bool TryHandleOpen(OrderSubaccountMessage dydxOrder)
    {
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
            return true;
        }

        // The submission confirmation never arrived in time so LEAN marked the order Invalid (see
        // OnPlaceOrderTimeout), but the timeout verdict was wrong: the order reached the chain and dYdX is
        // now reporting it. Re-register its broker id and tell LEAN it is live again so its fills update the
        // portfolio; silently ignoring them leaves LEAN unaware of a real broker position, which compounds
        // until margin pressure or liquidation exposes it.
        if (_timedOutOrders.TryRemove(dydxOrder.ClientId, out var timedOutOrder))
        {
            timedOutOrder.BrokerId.Add(dydxOrder.Id);
            _orderBrokerIdToClientIdMap.TryAdd(dydxOrder.Id, dydxOrder.ClientId);
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1,
                $"Order {timedOutOrder.Id} timed out awaiting its submission confirmation, but dYdX reported " +
                $"it afterwards (id: {dydxOrder.Id}); resuming its tracking"));
            OnOrderEvent(new OrderEvent(timedOutOrder, DateTime.UtcNow, OrderFee.Zero,
                "dYdX Order Event: order confirmed after timeout")
            {
                Status = OrderStatus.Submitted
            });
            return true;
        }

        return false;
    }

    private void HandleFills(OrderSubaccountMessage dydxOrder, SubaccountsUpdateMessage messageContents)
    {
        try
        {
            var leanOrder = _orderProvider.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();

            // check if the FILL event arrived before OPEN, or after the submission wait timed out
            if (leanOrder == null && TryHandleOpen(dydxOrder))
            {
                leanOrder = _orderProvider.GetOrdersByBrokerageId(dydxOrder.Id)?.SingleOrDefault();
            }

            if (leanOrder == null)
            {
                // A fill we cannot attribute to any order LEAN knows about: the portfolio no longer matches
                // the broker account (e.g. another session trading the same subaccount). Silently dropping
                // the fill hides the divergence until margin calls or liquidation expose it, so emit an
                // Error, which stops the algorithm under the default brokerage message handler.
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                    $"{nameof(dYdXBrokerage)}.{nameof(HandleFills)}: received fills for an unknown order " +
                    $"(id: {dydxOrder.Id}, client id: {dydxOrder.ClientId}). LEAN's portfolio state no " +
                    "longer matches the dYdX subaccount; verify the account holdings before resuming " +
                    "trading."));
                return;
            }

            if (leanOrder.Status == OrderStatus.Filled)
            {
                // A fully filled order cannot receive new fills: this is a re-delivered message (e.g. the
                // indexer resending events after a websocket reconnect) arriving after the order's fill
                // dedup set was dropped, and re-emitting its fills would double count holdings/cash.
                Log.Trace($"{nameof(dYdXBrokerage)}.{nameof(HandleFills)}: ignoring fills re-delivered " +
                    $"for filled order {leanOrder.Id} (brokerage id: {dydxOrder.Id})");
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
                if (!TryMarkFillProcessed(dydxOrder.Id, fill.Id))
                {
                    // already emitted this fill, skip it so holdings/cash are not double counted
                    continue;
                }

                var fillPrice = fill.Price;
                var fillQuantity = fill.Side == OrderDirection.Sell
                    ? -fill.Size
                    : fill.Size;
                var updTime = Time.ParseDate(fill.CreatedAt);
                var orderFee = OrderFee.Zero;
                if (fill.Fee is > 0)
                {
                    // TODO: fee in not in docs, but present in response. Not sure about fee currency
                    // see ref. https://docs.dydx.xyz/types/fill_subaccount_message
                    // might not be sent if zero fee
                    orderFee = new OrderFee(new CashAmount(fill.Fee.Value, leanOrder.PriceCurrency));
                }

                // dYdX has no PARTIALLY_FILLED order status (https://docs.dydx.xyz/types/order_status#orderstatus).
                // A fill only completes the order when the order itself reached FILLED status and it is the last
                // fill in the message; every other fill (including all fills that arrive while the order is still
                // OPEN) is a partial fill.
                var isLastFill = i == orderFills.Count - 1;
                var status = isLastFill && finalOrderStatus == OrderStatus.Filled
                    ? OrderStatus.Filled
                    : OrderStatus.PartiallyFilled;
                var orderEvent = new OrderEvent
                (
                    leanOrder.Id, leanOrder.Symbol, updTime, status,
                    fill.Side, fillPrice, fillQuantity,
                    orderFee, $"dYdX Order Event {fill.Side} (fill id: {fill.Id})"
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

    /// <summary>
    /// Records a fill id as processed for the given order, returning <c>true</c> if it had not been seen
    /// before (so it should be emitted) or <c>false</c> if it was already emitted. Fills without an id are
    /// always processed.
    /// </summary>
    /// <remarks>Only called from the serialized message-handling path, so it needs no synchronization.</remarks>
    private bool TryMarkFillProcessed(string brokerOrderId, string fillId)
    {
        if (string.IsNullOrEmpty(fillId))
        {
            // no id to deduplicate on, process it rather than risk dropping a real fill
            return true;
        }

        if (!_processedFillIdsByOrder.TryGetValue(brokerOrderId, out var fillIds))
        {
            _processedFillIdsByOrder[brokerOrderId] = fillIds = [];
        }

        // HashSet.Add returns false when the fill was already emitted for this order
        return fillIds.Add(fillId);
    }

    /// <summary>
    /// Drops the tracked fill ids for an order once it reaches a terminal status. No-op when the order id is
    /// null/empty or was never tracked (<see cref="Dictionary{TKey,TValue}.Remove(TKey)"/> throws on a null key).
    /// </summary>
    private void ForgetProcessedFills(string brokerOrderId)
    {
        if (!string.IsNullOrEmpty(brokerOrderId))
        {
            _processedFillIdsByOrder.Remove(brokerOrderId);
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

            orderBook.UncrossOrderBook();

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
}
