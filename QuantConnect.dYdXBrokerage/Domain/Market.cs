using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualBasic;
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Brokerages.dYdX.Domain.Enums;
using QuantConnect.dYdXBrokerage.dYdXProtocol.Clob;
using QuantConnect.dYdXBrokerage.dYdXProtocol.Subaccounts;
using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using dYdXOrder = QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order;
using Order = QuantConnect.Orders.Order;

namespace QuantConnect.Brokerages.dYdX.Domain;

public class Market
{
    private readonly Wallet _wallet;
    private readonly ISymbolMapper _symbolMapper;
    private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase;
    private readonly dYdXApiClient _apiClient;
    private readonly ConcurrentDictionary<uint, Models.Symbol> _markets = new();
    private readonly Lock _refreshLock = new();
    private DateTime _lastRefreshTime;

    public const ulong DefaultGasLimit = 1_000_000;
    private const uint ShortBlockWindow = 20u;
    private const decimal MarketPriceBuffer = 0.05m; //5% buffer

    public Market(
        Wallet wallet,
        ISymbolMapper symbolMapper,
        SymbolPropertiesDatabase symbolPropertiesDatabase,
        dYdXApiClient apiClient)
    {
        _wallet = wallet;
        _symbolMapper = symbolMapper;
        _symbolPropertiesDatabase = symbolPropertiesDatabase;
        _apiClient = apiClient;
    }

    private Models.Symbol GetMarketInfo(uint marketTicker)
    {
        if (DateTime.UtcNow - _lastRefreshTime > TimeSpan.FromMinutes(5))
        {
            RefreshMarkets();
        }

        if (!_markets.TryGetValue(marketTicker, out var marketInfo))
        {
            throw new Exception($"Market info not found for ClobPairId: {marketTicker}");
        }

        return marketInfo;
    }

    public void RefreshMarkets(IEnumerable<Models.Symbol> markets = null)
    {
        lock (_refreshLock)
        {
            if (DateTime.UtcNow - _lastRefreshTime < TimeSpan.FromMinutes(5))
            {
                return;
            }

            markets ??= _apiClient.Indexer.GetExchangeInfo().Symbols.Values;

            foreach (var symbol in markets)
            {
                _markets.AddOrUpdate(symbol.ClobPairId, symbol, (_, __) => symbol);
            }

            _lastRefreshTime = DateTime.UtcNow;
        }
    }

    public void UpdateOraclePrice(uint marketTicker, decimal oraclePrice)
    {
        if (_markets.TryGetValue(marketTicker, out var marketInfo))
        {
            lock (marketInfo)
            {
                marketInfo.OraclePrice = oraclePrice;
            }
        }
    }

    public dYdXOrder CreateOrder(Order order)
    {
        var orderProperties = order.Properties as dYdXOrderProperties;
        var symbolProperties = GetSymbolProperties(order);
        var marketTickerUInt = ParseMarketTicker(symbolProperties.MarketTicker);
        var marketInfo = GetMarketInfo(marketTickerUInt);
        var orderFlag = GetOrderFlags(order);
        var side = GetOrderSide(order.Direction);

        var dydxOrder = new dYdXOrder
        {
            OrderId = new OrderId
            {
                SubaccountId = new SubaccountId { Owner = _wallet.Address, Number = _wallet.SubaccountNumber },
                // TODO: dYdX does not return order Id; LEAN orderId always starts from 1 on each run, so we use ClientId as a workaround
                // ClientId = checked((uint)order.Id),
                ClientId = RandomUInt32(),
                OrderFlags = (uint)orderFlag,
                ClobPairId = marketTickerUInt
            },
            Side = side,
            Quantums = CalculateQuantums(order.AbsoluteQuantity, symbolProperties, marketInfo),
            TimeInForce = GetTimeInForce(order.Type, orderProperties?.PostOnly == true),
            ReduceOnly = orderProperties?.ReduceOnly == true,
            ClientMetadata = GetClientMetadata(order.Type),
            ConditionType = GetConditionType(order.Type),
            ConditionalOrderTriggerSubticks = GetConditionalOrderTriggerSubticks(order, symbolProperties)
        };

        if (orderFlag == OrderFlags.ShortTerm)
        {
            ConfigureShortTermOrder(dydxOrder, orderProperties, marketInfo, symbolProperties, side);
        }
        else
        {
            ConfigureLongTermOrder(dydxOrder, order, marketInfo, symbolProperties);
        }

        return dydxOrder;
    }

    private SymbolProperties GetSymbolProperties(Order order)
    {
        var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(
            order.Symbol.ID.Market,
            order.Symbol,
            order.Symbol.SecurityType, Currencies.USD);

        if (symbolProperties == null)
        {
            throw new Exception($"No symbol properties found for {order.Symbol}");
        }

        return symbolProperties;
    }

