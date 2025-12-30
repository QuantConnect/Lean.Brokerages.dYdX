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

using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using QuantConnect.Brokerages.dYdX.Extensions;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.dYdX.Tests.Extensions;

[TestFixture]
public class SubaccountExtensionsTests
{
    private SymbolPropertiesDatabase _symbolPropertiesDatabase;

    [SetUp]
    public void SetUp()
    {
        _symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
    }

    [Test]
    public void GetCashAmounts_WithNoOpenPerpPositionsAssetOnly_ReturnsOnlyAssetBalance()
    {
        // Arrange
        var subaccount = new Subaccount
        {
            AssetPositions = new Dictionary<string, AssetPosition>
            {
                { "USDC", new AssetPosition { Size = 100m, Symbol = "USDC" } }
            },
            OpenPerpetualPositions = new Dictionary<string, PerpetualPosition>()
        };

        // Act
        var cashAmounts = subaccount.GetCashAmounts(_symbolPropertiesDatabase, AccountType.Margin);

        // Assert
        Assert.AreEqual(1, cashAmounts.Count);
        Assert.AreEqual(100m, cashAmounts[0].Amount);
        Assert.AreEqual("USDC", cashAmounts[0].Currency);
    }

    [Test]
    public void GetCashAmounts_WithOneOpenPerpPosition_ReturnsAssetAndPerpPositionValue()
    {
        // Arrange
        var subaccount = new Subaccount
        {
            AssetPositions = new Dictionary<string, AssetPosition>
            {
                { "USDC", new AssetPosition { Size = 100m, Symbol = "USDC" } }
            },
            OpenPerpetualPositions = new Dictionary<string, PerpetualPosition>
            {
                {
                    "ETH-USD",
                    new PerpetualPosition
                    {
                        Symbol = "ETH-USD",
                        Size = 2m,
                        EntryPrice = 2000m
                    }
                }
            }
        };

        // Act
        var cashAmounts = subaccount.GetCashAmounts(_symbolPropertiesDatabase, AccountType.Margin);

        // Assert
        Assert.AreEqual(2, cashAmounts.Count);

        var usdcAmount = cashAmounts.Find(c => c.Currency == "USDC");
        Assert.IsNotNull(usdcAmount);
        Assert.AreEqual(100m, usdcAmount.Amount);

        var usdAmount = cashAmounts.Find(c => c.Currency == "USD");
        Assert.IsNotNull(usdAmount);
        Assert.AreEqual(4000m, usdAmount.Amount);
    }

    [Test]
    public void GetCashAmounts_WithTwoOpenPerpPositionsSameQuoteCurrency_ReturnsCombinedValue()
    {
        // Arrange
        var subaccount = new Subaccount
        {
            AssetPositions = new Dictionary<string, AssetPosition>
            {
                { "USDC", new AssetPosition { Size = 100m, Symbol = "USDC" } }
            },
            OpenPerpetualPositions = new Dictionary<string, PerpetualPosition>
            {
                {
                    "ETH-USD",
                    new PerpetualPosition
                    {
                        Symbol = "ETH-USD",
                        Size = 2m,
                        EntryPrice = 2000m
                    }
                },
                {
                    "BTC-USD",
                    new PerpetualPosition
                    {
                        Symbol = "BTC-USD",
                        Size = 0.5m,
                        EntryPrice = 40000m
                    }
                }
            }
        };

        // Act
        var cashAmounts = subaccount.GetCashAmounts(_symbolPropertiesDatabase, AccountType.Margin);

        // Assert
        Assert.AreEqual(2, cashAmounts.Count);
        var usdcAmount = cashAmounts.Find(c => c.Currency == "USDC");
        Assert.IsNotNull(usdcAmount);
        // 100 (asset)
        Assert.AreEqual(100m, usdcAmount.Amount);

        var usdAmount = cashAmounts.Find(c => c.Currency == "USD");
        Assert.IsNotNull(usdAmount);
        // 2 * 2000 (ETH) + 0.5 * 40000 (BTC) = 4000 + 20000 = 24000
        Assert.AreEqual(24000m, usdAmount.Amount);
    }

    [Test]
    public void GetCashAmounts_WithCashAccountType_IgnoresPerpPositions()
    {
        // Arrange
        var subaccount = new Subaccount
        {
            AssetPositions = new Dictionary<string, AssetPosition>
            {
                { "USDC", new AssetPosition { Size = 100m, Symbol = "USDC" } }
            },
            OpenPerpetualPositions = new Dictionary<string, PerpetualPosition>
            {
                {
                    "ETH-USD",
                    new PerpetualPosition
                    {
                        Symbol = "ETH-USD",
                        Size = 2m,
                        EntryPrice = 2000m
                    }
                }
            }
        };

        // Act
        var cashAmounts = subaccount.GetCashAmounts(_symbolPropertiesDatabase, AccountType.Cash);

        // Assert
        Assert.AreEqual(1, cashAmounts.Count);
        Assert.AreEqual(100m, cashAmounts[0].Amount);
        Assert.AreEqual("USDC", cashAmounts[0].Currency);
    }
}