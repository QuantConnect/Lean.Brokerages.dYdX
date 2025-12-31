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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Api;
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Brokerages.dYdX.Models.WebSockets;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;
using HistoryRequest = QuantConnect.Data.HistoryRequest;
using dYdXOpenInterest = QuantConnect.Brokerages.dYdX.Models.OpenInterest;

namespace QuantConnect.Brokerages.dYdX;

[BrokerageFactory(typeof(dYdXBrokerageFactory))]
public partial class dYdXBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
{
    private class ModulesReadLicenseRead : QuantConnect.Api.RestResponse
    {
        [JsonProperty(PropertyName = "license")]
        public string License;

        [JsonProperty(PropertyName = "organizationId")]
        public string OrganizationId;
    }

    private const string MarketName = Market.dYdX;
    private const SecurityType SecurityType = QuantConnect.SecurityType.CryptoFuture;
    private const int ProductId = 421;

    // TODO: Confirm whether the indexer WebSocket rate limits are global (shared across all channels)
    // or applied per category/channel.
    // Ref: https://docs.dydx.xyz/concepts/trading/limits/rate-limits#indexer-rate-limits
    private const int MaxSymbolsPerConnection = 16;

    private IAlgorithm _algorithm;
    private IOrderProvider _orderProvider;
    private IDataAggregator _aggregator;
    private LiveNodePacket _job;
    private RateGate _connectionRateLimiter;
    private readonly ConcurrentDictionary<uint, Tuple<ManualResetEventSlim, Order>> _pendingOrders = new();
    private readonly ConcurrentDictionary<string, uint> _orderBrokerIdToClientIdMap = new();
    private static readonly TimeSpan WaitPlaceOrderEventTimeout = TimeSpan.FromSeconds(15);
    private Domain.Market _market;
    private SymbolPropertiesDatabaseSymbolMapper _symbolMapper;
    private dYdXApiClient _apiClient;
    private HttpClient _historyHttpClient;

    private bool _unsupportedAssetHistoryLogged;
    private bool _unsupportedResolutionHistoryLogged;
    private bool _unsupportedTickTypeHistoryLogged;
    private bool _invalidTimeRangeHistoryLogged;

    private readonly Dictionary<Resolution, string> _knownResolutions = new()
    {
        { Resolution.Minute, "1MIN" },
        { Resolution.Hour, "1HOUR" },
        { Resolution.Daily, "1DAY" }
    };

    private static readonly SymbolPropertiesDatabase SymbolPropertiesDatabase =
        SymbolPropertiesDatabase.FromDataFolder();

    private BrokerageConcurrentMessageHandler<WebSocketMessage> _messageHandler;

    private readonly ManualResetEvent _connectionConfirmedEvent = new(false);

    private Wallet Wallet { get; set; }

    public override string AccountBaseCurrency { get; protected set; } = "USDC";

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
    /// <param name="address">The address associated with the mnemonic</param>
    /// <param name="chainId">Chain ID for the wallet</param>
    /// <param name="subaccountNumber">The subaccount number to use for this wallet</param>
    /// <param name="nodeRestUrl">The REST URL of the node to connect to</param>
    /// <param name="nodeGrpcUrl">The gRPC URL of the node to connect to</param>
    /// <param name="indexerRestUrl">The REST URL of the indexer to connect to</param>
    /// <param name="indexerWssUrl">The WebSocket URL of the indexer to connect to</param>
    /// <param name="algorithm">The algorithm instance</param>
    /// <param name="orderProvider">The order provider instance</param>
    /// <param name="aggregator">The aggregator instance</param>
    /// <param name="job">The live node packet</param>
    public dYdXBrokerage(string privateKey, string address, string chainId, uint subaccountNumber,
        string nodeRestUrl,
        string nodeGrpcUrl,
        string indexerRestUrl,
        string indexerWssUrl,
        IAlgorithm algorithm,
        IOrderProvider orderProvider,
        LiveNodePacket job) :
        base(MarketName)
    {
        Initialize(
            privateKey,
            address,
            chainId,
            subaccountNumber,
            nodeRestUrl,
            nodeGrpcUrl,
            indexerRestUrl,
            indexerWssUrl,
            algorithm,
            orderProvider,
            job);
    }

