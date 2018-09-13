/*
Copyright 2018 Exchange Coin (excc.co)
Authors: Wojciech Harzowski (wojciech.harzowski@pragmaticcoders.com)
*/

using MiningCore.Configuration;

namespace MiningCore.Blockchain.ExchangeCoin.Configuration
{
    public class ExchangeCoinPoolConfigExtra
    {
        /// <summary>
        /// Maximum number of tracked jobs.
        /// Default: 12 - you should increase this value if your blockrefreshinterval is higher than 300ms
        /// </summary>
        public int? MaxActiveJobs { get; set; }

        /// <summary>
        /// Set to true to limit RPC commands to old Bitcoin command set
        /// </summary>
        public bool? HasLegacyDaemon { get; set; }

        /// <summary>
        /// Blocktemplate stream published via ZMQ
        /// </summary>
        public ZmqPubSubEndpointConfig BtStream { get; set; }
    }
}