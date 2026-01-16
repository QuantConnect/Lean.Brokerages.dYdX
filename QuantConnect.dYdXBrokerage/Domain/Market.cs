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
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Brokerages.dYdX.Domain.Enums;
using QuantConnect.dYdXBrokerage.dYdXProtocol.Clob;
using QuantConnect.dYdXBrokerage.dYdXProtocol.Subaccounts;
using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using QuantConnect.Brokerages.dYdX.Models;
using dYdXOrder = QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order;
using Order = QuantConnect.Orders.Order;

namespace QuantConnect.Brokerages.dYdX.Domain;

public class Market
{
    private readonly Wallet _wallet;
    private readonly SymbolPropertiesDatabaseSymbolMapper _symbolMapper;
    private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase;
    private readonly dYdXApiClient _apiClient;
    private readonly Dictionary<string, Models.Symbol> _markets = new();
    private uint _lastBlockHeight;
    private DateTime _lastMarketRefreshTime;
    private DateTime _lastBlockHeightUpdateTime;
    private bool _gtcWarningSent;

    public const ulong DefaultGasLimit = 1_000_000;
    private const uint ShortBlockWindow = 20u;
    private const decimal MarketPriceBuffer = 0.05m; //5% buffer

    public event Action<BrokerageMessageEvent> BrokerageMessage;

    public Market(
        Wallet wallet,
        SymbolPropertiesDatabaseSymbolMapper symbolMapper,
        SymbolPropertiesDatabase symbolPropertiesDatabase,
        dYdXApiClient apiClient)
    {
        _wallet = wallet;
        _symbolMapper = symbolMapper;
        _symbolPropertiesDatabase = symbolPropertiesDatabase;
        _apiClient = apiClient;
    }

    private Models.Symbol GetMarketInfo(string marketTicker)
    {
        if (DateTime.UtcNow - _lastMarketRefreshTime > TimeSpan.FromMinutes(5))
        {
            RefreshMarkets();
        }

        if (!_markets.TryGetValue(marketTicker, out var marketInfo))
        {
            throw new Exception($"Market info not found for Ticker: {marketTicker}");
        }

        return marketInfo;
    }

    public void RefreshMarkets(IEnumerable<Models.Symbol> markets = null)
    {
        _markets.Clear();
        markets ??= _apiClient.Indexer.GetExchangeInfo().Symbols.Values;

        foreach (var symbol in markets)
        {
            if (!_symbolMapper.IsKnownBrokerageSymbol(symbol.Ticker))
            {
                continue;
            }

            _markets.Add(symbol.Ticker, symbol);
        }

        _lastMarketRefreshTime = DateTime.UtcNow;
    }

    public void UpdateOraclePrice(string marketTicker, decimal oraclePrice)
    {
        if (_markets.TryGetValue(marketTicker, out var marketInfo))
        {
            marketInfo.OraclePrice = oraclePrice;
        }
    }

    public Order ParseOrder(OrderDto orderDto)
    {
        var symbol = _symbolMapper.GetLeanSymbol(orderDto.Ticker, SecurityType.CryptoFuture, QuantConnect.Market.DYDX);

        decimal size = orderDto.Size, price = orderDto.Price, triggerPrice = orderDto.TriggerPrice;
        var quantity = orderDto.Side switch
        {
            OrderDirection.Buy => size,
            OrderDirection.Sell => -size,
            _ => throw new Exception($"Unexpected code path: orderDirection for {orderDto.Id}")
        };

        var orderType = ParseOrderType(orderDto);
        Order order = orderType switch
        {
            OrderType.Limit => new LimitOrder(
                symbol,
                quantity,
                price,
                orderDto.UpdatedAt,
                properties: new dYdXOrderProperties
                {
                    PostOnly = orderDto.PostOnly,
                    ReduceOnly = orderDto.ReduceOnly,
                    TimeInForce = new GoodTilDateTimeInForce(Time.ParseDate(orderDto.GoodTilBlockTime))
                }),
            OrderType.Market => new MarketOrder(
                symbol,
                quantity,
                orderDto.UpdatedAt,
                properties: new dYdXOrderProperties
                {
                    ReduceOnly = orderDto.ReduceOnly
                }),
            OrderType.StopMarket => new StopMarketOrder(
                symbol,
                quantity,
                triggerPrice,
                orderDto.UpdatedAt,
                properties: new dYdXOrderProperties
                {
                    ReduceOnly = orderDto.ReduceOnly
                }),
            OrderType.StopLimit => new StopLimitOrder(
                symbol,
                quantity,
                triggerPrice,
                price,
                orderDto.UpdatedAt,
                properties: new dYdXOrderProperties
                {
                    PostOnly = orderDto.PostOnly,
                    ReduceOnly = orderDto.ReduceOnly,
                    TimeInForce = new GoodTilDateTimeInForce(Time.ParseDate(orderDto.GoodTilBlockTime))
                }),
            _ => new MarketOrder() // Fallback
        };

        order.Status = ParseOrderStatus(orderDto.Status);
        order.BrokerId.Add(orderDto.Id);

        return order;
    }

