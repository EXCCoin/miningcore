using Newtonsoft.Json;

namespace MiningCore.Blockchain.ExchangeCoin.DaemonResponses
{

    public class ExchangeCoinGetWork
    {

        /// <summary>
        /// The hash target
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// data encoded in hexadecimal (byte-for-byte)
        /// </summary>
        public string Data { get; set; }


    }
}