using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace PlayWithSpv
{
	public class PartialBlockChain
	{
		#region Members

		public Network Network { get; private set; }
		public ConcurrentDictionary<int, PartialBlock> Chain { get; } = new ConcurrentDictionary<int, PartialBlock>();

		/// <summary> int: block height, if tx is not found yet -1 </summary>
		public ConcurrentDictionary<uint256, int> TrackedTransactions { get; }
			= new ConcurrentDictionary<uint256, int>();
		public HashSet<Script> TrackedScriptPubKeys { get; }
			= new HashSet<Script>();
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
				while (_fullBlockBuffer.Count >= 50)
				{
					// Remove the oldest block
					var smallest = _fullBlockBuffer.Keys.Min();
					Block b;
					_fullBlockBuffer.TryRemove(smallest, out b);
				}
				return _fullBlockBuffer;
			}
		}

		public int WorstHeight => Chain.Count == 0 ? -1 : Chain.Values.Select(partialBlock => partialBlock.Height).Min();
		public int BestHeight => Chain.Count == 0 ? -1 : Chain.Values.Select(partialBlock => partialBlock.Height).Max();
		public int BlockCount => Chain.Count;

		#endregion

		#region Constructors

		private PartialBlockChain()
		{
		}
		public PartialBlockChain(Network network)
		{
			Network = network;
		}

		#endregion

		#region Tracking

		/// <summary> Track a transaction </summary>
		/// <returns>False if not found. When confirms, it starts tracking. If too old you need to resync the chain.</returns>
		public bool Track(uint256 transactionId)
		{
			if(TrackedTransactions.Keys.Contains(transactionId))
			{
				var tracked = TrackedTransactions.First(x => x.Key.Equals(transactionId));
				if(tracked.Value == -1) return false;
				else return true;
			}

			TrackedTransactions.AddOrReplace(transactionId, -1);

			Transaction transaction = null;
			Block block = null;
			foreach(var b in FullBlockBuffer.Values)
			{
				Transaction tx = b.Transactions.FirstOrDefault(x => transactionId.Equals(x.GetHash()));
				if(tx != default(Transaction))
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
				PartialBlock partialBlock =
					Chain.First(x => block.Header.GetHash().Equals(x.Value.MerkleProof.Header.GetHash())).Value;

				partialBlock.Transactions.Add(transaction);
				var transactionHashes = partialBlock.MerkleProof.PartialMerkleTree.GetMatchedTransactions() as HashSet<uint256>;
				transactionHashes.Add(transaction.GetHash());
				partialBlock.MerkleProof = block.Filter(transactionHashes.ToArray());

				return true;
			}
		}
		/// <param name="scriptPubKey">BitcoinAddress.ScriptPubKey</param>
		/// <param name="searchFullBlockBuffer">If true: it look for transactions in the buffered full blocks in memory</param>
		public void Track(Script scriptPubKey, bool searchFullBlockBuffer = false)
		{
			TrackedScriptPubKeys.Add(scriptPubKey);

			foreach(var block in FullBlockBuffer)
			{
				TrackIfFindRelatedTransactions(scriptPubKey, block.Key, block.Value);
			}
		}
		private void TrackIfFindRelatedTransactions(Script scriptPubKey, int height, Block block)
		{
			foreach(var tx in block.Transactions)
			{
				foreach(var output in tx.Outputs)
				{
					if(output.ScriptPubKey.Equals(scriptPubKey))
					{
						TrackedTransactions.AddOrReplace(tx.GetHash(), height);
					}
				}
			}
		}
		private HashSet<uint256> GetNotYetFoundTrackedTransactions()
		{
			var notFound = new HashSet<uint256>();
			foreach (var tx in TrackedTransactions)
			{
				if (tx.Value == -1)
				{
					notFound.Add(tx.Key);
				}
			}
			return notFound;
		}

		#endregion

		public void ReorgOne()
		{
			// remove the last block
			PartialBlock pb;
			if(Chain.Count != 0)
			{
				Chain.TryRemove(BestHeight, out pb);

				if(pb.Transactions.Count != 0)
				{
					// set the transactions to unconfirmed
					foreach(var txId in pb.Transactions.Select(x => x.GetHash()))
					{
						TrackedTransactions.AddOrReplace(txId, -1);
					}
				}
			}

			// remove the last block from the buffer too
			Block b;
			if(FullBlockBuffer.Count() != 0)
			{
				FullBlockBuffer.TryRemove(FullBlockBuffer.Keys.Max(), out b);
			}
		}

		public void Add(int height, Block block)
		{
			foreach(var spk in TrackedScriptPubKeys)
			{
				TrackIfFindRelatedTransactions(spk, height, block);
			}

			FullBlockBuffer.AddOrReplace(height, block);
			HashSet<uint256> notFoundTransactions = GetNotYetFoundTrackedTransactions();
			HashSet<uint256> foundTransactions = new HashSet<uint256>();
			foreach(var txid in notFoundTransactions)
			{
				if(block.Transactions.Any(x => x.GetHash().Equals(txid)))
				{
					foundTransactions.Add(txid);
				}
			}
			MerkleBlock merkleProof = foundTransactions.Count == 0 ? block.Filter() : block.Filter(foundTransactions.ToArray());
			var partialBlock = new PartialBlock(height, merkleProof);
			foreach(var txid in foundTransactions)
			{
				foreach(var tx in block.Transactions)
				{
					if(tx.GetHash().Equals(txid))
						partialBlock.Transactions.Add(tx);
				}
			}

			Chain.AddOrReplace(partialBlock.Height, partialBlock);
		}

		#region Saving

		private readonly SemaphoreSlim Saving = new SemaphoreSlim(1, 1);

		private enum FilesNames
		{
			TrackedScriptPubKeys,
			TrackedTransactions,
			PartialBlockChain
		}

		private static readonly byte[] blockSep = new byte[] { 0x10, 0x1A, 0x7B, 0x23, 0x5D, 0x12, 0x7D };
		public async Task SaveAsync(string partialChainFolderPath)
		{
			await Saving.WaitAsync().ConfigureAwait(false);
			try
			{
				if (TrackedScriptPubKeys.Count > 0 || TrackedTransactions.Count > 0 || Chain.Count > 0)
				{
					Directory.CreateDirectory(partialChainFolderPath);
				}

				if(TrackedScriptPubKeys.Count > 0)
				{
					File.WriteAllLines(
						Path.Combine(partialChainFolderPath, FilesNames.TrackedScriptPubKeys.ToString()),
						TrackedScriptPubKeys.Select(x => x.ToString()));
				}

				if(TrackedTransactions.Count > 0)
				{
					File.WriteAllLines(
						Path.Combine(partialChainFolderPath, FilesNames.TrackedTransactions.ToString()),
						TrackedTransactions.Select(x => $"{x.Key}:{x.Value}"));
				}

				if(Chain.Count > 0)
				{
					byte[] toFile = Chain.Values.First().ToBytes();
					foreach (var block in Chain.Values.Skip(1))
					{
						toFile = toFile.Concat(blockSep).Concat(block.ToBytes()).ToArray();
					}

					File.WriteAllBytes(Path.Combine(partialChainFolderPath, FilesNames.PartialBlockChain.ToString()),
						toFile);
				}
			}
			finally
			{
				Saving.Release();
			}
		}

		public async Task LoadAsync(string partialChainFolderPath)
		{
			await Saving.WaitAsync().ConfigureAwait(false);
			try
			{
				if(!Directory.Exists(partialChainFolderPath))
					throw new DirectoryNotFoundException($"No Blockchain found at {partialChainFolderPath}");

				var tspb = Path.Combine(partialChainFolderPath, FilesNames.TrackedScriptPubKeys.ToString());
				if(File.Exists(tspb) && new FileInfo(tspb).Length != 0)
				{
					foreach(var line in File.ReadAllLines(tspb))
					{
						TrackedScriptPubKeys.Add(new Script(line));
					}
				}

				var tt = Path.Combine(partialChainFolderPath, FilesNames.TrackedTransactions.ToString());
				if(File.Exists(tt) && new FileInfo(tt).Length != 0)
				{
					foreach(var line in File.ReadAllLines(tt))
					{
						var pieces = line.Split(':');
						TrackedTransactions.TryAdd(new uint256(pieces[0]), int.Parse(pieces[1]));
					}
				}

				var pbc = Path.Combine(partialChainFolderPath, FilesNames.PartialBlockChain.ToString());
				if(File.Exists(pbc) && new FileInfo(pbc).Length != 0)
				{
					foreach(var block in Help.Separate(File.ReadAllBytes(pbc), blockSep))
					{
						PartialBlock pb = new PartialBlock().FromBytes(block);

						Chain.TryAdd(pb.Height, pb);
					}
				}
			}
			finally
			{
				Saving.Release();
			}
		}

		#endregion
	}

	public class PartialBlock
	{
		public int Height { get; set; }
		public MerkleBlock MerkleProof { get; set; } = new MerkleBlock();
		public HashSet<Transaction> Transactions { get; } = new HashSet<Transaction>();

		public PartialBlock()
		{

		}

		public PartialBlock(int height, MerkleBlock merkleProof)
		{
			Height = height;
			MerkleProof = merkleProof;
		}

		private static readonly byte[] txSep = new byte[] {0x30, 0x15, 0x7A, 0x29, 0x5F, 0x1D, 0x7D};
		private static readonly byte[] membSep = new byte[] { 0x3D, 0x16, 0x22, 0x3D, 0x73, 0x50, 0x1 };
		public byte[] ToBytes()
		{
			var merkleProof = MerkleProof.ToBytes();
			byte[] transactions = null;
			if (Transactions.Count > 0)
			{
				transactions = Transactions.First().ToBytes();
				foreach (var tx in Transactions.Skip(1))
				{
					transactions = transactions.Concat(txSep).Concat(tx.ToBytes()).ToArray();
				}
			}
			var ret = BitConverter.GetBytes(Height).Concat(membSep).Concat(merkleProof).Concat(membSep).ToArray();
			if(transactions == null)
			{
				return ret;
			}
			else
			{
				return ret.Concat(transactions).ToArray();
			}
		}
		public PartialBlock FromBytes(byte[] b)
		{
			byte[][] pieces = Help.Separate(b, membSep);

			Height = BitConverter.ToInt32(pieces[0], 0);

			MerkleProof.FromBytes(pieces[1]);

			if(pieces[2].Length !=0)
			{
				foreach(byte[] tx in Help.Separate(pieces[2], txSep))
				{
					Transactions.Add(new Transaction(tx));
				}
			}

			return this;
		}
	}

	internal static class Help
	{
		internal static byte[][] Separate(byte[] source, byte[] separator)
		{
			var Parts = new List<byte[]>();
			var Index = 0;
			byte[] Part;
			for (var I = 0; I < source.Length; ++I)
			{
				if (Equals(source, separator, I))
				{
					Part = new byte[I - Index];
					Array.Copy(source, Index, Part, 0, Part.Length);
					Parts.Add(Part);
					Index = I + separator.Length;
					I += separator.Length - 1;
				}
			}
			Part = new byte[source.Length - Index];
			Array.Copy(source, Index, Part, 0, Part.Length);
			Parts.Add(Part);
			return Parts.ToArray();
		}
		private static bool Equals(byte[] source, byte[] separator, int index)
		{
			for (int i = 0; i < separator.Length; ++i)
				if (index + i >= source.Length || source[index + i] != separator[i])
					return false;
			return true;
		}
	}
}