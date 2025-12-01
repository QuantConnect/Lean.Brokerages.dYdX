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
using System.Threading;
using Newtonsoft.Json;
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Brokerages.dYdX.Models.WebSockets;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX;

[BrokerageFactory(typeof(dYdXBrokerageFactory))]
public partial class dYdXBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler, IDataQueueUniverseProvider
{
    private const string MarketName = Market.dYdX;
    private const SecurityType SecurityType = QuantConnect.SecurityType.CryptoFuture;

    private string _indexerWssUrl;

    private IAlgorithm _algorithm;
    private IDataAggregator _aggregator;
    private LiveNodePacket _job;
    private readonly EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;
    private RateGate _connectionRateLimiter;

    private Domain.Market _market;
    private SymbolPropertiesDatabaseSymbolMapper _symbolMapper;

    private static readonly SymbolPropertiesDatabase SymbolPropertiesDatabase =
        SymbolPropertiesDatabase.FromDataFolder();

    private BrokerageConcurrentMessageHandler<WebSocketMessage> _messageHandler;

    private Lazy<dYdXApiClient> _apiClientLazy;

    private ManualResetEvent _connectionConfirmedEvent = new(false);

    private Wallet Wallet { get; set; }

    /// <summary>
    /// API client
    /// </summary>
    private dYdXApiClient ApiClient => _apiClientLazy.Value;

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
    /// <param name="privateKey">The private key for the wallet</param>
    /// <param name="mnemonic">The mnemonic phrase (12, 15, 18, 21, or 24 words)</param>
    /// <param name="address">The address associated with the mnemonic</param>
    /// <param name="chainId">Chain ID for the wallet</param>
    /// <param name="subaccountNumber">The subaccount number to use for this wallet</param>
    /// <param name="nodeRestUrl">The REST URL of the node to connect to</param>
    /// <param name="nodeGrpcUrl">The gRPC URL of the node to connect to</param>
    /// <param name="indexerRestUrl">The REST URL of the indexer to connect to</param>
    /// <param name="indexerWssUrl">The WebSocket URL of the indexer to connect to</param>
    /// <param name="algorithm">The algorithm instance</param>
    /// <param name="aggregator">The aggregator instance</param>
    /// <param name="job">The live node packet</param>
    public dYdXBrokerage(string privateKey, string mnemonic, string address, string chainId, uint subaccountNumber,
        string nodeRestUrl,
        string nodeGrpcUrl,
        string indexerRestUrl,
        string indexerWssUrl,
        IAlgorithm algorithm,
        IDataAggregator aggregator,
        LiveNodePacket job) :
        base(MarketName)
    {
        Initialize(
            privateKey,
            mnemonic,
            address,
            chainId,
            subaccountNumber,
            nodeRestUrl,
            nodeGrpcUrl,
            indexerRestUrl,
            indexerWssUrl,
            algorithm,
            aggregator,
            job);

        _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
        _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
        _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);
    }

    private void Initialize(
        string privateKeyHex,
        string mnemonic,
        string address,
        string chainId,
        uint subaccountNumber,
        string nodeRestUrl,
        string nodeGrpcUrl,
        string indexerRestUrl,
        string indexerWssUrl,
        IAlgorithm algorithm,
        IDataAggregator aggregator,
        LiveNodePacket job)
    {
        if (IsInitialized)
        {
            return;
        }

        base.Initialize(indexerWssUrl, new WebSocketClientWrapper(), null, null, null);

        _indexerWssUrl = indexerWssUrl;
        _job = job;
        _algorithm = algorithm;
        _aggregator = aggregator;

        _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(MarketName);

        _messageHandler = new BrokerageConcurrentMessageHandler<WebSocketMessage>(OnUserMessage);

        // Rate gate limiter useful for API/WS calls
        _connectionRateLimiter = new RateGate(2, TimeSpan.FromSeconds(1));

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

                var client = GetApiClient(nodeRestUrl, nodeGrpcUrl, indexerRestUrl);
                return client;
            });

            var wallet = BuildWallet(ApiClient, privateKeyHex, mnemonic, address, chainId, subaccountNumber);

            if (wallet == null)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, "Mnemonic and PrivateKey"));
                throw new Exception("Mnemonic and PrivateKey is missing");
            }

            Wallet = wallet;
            _market = new Domain.Market(wallet, _symbolMapper, SymbolPropertiesDatabase, ApiClient);

            Connect();
        }
    }

    private dYdXApiClient GetApiClient(string nodeRestUrl, string nodeGrpcUrl, string indexerUrl)
    {
        return new dYdXApiClient(nodeRestUrl, nodeGrpcUrl, indexerUrl);
    }

    private Wallet BuildWallet(dYdXApiClient apiClient,
        string privateKeyHex,
        string mnemonic,
        string address,
        string chainId,
        uint subaccountNumber)
    {
        if (!string.IsNullOrEmpty(privateKeyHex))
        {
            return Wallet.FromPrivateKey(apiClient, privateKeyHex, address, chainId, subaccountNumber);
        }

        if (!string.IsNullOrEmpty(mnemonic))
        {
            return Wallet.FromMnemonic(apiClient, mnemonic, address, chainId, subaccountNumber);
        }

        return null;
    }

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

    /// <summary>
    /// Checks if this brokerage supports the specified symbol
    /// </summary>
    /// <param name="symbol">The symbol</param>
    /// <returns>returns true if brokerage supports the specified symbol; otherwise false</returns>
    private bool CanSubscribe(Symbol symbol)
    {
        if (symbol.Value.IndexOfInvariant("universe", true) != -1 || symbol.IsCanonical())
        {
            return false;
        }

        return symbol.SecurityType == SecurityType.CryptoFuture &&
               symbol.ID.Market == MarketName &&
               _symbolMapper.IsKnownLeanSymbol(symbol);
    }

    /// <summary>
    /// Adds the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
    protected override bool Subscribe(IEnumerable<Symbol> symbols)
    {
        // throw new NotImplementedException();
        return true;
    }

    private bool Subscribe(string channel, string id = null, bool batched = false)
    {
        _connectionRateLimiter.WaitToProceed();
        WebSocket.Send(JsonConvert.SerializeObject(new SubscribeRequestSchema
            {
                Channel = channel,
                Id = id,
                Batched = batched
            }
        ));
        return true;
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

    public override void Dispose()
    {
        if (_apiClientLazy?.IsValueCreated == true)
        {
            ApiClient.DisposeSafely();
        }

        _connectionConfirmedEvent?.DisposeSafely();
        _connectionRateLimiter?.DisposeSafely();
        SubscriptionManager?.DisposeSafely();
        base.Dispose();
    }
}