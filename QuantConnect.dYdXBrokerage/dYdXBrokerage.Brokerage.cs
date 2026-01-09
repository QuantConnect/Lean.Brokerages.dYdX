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
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Brokerages.dYdX.Extensions;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;
using Order = QuantConnect.Orders.Order;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    /// <summary>
    /// Gets all open orders on the account.
    /// </summary>
    /// <returns>The open orders returned from dYdX</returns>
    public override List<Order> GetOpenOrders()
    {
        var orders = new List<Order>();
        var dydxOrders = _apiClient.Indexer.GetOpenOrders(Wallet);
        foreach (var dydxOrder in dydxOrders)
        {
            var order = _market.ParseOrder(dydxOrder);
            if (order.Status.IsClosed())
            {
                continue;
            }

            _orderBrokerIdToClientIdMap.TryAdd(dydxOrder.Id, dydxOrder.ClientId);
            orders.Add(order);
        }

        return orders;
    }

    /// <summary>
    /// Gets all holdings for the account
    /// </summary>
    /// <returns>The current holdings from the account</returns>
    public override List<Holding> GetAccountHoldings()
    {
        // dYdX Indexer provides open perpetual positions. We'll query subaccount 0 by default.
        // This can be extended to support multiple subaccounts if needed.
        try
        {
            var positionsResponse = _apiClient.Indexer.GetPerpetualPositions(Wallet);
            var holdings = new List<Holding>();

            if (positionsResponse?.Positions == null)
            {
                return holdings;
            }

            foreach (var pos in positionsResponse.Positions)
            {
                var ticker = pos.Symbol;
                if (string.IsNullOrWhiteSpace(ticker))
                {
                    continue;
                }

                var symbol = _symbolMapper.GetLeanSymbol(ticker, SecurityType, MarketName);

                var holding = new Holding
                {
                    Symbol = symbol,
                    Quantity = pos.Size,
                    AveragePrice = pos.EntryPrice,
                    UnrealizedPnL = pos.UnrealizedPnl
                };

                holdings.Add(holding);
            }

            return holdings;
        }
        catch (Exception err)
        {
            Log.Error(
                $"dYdXBrokerage.GetAccountHoldings() Error: {err.Message} Source {err.Source} Stack {err.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// Gets the current cash balance for each currency held in the brokerage account
    /// </summary>
    /// <returns>The current cash balance for each currency available for trading</returns>
    public override List<CashAmount> GetCashBalance()
    {
        if (_algorithm == null)
        {
            return [];
        }

        var subaccount = _apiClient.Indexer.GetSubaccount(Wallet);
        return subaccount.GetCashAmounts(SymbolPropertiesDatabase, _algorithm.BrokerageModel.AccountType);
    }

    /// <summary>
    /// Places a new order and assigns a new broker ID to the order
    /// </summary>
    /// <param name="order">The order to be placed</param>
    /// <returns>True if the request for a new order has been placed, false otherwise</returns>
    public override bool PlaceOrder(Order order)
    {
        if (!CanSubscribe(order.Symbol))
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1,
                $"Symbol is not supported {order.Symbol}"));
            return false;
        }

        var resetEvent = new ManualResetEventSlim(false);
        var clientId = RandomUInt32();
        var gasLimit = (order.Properties as dYdXOrderProperties)?.GasLimit ?? Domain.Market.DefaultGasLimit;
        dYdXPlaceOrderResponse result = null;
        _messageHandler.WithLockedStream(() =>
        {
            try
            {
                // dYdX Market consumes block height and oracle price to create order
                // those are updated on WS stream events, and that's why we need to wait for it
                var dydxOrder = _market.CreateOrder(order, clientId);
                _pendingOrders[clientId] = Tuple.Create(resetEvent, order);
                result = _apiClient.Node.PlaceOrder(Wallet, dydxOrder, gasLimit);
                if (result.Code == 0)
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event")
                    {
                        Status = OrderStatus.New
                    });
                }
                else
                {
                    var message =
                        $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {result.Message}";
                    OnOrderEvent(new OrderEvent(
                            order,
                            DateTime.UtcNow,
                            OrderFee.Zero,
                            result.Message)
                        { Status = OrderStatus.Invalid });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                }
            }
            catch (Exception ex)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero,
                    "dYdX Order Event: " + ex.Message)
                {
                    Status = OrderStatus.Invalid
                });
            }
        });

        if (result?.Code == 0 && !resetEvent.Wait(WaitPlaceOrderEventTimeout))
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "Order timed out"));
        }

        _pendingOrders.TryRemove(clientId, out _);
        resetEvent.DisposeSafely();

        return true;
    }

    /// <summary>
    /// Updates the order with the same id
    /// </summary>
    /// <param name="order">The new order information</param>
    /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
    public override bool UpdateOrder(Order order)
    {
        throw new NotSupportedException(
            "dYdXBrokerage.UpdateOrder: Order update not supported. Please cancel and re-create.");
    }

    /// <summary>
    /// Cancels the order with the specified ID
    /// </summary>
    /// <param name="order">The order to cancel</param>
    /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
    public override bool CancelOrder(Order order)
    {
        if (order.Status == OrderStatus.Filled || order.Type == OrderType.Market)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "Order already filled"));
            return false;
        }

        if (order.Status is OrderStatus.Canceled)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "Order already canceled"));
            return false;
        }

        // TODO: Do we need to remote map record for cancelled orders?
        if (!_orderBrokerIdToClientIdMap.TryGetValue(order.BrokerId.First(), out var clientId))
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, $"Order {order.Id} has no clientId"));
            return false;
        }

        dYdXCancelOrderResponse result;
        bool submitted = true;
        _messageHandler.WithLockedStream(() =>
        {
            try
            {
                var dydxOrder = _market.CreateOrder(order, clientId);
                var gasLimit = (order.Properties as dYdXOrderProperties)?.GasLimit ?? Domain.Market.DefaultGasLimit;
                result = _apiClient.Node.CancelOrder(Wallet, dydxOrder, gasLimit);
            }
            catch (Exception ex)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, ex.Message));
                submitted = false;
                return;
            }

            if (result.Code != 0)
            {
                var message =
                    $"Cancel order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {result.Message}";
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
                submitted = false;
            }
        });
        return submitted;
    }

    /// <summary>
    /// Connects the client to the broker's remote servers
    /// </summary>
    public override void Connect()
    {
        if (_algorithm == null)
        {
            return;
        }

        if (IsConnected)
        {
            return;
        }

        WebSocket.Error += (sender, error) =>
        {
            if (sender is WebSocketClientWrapper { IsOpen: false })
            {
                _connectionConfirmedEvent.Reset();
                OnMessage(BrokerageMessageEvent.Disconnected(error.Message));
            }
        };

        WebSocket.Open += OnReconnect;
        ConnectSync();
    }

    private void OnReconnect(object sender, EventArgs e)
    {
        Task.Run(() =>
        {
            try
            {
                // Wait for the brokerage to send the "Connected/Auth" message again
                WaitConnectionConfirmationSync();

                // Once confirmed, re-send subscriptions
                SubscribeToFixedChannels(sender, e);
            }
            catch (Exception ex)
            {
                Log.Error($"{nameof(dYdXBrokerage)}.{nameof(OnReconnect)}: {ex.Message}");
            }
        });
    }

    private void SubscribeToFixedChannels(object sender, EventArgs e)
    {
        Subscribe("v4_markets", batched: true);
        Subscribe("v4_subaccounts", id: $"{Wallet.Address}/{Wallet.SubaccountNumber}", batched: false);
    }

    private void WaitConnectionConfirmationSync()
    {
        var connectingValidFor = TimeSpan.FromSeconds(30);

        if (!_connectionConfirmedEvent.WaitOne(connectingValidFor))
        {
            throw new TimeoutException("Websockets connection timeout.");
        }
    }

    /// <summary>
    /// Disconnects the client from the broker's remote servers
    /// </summary>
    public override void Disconnect()
    {
        if (WebSocket?.IsOpen != true)
        {
            return;
        }

        WebSocket.Close();
    }
}