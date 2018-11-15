﻿/*
Copyright 2018 ExchangeCoin (excc.co)
*/

using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.JsonRpc;
using MiningCore.Messaging;
using MiningCore.Mining;
using MiningCore.Notifications;
using MiningCore.Notifications.Messages;
using MiningCore.Persistence;
using MiningCore.Persistence.Repositories;
using MiningCore.Stratum;
using MiningCore.Time;
using Newtonsoft.Json;
using NLog;

namespace MiningCore.Blockchain.ExchangeCoin
{
    public class ExchangeCoinPoolBase : PoolBase
    {
        public ExchangeCoinPoolBase(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus)
        {
        }

        protected object currentJobParams;
        protected ExchangeCoinJobManager manager;

        protected virtual async Task OnSubscribeAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
            {
                await client.RespondErrorAsync(StratumError.Other, "missing request id", request.Id);
                return;
            }

            var context = client.ContextAs<BitcoinWorkerContext>();
            var requestParams = request.ParamsAs<string[]>();

            var data = new object[]
                {
                    new object[]
                    {
                        new object[] { BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty } },
                        new object[] { BitcoinStratumMethods.MiningNotify, client.ConnectionId }
                    }
                }
                .Concat(manager.GetSubscriberData(client))
                .ToArray();

            await client.RespondAsync(data, request.Id);

            // setup worker context
            context.IsSubscribed = true;
            context.UserAgent = requestParams?.Length > 0 ? requestParams[0].Trim() : null;

