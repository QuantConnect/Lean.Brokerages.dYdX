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

using System.Reflection;
using Grpc.Core;
using NUnit.Framework;
using QuantConnect.Brokerages.dYdX.Api;

namespace QuantConnect.Brokerages.dYdX.Tests
{
    [TestFixture]
    public class dYdXNodeClientTests
    {
        private static readonly MethodInfo _isSequenceError = typeof(dYdXNodeClient)
            .GetMethod("IsSequenceError", BindingFlags.NonPublic | BindingFlags.Static);

        private static bool IsSequenceError(uint code, string rawLog)
            => (bool)_isSequenceError.Invoke(null, new object[] { code, rawLog });

        private static readonly MethodInfo _isTransient = typeof(dYdXNodeClient)
            .GetMethod("IsTransient", BindingFlags.NonPublic | BindingFlags.Static);

        private static bool IsTransient(StatusCode status)
            => (bool)_isTransient.Invoke(null, new object[] { status });

        // Issue #33: a nonce mismatch must be recognized so the transaction is refreshed and retried rather
        // than failing the order. dYdX surfaces it as the dedicated wrong-sequence code (32) or, when signing
        // through an authenticator, as a signature-verification failure whose log references the sequence.
        [TestCase(32u, null, true, TestName = "WrongSequenceCode")]
        [TestCase(32u, "account sequence mismatch, expected 5, got 4", true, TestName = "WrongSequenceCodeWithLog")]
        [TestCase(2u, "account sequence mismatch, expected 5, got 4: incorrect account sequence", true, TestName = "SequenceMismatchInLog")]
        [TestCase(4u, "authentication failed for message 0, authenticator id 2025, type SignatureVerification: signature verification failed; please verify account number (0), sequence (414) and chain-id", true, TestName = "AuthenticatorSignatureFailureReferencingSequence")]
        [TestCase(5u, "spendable balance is smaller than required: insufficient funds", false, TestName = "InsufficientFundsIsNotRetried")]
        [TestCase(11u, "out of gas", false, TestName = "OutOfGasIsNotRetried")]
        [TestCase(2u, null, false, TestName = "GenericFailureWithNoLogIsNotRetried")]
        public void DetectsSequenceErrors(uint code, string rawLog, bool expected)
        {
            Assert.AreEqual(expected, IsSequenceError(code, rawLog));
        }

        // Transient node/transport faults are safe to re-broadcast (the tx was not processed). Ambiguous
        // statuses (e.g. DeadlineExceeded, where the tx may already be committed) must NOT be treated as
        // transient, to avoid a double-submit.
        [TestCase(StatusCode.Unavailable, true, TestName = "UnavailableIsTransient")]
        [TestCase(StatusCode.ResourceExhausted, true, TestName = "ResourceExhaustedIsTransient")]
        [TestCase(StatusCode.DeadlineExceeded, false, TestName = "DeadlineExceededIsNotTransient")]
        [TestCase(StatusCode.Internal, false, TestName = "InternalIsNotTransient")]
        [TestCase(StatusCode.InvalidArgument, false, TestName = "InvalidArgumentIsNotTransient")]
        [TestCase(StatusCode.OK, false, TestName = "OkIsNotTransient")]
        public void DetectsTransientFaults(StatusCode status, bool expected)
        {
            Assert.AreEqual(expected, IsTransient(status));
        }
    }
}
