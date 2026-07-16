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
using System.Reflection;
using Google.Protobuf;
using NUnit.Framework;
using QuantConnect.Brokerages.dYdX.Domain;
using QuantConnect.dYdXBrokerage.dYdXProtocol.AccountPlus;

namespace QuantConnect.Brokerages.dYdX.Tests.Domain
{
    [TestFixture]
    public class WalletTests
    {
        // canonical secp256k1 test vector: private key 1 -> compressed public key = compressed G
        private const string PrivateKeyOne = "0000000000000000000000000000000000000000000000000000000000000001";
        private const string CompressedG = "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798";

        private static readonly MethodInfo _compressedPublicKey = typeof(Wallet.Builder)
            .GetMethod("CompressedPublicKey", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo _referencesPublicKey = typeof(Wallet.Builder)
            .GetMethod("ReferencesPublicKey", BindingFlags.NonPublic | BindingFlags.Static);

        private static byte[] PublicKeyOf(string privateKeyHex)
            => (byte[])_compressedPublicKey.Invoke(null, [privateKeyHex]);

        private static bool References(AccountAuthenticator authenticator, byte[] publicKey)
            => (bool)_referencesPublicKey.Invoke(null,
                [authenticator, publicKey, Convert.ToBase64String(publicKey)]);

        [Test]
        public void DerivesCompressedPublicKeyFromPrivateKey()
        {
            Assert.AreEqual(CompressedG, Convert.ToHexString(PublicKeyOf(PrivateKeyOne)));
        }

        [Test]
        public void MatchesBareSignatureVerificationAuthenticatorByRawConfigBytes()
        {
            // a bare SignatureVerification authenticator stores the raw compressed public key as its config
            var publicKey = PublicKeyOf(PrivateKeyOne);
            var authenticator = new AccountAuthenticator
            {
                Id = 2106,
                Type = "SignatureVerification",
                Config = ByteString.CopyFrom(publicKey)
            };

            Assert.IsTrue(References(authenticator, publicKey));
        }

        [Test]
        public void MatchesCompositeAuthenticatorByBase64KeyInJsonConfig()
        {
            // composite authenticators (AllOf/AnyOf) store JSON whose nested SignatureVerification
            // configs are the base64 of the public key
            var publicKey = PublicKeyOf(PrivateKeyOne);
            var json = $"[{{\"type\":\"SignatureVerification\",\"config\":\"{Convert.ToBase64String(publicKey)}\"}}," +
                       "{\"type\":\"SubaccountFilter\",\"config\":\"MA==\"}]";
            var authenticator = new AccountAuthenticator
            {
                Id = 2106,
                Type = "AllOf",
                Config = ByteString.CopyFromUtf8(json)
            };

            Assert.IsTrue(References(authenticator, publicKey));
        }

        [Test]
        public void DoesNotMatchAuthenticatorRegisteredForAnotherKey()
        {
            // The live incident (issue #37): the account's first-listed authenticator belonged to a
            // different key, the connector selected it blindly and every broadcast failed on-chain
            // with an "authentication failed" error that was misread as a sequence error.
            var otherKey = PublicKeyOf("0000000000000000000000000000000000000000000000000000000000000002");
            var authenticator = new AccountAuthenticator
            {
                Id = 2025,
                Type = "SignatureVerification",
                Config = ByteString.CopyFrom(otherKey)
            };

            Assert.IsFalse(References(authenticator, PublicKeyOf(PrivateKeyOne)));
        }

        [Test]
        public void RotateAuthenticatorCyclesThroughAllCandidates()
        {
            // Observed live: the chain transiently rejected the GOOD authenticator (code 104), the wallet
            // moved to the stale one, and a drop-based rotation could never come back — every subsequent
            // order failed. Rotation must cycle so the pool is never exhausted.
            var wallet = CreateWallet(authenticators: new Queue<ulong>([2025ul, 2106ul]));

            Assert.IsTrue(wallet.TryGetAuthenticatorId(out var authenticatorId));
            Assert.AreEqual(2025ul, authenticatorId);
            wallet.AuthenticatorId = authenticatorId;

            // the cached authenticator failed authentication: rotate to the next candidate
            wallet.RotateAuthenticator();
            Assert.IsTrue(wallet.TryGetAuthenticatorId(out authenticatorId));
            Assert.AreEqual(2106ul, authenticatorId);
            wallet.AuthenticatorId = authenticatorId;

            // rotating again cycles back to the first candidate instead of stranding the wallet
            wallet.RotateAuthenticator();
            Assert.IsTrue(wallet.TryGetAuthenticatorId(out authenticatorId));
            Assert.AreEqual(2025ul, authenticatorId);
        }

        [Test]
        public void RotateAuthenticatorRetriesTheOnlyCandidateOnSingleAuthenticatorAccounts()
        {
            // a genuine stale-sequence retry (issue #33) must still have an authenticator to sign with
            var wallet = CreateWallet(authenticators: new Queue<ulong>([2106ul]));

            Assert.IsTrue(wallet.TryGetAuthenticatorId(out var authenticatorId));
            Assert.AreEqual(2106ul, authenticatorId);
            wallet.AuthenticatorId = authenticatorId;

            wallet.RotateAuthenticator();
            Assert.IsTrue(wallet.TryGetAuthenticatorId(out authenticatorId));
            Assert.AreEqual(2106ul, authenticatorId);
        }

        private static Wallet CreateWallet(Queue<ulong> authenticators)
        {
            var constructor = typeof(Wallet).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
            return (Wallet)constructor.Invoke(
            [
                PrivateKeyOne, CompressedG, "/cosmos.crypto.secp256k1.PubKey",
                "dydx1address", 1ul, 0u, 0ul, "dydx-mainnet-1", authenticators
            ]);
        }
    }
}
