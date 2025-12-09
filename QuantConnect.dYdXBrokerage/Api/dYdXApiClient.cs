using System;
using System.Collections.Generic;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX.Api;

public class dYdXApiClient(string nodeEndpointRest, string nodeEndpointGrpc, string indexerEndpointRest) : IDisposable
{
    public dYdXIndexerClient Indexer { get; } = new(indexerEndpointRest);
    public dYdXNodeClient Node { get; } = new(nodeEndpointRest, nodeEndpointGrpc);

    public void Dispose()
    {
        Node?.DisposeSafely();
    }
}