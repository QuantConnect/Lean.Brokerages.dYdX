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
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX
{
    /// <summary>
    /// Provides a template implementation of BrokerageFactory
    /// </summary>
    public class dYdXBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// Gets the brokerage data required to run the brokerage from configuration/disk
        /// </summary>
        /// <remarks>
        /// The implementation of this property will create the brokerage data dictionary required for
        /// running live jobs. See <see cref="IJobQueueHandler.NextJob"/>
        /// </remarks>
        public override Dictionary<string, string> BrokerageData => new()
        {
            { "dydx-private-key-hex", Config.Get("dydx-private-key-hex") },
            { "dydx-address", Config.Get("dydx-address") },
            { "dydx-subaccount-number", Config.Get("dydx-subaccount-number") },

            // mainnet
            // use KingNodes by default for the reason of better testings, and no rest endpoint for OEGS
            { "dydx-node-api-rest", Config.Get("dydx-node-api-rest", "https://dydx-ops-rest.kingnodes.com:443") },
            { "dydx-node-api-grpc", Config.Get("dydx-node-api-grpc", "https://dydx-ops-grpc.kingnodes.com:443") },
            { "dydx-indexer-api-rest", Config.Get("dydx-indexer-api-rest", "https://indexer.dydx.trade/v4") },
            { "dydx-indexer-api-wss", Config.Get("dydx-indexer-api-wss", "wss://indexer.dydx.trade/v4/ws")},
            { "dydx-chain-id", Config.Get("dydx-chain-id", "dydx-mainnet-1") }

            // testnet
            // { "dydx-node-api-rest", Config.Get("dydx-node-api-rest", "https://test-dydx-rest.kingnodes.com") },
            // { "dydx-node-api-grpc", Config.Get("dydx-node-api-grpc", "https://test-dydx-rest.kingnodes.com:443") },
            // { "dydx-indexer-api-rest", Config.Get("dydx-indexer-api-rest", "https://indexer.v4testnet.dydx.exchange/v4") },
            // {
            //     "dydx-indexer-api-wss",
            //     Config.Get("dydx-indexer-api-wss", "wss://indexer.v4testnet.dydx.exchange/v4/ws")
            // },
            // { "dydx-chain-id", Config.Get("dydx-chain-id", "dydx-testnet-4") }
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="dYdXBrokerageFactory"/> class
        /// </summary>
        public dYdXBrokerageFactory() : base(typeof(dYdXBrokerage))
        {
        }

        /// <summary>
        /// Gets a brokerage model that can be used to model this brokerage's unique behaviors
        /// </summary>
        /// <param name="orderProvider">The order provider</param>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new dYdXBrokerageModel();

        /// <summary>
        /// Creates a new IBrokerage instance
        /// </summary>
        /// <param name="job">The job packet to create the brokerage for</param>
        /// <param name="algorithm">The algorithm instance</param>
        /// <returns>A new brokerage instance</returns>
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var errors = new List<string>();
            var privateKey = Read<string>(job.BrokerageData, "dydx-private-key-hex", errors);
            var address = Read<string>(job.BrokerageData, "dydx-address", errors);
            var subaccountNumber = Read<uint>(job.BrokerageData, "dydx-subaccount-number", errors);
            var nodeRestUrl = Read<string>(job.BrokerageData, "dydx-node-api-rest", errors);
            var nodeGrpcUrl = Read<string>(job.BrokerageData, "dydx-node-api-grpc", errors);
            var indexerRestUrl = Read<string>(job.BrokerageData, "dydx-indexer-api-rest", errors);
            var indexerWssUrl = Read<string>(job.BrokerageData, "dydx-indexer-api-wss", errors);
            var chainId = Read<string>(job.BrokerageData, "dydx-chain-id", errors);

            if (errors.Count != 0)
            {
                // if we had errors then we can't create the instance
                throw new ArgumentException($"{nameof(dYdXBrokerageFactory)} has not found of config key(s):" + string.Join(Environment.NewLine, errors));
            }

            var brokerage =
                new dYdXBrokerage(
                    privateKey,
                    address,
                    chainId,
                    subaccountNumber,
                    nodeRestUrl,
                    nodeGrpcUrl,
                    indexerRestUrl,
                    indexerWssUrl,
                    algorithm,
                    algorithm?.Transactions,
                    job);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);

            return brokerage;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
            // not used
        }
    }
}