            // send intial update
            await client.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
        }

        protected virtual async Task OnAuthorizeAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            if (request.Id == null)
            {
                await client.RespondErrorAsync(StratumError.Other, "missing request id", request.Id);
                return;
            }

            var context = client.ContextAs<BitcoinWorkerContext>();
            var requestParams = request.ParamsAs<string[]>();
            var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
            var password = requestParams?.Length > 1 ? requestParams[1] : null;
            var passParts = password?.Split(PasswordControlVarsSeparator);

            // extract worker/miner
            var split = workerValue?.Split('.');
            var minerName = split?.FirstOrDefault()?.Trim();
            var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

            // assumes that workerName is an address
            context.IsAuthorized = !string.IsNullOrEmpty(minerName) && await manager.ValidateAddressAsync(minerName);
            context.MinerName = minerName;
            context.WorkerName = workerName;

            if (context.IsAuthorized)
            {
                // respond
                await client.RespondAsync(context.IsAuthorized, request.Id);

                // log association
                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] = {workerValue} = {client.RemoteEndpoint.Address}");

                // extract control vars from password
                var staticDiff = GetStaticDiffFromPassparts(passParts);
                if (staticDiff.HasValue &&
                    (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                        context.VarDiff == null && staticDiff.Value > context.Difficulty))
                {
                    context.VarDiff = null; // disable vardiff
                    context.SetDifficulty(staticDiff.Value);

                    await client.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
                }
            }

            else
            {
                // respond
                await client.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Banning unauthorized worker for 60 sec");

                banManager.Ban(client.RemoteEndpoint.Address, TimeSpan.FromSeconds(60));

                DisconnectClient(client);
            }
        }

        protected virtual async Task OnSubmitAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<BitcoinWorkerContext>();

            try
            {
                if (request.Id == null)
                    throw new StratumException(StratumError.MinusOne, "missing request id");

                // check age of submission (aged submissions are usually caused by high server load)
                var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

                if (requestAge > maxShareAge)
                {
                    logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Dropping stale share submission request (not client's fault)");
                    return;
                }

                // check worker state
                context.LastActivity = clock.Now;

                // validate worker
                if (!context.IsAuthorized)
                    throw new StratumException(StratumError.UnauthorizedWorker, "Unauthorized worker");
                else if (!context.IsSubscribed)
                    throw new StratumException(StratumError.NotSubscribed, "Not subscribed");

                // submit
                var requestParams = request.ParamsAs<string[]>();
                var share = await manager.SubmitShareAsync(client, requestParams);

                await client.RespondAsync(true, request.Id);

                // publish
                messageBus.SendMessage(new ClientShare(client, share));

                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

                // update pool stats
                if (share.IsBlockCandidate)
                    poolStats.LastPoolBlockTime = clock.Now;

                // update client stats
                context.Stats.ValidShares++;
                await UpdateVarDiffAsync(client);
            }

            catch(StratumException ex)
            {
                await client.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);

                // telemetry
                PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

                // update client stats
                context.Stats.InvalidShares++;
                logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Share rejected: {ex.Code}");

                // banning
                ConsiderBan(client, context, poolConfig.Banning);
            }
        }

        private async Task OnSuggestDifficultyAsync(StratumClient client, Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;
            var context = client.ContextAs<BitcoinWorkerContext>();

            // acknowledge
            await client.RespondAsync(true, request.Id);

            try
            {
                var requestedDiff = (double) Convert.ChangeType(request.Params, TypeCode.Double);

                // client may suggest higher-than-base difficulty, but not a lower one
                var poolEndpoint = poolConfig.Ports[client.PoolEndpoint.Port];

                if (requestedDiff > poolEndpoint.Difficulty)
                {
                    context.SetDifficulty(requestedDiff);
                    await client.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

                    logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Difficulty set to {requestedDiff} as requested by miner");
                }
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"[{LogCat}] Unable to convert suggested difficulty {request.Params}");
            }
        }

        protected virtual Task OnNewJob(object jobParams)
        {
            currentJobParams = jobParams;

            logger.Info(() => $"[{LogCat}] Broadcasting job");

            var tasks = ForEachClient(async client =>
            {
                var context = client.ContextAs<BitcoinWorkerContext>();

                if (context.IsSubscribed && context.IsAuthorized)
                {
                    // check alive
                    var lastActivityAgo = clock.Now - context.LastActivity;

                    if (poolConfig.ClientConnectionTimeout > 0 &&
                        lastActivityAgo.TotalSeconds > poolConfig.ClientConnectionTimeout)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Booting zombie-worker (idle-timeout exceeded)");
                        DisconnectClient(client);
                        return;
                    }

                    // varDiff: if the client has a pending difficulty change, apply it now
                    if (context.ApplyPendingDifficulty())
                        await client.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

                    // send job
                    await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
                }
            });

            return Task.WhenAll(tasks);
        }

        #region Overrides

        protected ExchangeCoinJobManager CreateJobManager()
        {
            return ctx.Resolve<ExchangeCoinJobManager>(
                new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider()));
        }

        protected override async Task SetupJobManager(CancellationToken ct)
        {
            manager = CreateJobManager();
            manager.Configure(poolConfig, clusterConfig);

            await manager.StartAsync(ct);

            if (poolConfig.EnableInternalStratum == true)
            {
                disposables.Add(manager.Jobs
                    .Select(x => Observable.FromAsync(() => OnNewJob(x)))
                    .Concat()
                    .Subscribe());

                // we need work before opening the gates
                await manager.Jobs.Take(1).ToTask(ct);
            }
        }

        protected override void InitStats()
        {
            base.InitStats();

            blockchainStats = manager.BlockchainStats;
        }

        protected override WorkerContextBase CreateClientContext()
        {
            return new BitcoinWorkerContext();
        }

        protected override async Task OnRequestAsync(StratumClient client,
            Timestamped<JsonRpcRequest> tsRequest)
        {
            var request = tsRequest.Value;

            switch(request.Method)
            {
                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(client, tsRequest);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(client, tsRequest);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(client, tsRequest);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    await OnSuggestDifficultyAsync(client, tsRequest);
                    break;

                case BitcoinStratumMethods.GetTransactions:
                    //OnGetTransactions(client, tsRequest);
                    // ignored
                    break;

                case BitcoinStratumMethods.ExtraNonceSubscribe:
                    // ignored
                    break;

                case BitcoinStratumMethods.MiningMultiVersion:
                    // ignored
                    break;

                default:
                    logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await client.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        public override double HashrateFromShares(double shares, double interval)
        {
            return shares / interval;
        }

        protected override async Task OnVarDiffUpdateAsync(StratumClient client, double newDiff)
        {
            var context = client.ContextAs<BitcoinWorkerContext>();
            context.EnqueueNewDifficulty(newDiff);

            // apply immediately and notify client
            if (context.HasPendingDifficulty)
            {
                context.ApplyPendingDifficulty();

                await client.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
                await client.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
            }
        }

        #endregion // Overrides
    }
}