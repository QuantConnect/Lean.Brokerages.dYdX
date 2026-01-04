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
using System.Linq;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using QuantConnect.Brokerages.dYdX.Api;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX.Domain;

/// <summary>
/// Represents a cryptocurrency wallet for dYdX operations
/// </summary>
public class Wallet
{
    private Queue<ulong> _authenticators;

    /// <summary>
    /// The hexadecimal private key string of the authenticator (not the address itself)
    /// </summary>
    private string PrivateKey { get; }

    public string PublicKey { get; }
    public string PublicKeyType { get; }
    public string Address { get; }
    public ulong AccountNumber { get; }
    public uint SubaccountNumber { get; }
    public ulong Sequence { get; }
    public string ChainId { get; }
    public ulong? AuthenticatorId { get; set; }

    /// <summary>
    /// Initializes a new instance of the Wallet class
    /// </summary>
    /// <param name="privateKey">The private key for the wallet</param>
    /// <param name="publicKey">The public key for the wallet</param>
    /// <param name="publicKeyType">The public key type for the wallet</param>
    /// <param name="address">The address associated with the wallet</param>
    /// <param name="accountNumber">The account number for the wallet</param>
    /// <param name="subaccountNumber">The subaccount number for the wallet</param>
    /// <param name="sequence">The sequence number for the wallet</param>
    /// <param name="chainId">The chain ID for the wallet</param>
    /// <param name="authenticatorId">The authenticator ID for the wallet</param>
    /// <param name="authenticators">The authenticators for the wallet</param>
    private Wallet(string privateKey,
        string publicKey,
        string publicKeyType,
        string address,
        ulong accountNumber,
        uint subaccountNumber,
        ulong sequence,
        string chainId,
        ulong? authenticatorId = null,
        List<ulong> authenticators = null)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
        PublicKeyType = publicKeyType;
        Address = address;
        AccountNumber = accountNumber;
        SubaccountNumber = subaccountNumber;
        Sequence = sequence;
        ChainId = chainId;
        AuthenticatorId = authenticatorId;
        if (authenticators != null)
        {
            _authenticators = new Queue<ulong>(authenticators);
        }
    }

    /// <summary>
    /// Creates a wallet with Authenticator-base authentication
    /// </summary>
    /// <param name="apiClient">The dYdX API client</param>
    /// <param name="privateKeyHex">The hexadecimal private key string of the authenticator (not the address itself)</param>
    /// <param name="address">The address associated with the mnemonic</param>
    /// <param name="chainId">Chain ID for the wallet</param>
    /// <param name="subaccountNumber">The subaccount number to use for this wallet</param>
    /// <returns>A new Wallet instance initialized with the provided private key</returns>
    /// <exception cref="ArgumentException">Thrown when privateKey is null, empty, or whitespace</exception>
    public static Wallet FromAuthenticator(dYdXApiClient apiClient,
        string privateKeyHex,
        string address,
        string chainId,
        uint subaccountNumber
    )
        => Builder
            .Create(address, apiClient)
            .WithAuthenticator(privateKeyHex)
            .WithChainId(chainId)
            .WithSubaccount(subaccountNumber)
            .Build();

    public byte[] Sign(byte[] signDocBytes)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] messageHash32 = sha256.ComputeHash(signDocBytes);

        var privateKey32 = Convert.FromHexString(PrivateKey);

        // privateKey32: 32 bytes
        var curve = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var d = new BigInteger(1, privateKey32);
        var priv = new ECPrivateKeyParameters(d, domain);

        var signer = new ECDsaSigner();
        signer.Init(true, priv);
        var rs = signer.GenerateSignature(messageHash32); // r, s as BigInteger

        BigInteger r = rs[0];
        BigInteger s = rs[1];

        // enforce low-s (s = min(s, n - s))
        if (s.CompareTo(domain.N.ShiftRight(1)) > 0)
            s = domain.N.Subtract(s);

        byte[] rBytes = r.ToByteArrayUnsigned();
        byte[] sBytes = s.ToByteArrayUnsigned();

        byte[] rPadded = new byte[32];
        byte[] sPadded = new byte[32];
        Buffer.BlockCopy(rBytes, 0, rPadded, 32 - rBytes.Length, rBytes.Length);
        Buffer.BlockCopy(sBytes, 0, sPadded, 32 - sBytes.Length, sBytes.Length);

        return rPadded.Concat(sPadded).ToArray(); // 64 bytes r||s
    }

    public bool TryGetAuthenticatorId(out ulong authenticatorId)
    {
        if (AuthenticatorId.HasValue)
        {
            authenticatorId = AuthenticatorId.Value;
            return true;
        }

        if (!_authenticators.IsNullOrEmpty())
        {
            authenticatorId = _authenticators.Peek();
            return true;
        }

        authenticatorId = 0;
        return false;
    }

    public void DequeueAuthenticatorId()
    {
        if (_authenticators.Count > 0)
        {
            _authenticators.Dequeue();
        }
    }

    public class Builder
    {
        private const int MaxAuthenticatorsQueueSize = 1000;

        private readonly dYdXApiClient _apiClient;

        private string _privateKeyHex;
        private string _publicKeyHex;
        private string _publicKeyType;
        private string _mnemonic;
        private string _address;
        private string _chainId;
        private uint _subaccountNumber;
        private ulong? _authenticatorId;
        private string _authenticatorPrivateKey;

        private Builder(string address, dYdXApiClient apiClient)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address cannot be null or empty", nameof(address));
            }

            _address = address;
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        public static Builder Create(string address, dYdXApiClient apiClient)
            => new(address, apiClient);

        public Builder WithPrivateKey(string privateKeyHex)
        {
            if (string.IsNullOrWhiteSpace(privateKeyHex))
            {
                throw new ArgumentException("Private key cannot be null or empty", nameof(privateKeyHex));
            }

            _privateKeyHex = StripHexPrefix(privateKeyHex);

            // clear conflicting state
            _authenticatorId = null;
            _authenticatorPrivateKey = null;
            _mnemonic = null;
            return this;
        }

        public Builder WithMnemonic(string mnemonicPhrase)
        {
            throw new NotImplementedException("Mnemonic phrase not yet supported");
        }

        public Builder WithAuthenticator(ulong? id, string privateKeyHex)
        {
            if (string.IsNullOrWhiteSpace(privateKeyHex))
            {
                throw new ArgumentException("Private key cannot be null or empty", nameof(privateKeyHex));
            }

            _authenticatorId = id;
            _authenticatorPrivateKey = StripHexPrefix(privateKeyHex);

            // clear conflicting state
            _privateKeyHex = null;
            _mnemonic = null;
            return this;
        }

        public Builder WithAuthenticator(string privateKeyHex)
        {
            return WithAuthenticator(null, privateKeyHex);
        }

        private string StripHexPrefix(string privateKeyHex)
        {
            if (privateKeyHex.StartsWith("0x"))
            {
                return privateKeyHex.Substring(2);
            }

            return privateKeyHex;
        }

        public Builder WithSubaccount(uint subaccountNumber)
        {
            _subaccountNumber = subaccountNumber;
            return this;
        }

        public Builder WithChainId(string chainId)
        {
            _chainId = chainId;
            return this;
        }

        public Builder WithPublicKey(string publicKeyHex)
        {
            _publicKeyHex = publicKeyHex;
            return this;
        }

        public Builder WithPublicKeyType(string publicKeyType)
        {
            _publicKeyType = publicKeyType;
            return this;
        }

        public Wallet Build()
        {
            if (string.IsNullOrWhiteSpace(_address))
            {
                throw new InvalidOperationException("Address must be specified");
            }

            if (string.IsNullOrWhiteSpace(_chainId))
            {
                throw new InvalidOperationException("Network ChainId must be specified");
            }

            // derive private key if needed
            string privateKeyHex = _privateKeyHex;
            if (privateKeyHex == null && _mnemonic != null)
            {
                privateKeyHex = PrivateKeyHexFromMnemonic(_mnemonic);
            }

            List<ulong> authenticators = null;
            if (privateKeyHex == null && _authenticatorPrivateKey != null)
            {
                authenticators = _authenticatorId.HasValue
                    ? [_authenticatorId.Value]
                    : _apiClient.Node.GetAuthenticators(_address);
                if (authenticators.Count == 0 || authenticators.Count > MaxAuthenticatorsQueueSize)
                {
                    throw new InvalidOperationException("No authenticators found for address");
                }

                privateKeyHex = _authenticatorPrivateKey;
                if (authenticators.Count == 1)
                {
                    _authenticatorId = authenticators.First();
                }
            }

            if (string.IsNullOrWhiteSpace(privateKeyHex))
            {
                throw new InvalidOperationException("Private key or mnemonic must be provided");
            }

            var account = _apiClient.Node.GetAccount(_address);

            return new Wallet(
                privateKeyHex,
                _publicKeyHex ?? account.PublicKey.Key,
                _publicKeyType ?? account.PublicKey.Type,
                _address,
                account.AccountNumber,
                _subaccountNumber,
                account.Sequence,
                _chainId,
                _authenticatorId,
                authenticators
            );
        }

        // if you want to reuse the Wallet's method, make this internal in Wallet and call it
        private static string PrivateKeyHexFromMnemonic(string mnemonicPhrase)
        {
            // TODO: Implement BIP39 mnemonic to private key derivation
            throw new NotImplementedException();
        }
    }
}