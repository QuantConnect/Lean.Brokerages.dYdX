using System;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Brokerages.Template.Api;

namespace QuantConnect.Brokerages.dYdX;

public class dYdXNodeClient(string baseUrl)
{
    private readonly Lazy<dYdXRestClient> _lazyRestClient = new(() => new dYdXRestClient(baseUrl.TrimEnd('/')));
    private dYdXRestClient _restClient => _lazyRestClient.Value;

    public dYdXAccountBalances GetCashBalance(string address)
    {
        return _restClient.Get<dYdXAccountBalances>($"/cosmos/bank/v1beta1/balances/{address}");
    }
}