﻿/*
Copyright 2018 ExchangeCoin (excc.co)
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.ExchangeCoin.Configuration;
using MiningCore.Blockchain.ExchangeCoin.DaemonInterface;
using MiningCore.Configuration;
using MiningCore.Contracts;
using MiningCore.Crypto;
using MiningCore.DaemonInterface;
using MiningCore.Extensions;
using MiningCore.JsonRpc;
using MiningCore.Messaging;
using MiningCore.Notifications.Messages;
using MiningCore.Stratum;
using MiningCore.Time;
using MiningCore.Util;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Block = MiningCore.Blockchain.ExchangeCoin.DaemonInterface.Block;
using Transaction = MiningCore.Blockchain.ExchangeCoin.DaemonInterface.Transaction;

namespace MiningCore.Blockchain.ExchangeCoin
{
    public class ExchangeCoinJobManager: JobManagerBase<ExchangeCoinJob>
    {
        public ExchangeCoinJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) :
            base(ctx, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(messageBus, nameof(messageBus));
            Contract.RequiresNonNull(extraNonceProvider, nameof(extraNonceProvider));

            _clock = clock;
            _extraNonceProvider = extraNonceProvider;
        }

        private readonly IMasterClock _clock;
        private DaemonClient _daemon;
        private DaemonClient _wallet;
        private readonly IExtraNonceProvider _extraNonceProvider;
        private const int ExtranonceBytes = 12;
        private int _maxActiveJobs = 4;
        private ExchangeCoinPoolConfigExtra _extraPoolConfig;
        private ExchangeCoinPoolPaymentProcessingConfigExtra _extraPoolPaymentProcessingConfig;
        private readonly List<ExchangeCoinJob> _validJobs = new List<ExchangeCoinJob>();
        private DateTime? _lastJobRebroadcast;
        private IHashAlgorithm _headerHasher;
        private TimeSpan _jobRebroadcastTimeout;
        private BitcoinNetworkType _networkType;
        private IDestination _poolAddressDestination;

        protected virtual void SetupJobUpdates()
        {
	        if (poolConfig.EnableInternalStratum == false)
		        return;

            _jobRebroadcastTimeout = TimeSpan.FromSeconds(Math.Max(1, poolConfig.JobRebroadcastTimeout));
            var blockSubmission = blockSubmissionSubject.Synchronize();
            var pollTimerRestart = blockSubmissionSubject.Synchronize();

            var triggers = new List<IObservable<(bool Force, string Via, string Data)>>
            {
                blockSubmission.Select(x=> (false, "Block-submission", (string) null))
            };

            if (_extraPoolConfig?.BtStream == null)
            {
                if (poolConfig.BlockRefreshInterval > 0)
                {
                    // periodically update block-template
                    triggers.Add(Observable.Timer(TimeSpan.FromMilliseconds(poolConfig.BlockRefreshInterval))
                        .TakeUntil(pollTimerRestart)
                        .Select(_ => (false, "RPC polling", (string) null))
                        .Repeat());
                }

                else
                {
                    // get initial blocktemplate
                    triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
                        .Select(_ => (false, "Initial template", (string) null))
                        .TakeWhile(_ => !hasInitialBlockTemplate));
                }

                // periodically update transactions for current template
                triggers.Add(Observable.Timer(_jobRebroadcastTimeout)
                    .TakeUntil(pollTimerRestart)
                    .Select(_ => (true, "Job-Refresh", (string) null))
                    .Repeat());
            }

            else
            {
                var btStream = BtStreamSubscribe(_extraPoolConfig.BtStream);

                if (poolConfig.JobRebroadcastTimeout > 0)
                {
                    var interval = TimeSpan.FromSeconds(Math.Max(1, poolConfig.JobRebroadcastTimeout - 0.1d));

                    triggers.Add(btStream
                        .Select(json => (!_lastJobRebroadcast.HasValue || (_clock.Now - _lastJobRebroadcast >= interval), "BT-Stream", json))
                        .Publish()
                        .RefCount());
                }

                else
                {
                    triggers.Add(btStream
                        .Select(json => (false, "BT-Stream", json))
                        .Publish()
                        .RefCount());
                }

                // get initial blocktemplate
                triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
                    .Select(_ => (false, "Initial template", (string)null))
                    .TakeWhile(_ => !hasInitialBlockTemplate));
            }

            Jobs = Observable.Merge(triggers)
                .Select(x => Observable.FromAsync(() => UpdateJob(x.Force, x.Via, x.Data)))
                .Concat()
                .Where(x=> x.IsNew || x.Force)
                .Do(x =>
                {
                    if(x.IsNew)
                        hasInitialBlockTemplate = true;
                })
                .Select(x=> GetJobParamsForStratum(x.IsNew))
                .Publish()
                .RefCount();
        }

        protected virtual async Task<DaemonResponse<ExchangeCoinGetWork>> GetWorkAsync()
        {
            logger.LogInvoke(LogCat);

            var result = await _daemon.ExecuteCmdAnyAsync<ExchangeCoinGetWork>(BitcoinCommands.GetWork);

            return result;
        }

        protected virtual DaemonResponse<ExchangeCoinGetWork> GetWorkFromJson(string json)
        {
            logger.LogInvoke(LogCat);

            var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

            return new DaemonResponse<ExchangeCoinGetWork>
            {
                Response = result.ResultAs<ExchangeCoinGetWork>(),
            };
        }

        protected virtual async Task ShowDaemonSyncProgressAsync()
        {
            var infos = await _daemon.ExecuteCmdAllAsync<DaemonInfo>(BitcoinCommands.GetInfo);

            if (infos.Length > 0)
            {
                var blockCount = infos
                    .Max(x => x.Response?.Blocks);

                if (blockCount.HasValue)
                {
                    // get list of peers and their highest block height to compare to ours
                    var peerInfo = await _daemon.ExecuteCmdAnyAsync<PeerInfo[]>(BitcoinCommands.GetPeerInfo);
                    var peers = peerInfo.Response;

                    if (peers != null && peers.Length > 0)
                    {
                        var totalBlocks = peers.Max(x => x.StartingHeight);
                        var percent = totalBlocks > 0 ? (double)blockCount / totalBlocks * 100 : 0;
                        logger.Info(() => $"[{LogCat}] Daemons have downloaded {percent:0.00}% of blockchain from {peers.Length} peers");
                    }
                }
            }
        }

        private async Task UpdateNetworkStatsAsync()
        {
            logger.LogInvoke(LogCat);

            try
            {
                var results = await _daemon.ExecuteBatchAnyAsync(
                    new DaemonCmd(BitcoinCommands.GetConnectionCount)
                );

                if (results.Any(x => x.Error != null))
                {
                    var errors = results.Where(x => x.Error != null).ToArray();

                    if (errors.Any())
                        logger.Warn(() => $"[{LogCat}] Error(s) refreshing network stats: {string.Join(", ", errors.Select(y => y.Error.Message))}");
                }

                var connectionCountResponse = results[0].Response.ToObject<object>();

                BlockchainStats.ConnectedPeers = (int)(long)connectionCountResponse;
            }

            catch (Exception e)
            {
                logger.Error(e);
            }
        }

        protected virtual async Task<(bool Accepted, string CoinbaseTransaction)> SubmitBlockAsync(Share share,
            string blockHex)
        {
            int workPaddingLen = 40;
            string workPadding = new String('0', workPaddingLen * 2);
            var work = blockHex + workPadding;

            // execute command batch
            var results = await _daemon.ExecuteBatchAnyAsync(
                new DaemonCmd(BitcoinCommands.GetWork, new List<string>{work}),
                new DaemonCmd(BitcoinCommands.GetBlock, new[] { share.BlockHash })
            );

            // did submission succeed?
            var submitResult = results[0];
            var submitError = submitResult.Error?.Message ?? submitResult.Error?.Code.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(submitError))
            {
                logger.Warn(() => $"[{LogCat}] Block {share.BlockHeight} submission failed with: {submitError}");
                messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}failed to submit block {share.BlockHeight}: {submitError}"));
                return (false, null);
            }

            // was it accepted?
            var acceptResult = results[1];
            var block = acceptResult.Response?.ToObject<Block>();
            var accepted = acceptResult.Error == null && block?.Hash == share.BlockHash;

            if (!accepted)
            {
                logger.Warn(() => $"[{LogCat}] Block {share.BlockHeight} submission failed for pool {poolConfig.Id} because block was not found after submission");
                messageBus.SendMessage(new AdminNotification($"[{share.PoolId.ToUpper()}]-[{share.Source}] Block submission failed", $"[{share.PoolId.ToUpper()}]-[{share.Source}] Block {share.BlockHeight} submission failed for pool {poolConfig.Id} because block was not found after submission"));
            }

            return (accepted, block?.Transactions.FirstOrDefault());
        }

        protected virtual void SetupCrypto()
        {
            var coinProps = BitcoinProperties.GetCoinProperties(poolConfig.Coin.Type, poolConfig.Coin.Algorithm);

            if (coinProps == null)
                logger.ThrowLogPoolStartupException($"Coin Type '{poolConfig.Coin.Type}' not supported by this Job Manager", LogCat);

            _headerHasher = coinProps.HeaderHasher;
            ShareMultiplier = coinProps.ShareMultiplier;
        }

        #region API-Surface

        public IObservable<object> Jobs { get; private set; }

        public virtual async Task<bool> ValidateAddressAsync(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            var result = await _daemon.ExecuteCmdAnyAsync<ValidateAddressResponse>(
                BitcoinCommands.ValidateAddress, new[] { address });

            return result.Response != null && result.Response.IsValid;
        }

        public virtual object[] GetSubscriberData(StratumClient worker)
        {
            Contract.RequiresNonNull(worker, nameof(worker));

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // assign unique ExtraNonce1 to worker (miner)
            context.ExtraNonce1 = _extraNonceProvider.Next();

            string extraNonce1Padding = new String('0', ExtranonceBytes * 2 - context.ExtraNonce1.Length);

            // setup response data
            var responseData = new object[]
            {
                extraNonce1Padding + context.ExtraNonce1,
                ExtranonceBytes - context.ExtraNonce1.Length / 2,
            };

            return responseData;
        }

        public virtual async Task<Share> SubmitShareAsync(StratumClient worker, object submission)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(submission, nameof(submission));

            logger.LogInvoke(LogCat, new object[] { worker.ConnectionId });

            if (!(submission is object[] submitParams))
                throw new StratumException(StratumError.Other, "invalid params");

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // extract params
            var workerValue = (submitParams[0] as string)?.Trim();
            var jobId = submitParams[1] as string;
            var extraNonce2 = submitParams[2] as string;
            var nTime = submitParams[3] as string;
            var nonce = submitParams[4] as string;
            var solution = submitParams[5] as string;

            if (string.IsNullOrEmpty(workerValue))
                throw new StratumException(StratumError.Other, "missing or invalid workername");

            if (string.IsNullOrEmpty(solution))
                throw new StratumException(StratumError.Other, "missing or invalid solution");

            ExchangeCoinJob job;

            lock(jobLock)
            {
                job = _validJobs.FirstOrDefault(x => x.JobId == jobId);
            }

            if (job == null)
                throw new StratumException(StratumError.JobNotFound, "job not found");

            // extract worker/miner/payoutid
            var split = workerValue.Split('.');
            var minerName = split[0];
            var workerName = split.Length > 1 ? split[1] : null;

            // validate & process
            var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce, solution);

            // if block candidate, submit & check if accepted by network
            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"[{LogCat}] Submitting block {share.BlockHeight} [{share.BlockHash}]");

                var acceptResponse = await SubmitBlockAsync(share, blockHex);

                // is it still a block candidate?
                share.IsBlockCandidate = acceptResponse.Accepted;

                if (share.IsBlockCandidate)
                {
                    logger.Info(() => $"[{LogCat}] Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {minerName}");

                    blockSubmissionSubject.OnNext(Unit.Default);

                    // persist the coinbase transaction-hash to allow the payment processor
                    // to verify later on that the pool has received the reward for the block
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTransaction;
                    
                    // check what is a value of CoinbaseTransaction and assign it to block reward
                    var result = await _wallet.ExecuteCmdSingleAsync<JToken>(BitcoinCommands.GetTransaction, new [] {acceptResponse.CoinbaseTransaction});
                    Transaction transactionInfo = result.Response?.ToObject<Transaction>();
                    if (transactionInfo != null) 
                        share.BlockReward = (decimal) transactionInfo.Details?[0]?.Amount;
                }

                else
                {
                    // clear fields that no longer apply
                    share.TransactionConfirmationData = null;
                }
            }

            // enrich share with common data
            share.PoolId = poolConfig.Id;
            share.IpAddress = worker.RemoteEndpoint.Address.ToString();
            share.Miner = minerName;
            share.Worker = workerName;
            share.UserAgent = context.UserAgent;
            share.Source = clusterConfig.ClusterName;
            share.NetworkDifficulty = job.Difficulty;
            share.Difficulty = share.Difficulty / ShareMultiplier;
            share.Created = _clock.Now;

            return share;
        }

        public BlockchainStats BlockchainStats { get; } = new BlockchainStats();
        public double ShareMultiplier { get; private set; }

        #endregion // API-Surface

        #region Overrides

        protected override string LogCat => "ExchangeCoin Job Manager";

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            _extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<ExchangeCoinPoolConfigExtra>();
            _extraPoolPaymentProcessingConfig = poolConfig.PaymentProcessing?.Extra?.SafeExtensionDataAs<ExchangeCoinPoolPaymentProcessingConfigExtra>();

            if (_extraPoolConfig?.MaxActiveJobs.HasValue == true)
                _maxActiveJobs = _extraPoolConfig.MaxActiveJobs.Value;

            base.Configure(poolConfig, clusterConfig);
        }

        protected override void ConfigureDaemons()
        {
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            _daemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            _daemon.Configure(poolConfig.Daemons);
            
            _wallet = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            _wallet.Configure(_extraPoolConfig.Wallets);
        }

        protected override async Task<bool> AreDaemonsHealthyAsync()
        {
            var responses = await _daemon.ExecuteCmdAllAsync<DaemonInfo>(BitcoinCommands.GetInfo);

            if (responses.Where(x => x.Error?.InnerException?.GetType() == typeof(DaemonClientException))
                .Select(x => (DaemonClientException)x.Error.InnerException)
                .Any(x => x.Code == HttpStatusCode.Unauthorized))
                logger.ThrowLogPoolStartupException($"Daemon reports invalid credentials", LogCat);

            return responses.All(x => x.Error == null);
        }

        protected override async Task<bool> AreDaemonsConnectedAsync()
        {
            var response = await _daemon.ExecuteCmdAnyAsync<DaemonInfo>(BitcoinCommands.GetInfo);

            return response.Error == null && response.Response.Connections > 0;
        }

        protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
        {
            var syncPendingNotificationShown = false;

            while(true)
            {
                var responses = await _daemon.ExecuteCmdAllAsync<ExchangeCoinGetWork>(BitcoinCommands.GetWork);

                var isSynched = responses.All(x => x.Error == null);

                if (isSynched)
                {
                    logger.Info(() => $"[{LogCat}] All daemons synched with blockchain");
                    break;
                }

                if (!syncPendingNotificationShown)
                {
                    logger.Info(() => $"[{LogCat}] Daemons still syncing with network. Manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync();

                // delay retry by 5s
                await Task.Delay(5000, ct);
            }
        }

        protected override async Task PostStartInitAsync(CancellationToken ct)
        {
            var commands = new[]
            {
                new DaemonCmd(BitcoinCommands.ValidateAddress, new[] { poolConfig.Address }),
                new DaemonCmd(BitcoinCommands.GetInfo),
                new DaemonCmd(BitcoinCommands.GetDifficulty),
            };

            var results = await _daemon.ExecuteBatchAnyAsync(commands);

            if (results.Any(x => x.Error != null))
            {
                var resultList = results.ToList();
                var errors = results.Where(x => x.Error != null && commands[resultList.IndexOf(x)].Method != BitcoinCommands.SubmitBlock)
                    .ToArray();

                if (errors.Any())
                    logger.ThrowLogPoolStartupException($"Init RPC failed: {string.Join(", ", errors.Select(y => y.Error.Message))}", LogCat);
            }
            
            // extract results
            var validateAddressResponse = results[0].Response.ToObject<ValidateAddressResponse>();
            var daemonInfoResponse = results[1].Response.ToObject<DaemonInfo>();
            var difficultyResponse = results[2].Response.ToObject<JToken>();

            if (clusterConfig.PaymentProcessing?.Enabled == true)
            {
                var result = await _wallet.ExecuteCmdAnyAsync<ValidateAddressResponse>(
                    BitcoinCommands.ValidateAddress, new[] { poolConfig.Address });
                
                // extract results
                validateAddressResponse = result.Response;
            }

            // ensure pool owns wallet
            if (!validateAddressResponse.IsValid)
                logger.ThrowLogPoolStartupException($"Daemon reports pool-address '{poolConfig.Address}' as invalid", LogCat);
            
            if (clusterConfig.PaymentProcessing?.Enabled == true && !validateAddressResponse.IsMine)
                logger.ThrowLogPoolStartupException($"Daemon does not own pool-address '{poolConfig.Address}'", LogCat);

            // Create pool address script from response
            _poolAddressDestination = AddressToDestination(poolConfig.Address);

            // chain detection
            _networkType = daemonInfoResponse.Testnet ? BitcoinNetworkType.Test : BitcoinNetworkType.Main;

            // update stats
            BlockchainStats.NetworkType = _networkType.ToString();
            BlockchainStats.RewardType = "POW";

            await UpdateNetworkStatsAsync();

            // Periodically update network stats
            Observable.Interval(TimeSpan.FromMinutes(10))
                .Select(via => Observable.FromAsync(()=> UpdateNetworkStatsAsync()))
                .Concat()
                .Subscribe();

            SetupCrypto();
            SetupJobUpdates();
        }

        protected virtual IDestination AddressToDestination(string address)
        {
            return BitcoinUtils.AddressToDestination(address);
        }

        protected virtual async Task<(bool IsNew, bool Force)> UpdateJob(bool forceUpdate, string via = null, string json = null)
        {
            logger.LogInvoke(LogCat);

            try
            {
                if (forceUpdate)
                    _lastJobRebroadcast = _clock.Now;

                var response = string.IsNullOrEmpty(json) ?
                    await GetWorkAsync() :
                    GetWorkFromJson(json);

                // may happen if daemon is currently not connected to peers
                if (response.Error != null)
                {
                    logger.Warn(() => $"[{LogCat}] Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                    return (false, forceUpdate);
                }

                var work = response.Response;
                var blockHeader = new ExchangeCoinBlockHeader(work.Data);

                var job = currentJob;
                var isNew = job == null ||
                    (job.BlockHeader?.PrevBlock != blockHeader.PrevBlock &&
                     blockHeader.Height > job.BlockHeader?.Height);

                if (isNew || forceUpdate)
                {
                    job = new ExchangeCoinJob();

                    job.Init(work, NextJobId(),
                        poolConfig, clusterConfig, _clock, _poolAddressDestination, _networkType,
                        ShareMultiplier, _extraPoolPaymentProcessingConfig?.BlockrewardMultiplier ?? 1.0m,
                        _headerHasher);

                    lock (jobLock)
                    {
                        if (isNew)
                        {
                            if(via != null)
                                logger.Info(()=> $"[{LogCat}] Detected new block {blockHeader.Height} via {via}");
                            else
                                logger.Info(() => $"[{LogCat}] Detected new block {blockHeader.Height}");

                            _validJobs.Clear();

                            // update stats
                            BlockchainStats.LastNetworkBlockTime = _clock.Now;
                            BlockchainStats.BlockHeight = blockHeader.Height;
                            BlockchainStats.NetworkDifficulty = job.Difficulty;
                        }

                        else
                        {
                            // trim active jobs
                            while(_validJobs.Count > _maxActiveJobs - 1)
                                _validJobs.RemoveAt(0);
                        }

                        _validJobs.Add(job);
                    }

                    currentJob = job;
                }

                return (isNew, forceUpdate);
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{LogCat}] Error during {nameof(UpdateJob)}");
            }

            return (false, forceUpdate);
        }

        protected virtual object GetJobParamsForStratum(bool isNew)
        {
            var job = currentJob;
            return job?.GetJobParams(isNew);
        }

        #endregion // Overrides
    }
}