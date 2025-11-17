using System;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Brokerages.Template.Api;

namespace QuantConnect.Brokerages.dYdX;

public class dYdXIndexerClient(string baseUrl)
{
    private readonly Lazy<dYdXRestClient> _lazyRestClient = new(() => new dYdXRestClient(baseUrl.TrimEnd('/')));
    private dYdXRestClient _restClient => _lazyRestClient.Value;

    /// <summary>
    /// Calls indexer to get perpetual positions, see https://docs.dydx.xyz/indexer-client/http#list-positions
    /// </summary>
    /// <param name="address">The wallet address that owns the account.</param>
    /// <param name="subaccountNumber">The identifier for the specific subaccount within the wallet address.</param>
    /// <param name="status">Filter to retrieve positions with a specific status. If not provided, all positions will be returned regardless of status. Defaults to "OPEN".</param>
    /// <returns></returns>
    public dYdXPerpetualPositionsResponse GetPerpetualPositions(
        string address,
        int subaccountNumber,
        string status = "OPEN")
    {
        var path =
            $"/v4/perpetualPositions?address={Uri.EscapeDataString(address)}&subaccountNumber={subaccountNumber}&status={Uri.EscapeDataString(status)}";
        return _restClient.Get<dYdXPerpetualPositionsResponse>(path);
    }
}