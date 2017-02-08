using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using NBitcoin;
using Newtonsoft.Json;

namespace PlayWithSpv
{
    public class PartialFullBlockChain
    {
		public Network Network { get; }
	    private readonly ConcurrentDictionary<ChainedBlock, Block> _chain = new ConcurrentDictionary<ChainedBlock, Block>();

	    private PartialFullBlockChain()
	    {

	    }

		public PartialFullBlockChain(Network network)
	    {
		    Network = network;
	    }

	    public int WorstHeight => _chain.Count == 0 ? -1 : _chain.Keys.Select(chainedBlock => chainedBlock.Height).Min();

	    public int BestHeight => _chain.Count == 0 ? -1 : _chain.Keys.Select(chainedBlock => chainedBlock.Height).Max();

	    public int Count => _chain.Count;

		public void AddOrReplace(ChainedBlock key, Block value)
		{
			if(key.HashBlock != value.GetHash()) throw new ArgumentException("key.HashBlock != value.GetHash()");

			_chain.AddOrReplace(key, value);
		}

	    private static readonly object Saving = new object();
	    public void Save(string fullChainFilePath)
	    {
		    lock(Saving)
		    {
			    throw new NotImplementedException();
		    }
	    }
		public void Load(string fullChainFilePath)
		{
			lock (Saving)
			{
				throw new NotImplementedException();
			}
		}
	}
}
