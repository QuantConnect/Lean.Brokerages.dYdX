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
using System.Net.Http;
using Cosmos.Crypto.Secp256K1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Brokerages.dYdX.Domain.Enums;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.dYdXBrokerage.Cosmos.Base.Tendermint.V1Beta1;
using QuantConnect.dYdXBrokerage.Cosmos.Tx;
using QuantConnect.dYdXBrokerage.Cosmos.Tx.Signing;
using QuantConnect.dYdXBrokerage.dYdXProtocol.AccountPlus;
using QuantConnect.dYdXBrokerage.dYdXProtocol.Clob;
using QuantConnect.Util;
using Order = QuantConnect.dYdXBrokerage.dYdXProtocol.Clob.Order;
using TendermintService = QuantConnect.dYdXBrokerage.Cosmos.Base.Tendermint.V1Beta1.Service;
using TxService = QuantConnect.dYdXBrokerage.Cosmos.Tx.Service;

namespace QuantConnect.Brokerages.dYdX.Api;

public class dYdXNodeClient : IDisposable
{
    private const int ErrorCodeUnauthorized = 104;

    private readonly dYdXRestClient _restClient;
    private readonly GrpcChannel _grpcChannel;
    private readonly TxService.ServiceClient _txService;
    private readonly TendermintService.ServiceClient _tendermintService;
    private readonly Query.QueryClient _accountPlusService;
    private readonly RateGate _rateGate;

