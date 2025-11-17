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
using QuantConnect.Data;
using QuantConnect.Packets;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using QuantConnect.Brokerages.dYdX.Api;

namespace QuantConnect.Brokerages.dYdX;

[BrokerageFactory(typeof(dYdXBrokerageFactory))]
public partial class dYdXBrokerage : Brokerage, IDataQueueHandler, IDataQueueUniverseProvider
{
    private const string MarketName = Market.dYdX;
    private const SecurityType SecurityType = QuantConnect.SecurityType.CryptoFuture;

    private int _subaccountNumber;
    private IAlgorithm _algorithm;
    private IDataAggregator _aggregator;
    private LiveNodePacket _job;
    private readonly EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;
    private SymbolPropertiesDatabaseSymbolMapper _symbolMapper;

    private Lazy<dYdXApiClient> _apiClientLazy;

    /// <summary>
    /// API client
    /// </summary>
    protected dYdXApiClient ApiClient => _apiClientLazy.Value;

    /// <summary>
    /// Returns true if we're currently connected to the broker
    /// </summary>
    public override bool IsConnected => _apiClientLazy?.IsValueCreated ?? false;

    /// <summary>
    /// Parameterless constructor for brokerage
    /// </summary>
    /// <remarks>This parameterless constructor is required for brokerages implementing <see cref="IDataQueueHandler"/></remarks>
    public dYdXBrokerage() : base(MarketName)
    {
    }

    /// <summary>
    /// Creates a new instance
    /// </summary>
    /// <param name="aggregator">consolidate ticks</param>
    public dYdXBrokerage(string address, int subaccountNumber, string nodeUrl, string indexerUrl,
        IAlgorithm algorithm,
        IDataAggregator aggregator,
        LiveNodePacket job) :
        base(MarketName)
    {
        Initialize(address, subaccountNumber, nodeUrl, indexerUrl, algorithm, aggregator, job);

        _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
        _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
        _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

        // Useful for some brokerages:

        // Brokerage helper class to lock websocket message stream while executing an action, for example placing an order
        // avoid race condition with placing an order and getting filled events before finished placing
        // _messageHandler = new BrokerageConcurrentMessageHandler<>();

        // Rate gate limiter useful for API/WS calls
        // _connectionRateLimiter = new RateGate();
    }

    private void Initialize(string address, int subaccountNumber, string nodeUrl, string indexerUrl,
        IAlgorithm algorithm,
        IDataAggregator aggregator,
        LiveNodePacket job)
    {
        _job = job;
        _algorithm = algorithm;
        _aggregator = aggregator;
        _subaccountNumber = subaccountNumber;

        _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(MarketName);

        // can be null if dYdXBrokerage is used as DataQueueHandler only
        if (_algorithm != null)
        {
            _apiClientLazy = new Lazy<dYdXApiClient>(() =>
            {
                if (string.IsNullOrEmpty(address))
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, "Address is missing"));
                    throw new Exception("Address is missing");
                }

                var client = GetApiClient(address, nodeUrl, indexerUrl);

                return client;
            });
        }

        dYdXApiClient GetApiClient(string address, string nodeUrl, string indexerUrl)
        {
            return new dYdXApiClient(address, nodeUrl, indexerUrl);
        }
    }

    #region IDataQueueHandler

    /// <summary>
    /// Subscribe to the specified configuration
    /// </summary>
    /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
    /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
    /// <returns>The new enumerator for this subscription request</returns>
    public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        if (!CanSubscribe(dataConfig.Symbol))
        {
            return null;
        }

        var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
        _subscriptionManager.Subscribe(dataConfig);

        return enumerator;
    }

    /// <summary>
    /// Removes the specified configuration
    /// </summary>
    /// <param name="dataConfig">Subscription config to be removed</param>
    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        _subscriptionManager.Unsubscribe(dataConfig);
        _aggregator.Remove(dataConfig);
    }

    /// <summary>
    /// Sets the job we're subscribing for
    /// </summary>
    /// <param name="job">Job we're subscribing for</param>
    public void SetJob(LiveNodePacket job)
    {
        throw new NotImplementedException();
    }

    #endregion


    #region IDataQueueUniverseProvider

    /// <summary>
    /// Method returns a collection of Symbols that are available at the data source.
    /// </summary>
    /// <param name="symbol">Symbol to lookup</param>
    /// <param name="includeExpired">Include expired contracts</param>
    /// <param name="securityCurrency">Expected security currency(if any)</param>
    /// <returns>Enumerable of Symbols, that are associated with the provided Symbol</returns>
    public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns whether selection can take place or not.
    /// </summary>
    /// <remarks>This is useful to avoid a selection taking place during invalid times, for example IB reset times or when not connected,
    /// because if allowed selection would fail since IB isn't running and would kill the algorithm</remarks>
    /// <returns>True if selection can take place</returns>
    public bool CanPerformSelection()
    {
        throw new NotImplementedException();
    }

    #endregion

    private bool CanSubscribe(Symbol symbol)
    {
        if (symbol.Value.IndexOfInvariant("universe", true) != -1 || symbol.IsCanonical())
        {
            return false;
        }

        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
    private bool Subscribe(IEnumerable<Symbol> symbols)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Removes the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
    private bool Unsubscribe(IEnumerable<Symbol> symbols)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the history for the requested symbols
    /// <see cref="IBrokerage.GetHistory(Data.HistoryRequest)"/>
    /// </summary>
    /// <param name="request">The historical data request</param>
    /// <returns>An enumerable of bars covering the span specified in the request</returns>
    public override IEnumerable<BaseData> GetHistory(Data.HistoryRequest request)
    {
        if (!CanSubscribe(request.Symbol))
        {
            return null; // Should consistently return null instead of an empty enumerable
        }

        throw new NotImplementedException();
    }
}