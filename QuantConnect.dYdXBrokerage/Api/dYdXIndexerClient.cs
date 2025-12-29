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
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Brokerages.dYdX.Models;

namespace QuantConnect.Brokerages.dYdX.Api;

public class dYdXIndexerClient(string baseUrl)
{
    private readonly dYdXRestClient _restClient = new(baseUrl, new(100, TimeSpan.FromSeconds(10)));

    /// <summary>
    /// Calls indexer to get perpetual positions, see https://docs.dydx.xyz/indexer-client/http#list-positions
    /// </summary>
    /// <param name="wallet">Wallet to retrieve positions for</param>
    /// <param name="status">Filter to retrieve positions with a specific status. If not provided, all positions will be returned regardless of status. Defaults to "OPEN".</param>
    /// <returns></returns>
    public PerpetualPositionsResponse GetPerpetualPositions(Wallet wallet, string status = "OPEN")
    {
        var path =
            $"perpetualPositions?address={Uri.EscapeDataString(wallet.Address)}&subaccountNumber={
                wallet.SubaccountNumber}";
        if (!string.IsNullOrEmpty(status))
        {
            path += $"&status={Uri.EscapeDataString(status)}";
        }

        return _restClient.Get<PerpetualPositionsResponse>(path);
    }

    public ExchangeInfo GetExchangeInfo()
    {
        return _restClient.Get<ExchangeInfo>("perpetualMarkets");
    }

    public IEnumerable<OrderDto> GetOpenOrders(Wallet wallet, string status = "OPEN")
    {
        var path = $"orders?address={wallet.Address}&subaccountNumber={wallet.SubaccountNumber}";
        if (!string.IsNullOrEmpty(status))
        {
            path += $"&status={Uri.EscapeDataString(status)}";
        }

        return _restClient.Get<IEnumerable<OrderDto>>(path);
    }

    /// <summary>
    /// Retrieves the cash balance for a given wallet from the dYdX indexer API. Ref https://docs.dydx.xyz/indexer-client/http#get-asset-positions
    /// </summary>
    /// <param name="wallet">Wallet for which to retrieve the cash balance</param>
    /// <returns>Asset positions for the specified wallet</returns>
    public Subaccount GetSubaccount(Wallet wallet)
    {
        return _restClient.Get<SubaccountResponse>(
                $"addresses/{wallet.Address}/subaccountNumber/{wallet.SubaccountNumber}")
            .Subaccount;
    }
}