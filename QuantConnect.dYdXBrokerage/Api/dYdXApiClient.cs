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