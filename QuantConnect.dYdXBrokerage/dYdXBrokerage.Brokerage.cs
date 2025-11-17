using System;
using QuantConnect.Orders;
using QuantConnect.Securities;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;

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
            var positionsResponse = ApiClient.GetOpenPerpetualPositions(_subaccountNumber);
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
        var balances = ApiClient.GetCashBalance();
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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Updates the order with the same id
    /// </summary>
    /// <param name="order">The new order information</param>
    /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
    public override bool UpdateOrder(Order order)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Cancels the order with the specified ID
    /// </summary>
    /// <param name="order">The order to cancel</param>
    /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
    public override bool CancelOrder(Order order)
    {
        throw new NotImplementedException();
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
    }

    /// <summary>
    /// Disconnects the client from the broker's remote servers
    /// </summary>
    public override void Disconnect()
    {
        // TODO: Implement
        // throw new NotImplementedException();
    }
}