    private void Initialize(
        string privateKeyHex,
        string address,
        string chainId,
        uint subaccountNumber,
        string nodeRestUrl,
        string nodeGrpcUrl,
        string indexerRestUrl,
        string indexerWssUrl,
        IAlgorithm algorithm,
        IOrderProvider orderProvider,
        LiveNodePacket job)
    {
        if (IsInitialized)
        {
            return;
        }

        ValidateSubscription();

        base.Initialize(indexerWssUrl, new WebSocketClientWrapper(), httpClient: null, null, null);

        _job = job;
        _algorithm = algorithm;
        _orderProvider = orderProvider;

        _aggregator = Composer.Instance.GetPart<IDataAggregator>();
        if (_aggregator == null)
        {
            var aggregatorName = Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
            Log.Trace(
                $"{nameof(dYdXBrokerage)}.{nameof(Initialize)}: found no data aggregator instance, creating {aggregatorName}");
            _aggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(aggregatorName);
        }

        _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(MarketName);

        _messageHandler = new BrokerageConcurrentMessageHandler<WebSocketMessage>(OnUserMessage);

        // Rate gate limiter useful for API/WS calls
        // TODO: it's global now, but for perf reasons can be per connection
        // Ref: https://docs.dydx.xyz/interaction/data/feeds#rate-limiting
        _connectionRateLimiter = new RateGate(2, TimeSpan.FromSeconds(1));

        var maximumWebSocketConnections = Config.GetInt("dydx-maximum-websocket-connections");
        int maxSymbolsPerWebsocketConnection =
            Config.GetInt("dydx-maximum-symbols-per-connection", MaxSymbolsPerConnection);
        var symbolWeights = maximumWebSocketConnections > 0
            ? FetchSymbolWeights(_symbolMapper, indexerRestUrl)
            : null;

        _historyHttpClient = new HttpClient
        {
            BaseAddress = new Uri(indexerRestUrl.TrimEnd('/') + "/")
        };

        var subscriptionManager = new BrokerageMultiWebSocketSubscriptionManager(
            indexerWssUrl,
            maxSymbolsPerWebsocketConnection,
            maximumWebSocketConnections,
            symbolWeights,
            () => new WebSocketClientWrapper(),
            Subscribe,
            Unsubscribe,
            OnDataMessage,
            TimeSpan.Zero,
            _connectionRateLimiter);

        SubscriptionManager = subscriptionManager;

        // can be null if dYdXBrokerage is used as DataQueueHandler only
        if (_algorithm != null)
        {
            if (string.IsNullOrEmpty(address))
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, "Address is missing"));
                throw new Exception("Address is missing");
            }

            _apiClient = GetApiClient(nodeRestUrl, nodeGrpcUrl, indexerRestUrl);
            try
            {
                Wallet = BuildWallet(_apiClient, privateKeyHex, address, chainId, subaccountNumber);
            }
            catch (Exception e)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, e.Message));
                throw;
            }

            _market = new Domain.Market(Wallet, _symbolMapper, SymbolPropertiesDatabase, _apiClient);
            Connect();
        }
    }

    private dYdXApiClient GetApiClient(string nodeRestUrl, string nodeGrpcUrl, string indexerUrl)
    {
        return new dYdXApiClient(nodeRestUrl, nodeGrpcUrl, indexerUrl);
    }

    private Wallet BuildWallet(dYdXApiClient apiClient,
        string privateKeyHex,
        string address,
        string chainId,
        uint subaccountNumber)
    {
        if (!string.IsNullOrEmpty(privateKeyHex))
        {
            return Wallet.FromPrivateKey(apiClient, privateKeyHex, address, chainId, subaccountNumber);
        }

        return null;
    }

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
        // Not actually used as we use BrokerageMultiWebSocketSubscriptionManager
        return true;
    }

    /// <summary>
    /// Subscribes to the requested symbol (using an individual streaming channel)
    /// </summary>
    /// <param name="webSocket">The websocket instance</param>
    /// <param name="symbol">The symbol to subscribe</param>
    private bool Subscribe(IWebSocket webSocket, Symbol symbol)
    {
        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
        SubscribeToWebSocketChannel(
            webSocket,
            "v4_orderbook",
            brokerageSymbol,
            batched: false);

        SubscribeToWebSocketChannel(
            webSocket,
            "v4_trades",
            brokerageSymbol,
            batched: false);

        return true;
    }

    /// <summary>
    /// Subscribes the main WebSocket to a specified channel with optional parameters for ID and batching.
    /// </summary>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <param name="id">An optional identifier for the subscription.</param>
    /// <param name="batched">A boolean value indicating whether to batch the subscription.</param>
    private void Subscribe(string channel, string id = null, bool batched = false)
    {
        SubscribeToWebSocketChannel(WebSocket, channel, id, batched);
    }

    private void SubscribeToWebSocketChannel(IWebSocket ws, string channel, string id = null, bool batched = false)
    {
        _connectionRateLimiter.WaitToProceed();
        ws.Send(JsonConvert.SerializeObject(new SubscribeRequestSchema
            {
                Channel = channel,
                Id = id,
                Batched = batched
            }
        ));
    }

    /// <summary>
    /// Removes the specified symbols to the subscription
    /// </summary>
    /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
    private bool Unsubscribe(IEnumerable<Symbol> symbols)
    {
        // Not used as we use BrokerageMultiWebSocketSubscriptionManager
        throw new NotImplementedException();
    }

    /// <summary>
    /// Removes the specified symbols from the subscription
    /// </summary>
    /// <param name="webSocket">The websocket instance</param>
    /// <param name="symbol">The symbol to unsubscribe</param>
    private bool Unsubscribe(IWebSocket webSocket, Symbol symbol)
    {
        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
        UnsubscribeFromWebSocketChannel(
            webSocket,
            "v4_orderbook",
            brokerageSymbol);

        UnsubscribeFromWebSocketChannel(
            webSocket,
            "v4_trades",
            brokerageSymbol);

        return true;
    }

    private void UnsubscribeFromWebSocketChannel(IWebSocket ws, string channel, string id = null)
    {
        ws.Send(JsonConvert.SerializeObject(new UnsubscribeRequestSchema
            {
                Channel = channel,
                Id = id
            }
        ));
    }

    /// <summary>
    /// Gets the history for the requested symbols
    /// <see cref="IBrokerage.GetHistory(HistoryRequest)"/>
    /// </summary>
    /// <param name="request">The historical data request</param>
    /// <returns>An enumerable of bars covering the span specified in the request</returns>
    public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
    {
        if (!CanSubscribe(request.Symbol))
        {
            if (!_unsupportedAssetHistoryLogged)
            {
                _unsupportedAssetHistoryLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidSymbol",
                    $"{request.Symbol} is not supported, no history returned"));
            }

            return null;
        }

        if (!_knownResolutions.TryGetValue(request.Resolution, out var brokerageResolution))
        {
            if (!_unsupportedResolutionHistoryLogged)
            {
                _unsupportedResolutionHistoryLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"{request.Resolution} resolution is not supported, no history returned"));
            }

            return null;
        }

        if (request.TickType is TickType.Quote)
        {
            if (!_unsupportedTickTypeHistoryLogged)
            {
                _unsupportedTickTypeHistoryLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"{request.TickType} tick type not supported, no history returned"));
            }

            return null;
        }

        if (request.StartTimeUtc > request.EndTimeUtc)
        {
            if (!_invalidTimeRangeHistoryLogged)
            {
                _invalidTimeRangeHistoryLogged = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidDateRange",
                    "The history request start date must precede the end date, no history returned"));
            }

            return null;
        }

        var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
        var url = BuildCandlesUrl(brokerageSymbol, brokerageResolution, request.StartTimeUtc, request.EndTimeUtc);
        var historyData = _historyHttpClient.DownloadData(url);

        if (request.TickType is TickType.OpenInterest)
        {
            return GetOpenInterest(request, historyData);
        }

        return GetKlines(request, historyData);
    }

    /// <summary>
    /// Builds the candles URL with query parameters for historical data requests
    /// </summary>
    private string BuildCandlesUrl(string brokerageSymbol, string resolution, DateTime startTimeUtc,
        DateTime endTimeUtc)
    {
        var fromIso = startTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toIso = endTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return
            $"candles/perpetualMarkets/{brokerageSymbol}?resolution={resolution}&fromIso={fromIso}&toIso={toIso}";
    }

    private IEnumerable<Data.Market.OpenInterest> GetOpenInterest(HistoryRequest request, string historyData)
    {
        var response = JsonConvert.DeserializeObject<PerpertualMarketHistory<dYdXOpenInterest>>(historyData);
        foreach (var oi in response.Candles)
        {
            yield return new Data.Market.OpenInterest(
                Time.ParseDate(oi.StartedAt),
                request.Symbol,
                oi.StartingOpenInterest);
        }
    }

    private IEnumerable<TradeBar> GetKlines(HistoryRequest request, string historyData)
    {
        var period = request.Resolution.ToTimeSpan();
        var response = JsonConvert.DeserializeObject<PerpertualMarketHistory<Candle>>(historyData);
        foreach (var kline in response.Candles)
        {
            yield return new TradeBar
            {
                Time = Time.ParseDate(kline.StartedAt),
                Symbol = request.Symbol,
                Low = kline.Low,
                High = kline.High,
                Open = kline.Open,
                Close = kline.Close,
                Volume = kline.BaseTokenVolume,
                Value = kline.Close,
                DataType = MarketDataType.TradeBar,
                Period = period
            };
        }
    }

    public override void Dispose()
    {
        _apiClient?.DisposeSafely();
        _historyHttpClient?.DisposeSafely();
        _connectionConfirmedEvent?.DisposeSafely();
        _connectionRateLimiter?.DisposeSafely();
        SubscriptionManager?.DisposeSafely();
        base.Dispose();
    }

    /// <summary>
    /// Validate the user of this project has permission to be using it via our web API.
    /// </summary>
    private static void ValidateSubscription()
    {
        try
        {
            const int productId = ProductId;
            var userId = Globals.UserId;
            var token = Globals.UserToken;
            var organizationId = Globals.OrganizationID;
            // Verify we can authenticate with this user and token
            var api = new ApiConnection(userId, token);
            if (!api.Connected)
            {
                throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
            }

            // Compile the information we want to send when validating
            var information = new Dictionary<string, object>()
            {
                { "productId", productId },
                { "machineName", Environment.MachineName },
                { "userName", Environment.UserName },
                { "domainName", Environment.UserDomainName },
                { "os", Environment.OSVersion }
            };
            // IP and Mac Address Information
            try
            {
                var interfaceDictionary = new List<Dictionary<string, object>>();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                {
                    var interfaceInformation = new Dictionary<string, object>();
                    // Get UnicastAddresses
                    var addresses = nic.GetIPProperties().UnicastAddresses
                        .Select(uniAddress => uniAddress.Address)
                        .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                    // If this interface has non-loopback addresses, we will include it
                    if (!addresses.IsNullOrEmpty())
                    {
                        interfaceInformation.Add("unicastAddresses", addresses);
                        // Get MAC address
                        interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                        // Add Interface name
                        interfaceInformation.Add("name", nic.Name);
                        // Add these to our dictionary
                        interfaceDictionary.Add(interfaceInformation);
                    }
                }

                information.Add("networkInterfaces", interfaceDictionary);
            }
            catch (Exception)
            {
                // NOP, not necessary to crash if fails to extract and add this information
            }

            // Include our OrganizationId is specified
            if (!string.IsNullOrEmpty(organizationId))
            {
                information.Add("organizationId", organizationId);
            }

            // Create HTTP request
            using var request = ApiUtils.CreateJsonPostRequest("modules/license/read", information);

            api.TryRequest(request, out ModulesReadLicenseRead result);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
            }

            var encryptedData = result.License;
            // Decrypt the data we received
            DateTime? expirationDate = null;
            long? stamp = null;
            bool? isValid = null;
            if (encryptedData != null)
            {
                // Fetch the org id from the response if we are null, we need it to generate our validation key
                if (string.IsNullOrEmpty(organizationId))
                {
                    organizationId = result.OrganizationId;
                }

                // Create our combination key
                var password = $"{token}-{organizationId}";
                var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                // Split the data
                var info = encryptedData.Split("::");
                var buffer = Convert.FromBase64String(info[0]);
                var iv = Convert.FromBase64String(info[1]);
                // Decrypt our information
                using var aes = Aes.Create();
                var decryptor = aes.CreateDecryptor(key, iv);
                using var memoryStream = new MemoryStream(buffer);
                using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                using var streamReader = new StreamReader(cryptoStream);
                var decryptedData = streamReader.ReadToEnd();
                if (!decryptedData.IsNullOrEmpty())
                {
                    var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                    expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                    isValid = jsonInfo["isValid"]?.Value<bool>();
                    stamp = jsonInfo["stamped"]?.Value<int>();
                }
            }

            // Validate our conditions
            if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
            {
                throw new InvalidOperationException("Failed to validate subscription.");
            }

            var nowUtc = DateTime.UtcNow;
            var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
            if (timeSpan > TimeSpan.FromHours(12))
            {
                throw new InvalidOperationException("Invalid API response.");
            }

            if (!isValid.Value)
            {
                throw new ArgumentException(
                    $"Your subscription is not valid, please check your product subscriptions on our website.");
            }

            if (expirationDate < nowUtc)
            {
                throw new ArgumentException(
                    $"Your subscription expired {expirationDate}, please renew in order to use this product.");
            }
        }
        catch (Exception e)
        {
            Log.Error(
                $"{nameof(dYdXBrokerage)}.{nameof(ValidateSubscription)}: Failed during validation, shutting down. Error : {e.Message}");
            Environment.Exit(1);
        }
    }


    private static Dictionary<Symbol, int> FetchSymbolWeights(
        SymbolPropertiesDatabaseSymbolMapper symbolMapper,
        string indexerRestUrl)
    {
        var weights = new Dictionary<Symbol, int>();
        var data = QuantConnect.Extensions.DownloadData($"{indexerRestUrl}/perpetualMarkets");
        var markets = JsonConvert.DeserializeObject<ExchangeInfo>(data);
        var totalMarketVolume24H = markets.Symbols.Values
            .Select(x => x.Volume24H)
            .Sum();
        foreach (var brokerageSymbol in markets.Symbols.Values)
        {
            Symbol leanSymbol;
            try
            {
                leanSymbol = symbolMapper.GetLeanSymbol(brokerageSymbol.Ticker, SecurityType.CryptoFuture, MarketName);
            }
            catch (Exception)
            {
                //The api returns some currently unsupported symbols we can ignore these right now
                continue;
            }

            // normalize volume24H by total market volume24H
            // so the total weight of all symbols is less or equal int.MaxValue
            var normalizedVolume24H = brokerageSymbol.Volume24H / totalMarketVolume24H;
            var weight = (int)(int.MaxValue * normalizedVolume24H);

            weights.Add(leanSymbol, weight);
        }

        return weights;
    }
}