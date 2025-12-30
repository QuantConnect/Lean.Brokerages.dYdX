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
using System.Linq;
using QuantConnect.Brokerages.dYdX.Models;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.dYdX.Extensions;

public static class SubaccountExtensions
{
    /// <summary>
    /// Provides extension methods for the <see cref="Subaccount"/> class to enhance its functionality.
    /// </summary>
    extension(Subaccount source)
    {
        /// <summary>
        /// Retrieves a list of cash amounts representing the balances for each currency in the subaccount,
        /// including adjustments for margin account types based on open perpetual positions.
        /// </summary>
        /// <param name="symbolPropertiesDatabase">
        /// The database containing symbol properties such as quote currencies for various instruments.
        /// </param>
        /// <param name="accountType">
        /// The account type determining the inclusion of adjustments specific to margin accounts.
        /// </param>
        /// <returns>
        /// A list of <see cref="CashAmount"/> objects representing the balances for each currency in the subaccount.
        /// </returns>
        public List<CashAmount> GetCashAmounts(
            SymbolPropertiesDatabase symbolPropertiesDatabase,
            AccountType accountType)
        {
            var balances = new Dictionary<string, CashAmount>();

            foreach (var (asset, assetPosition) in source.AssetPositions)
            {
                balances.Add(asset, new CashAmount(assetPosition.Size, asset));
            }

            if (accountType == AccountType.Margin)
            {
                foreach (var (instrument, positions) in source.OpenPerpetualPositions)
                {
                    var symbol = Symbol.Create(instrument, SecurityType.CryptoFuture, Market.dYdX);
                    var symbolProperties = symbolPropertiesDatabase.GetSymbolProperties(
                        symbol.ID.Market,
                        symbol,
                        symbol.SecurityType,
                        Currencies.USD);

                    var quoteQuantity = positions.Size * positions.EntryPrice;
                    balances[symbolProperties.QuoteCurrency] =
                        balances.TryGetValue(symbolProperties.QuoteCurrency, out var quoteCurrencyAmount)
                            ? new CashAmount(quoteQuantity + quoteCurrencyAmount.Amount, symbolProperties.QuoteCurrency)
                            : new CashAmount(quoteQuantity, symbolProperties.QuoteCurrency);
                }
            }

            return balances.Values.ToList();
        }
    }
}