using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    /// <summary>
    /// Gets all open orders on the account.
    /// NOTE: The order objects returned do not have QC order IDs.
    /// </summary>
    /// <returns>The open orders returned from IB</returns>
    public override List<Order> GetOpenOrders()
    {
        // TODO: Implement
        // throw new NotImlementedException();
        return [];
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
            var positionsResponse = ApiClient.Indexer.GetPerpetualPositions(Wallet);
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
                    Quantity = pos.Quantity,
                    AveragePrice = pos.EntryPrice,
                    UnrealizedPnL = pos.UnrealizedPnl
                };

                holdings.Add(holding);
            }

            return holdings;
        }
        catch
        {
            // For safety, return empty on failure. Brokerage should surface errors via messaging if needed.
            return [];
        }
    }

    /// <summary>
    /// Gets the current cash balance for each currency held in the brokerage account
    /// </summary>
    /// <returns>The current cash balance for each currency available for trading</returns>
    public override List<CashAmount> GetCashBalance()
    {
        var balances = ApiClient.Node.GetCashBalance(Wallet);
        return balances
            .Balances
            .Select(b => new CashAmount(b.Amount, b.Denom.LazyToUpper()))
            .ToList();
    }

    /// <summary>
    /// Places a new order and assigns a new broker ID to the order
    /// </summary>
    /// <param name="order">The order to be placed</param>
    /// <returns>True if the request for a new order has been placed, false otherwise</returns>
    public override bool PlaceOrder(Order order)
    {
        dYdXPlaceOrderResponse result;
        try
        {
            var dydxOrder = _market.CreateOrder(order);
            var gasLimit = (order.Properties as dYdXOrderProperties)?.GasLimit ?? Domain.Market.DefaultGasLimit;
            result = ApiClient.Node.PlaceOrder(Wallet, dydxOrder, gasLimit);
            if (result.Code == 0)
            {
                order.BrokerId.Add(result.OrderId.ToString());
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event")
                {
                    Status = OrderStatus.Submitted
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
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event: " + ex.Message)
            {
                Status = OrderStatus.Invalid
            });
        }

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

        dYdXCancelOrderResponse result;
        try
        {
            var dydxOrder = _market.CreateOrder(order);
            var gasLimit = (order.Properties as dYdXOrderProperties)?.GasLimit ?? Domain.Market.DefaultGasLimit;
            result = ApiClient.Node.CancelOrder(Wallet, dydxOrder, gasLimit);
        }
        catch (Exception ex)
        {
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event: " + ex.Message)
            {
                Status = OrderStatus.Invalid
            });
            return false;
        }

        if (result.Code == 0)
        {
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "dYdX Order Event")
            {
                Status = OrderStatus.CancelPending
            });
        }
        else
        {
            var message =
                $"Cancel order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {result.Message}";
            OnOrderEvent(new OrderEvent(
                    order,
                    DateTime.UtcNow,
                    OrderFee.Zero,
                    result.Message)
                { Status = OrderStatus.Invalid });
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));
        }

        return true;
    }

    /// <summary>
    /// Connects the client to the broker's remote servers
    /// </summary>
    public override void Connect()
    {
        if (IsConnected)
        {
            return;
        }

        _ = ApiClient;
        Log.Trace($"Connected {ApiClient}");

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
                Log.Error($"dYdXBrokerage.OnReconnect: {ex.Message}");
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