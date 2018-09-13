/*
Copyright 2018 Exchange Coin (excc.co)
*/

using NBitcoin;
using NBitcoin.DataEncoders;

namespace MiningCore.Blockchain.ExchangeCoin
{
    public class ExchangeCoinBlockHeader : IBitcoinSerializable
    {

        public ExchangeCoinBlockHeader(string hex)
            : this(Encoders.Hex.DecodeData(hex))
        {
        }

        public ExchangeCoinBlockHeader(byte[] bytes)
        {
            this.ReadWrite(bytes);
        }

        // Total 280 bytes
        private int version;                             // 4 bytes
        private byte[] prevBlock = new byte[32];         // 32 bytes
        private byte[] merkleRoot = new byte[32];        // 32 bytes
        private byte[] stakeRoot = new byte[32];         // 32 bytes
        private ushort voteBits;                         // 2 bytes
        private byte[] finalState = new byte[6];         // 6 bytes
        private ushort voters;                           // 2 bytes
        private byte freshStake;                         // 1 byte
        private byte revocations;                        // 1 byte
        private uint poolSize;                           // 4 bytes
        private uint bits;                               // 4 bytes
        private ulong sBits;                             // 8 bytes
        private uint height;                             // 4 bytes
        private uint size;                               // 4 bytes
        private uint timestamp;                          // 4 bytes
        private uint nonce;                              // 4 bytes
        private byte[] extraData = new byte[32];         // 32 bytes
        private uint stakeVersion;                       // 4 bytes
        private byte[] equihashSolution = new byte[100]; // 100 bytes

        public int Version
        {
            get => version;
            set => version = value;
        }

        public byte[] PrevBlock
        {
            get => prevBlock;
            set => prevBlock = value;
        }

        public byte[] MerkleRoot
        {
            get => merkleRoot;
            set => merkleRoot = value;
        }

        public byte[] StakeRoot
        {
            get => stakeRoot;
            set => stakeRoot = value;
        }

        public ushort VoteBits
        {
            get => voteBits;
            set => voteBits = value;
        }

        public byte[] FinalState
        {
            get => finalState;
            set => finalState = value;
        }

        public ushort Voters
        {
            get => voters;
            set => voters = value;
        }

        public byte FreshStake
        {
            get => freshStake;
            set => freshStake = value;
        }

        public byte Revocations
        {
            get => revocations;
            set => revocations = value;
        }

        public uint PoolSize
        {
            get => poolSize;
            set => poolSize = value;
        }

        public uint Bits
        {
            get => bits;
            set => bits = value;
        }

        public ulong SBits
        {
            get => sBits;
            set => sBits = value;
        }

        public uint Height
        {
            get => height;
            set => height = value;
        }

        public uint Size
        {
            get => size;
            set => size = value;
        }

        public uint Timestamp
        {
            get => timestamp;
            set => timestamp = value;
        }

        public uint Nonce
        {
            get => nonce;
            set => nonce = value;
        }

        public byte[] ExtraData
        {
            get => extraData;
            set => extraData = value;
        }

        public uint StakeVersion
        {
            get => stakeVersion;
            set => stakeVersion = value;
        }

        public byte[] EquihashSolution
        {
            get => equihashSolution;
            set => equihashSolution = value;
        }

        #region IBitcoinSerializable Members

        public void ReadWrite(BitcoinStream stream)
        {
            stream.ReadWrite(ref version);
            stream.ReadWrite(ref prevBlock);
            stream.ReadWrite(ref merkleRoot);
            stream.ReadWrite(ref stakeRoot);
            stream.ReadWrite(ref voteBits);
            stream.ReadWrite(ref finalState);
            stream.ReadWrite(ref voters);
            stream.ReadWrite(ref freshStake);
            stream.ReadWrite(ref revocations);
            stream.ReadWrite(ref poolSize);
            stream.ReadWrite(ref bits);
            stream.ReadWrite(ref sBits);
            stream.ReadWrite(ref height);
            stream.ReadWrite(ref size);
            stream.ReadWrite(ref timestamp);
            stream.ReadWrite(ref nonce);
            stream.ReadWrite(ref extraData);
            stream.ReadWrite(ref stakeVersion);
            stream.ReadWrite(ref equihashSolution);
        }

        #endregion

        public void ReadWriteWithoutSolution(BitcoinStream stream)
        {   // 180 bytes (1 - 180)
            stream.ReadWrite(ref version);
            stream.ReadWrite(ref prevBlock);
            stream.ReadWrite(ref merkleRoot);
            stream.ReadWrite(ref stakeRoot);
            stream.ReadWrite(ref voteBits);
            stream.ReadWrite(ref finalState);
            stream.ReadWrite(ref voters);
            stream.ReadWrite(ref freshStake);
            stream.ReadWrite(ref revocations);
            stream.ReadWrite(ref poolSize);
            stream.ReadWrite(ref bits);
            stream.ReadWrite(ref sBits);
            stream.ReadWrite(ref height);
            stream.ReadWrite(ref size);
            stream.ReadWrite(ref timestamp);
            stream.ReadWrite(ref nonce);
            stream.ReadWrite(ref extraData);
            stream.ReadWrite(ref stakeVersion);
        }

        public void ReadWriteInitialCoinbase(BitcoinStream stream)
        {   // 144 bytes (37 - 180)
            stream.ReadWrite(ref merkleRoot);
            stream.ReadWrite(ref stakeRoot);
            stream.ReadWrite(ref voteBits);
            stream.ReadWrite(ref finalState);
            stream.ReadWrite(ref voters);
            stream.ReadWrite(ref freshStake);
            stream.ReadWrite(ref revocations);
            stream.ReadWrite(ref poolSize);
            stream.ReadWrite(ref bits);
            stream.ReadWrite(ref sBits);
            stream.ReadWrite(ref height);
            stream.ReadWrite(ref size);
            stream.ReadWrite(ref timestamp);
            stream.ReadWrite(ref nonce);
            stream.ReadWrite(ref extraData);
            stream.ReadWrite(ref stakeVersion);
        }

        public void ReadWriteFinalCoinbase(BitcoinStream stream)
        {   // 100 bytes (181 - 280)
            stream.ReadWrite(ref equihashSolution);
        }

        internal void SetNull()
        {
            version = 0;
            prevBlock = new byte[32];
            merkleRoot = new byte[32];
            stakeRoot = new byte[32];
            voteBits = 0;
            finalState = new byte[6];
            voters = 0;
            freshStake = 0;
            revocations = 0;
            poolSize = 0;
            bits = 0;
            sBits = 0;
            height = 0;
            size = 0;
            timestamp = 0;
            nonce = 0;
            extraData = new byte[32];
            stakeVersion = 0;
            equihashSolution = new byte[100];
        }
    }
}