    private OrderType ParseOrderType(OrderDto orderDto)
    {
        var orderFlags = orderDto.OrderFlags;
        if (!Enum.IsDefined(typeof(OrderFlags), orderFlags))
        {
            throw new InvalidCastException($"Unexpected : orderFlags for {orderFlags}");
        }

        return (OrderFlags)orderFlags switch
        {
            OrderFlags.LongTerm => OrderType.Limit,
            OrderFlags.ShortTerm when !string.IsNullOrEmpty(orderDto.GoodTilBlockTime) => OrderType.Limit,
            OrderFlags.ShortTerm when !string.IsNullOrEmpty(orderDto.GoodTilBlock) => OrderType.Market,
            OrderFlags.Conditional when orderDto.ClientMetadata == 1u => OrderType.StopMarket,
            OrderFlags.Conditional => OrderType.StopLimit,
            _ => OrderType.Limit
        };
    }

    public static OrderStatus ParseOrderStatus(string status)
    {
        return status?.ToUpperInvariant() switch
        {
            "BEST_EFFORT_OPENED" => OrderStatus.Submitted,
            "OPEN" => OrderStatus.Submitted,
            "FILLED" => OrderStatus.Filled,
            "BEST_EFFORT_CANCELED" => OrderStatus.Canceled,
            "CANCELED" => OrderStatus.Canceled,
            "PENDING" => OrderStatus.New,
            _ => OrderStatus.None
        };
    }

    public dYdXOrder CreateOrder(Order order, uint clientId)
    {
        var orderProperties = order.Properties as dYdXOrderProperties;
        var symbolProperties = GetSymbolProperties(order);
        var marketInfo = GetMarketInfo(symbolProperties.MarketTicker);
        var orderFlag = GetOrderFlags(order);
        var side = GetOrderSide(order.Direction);

        var dydxOrder = new dYdXOrder
        {
            OrderId = new OrderId
            {
                SubaccountId = new SubaccountId { Owner = _wallet.Address, Number = _wallet.SubaccountNumber },
                ClientId = clientId,
                OrderFlags = (uint)orderFlag,
                ClobPairId = marketInfo.ClobPairId
            },
            Side = side,
            Quantums = CalculateQuantums(order.AbsoluteQuantity, symbolProperties, marketInfo),
            TimeInForce = GetTimeInForce(order.Type, orderProperties),
            ReduceOnly = orderProperties?.ReduceOnly == true,
            ClientMetadata = GetClientMetadata(order.Type),
            ConditionType = GetConditionType(order.Type),
            ConditionalOrderTriggerSubticks = GetConditionalOrderTriggerSubticks(order, symbolProperties, marketInfo)
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
            order.Symbol.SecurityType,
            Currencies.USD);

        if (symbolProperties == null)
        {
            throw new Exception($"No symbol properties found for {order.Symbol}");
        }

        return symbolProperties;
    }

    private void ConfigureShortTermOrder(dYdXOrder dydxOrder, dYdXOrderProperties orderProperties,
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
        dydxOrder.GoodTilBlock = GetBlockHeight() + (orderProperties?.GoodTilBlockOffset ?? ShortBlockWindow);
    }

