using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NBitcoin;

namespace PlayWithSpv
{
    public class PartialBlockChain
    {
		public Network Network { get; }
	    private readonly ConcurrentBag<PartialBlock> _chain = new ConcurrentBag<PartialBlock>();

	    private PartialBlockChain()
	    {
	    }
		public PartialBlockChain(Network network)
	    {
		    Network = network;
	    }

	    public int WorstHeight => _chain.Count == 0 ? -1 : _chain.Select(partialBlock => partialBlock.ChainedHeader.Height).Min();
	    public int BestHeight => _chain.Count == 0 ? -1 : _chain.Select(partialBlock => partialBlock.ChainedHeader.Height).Max();
		public int BlockCount => _chain.Count;

		/// <summary> int: block height, if tx is not found yet -1 </summary>
	    public ConcurrentDictionary<int, uint256> TrackedTransactions { get; }
			= new ConcurrentDictionary<int, uint256>();

	    private readonly ConcurrentDictionary<int, Block> _fullBlockBuffer = new ConcurrentDictionary<int, Block>();
		/// <summary>
		/// int: block height
		/// Max blocks in Memory is 50, removes the oldest one automatically if full
		///  </summary>
		public ConcurrentDictionary<int, Block> FullBlockBuffer
	    {
			get
			{
				// Don't keep more than 50 blocks in memory
				while(_fullBlockBuffer.Count >= 50)
				{
					// Remove the oldest block
					var smallest = _fullBlockBuffer.Keys.Min();
					Block b;
					_fullBlockBuffer.TryRemove(smallest, out b);
				}
				return _fullBlockBuffer;
			}
	    }

	    /// <summary> Track a transaction </summary>
		/// <returns>False if not found. When confirms, it starts tracking. If too old you need to resync the chain.</returns>
	    public bool Track(uint256 transactionId)
	    {
		    TrackedTransactions.AddOrReplace(-1, transactionId);

			Transaction transaction = null;
			Block block = null;
			foreach(var b in FullBlockBuffer.Values)
			{
				Transaction tx = b.Transactions.FirstOrDefault(x => transactionId.Equals(x.GetHash()));
				if (tx != default(Transaction))
				{
					transaction = tx;
					block = b;
					break;
				}
			}

			// This warning doesn't make sense:
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse
			if(block == null || transaction == null)
			{
				return false;
			}
			else
			{
				PartialBlock partialBlock = _chain.First(x => block.Header.GetHash().Equals(x.ChainedHeader.Header.GetHash()));

				partialBlock.Transactions.Add(transaction);
				var transactionHashes = partialBlock.MerkleProof.PartialMerkleTree.GetMatchedTransactions() as HashSet<uint256>;
				transactionHashes.Add(transaction.GetHash());
				partialBlock.MerkleProof = block.Filter(transactionHashes.ToArray());

				return true;
			}
		}

		public void Add(ChainedBlock chainedHeader, Block block)
		{
			if(chainedHeader.HashBlock != block.GetHash()) throw new ArgumentException("key.HashBlock != value.GetHash()");

			FullBlockBuffer.AddOrReplace(chainedHeader.Height, block);
			var notFoundTransactions = GetNotYetFoundTrackedTransactions();
			HashSet<uint256> foundTransactions = new HashSet<uint256>();
			foreach(var txid in notFoundTransactions)
			{
				if(block.Transactions.Any(x => x.GetHash().Equals(txid)))
				{
					foundTransactions.Add(txid);
				}
			}
			MerkleBlock merkleProof = foundTransactions.Count == 0 ? block.Filter() : block.Filter(foundTransactions.ToArray());
			var partialBlock = new PartialBlock(chainedHeader, merkleProof);
			foreach (var txid in foundTransactions)
			{
				foreach (var tx in block.Transactions)
				{
					if(tx.GetHash().Equals(txid))
						partialBlock.Transactions.Add(tx);
				}
			}

			_chain.Add(partialBlock);
		}

	    private HashSet<uint256> GetNotYetFoundTrackedTransactions()
	    {
		    var notFound = new HashSet<uint256>();
		    foreach(var tx in TrackedTransactions)
		    {
			    if(tx.Key == -1)
			    {
				    notFound.Add(tx.Value);
			    }
		    }
		    return notFound;
	    }

	    private static readonly object Saving = new object();
	    public void Flush(string partialChainFilePath)
	    {
		    lock(Saving)
		    {
			    //throw new NotImplementedException();
		    }
	    }
		public void Load(string partialChainFilePath)
		{
			lock (Saving)
			{
				//throw new NotImplementedException();
			}
		}
	}

	public class PartialBlock
	{
		public ChainedBlock ChainedHeader { get; }
		public MerkleBlock MerkleProof { get; set; }
		public ConcurrentBag<Transaction> Transactions { get; }

		public PartialBlock(ChainedBlock chainedHeader, MerkleBlock merkleProof)
		{
			ChainedHeader = chainedHeader;
			MerkleProof = merkleProof;
			Transactions = new ConcurrentBag<Transaction>();
		}
	}
}
