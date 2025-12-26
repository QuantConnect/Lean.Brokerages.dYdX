namespace QuantConnect.Brokerages.dYdX.Extensions;

public static class OrderBookExtensions
{
    /// <summary>
    /// Crossed prices where best bid > best ask may happen.
    /// This happens because the dydx network is decentralized, operated by 42 validators where the order book updates can be sent by any of the validators and therefore may arrive out of sequence to the full node/indexer
    /// see ref https://docs.dydx.xyz/interaction/data/watch-orderbook#uncrossing-the-orderbook
    /// </summary>
    /// <param name="orderBook">Order book to uncross</param>
    public static void UncrossOrderBook(this DefaultOrderBook orderBook)
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