﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using LedgerWallet;
using NBitcoin;

namespace BTCPayServer.Services
{
    public class LedgerHardwareWalletService : HardwareWalletService
    {
        class WebSocketTransport : LedgerWallet.Transports.ILedgerTransport, IDisposable
        {
            private readonly WebSocket webSocket;

            public WebSocketTransport(System.Net.WebSockets.WebSocket webSocket)
            {
                if (webSocket == null)
                    throw new ArgumentNullException(nameof(webSocket));
                this.webSocket = webSocket;
            }

            SemaphoreSlim _Semaphore = new SemaphoreSlim(1, 1);
            public async Task<byte[][]> Exchange(byte[][] apdus, CancellationToken cancellationToken)
            {
                await _Semaphore.WaitAsync();
                List<byte[]> responses = new List<byte[]>();
                try
                {
                    foreach (var apdu in apdus)
                    {
                        await this.webSocket.SendAsync(new ArraySegment<byte>(apdu), WebSocketMessageType.Binary, true, cancellationToken);
                    }
                    foreach (var apdu in apdus)
                    {
                        byte[] response = new byte[300];
                        var result = await this.webSocket.ReceiveAsync(new ArraySegment<byte>(response), cancellationToken);
                        Array.Resize(ref response, result.Count);
                        responses.Add(response);
                    }
                }
                finally
                {
                    _Semaphore.Release();
                }
                return responses.ToArray();
            }

            public void Dispose()
            {
                _Semaphore.Dispose();
            }
        }

        private readonly LedgerClient _Ledger;
        public LedgerClient Ledger
        {
            get
            {
                return _Ledger;
            }
        }

        public override string Device => "Ledger wallet";

        WebSocketTransport _Transport = null;
        public LedgerHardwareWalletService(System.Net.WebSockets.WebSocket ledgerWallet)
        {
            if (ledgerWallet == null)
                throw new ArgumentNullException(nameof(ledgerWallet));
            _Transport = new WebSocketTransport(ledgerWallet);
            _Ledger = new LedgerClient(_Transport);
            _Ledger.MaxAPDUSize = 90;
        }

        public override async Task<LedgerTestResult> Test(CancellationToken cancellation)
        {
            var version = await Ledger.GetFirmwareVersionAsync(cancellation);
            return new LedgerTestResult() { Success = true };
        }

        public override async Task<BitcoinExtPubKey> GetExtPubKey(BTCPayNetwork network, KeyPath keyPath, CancellationToken cancellation)
        {
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            return await GetExtPubKey(network, keyPath, false, cancellation);
        }
        public override async Task<PubKey> GetPubKey(BTCPayNetwork network, KeyPath keyPath, CancellationToken cancellation)
        {
            if (network == null)
                throw new ArgumentNullException(nameof(network));
            return (await GetExtPubKey(network, keyPath, false, cancellation)).GetPublicKey();
        }

        private async Task<BitcoinExtPubKey> GetExtPubKey(BTCPayNetwork network, KeyPath account, bool onlyChaincode, CancellationToken cancellation)
        {
            var pubKey = await Ledger.GetWalletPubKeyAsync(account, cancellation: cancellation);
            try
            {
                pubKey.GetAddress(network.NBitcoinNetwork);
            }
            catch
            {
                if (network.NBitcoinNetwork.NetworkType == NetworkType.Mainnet)
                    throw new HardwareWalletException($"The opened ledger app does not seems to support {network.NBitcoinNetwork.Name}.");
            }
            var parentFP = onlyChaincode || account.Indexes.Length == 0 ? default : (await Ledger.GetWalletPubKeyAsync(account.Parent, cancellation: cancellation)).UncompressedPublicKey.Compress().GetHDFingerPrint();
            var extpubkey = new ExtPubKey(pubKey.UncompressedPublicKey.Compress(),
                                            pubKey.ChainCode,
                                            (byte)account.Indexes.Length,
                                            parentFP,
                                            account.Indexes.Length == 0 ? 0 : account.Indexes.Last()).GetWif(network.NBitcoinNetwork);
            return extpubkey;
        }
        class HDKey
        {
            public PubKey PubKey { get; set; }
            public KeyPath KeyPath { get; set; }
        }
        public override async Task<PSBT> SignTransactionAsync(PSBT psbt, HDFingerprint? rootFingerprint, BitcoinExtPubKey accountKey, Script changeHint, CancellationToken cancellationToken)
        {
            HashSet<HDFingerprint> knownFingerprints = new HashSet<HDFingerprint>();
            knownFingerprints.Add(accountKey.GetPublicKey().GetHDFingerPrint());
            if (rootFingerprint is HDFingerprint fp)
                knownFingerprints.Add(fp);
            var unsigned = psbt.GetGlobalTransaction();
            var changeKeyPath = psbt.Outputs
                                            .Where(o => changeHint == null ? true : changeHint == o.ScriptPubKey)
                                            .Select(o => (Output: o, HDKey: GetHDKey(knownFingerprints, accountKey, o)))
                                            .Where(o => o.HDKey != null)
                                            .Select(o => o.HDKey.KeyPath)
                                            .FirstOrDefault();
            var signatureRequests = psbt
                .Inputs
                .Select(i => (Input: i, HDKey: GetHDKey(knownFingerprints, accountKey, i)))
                .Where(i => i.HDKey != null)
                .Select(i => new SignatureRequest()
                {
                    InputCoin = i.Input.GetSignableCoin(),
                    InputTransaction = i.Input.NonWitnessUtxo,
                    KeyPath = i.HDKey.KeyPath,
                    PubKey = i.HDKey.PubKey
                }).ToArray();
            var signedTransaction = await Ledger.SignTransactionAsync(signatureRequests, unsigned, changeKeyPath, cancellationToken);
            if (signedTransaction == null)
                throw new HardwareWalletException("The ledger failed to sign the transaction");

            psbt = psbt.Clone();
            foreach (var signature in signatureRequests)
            {
                if (signature.Signature == null)
                    continue;
                var input = psbt.Inputs.FindIndexedInput(signature.InputCoin.Outpoint);
                if (input == null)
                    continue;
                input.PartialSigs.Add(signature.PubKey, signature.Signature);
            }
            return psbt;
        }

        private HDKey GetHDKey(HashSet<HDFingerprint> knownFingerprints, BitcoinExtPubKey accountKey, PSBTCoin coin)
        {
            // Check if the accountKey match this coin by checking if the non hardened last part of the path
            // can derive the same pubkey
            foreach (var key in coin.HDKeyPaths)
            {
                if (!knownFingerprints.Contains(key.Value.Item1))
                    continue;
                var accountKeyPath = key.Value.Item2.GetAccountKeyPath();
                // We might have a fingerprint collision, let's check
                if (accountKey.ExtPubKey.Derive(accountKeyPath).GetPublicKey() == key.Key)
                    return new HDKey() { KeyPath = key.Value.Item2, PubKey = key.Key };
            }
            return null;
        }

        public override void Dispose()
        {
            if (_Transport != null)
                _Transport.Dispose();
        }
    }
}
