namespace MiningCore.Blockchain.ExchangeCoin.DaemonInterface
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