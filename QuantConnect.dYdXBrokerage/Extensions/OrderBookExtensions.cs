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

namespace QuantConnect.Brokerages.dYdX.Extensions;

public static class OrderBookExtensions
{
    /// <summary>
    /// Provides extension methods for handling and processing order book data in a decentralized
    /// trading network environment.
    /// </summary>
    extension(DefaultOrderBook orderBook)
    {
        /// <summary>
        /// Crossed prices where best bid > best ask may happen.
        /// This happens because the dydx network is decentralized, operated by 42 validators where the order book updates can be sent by any of the validators and therefore may arrive out of sequence to the full node/indexer
        /// see ref https://docs.dydx.xyz/interaction/data/watch-orderbook#uncrossing-the-orderbook
        /// </summary>
        public void UncrossOrderBook()
        {
            // Get sorted lists: bids descending (highest first), asks ascending (lowest first)
            var bidPrice = orderBook.BestBidPrice;
            var askPrice = orderBook.BestAskPrice;

            while (bidPrice != 0 && askPrice != 0 && bidPrice > askPrice)
            {
                var bidSize = orderBook.BestBidSize;
                var askSize = orderBook.BestAskSize;

                if (bidSize > askSize)
                {
                    orderBook.UpdateBidRow(bidPrice, bidSize - askSize);
                    orderBook.RemoveAskRow(askPrice);
                }
                else if (bidSize < askSize)
                {
                    orderBook.UpdateAskRow(askPrice, askSize - bidSize);
                    orderBook.RemoveBidRow(bidPrice);
                }
                else
                {
                    orderBook.RemoveAskRow(askPrice);
                    orderBook.RemoveBidRow(bidPrice);
                }

                bidPrice = orderBook.BestBidPrice;
                askPrice = orderBook.BestAskPrice;
            }
        }
    }
}