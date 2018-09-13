/*
Copyright 2018 Exchange Coin (excc.co)
*/

using Autofac;
using AutoMapper;
using MiningCore.Configuration;
using MiningCore.Messaging;
using MiningCore.Notifications;
using MiningCore.Persistence;
using MiningCore.Persistence.Repositories;
using MiningCore.Time;
using Newtonsoft.Json;

namespace MiningCore.Blockchain.ExchangeCoin
{
    [CoinMetadata(CoinType.EXCC)]
    public class ExchangeCoinPool : ExchangeCoinPoolBase
    {
        public ExchangeCoinPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings,
            IConnectionFactory cf,
            IStatsRepository statsRepo,
            IMapper mapper,
            IMasterClock clock,
            IMessageBus messageBus) :
            base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus)
        {
        }
    }
}