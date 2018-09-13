/*
Copyright 2018 Exchange Coin (excc.co)
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.ExchangeCoin.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.Contracts;
using MiningCore.Crypto;
using MiningCore.Extensions;
using MiningCore.Stratum;
using MiningCore.Time;
using MiningCore.Util;
using NBitcoin;
using NBitcoin.BouncyCastle.Math;

namespace MiningCore.Blockchain.ExchangeCoin
{
    public class ExchangeCoinJob
    {
        protected IMasterClock clock;
        protected double shareMultiplier;
        protected IHashAlgorithm headerHasher;
        protected HashSet<string> submissions = new HashSet<string>();
        protected BigInteger blockTargetValue;
        protected byte[] coinbaseInitial;
        protected string coinbaseInitialHex;
        protected EquihashSolver equihash = EquihashSolver.Instance.Value;

        ///////////////////////////////////////////
        // GetJobParams related properties

        protected object[] jobParams;

        protected virtual void BuildCoinbase()
        {
            // build coinbase initial
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);
                BlockHeader.ReadWriteInitialCoinbase(bs);

                coinbaseInitial = stream.ToArray();
                coinbaseInitialHex = coinbaseInitial.ToHexString();
            }

            // build coinbase final
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);
                BlockHeader.ReadWriteFinalCoinbase(bs);
            }
        }

        protected bool RegisterSubmit(string nonce, string extraNonce, string solution)
        {
            lock(submissions)
            {
                var key = nonce.ToLower() + extraNonce.ToLower() + solution.ToLower();
                if (submissions.Contains(key))
                    return false;

                submissions.Add(key);
                return true;
            }
        }

        protected virtual (Share Share, string BlockHex) ProcessShareInternal(StratumClient worker, string extraNonce,
            uint nTime, string nonce, string solution)
        {
            var context = worker.ContextAs<BitcoinWorkerContext>();
            var solutionBytes = solution.HexToByteArray();
            var extraNonceBytes = extraNonce.HexToByteArray();
            var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

            // hash block-header
            var headerBytes = SerializeHeader(nTime, extraNonceBytes, nonceInt);

            // verify solution
//            if (!equihash.Verify(headerBytes, solutionBytes))
//                throw new StratumException(StratumError.Other, "invalid solution");

            // hash block-header
            var headerSolutionBytes = headerBytes.Concat(solutionBytes).ToArray();
            var headerHash = headerHasher.Digest(headerSolutionBytes);
            var headerValue = new uint256(headerHash);
            var headerHashBigInt = new BigInteger(headerHash.ToBigInteger().ToString());

            // calc share-diff
            var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;
            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;

            // check if the share meets the much harder block difficulty (block candidate)
            var isBlockCandidate = headerHashBigInt.CompareTo(blockTargetValue) < 1;

            // test if share meets at least workers current difficulty
            if (!isBlockCandidate && ratio < 0.99)
            {
                // check if share matched the previous difficulty from before a vardiff retarget
                if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / context.PreviousDifficulty.Value;

                    if (ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                    // use previous difficulty
                    stratumDifficulty = context.PreviousDifficulty.Value;
                }

                else
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            var result = new Share
            {
                BlockHeight = BlockHeader.Height,
                NetworkDifficulty = Difficulty * shareMultiplier,
                Difficulty = stratumDifficulty,
            };

            if (isBlockCandidate)
            {
                result.IsBlockCandidate = true;
                result.BlockHash = headerValue.ToString();

                var blockHex = headerSolutionBytes.ToHexString();

                return (result, blockHex);
            }

            return (result, null);
        }

        protected virtual byte[] SerializeHeader(uint nTime, byte[] extraNonce, uint nonce)
        {
            byte[] serialized;

            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);
                BlockHeader.ReadWrite(bs);
                serialized = stream.ToArray();
            }


            var tmpBlockHeader = new ExchangeCoinBlockHeader(serialized);
            tmpBlockHeader.Timestamp = nTime;
            tmpBlockHeader.Nonce = nonce;
            Array.Resize(ref extraNonce, 32);
            tmpBlockHeader.ExtraData = extraNonce;

            byte[] serializedTmp;
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);
                tmpBlockHeader.ReadWriteWithoutSolution(bs);

                serializedTmp = stream.ToArray();
            }

            return serializedTmp;
        }

        protected virtual byte[] SerializeBlock(byte[] header, byte[] solution)
        {
            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                bs.ReadWrite(ref header);
                bs.ReadWrite(ref solution);

                return stream.ToArray();
            }
        }

        #region API-Surface

        public ExchangeCoinBlockHeader BlockHeader { get; protected set; }
        public ExchangeCoinGetWork Work { get; protected set; }
        public double Difficulty { get; protected set; }

        public string JobId { get; protected set; }

        public virtual void Init(ExchangeCoinGetWork work, string jobId,
            PoolConfig poolConfig, ClusterConfig clusterConfig, IMasterClock clock,
            IDestination poolAddressDestination, BitcoinNetworkType networkType,
            bool isPoS, double shareMultiplier, decimal blockrewardMultiplier,
            IHashAlgorithm coinbaseHasher, IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
        {
            Contract.RequiresNonNull(work, nameof(work));
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(clusterConfig, nameof(clusterConfig));
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(poolAddressDestination, nameof(poolAddressDestination));
            Contract.RequiresNonNull(coinbaseHasher, nameof(coinbaseHasher));
            Contract.RequiresNonNull(headerHasher, nameof(headerHasher));
            Contract.RequiresNonNull(blockHasher, nameof(blockHasher));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId), $"{nameof(jobId)} must not be empty");

            this.clock = clock;

            Work = work;
            BlockHeader = new ExchangeCoinBlockHeader(work.Data);
            JobId = jobId;
            Difficulty = new Target(new NBitcoin.BouncyCastle.Math.BigInteger(Work.Target, 16)).Difficulty;

            this.shareMultiplier = shareMultiplier;

            this.headerHasher = headerHasher;

            if(!string.IsNullOrEmpty(Work.Target))
                blockTargetValue = new BigInteger(Work.Target.HexToByteArray().ToReverseArray());
            else
            {
                var tmp = new Target(BlockHeader.Bits);
                blockTargetValue = tmp.ToBigInteger();
            }

            BuildCoinbase();

            jobParams = new object[]
            {
                JobId,                                               // JobID
                BlockHeader.PrevBlock.ToHexString(),                 // PrevHash
                coinbaseInitialHex,                                  // Coinbase1
                "",                                                  // Coinbase2
                "",                                                  // MerkleBranches
                BlockHeader.Version.ToStringHex8().HexToByteArray().ReverseArray().ToHexString(),        // BlockVersion
                BlockHeader.Bits.ReverseByteOrder().ToStringHex8(),  // NBits
                BlockHeader.Timestamp.ReverseByteOrder().ToStringHex8(), // NTime
                false                                                // CleanJobs
            };
        }

        public virtual object GetJobParams(bool isNew)
        {
            jobParams[jobParams.Length - 1] = isNew;
            return jobParams;
        }

        public virtual (Share Share, string BlockHex) ProcessShare(StratumClient worker, string extraNonce, string nTime, string nonce, string solution)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce), $"{nameof(extraNonce)} must not be empty");
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime), $"{nameof(nTime)} must not be empty");
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(solution), $"{nameof(solution)} must not be empty");

            var context = worker.ContextAs<BitcoinWorkerContext>();

            // validate nTime
            if (nTime.Length != 8)
                throw new StratumException(StratumError.Other, "incorrect size of ntime");

            var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
            if (nTimeInt < BlockHeader.Timestamp || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
                throw new StratumException(StratumError.Other, "ntime out of range");

            // validate nonce
            if (extraNonce.Length != 24)
                throw new StratumException(StratumError.Other, "incorrect size of extraNonce2");

            var extraNonce2 = extraNonce.Substring(0, extraNonce.Length - 8);

            if (!extraNonce.Equals(extraNonce2 + context.ExtraNonce1))
                throw new StratumException(StratumError.Other, "incorrect extraNonce");

            // validate solution
            if (solution.Length != 200)
                throw new StratumException(StratumError.Other, "incorrect size of solution");

            // dupe check
            if (!RegisterSubmit(extraNonce, nonce, solution))
                throw new StratumException(StratumError.DuplicateShare, "duplicate share");

            return ProcessShareInternal(worker, extraNonce, nTimeInt, nonce, solution);
        }

        #endregion // API-Surface
    }
}
