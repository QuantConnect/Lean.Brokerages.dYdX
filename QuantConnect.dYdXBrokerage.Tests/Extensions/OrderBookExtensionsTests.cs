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

using NUnit.Framework;
using QuantConnect.Brokerages.dYdX.Extensions;

namespace QuantConnect.Brokerages.dYdX.Tests.Extensions;

[TestFixture]
public class OrderBookExtensionsTests
{
    [Test]
    public void UncrossOrderBook_WhenBidGreaterThanAsk_RemovesBothWhenSizesEqual()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateBidRow(101m, 10m);
        orderBook.UpdateAskRow(100m, 10m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(0, orderBook.BestBidPrice);
        Assert.AreEqual(0, orderBook.BestAskPrice);
    }

    [Test]
    public void UncrossOrderBook_WhenBidGreaterThanAsk_ReducesBidWhenBidSizeLarger()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateBidRow(101m, 15m);
        orderBook.UpdateAskRow(100m, 10m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(101m, orderBook.BestBidPrice);
        Assert.AreEqual(5m, orderBook.BestBidSize);
        Assert.AreEqual(0, orderBook.BestAskPrice);
    }

    [Test]
    public void UncrossOrderBook_WhenBidGreaterThanAsk_ReducesAskWhenAskSizeLarger()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateBidRow(101m, 10m);
        orderBook.UpdateAskRow(100m, 15m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(0, orderBook.BestBidPrice);
        Assert.AreEqual(100m, orderBook.BestAskPrice);
        Assert.AreEqual(5m, orderBook.BestAskSize);
    }

    [Test]
    public void UncrossOrderBook_WhenBidLessThanAsk_DoesNothing()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateBidRow(99m, 10m);
        orderBook.UpdateAskRow(100m, 10m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(99m, orderBook.BestBidPrice);
        Assert.AreEqual(10m, orderBook.BestBidSize);
        Assert.AreEqual(100m, orderBook.BestAskPrice);
        Assert.AreEqual(10m, orderBook.BestAskSize);
    }

    [Test]
    public void UncrossOrderBook_WhenMultipleCrossedLevels_UncrossesAll()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        // Add multiple crossed levels
        orderBook.UpdateBidRow(94m, 5m);
        orderBook.UpdateBidRow(102m, 10m);
        orderBook.UpdateBidRow(101m, 15m);
        orderBook.UpdateAskRow(100m, 8m);
        orderBook.UpdateAskRow(99m, 12m);
        orderBook.UpdateAskRow(98m, 20m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(94m, orderBook.BestBidPrice);
        Assert.AreEqual(5m, orderBook.BestBidSize);
        Assert.AreEqual(99m, orderBook.BestAskPrice);
        Assert.AreEqual(7m, orderBook.BestAskSize);
    }

    [Test]
    public void UncrossOrderBook_WhenSingleCrossedLevels_UncrossesAll()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        // Add multiple crossed levels
        orderBook.UpdateBidRow(94m, 5m);
        orderBook.UpdateBidRow(95m, 10m);
        orderBook.UpdateBidRow(101m, 15m);
        orderBook.UpdateAskRow(100m, 8m);
        orderBook.UpdateAskRow(99m, 12m);
        orderBook.UpdateAskRow(98m, 20m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(95m, orderBook.BestBidPrice);
        Assert.AreEqual(10m, orderBook.BestBidSize);
        Assert.AreEqual(98, orderBook.BestAskPrice);
        Assert.AreEqual(5, orderBook.BestAskSize);
    }

    [Test]
    public void UncrossOrderBook_WhenOrderBookEmpty_DoesNothing()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(0, orderBook.BestBidPrice);
        Assert.AreEqual(0, orderBook.BestAskPrice);
    }

    [Test]
    public void UncrossOrderBook_WhenOnlyBidsExist_DoesNothing()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateBidRow(100m, 10m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(100m, orderBook.BestBidPrice);
        Assert.AreEqual(10m, orderBook.BestBidSize);
        Assert.AreEqual(0, orderBook.BestAskPrice);
    }

    [Test]
    public void UncrossOrderBook_WhenOnlyAsksExist_DoesNothing()
    {
        // Arrange
        var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.DYDX);
        var orderBook = new DefaultOrderBook(symbol);

        orderBook.UpdateAskRow(100m, 10m);

        // Act
        orderBook.UncrossOrderBook();

        // Assert
        Assert.AreEqual(0, orderBook.BestBidPrice);
        Assert.AreEqual(100m, orderBook.BestAskPrice);
        Assert.AreEqual(10m, orderBook.BestAskSize);
    }
}