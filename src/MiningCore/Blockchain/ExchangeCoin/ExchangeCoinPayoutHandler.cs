/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.Bitcoin.Configuration;
using MiningCore.Blockchain.ExchangeCoin.Configuration;
using MiningCore.Configuration;
using MiningCore.DaemonInterface;
using MiningCore.Extensions;
using MiningCore.Messaging;
using MiningCore.Payments;
using MiningCore.Persistence;
using MiningCore.Persistence.Model;
using MiningCore.Persistence.Repositories;
using MiningCore.Time;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = MiningCore.Persistence.Model.Block;
using Contract = MiningCore.Contracts.Contract;
using Transaction = MiningCore.Blockchain.ExchangeCoin.DaemonInterface.Transaction;

namespace MiningCore.Blockchain.ExchangeCoin
{
    [CoinMetadata(CoinType.EXCC)]
    public class ExchangeCoinPayoutHandler : PayoutHandlerBase,
        IPayoutHandler
    {
        public ExchangeCoinPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf,
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));

            this.ctx = ctx;
        }

        protected readonly IComponentContext ctx;
        protected DaemonClient daemon;
        protected BitcoinCoinProperties coinProperties;
        protected ExchangeCoinPoolConfigExtra extraPoolConfig;
        protected BitcoinPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

        protected override string LogCategory => "Exchange Coin Payout Handler";

        #region IPayoutHandler

        public virtual Task ConfigureAsync(ClusterConfig clusterConfig, PoolConfig poolConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;

            extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<ExchangeCoinPoolConfigExtra>();
            extraPoolPaymentProcessingConfig = poolConfig.PaymentProcessing.Extra.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();
            coinProperties = BitcoinProperties.GetCoinProperties(poolConfig.Coin.Type, poolConfig.Coin.Algorithm);

            logger = LogUtil.GetPoolScopedLogger(typeof(ExchangeCoinPayoutHandler), poolConfig);

            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
            daemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            daemon.Configure(extraPoolConfig.Wallets);
            
            return Task.FromResult(true);
        }

        public virtual async Task<Block[]> ClassifyBlocksAsync(Block[] blocks)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(blocks, nameof(blocks));

            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var result = new List<Block>();

            for (var i = 0; i < pageCount; i++)
            {
                // get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
                var batch = page.Select(block => new[] { block.TransactionConfirmationData}).ToArray();

                for(var j = 0; j < batch.Length; j++)
                {  
                    var cmdResult = await daemon.ExecuteCmdSingleAsync<JToken>(BitcoinCommands.GetTransaction, batch[j]);

                    var transactionInfo = cmdResult.Response?.ToObject<Transaction>();
                    var block = page[j];

                    // check error
                    if (cmdResult.Error != null)
                    {
                        // Code -5 interpreted as "orphaned"
                        if (cmdResult.Error.Code == -5)
                        {
                            block.Status = BlockStatus.Orphaned;
                            result.Add(block);
                        }

                        else
                        {
                            logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
                        }
                    }

                    // check transaction block hash in case of duplicated block with the same coinbase
                    else if (!transactionInfo.BlockHash.Equals(block.Hash))
                    {
                        block.Status = BlockStatus.Orphaned;
                        result.Add(block);
                    }

                    // missing transaction details are interpreted as "orphaned"
                    else if (transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                    {
                        block.Status = BlockStatus.Orphaned;
                        result.Add(block);
                    }

                    else
                    {
                        switch(transactionInfo.Details[0].Category)
                        {
                            case "immature":
                                // update progress
                                var minConfirmations = extraPoolConfig?.MinimumConfirmations ?? BitcoinConstants.CoinbaseMinConfimations;
                                block.ConfirmationProgress = Math.Min(1.0d, (double) transactionInfo.Confirmations / minConfirmations);
                                block.Reward = transactionInfo.Details[0].Amount;
                                result.Add(block);
                                break;

                            case "generate":
                                // matured and spendable coinbase transaction
                                block.Status = BlockStatus.Confirmed;
                                block.ConfirmationProgress = 1;
                                block.Reward = transactionInfo.Details[0].Amount;
                                result.Add(block);

                                logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");
                                break;

                            default:
                                logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned. Category: {transactionInfo.Details[0].Category}");

                                block.Status = BlockStatus.Orphaned;
                                block.Reward = 0;
                                result.Add(block);
                                break;
                        }
                    }
                }
            }

            return result.ToArray();
        }

        public virtual Task CalculateBlockEffortAsync(Block block, double accumulatedBlockShareDiff)
        {
            block.Effort = accumulatedBlockShareDiff / block.NetworkDifficulty;

            return Task.FromResult(true);
        }

        public virtual Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool)
        {
            var blockRewardRemaining = block.Reward;

            // Distribute funds to configured reward recipients
            foreach(var recipient in poolConfig.RewardRecipients.Where(x => x.Percentage > 0))
            {
                var amount = block.Reward * (recipient.Percentage / 100.0m);
                var address = recipient.Address;

                blockRewardRemaining -= amount;

                // skip transfers from pool wallet to pool wallet
                if (address != poolConfig.Address)
                {
                    logger.Info(() => $"Adding {FormatAmount(amount)} to balance of {address}");
                    balanceRepo.AddAmount(con, tx, poolConfig.Id, poolConfig.Coin.Type, address, amount, $"Reward for block {block.BlockHeight}");
                }
            }

            return Task.FromResult(blockRewardRemaining);
        }

        public virtual async Task PayoutAsync(Balance[] balances)
        {
            Contract.RequiresNonNull(balances, nameof(balances));

            // build args
            var amounts = balances
                .Where(x => x.Amount > 0)
                .ToDictionary(x => x.Address, x => Math.Round(x.Amount, 8));

            if (amounts.Count == 0)
                return;

            logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

            object[] args;

            if (extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                var comment = (poolConfig.PoolName ?? clusterConfig.ClusterName ?? "MiningCore").Trim() + " Payment";
                var subtractFeesFrom = amounts.Keys.ToArray();

                args = new object[]
                {
                    "default", // default account
                    amounts, // addresses and associated amounts
                    1, // only spend funds covered by this many confirmations
                    comment, // tx comment
                    subtractFeesFrom // distribute transaction fee equally over all recipients
                };
            }

            else
            {
                args = new object[]
                {
                    "default", // default account
                    amounts, // addresses and associated amounts
                };
            }

            var didUnlockWallet = false;

            // send command
            tryTransfer:
            var result = await daemon.ExecuteCmdSingleAsync<string>(BitcoinCommands.SendMany, args, new JsonSerializerSettings());

            if (result.Error == null)
            {
                if (didUnlockWallet)
                {
                    // lock wallet
                    logger.Info(() => $"[{LogCategory}] Locking wallet");
                    await daemon.ExecuteCmdSingleAsync<JToken>(BitcoinCommands.WalletLock);
                }

                // check result
                var txId = result.Response;

                if (string.IsNullOrEmpty(txId))
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} did not return a transaction id!");
                else
                    logger.Info(() => $"[{LogCategory}] Payout transaction id: {txId}");

                PersistPayments(balances, txId);

                NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txId }, null);
            }

            else
            {
                if (result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if (!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResult = await daemon.ExecuteCmdSingleAsync<JToken>(BitcoinCommands.WalletPassphrase, new[]
                        {
                            (object) extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if (unlockResult.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        else
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {result.Error.Message} code {result.Error.Code}");
                    }

                    else
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
                }
            }
        }

        #endregion // IPayoutHandler
    }
}