    private uint ParseMarketTicker(string marketTicker)
    {
        if (!uint.TryParse(marketTicker, out var marketTickerAsUInt))
        {
            throw new Exception($"Invalid market ticker: {marketTicker}");
        }

        return marketTickerAsUInt;
    }

    private void ConfigureShortTermOrder(dYdXOrder dydxOrder, dYdXOrderProperties? orderProperties,
        Models.Symbol marketInfo,
        SymbolProperties symbolProperties, QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order.Types.Side side)
    {
        // Find the market configuration to get the current Oracle Price
        var oraclePrice = marketInfo.OraclePrice;

        // Calculate limit price: +5% for BUY, -5% for SELL to ensure fill
        var targetPrice = side == QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order.Types.Side.Buy
            ? oraclePrice * (1 + MarketPriceBuffer)
            : oraclePrice * (1 - MarketPriceBuffer);

        dydxOrder.Subticks = CalculateSubticks(targetPrice, symbolProperties, marketInfo);
        dydxOrder.GoodTilBlock = _apiClient.Node.GetLatestBlockHeight() +
                                 (orderProperties?.GoodTilBlockOffset ?? ShortBlockWindow);
    }

    private void ConfigureLongTermOrder(dYdXOrder dydxOrder, Order order, Models.Symbol marketInfo,
        SymbolProperties symbolProperties)
    {
        if (order.TimeInForce is not GoodTilDateTimeInForce dateTimeInForce)
        {
            throw new Exception($"Expected GoodTilDateTimeInForce {order.Type}");
        }

        dydxOrder.GoodTilBlockTime = Convert.ToUInt32(Time.DateTimeToUnixTimeStamp(dateTimeInForce.Expiry));
        dydxOrder.Subticks = CalculateSubticks(order.Price, symbolProperties, marketInfo);
    }

    private static ulong CalculateQuantums(decimal quantity, SymbolProperties symbolProperties,
        Models.Symbol marketInfo)
    {
        var quantums = Convert.ToUInt64(quantity * symbolProperties.StrikeMultiplier);
        quantums = (quantums / marketInfo.StepBaseQuantums) * marketInfo.StepBaseQuantums;
        return Math.Max(quantums, marketInfo.StepBaseQuantums);
    }

    private static ulong CalculateSubticks(decimal price, SymbolProperties symbolProperties, Models.Symbol marketInfo)
    {
        var subticks = Convert.ToUInt64(price * symbolProperties.PriceMagnifier);
        subticks = (subticks / marketInfo.SubticksPerTick) * marketInfo.SubticksPerTick;
        return Math.Max(subticks, marketInfo.SubticksPerTick);
    }

    private static dYdXOrder.Types.TimeInForce GetTimeInForce(OrderType type, bool postOnly)
    {
        return type switch
        {
            OrderType.Market => dYdXOrder.Types.TimeInForce.Ioc,
            OrderType.Limit or OrderType.StopLimit => postOnly
                ? dYdXOrder.Types.TimeInForce.PostOnly
                : dYdXOrder.Types.TimeInForce.Unspecified,
            OrderType.StopMarket => dYdXOrder.Types.TimeInForce.Ioc,
            _ => dYdXOrder.Types.TimeInForce.Unspecified
        };
    }

    private static uint GetClientMetadata(OrderType type)
    {
        return type switch
        {
            OrderType.Market or OrderType.StopMarket or OrderType.StopLimit => 1u,
            _ => 0u
        };
    }

    private static OrderFlags GetOrderFlags(Order order)
    {
        return order.Type switch
        {
            OrderType.Market => OrderFlags.ShortTerm,
            OrderType.Limit => order.TimeInForce is GoodTilDateTimeInForce ? OrderFlags.LongTerm : OrderFlags.ShortTerm,
            OrderType.StopLimit or OrderType.StopMarket => OrderFlags.Conditional,
            _ => throw new Exception($"Unexpected code path: orderFlags for {order.Type}")
        };
    }

    private static ulong GetConditionalOrderTriggerSubticks(Order order, SymbolProperties symbolProperties)
    {
        var stopPrice = order switch
        {
            StopMarketOrder stopMarket => stopMarket.StopPrice,
            StopLimitOrder stopLimit => stopLimit.StopPrice,
            _ => 0m
        };

        return Convert.ToUInt64(stopPrice * symbolProperties.PriceMagnifier);
    }

    private static dYdXOrder.Types.ConditionType GetConditionType(OrderType type)
    {
        return type is OrderType.StopMarket or OrderType.StopLimit
            ? dYdXOrder.Types.ConditionType.StopLoss
            : dYdXOrder.Types.ConditionType.Unspecified;
    }

    private static dYdXOrder.Types.Side GetOrderSide(OrderDirection direction)
    {
        return direction == OrderDirection.Buy
            ? QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order.Types.Side.Buy
            : QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order.Types.Side.Sell;
    }

    private static uint RandomUInt32()
    {
        return (uint)(Random.Shared.Next(1 << 30)) << 2 | (uint)(Random.Shared.Next(1 << 2));
    }
}