    private void ConfigureLongTermOrder(dYdXOrder dydxOrder, Order order, Models.Symbol marketInfo,
        SymbolProperties symbolProperties)
    {
        // simulate long term order expiry
        // Day => add 24 hours
        // GoodTilCanceled => add 90 days,
        // max supported by dYdX is 95 days, ref https://github.com/dydxprotocol/v4-chain/blob/4eb219b1b726df9ba17c9939e8bb9296f5e98bb3/protocol/x/clob/types/constants.go#L17
        if (!_gtcWarningSent)
        {
            _gtcWarningSent = true;
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, "GTC time in force not fully supported, order will expire in 90 days."));
        }

        var expiry = order.TimeInForce switch
        {
            GoodTilDateTimeInForce dateTimeInForce => dateTimeInForce.Expiry,
            DayTimeInForce => DateTime.UtcNow.AddHours(24),
            GoodTilCanceledTimeInForce => DateTime.UtcNow.AddDays(90),
            _ => throw new NotImplementedException($"Order's parameter '{nameof(order.TimeInForce)}' of type '{order.TimeInForce.GetType().Name}' is not supported.")
        };

        dydxOrder.GoodTilBlockTime = Convert.ToUInt32(Time.DateTimeToUnixTimeStamp(expiry));
        dydxOrder.Subticks = CalculateSubticks(order.Price, symbolProperties, marketInfo);
    }

    private void OnMessage(BrokerageMessageEvent brokerageMessageEvent)
    {
        BrokerageMessage?.Invoke(brokerageMessageEvent);
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

    private dYdXOrder.Types.TimeInForce GetTimeInForce(OrderType type, dYdXOrderProperties orderProperties = null)
    {
        return type switch
        {
            // MARKET orders: IOC if requested, otherwise Unspecified
            OrderType.Market or OrderType.StopMarket  =>
                orderProperties is { IOC: true }
                    ? dYdXOrder.Types.TimeInForce.Ioc
                    : dYdXOrder.Types.TimeInForce.Unspecified,

            // LIMIT orders: PostOnly if requested, IOC not supported and returns code = 3002, otherwise Unspecified
            OrderType.Limit   =>
                orderProperties switch
                {
                    { PostOnly: true } => dYdXOrder.Types.TimeInForce.PostOnly,
                    { IOC: true } => throw new ArgumentOutOfRangeException(nameof(orderProperties.IOC),"IOC not supported for LIMIT orders"),
                    _ => dYdXOrder.Types.TimeInForce.Unspecified
                },

            // STOP orders: support PostOnly and IOC
            OrderType.StopLimit  =>
                orderProperties switch
                {
                    { PostOnly: true } => dYdXOrder.Types.TimeInForce.PostOnly,
                    { IOC: true } => dYdXOrder.Types.TimeInForce.Ioc,
                    _ => dYdXOrder.Types.TimeInForce.Unspecified
                },

            _ => dYdXOrder.Types.TimeInForce.Unspecified
        };
    }

    private static uint GetClientMetadata(OrderType type)
    {
        return type switch
        {
            OrderType.Market or OrderType.StopMarket => 1u,
            _ => 0u
        };
    }

    private static OrderFlags GetOrderFlags(Order order)
    {
        return order.Type switch
        {
            OrderType.Market => OrderFlags.ShortTerm,
            OrderType.Limit => OrderFlags.LongTerm,
            OrderType.StopLimit or OrderType.StopMarket => OrderFlags.Conditional,
            _ => throw new Exception($"Unexpected code path: orderFlags for {order.Type}")
        };
    }

    private static ulong GetConditionalOrderTriggerSubticks(Order order, SymbolProperties symbolProperties, Models.Symbol marketInfo)
    {
        switch (order)
        {
            case StopMarketOrder stopMarket:
                return CalculateSubticks(stopMarket.StopPrice, symbolProperties, marketInfo);
            case StopLimitOrder stopLimit:
                return CalculateSubticks(stopLimit.StopPrice, symbolProperties, marketInfo);
            default:
                return 0;
        }
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

    private uint GetBlockHeight()
    {
        if (DateTime.UtcNow - _lastBlockHeightUpdateTime > TimeSpan.FromSeconds(5))
        {
            UpdateBlockHeigh(_apiClient.Node.GetLatestBlockHeight());
        }

        return _lastBlockHeight;
    }

    public void UpdateBlockHeigh(uint blockHeight)
    {
        _lastBlockHeight = blockHeight;
        _lastBlockHeightUpdateTime = DateTime.UtcNow;
    }
}