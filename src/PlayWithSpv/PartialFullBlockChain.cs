using System;
using System.Collections.Concurrent;
using System.Linq;
using NBitcoin;

namespace PlayWithSpv
{
    public class PartialFullBlockChain
    {
	    private readonly ConcurrentDictionary<ChainedBlock, Block> _chain = new ConcurrentDictionary<ChainedBlock, Block>();

		public int WorstHeight => _chain.Count == 0 ? -1 : _chain.Keys.Select(chainedBlock => chainedBlock.Height).Min();

	    public int BestHeight => _chain.Count == 0 ? -1 : _chain.Keys.Select(chainedBlock => chainedBlock.Height).Max();

	    public int Count => _chain.Count;

		public void AddOrReplace(ChainedBlock key, Block value)
		{
			if(key.HashBlock != value.GetHash()) throw new ArgumentException("key.HashBlock != value.GetHash()");

			_chain.AddOrReplace(key, value);
		}
	}
}
