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
using System.Net.WebSockets;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    private static uint RandomUInt32()
    {
        // we do not expect collisions here, so 31 bit is enough
        return (uint)Random.Shared.Next();
    }

    private static WebSocketClientWrapper CreateWebSocket()
    {
        // the indexer rejects connections without permessage-deflate compression with an HTTP 426
        // Ref: https://status.dydx.trade/incidents/jt0c1mfw8jlk
        return new WebSocketClientWrapper { DeflateOptions = new WebSocketDeflateOptions() };
    }

}