    public dYdXNodeClient(string restUrl, string grpcUrl)
    {
        _rateGate = new RateGate(250, TimeSpan.FromMinutes(1));
        _restClient = new dYdXRestClient(restUrl.TrimEnd('/'), _rateGate);

        var grpcChannelOptions = new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            }
        };

        var uri = new Uri(grpcUrl.TrimEnd('/'));
        _grpcChannel = GrpcChannel.ForAddress(uri, grpcChannelOptions);
        _txService = new TxService.ServiceClient(_grpcChannel);
        _tendermintService = new TendermintService.ServiceClient(_grpcChannel);
        _accountPlusService = new Query.QueryClient(_grpcChannel);
    }

    public uint GetLatestBlockHeight()
    {
        _rateGate.WaitToProceed();
        return checked((uint)_tendermintService.GetLatestBlock(new GetLatestBlockRequest()).Block.Header.Height);
    }

    public dYdXAccount GetAccount(string address)
    {
        var accountResponse = _restClient.Get<dYdXAccountResponse>($"cosmos/auth/v1beta1/accounts/{address}");
        return accountResponse.Account;
    }

    public IEnumerable<ulong> GetAuthenticators(string address)
    {
        var response = _accountPlusService.GetAuthenticators(new GetAuthenticatorsRequest { Account = address });
        if (response.AccountAuthenticators.IsNullOrEmpty())
        {
            throw new InvalidOperationException("No authenticators found for the provided address");
        }

        return response.AccountAuthenticators.Select(authenticator => authenticator.Id);
    }

    public dYdXPlaceOrderResponse PlaceOrder(Wallet wallet, Order order, ulong gasLimit)
    {
        var response = ExecuteTransaction(wallet, gasLimit, (txBody) => AddPlaceOrderMessage(txBody, order));
        return new dYdXPlaceOrderResponse
        {
            Code = response.TxResponse.Code,
            OrderId = order.OrderId.ClientId,
            TxHash = response.TxResponse.Txhash,
            Message = response.TxResponse.RawLog
        };
    }

    public dYdXCancelOrderResponse CancelOrder(Wallet wallet, Order order, ulong gasLimit)
    {
        var response = ExecuteTransaction(wallet, gasLimit, (txBody) => AdddCancelOrderMessage(txBody, order));
        return new dYdXCancelOrderResponse
        {
            Code = response.TxResponse.Code,
            OrderId = order.OrderId.ClientId,
            TxHash = response.TxResponse.Txhash,
            Message = response.TxResponse.RawLog
        };
    }

    private BroadcastTxResponse ExecuteTransaction(Wallet wallet, ulong gasLimit, Action<TxBody> bodyBuilder)
    {
        while (true)
        {
            // Explicitly use the Authenticator-based authentication method.
            // This ensures we rely on the authenticator ID rather than falling back
            // to wallet private keys or mnemonic phrases for transaction signing.
            if (!wallet.TryGetAuthenticatorId(out var authenticatorId))
            {
                throw new InvalidOperationException("No authenticators found for the provided address");
            }

            var txBody = CreateTxBody(authenticatorId);

            bodyBuilder(txBody);
            var response = BroadcastTransaction(wallet, txBody, gasLimit);
            if (response.TxResponse.Code != ErrorCodeUnauthorized)
            {
                wallet.AuthenticatorId = authenticatorId;

                return response;
            }

            // invalidate the authenticator ID as we received unauthorized error code and retry the transaction
            wallet.AuthenticatorId = null;
        }
    }

    private BroadcastTxResponse BroadcastTransaction(Wallet wallet, TxBody txBody, ulong gasLimit)
    {
        var authInfo = BuildAuthInfo(wallet, gasLimit);

        var txRaw = new TxRaw
        {
            BodyBytes = txBody.ToByteString(),
            AuthInfoBytes = authInfo.ToByteString()
        };

        var signdoc = new SignDoc
        {
            BodyBytes = txBody.ToByteString(),
            AuthInfoBytes = authInfo.ToByteString(),
            AccountNumber = wallet.AccountNumber,
            ChainId = wallet.ChainId
        };

        byte[] signatureBytes = wallet.Sign(signdoc.ToByteArray());
        txRaw.Signatures.Add(ByteString.CopyFrom(signatureBytes));

        _rateGate.WaitToProceed();
        return _txService.BroadcastTx(new BroadcastTxRequest
        {
            TxBytes = txRaw.ToByteString(),
            Mode = BroadcastMode.Sync
        });
    }

    private static TxBody CreateTxBody(ulong authenticatorId)
    {
        var txBody = new TxBody();

        var txExtensions = new TxExtension();
        txExtensions.SelectedAuthenticators.Add(authenticatorId);

        txBody.NonCriticalExtensionOptions.Add(
            new Any { TypeUrl = "/dydxprotocol.accountplus.TxExtension", Value = txExtensions.ToByteString() });

        return txBody;
    }

    private void AddPlaceOrderMessage(TxBody txBody, Order orderProto)
    {
        var msgPlaceOrder = new MsgPlaceOrder { Order = orderProto };
        var msg = new Any { TypeUrl = "/dydxprotocol.clob.MsgPlaceOrder", Value = msgPlaceOrder.ToByteString() };
        txBody.Messages.Add(msg);
    }

    private void AdddCancelOrderMessage(TxBody txBody, Order order)
    {
        var orderId = order.OrderId;
        var msgCancelOrder = new MsgCancelOrder
        {
            OrderId = orderId
        };

        if (orderId.OrderFlags == (uint)OrderFlags.ShortTerm)
        {
            // TODO: we want to cancel ASAP, so add a standard buffer.
            // use the block height of the order to cancel it ASAP
            msgCancelOrder.GoodTilBlock = order.GoodTilBlock;
        }
        else
        {
            // TODO: we want to cancel ASAP, so add a small buffer
            // use the block time of the order to cancel it ASAP
            msgCancelOrder.GoodTilBlockTime = order.GoodTilBlockTime;
        }

        var msg = new Any { TypeUrl = "/dydxprotocol.clob.MsgCancelOrder", Value = msgCancelOrder.ToByteString() };
        txBody.Messages.Add(msg);
    }

    private AuthInfo BuildAuthInfo(Wallet wallet, ulong gasLimit)
    {
        // This constructs the "signer info" which tells the chain
        // "I am using this Public Key to sign, and this is my Sequence number"
        var pubKey = new PubKey
        {
            // Assuming _wallet.PublicKey is the raw compressed 33-byte public key
            Key = ByteString.FromBase64(wallet.PublicKey)
        };

        var signerInfo = new SignerInfo
        {
            PublicKey = new Any
            {
                TypeUrl = wallet.PublicKeyType,
                Value = pubKey.ToByteString()
            },
            ModeInfo = new ModeInfo
            {
                Single = new ModeInfo.Types.Single { Mode = SignMode.Direct }
            },
            Sequence = wallet.Sequence
        };

        var authInfo = new AuthInfo
        {
            SignerInfos = { signerInfo },
            Fee = new Fee
            {
                GasLimit = gasLimit, // Set appropriate gas limit
                // If fees are required, add Coin objects to Amount
                // Amount = { new Coin { Denom = "adydx", Amount = "0" } }
            }
        };

        return authInfo;
    }

    public void Dispose()
    {
        _grpcChannel.DisposeSafely();
    }
}