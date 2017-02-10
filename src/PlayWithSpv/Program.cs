using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using Stratis.Bitcoin.BlockPulling;

namespace PlayWithSpv
{
	public class Program
	{
		private static readonly Network Network = Network.Main;
		private const int WalletCreationHeight = 451900; // 1087900;

		public static SemaphoreSlim SemaphoreSave = new SemaphoreSlim(1, 1);
		public static SemaphoreSlim SemaphoreSaveFullChain = new SemaphoreSlim(1, 1);
		private static readonly string _addressManagerFilePath = Path.Combine(SpvFolderPath, $"AddressManager{Network}.dat");
		private static readonly string _spvChainFilePath = Path.Combine(SpvFolderPath, $"LocalSpvChain{Network}.dat");
		private static readonly string _partialChainFolderPath = Path.Combine(SpvFolderPath, $"LocalPartialChain{Network}");
		private const string SpvFolderPath = "Spv";
		private static LookaheadBlockPuller BlockPuller;
		private static NodeConnectionParameters _connectionParameters;

		public static void Main(string[] args)
		{
			Directory.CreateDirectory(SpvFolderPath);

			// TestNet addresses, first time used
			//var a1 = BitcoinAddress.Create("2Mz3BiReit6sNrSh9EMuhwUnhtqf2B35HpN"); // testnet, 1088037
			//var a2 = BitcoinAddress.Create("mwiSUHLGngZd849Sz3TE6kRb7fHjJCuwKe"); // testnet, 1088031
			//var a3 = BitcoinAddress.Create("muE3Z5Lhdk3WerqVevH49htmV96HJu4RLJ"); // testnet, 1088031
			//LocalPartialChain.Track(a1.ScriptPubKey);
			//LocalPartialChain.Track(a2.ScriptPubKey);
			//LocalPartialChain.Track(a3.ScriptPubKey);
			//Console.WriteLine($"Tracking {a1}");
			//Console.WriteLine($"Tracking {a2}");
			//Console.WriteLine($"Tracking {a3}");

			_connectionParameters = new NodeConnectionParameters();

			//So we find nodes faster
			_connectionParameters.TemplateBehaviors.Add(new AddressManagerBehavior(AddressManager));
			//So we don't have to load the chain each time we start
			_connectionParameters.TemplateBehaviors.Add(new ChainBehavior(LocalSpvChain));

			_nodes = new NodesGroup(Network, _connectionParameters,
				new NodeRequirement
				{
					RequiredServices = NodeServices.Network,
					MinVersion = ProtocolVersion.SENDHEADERS_VERSION
				});
			var bp = new NodesBlockPuller(LocalSpvChain, _nodes.ConnectedNodes);
			_connectionParameters.TemplateBehaviors.Add(new NodesBlockPuller.NodesBlockPullerBehavior(bp));
			_nodes.NodeConnectionParameters = _connectionParameters;
			BlockPuller = (LookaheadBlockPuller)bp;

			Console.WriteLine("Start connecting to nodes...");
			_nodes.Connect();

			CancellationTokenSource cts = new CancellationTokenSource();

			var t1 = ReportConnectedNodeCountAsync(cts.Token);
			var t2 = ReportHeightAsync(cts.Token);
			var t3 = PeriodicSaveAsync(TimeSpan.FromMinutes(3), cts.Token);
			var t4 = BlockPullerJobAsync(cts.Token);
			var t5 = ReportTransactionsWhenAllBlocksDownAsync(cts.Token);
			ReportTransactions();

			Console.WriteLine("Press a key to exit...");
			Console.ReadKey();
			Console.WriteLine("Exiting...");

			cts.Cancel();
			Task.WhenAll(t1, t2, t3, t4, t5).Wait();

			SaveAllAsync().Wait();

			_nodes.Dispose();
		}

		private static async Task ReportTransactionsWhenAllBlocksDownAsync(CancellationToken ctsToken)
		{
			while(LocalPartialChain.BestHeight != LocalSpvChain.Height)
			{
				if (ctsToken.IsCancellationRequested)
				{
					Console.WriteLine($"{nameof(ReportTransactionsWhenAllBlocksDownAsync)} is stopped.");
					return;
				}
				await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
			}
			ReportTransactions();
		}
		private static void ReportTransactions()
		{
			if(LocalPartialChain.TrackedTransactions.Count == 0)
			{
				Console.WriteLine("No transactions to report.");
				return;
			}
			foreach(var tx in LocalPartialChain.TrackedTransactions)
			{
				Console.WriteLine("Height\tTxId");
				Console.WriteLine($"{tx.Value}\t{tx.Key}");
			}
		}

		private static async Task PeriodicSaveAsync(TimeSpan delay, CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested)
				{
					Console.WriteLine($"{nameof(PeriodicSaveAsync)} is stopped.");
					return;
				}
				await SaveAllAsync().ConfigureAwait(false);
				await Task.Delay(delay, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
			}
		}
		private static async Task SaveAllAsync()
		{
			await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
			{
				AddressManager.SavePeerFile(_addressManagerFilePath, Network);
				SaveSpvChain();
			}
			finally
			{
				SemaphoreSave.Release();
			}

			LocalPartialChain.SaveAsync(_partialChainFolderPath).Wait();
			Console.WriteLine($"{nameof(LocalPartialChain)} saved");
		}
		private static void SaveSpvChain()
		{
			using (var fs = File.Open(_spvChainFilePath, FileMode.Create))
			{
				LocalSpvChain.WriteTo(fs);
			}
			Console.WriteLine($"{nameof(LocalSpvChain)} saved");
		}

		private static NodesGroup _nodes;

		private static int prevNodeCount = -1;
		private static async Task ReportConnectedNodeCountAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if(ctsToken.IsCancellationRequested)
				{
					Console.WriteLine($"{nameof(ReportConnectedNodeCountAsync)} is stopped.");
					return;
				}

				var nodeCount = _nodes.ConnectedNodes.Count;
				if(prevNodeCount != nodeCount)
				{
					prevNodeCount = nodeCount;
					Console.WriteLine($"Number of connected nodes: {nodeCount}");
				}
				await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
			}
		}
		private static int _prevSpvHeight = -1;
		private static int _prevPartialHeight = -1;
		private static async Task ReportHeightAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested)
				{
					Console.WriteLine($"{nameof(ReportHeightAsync)} is stopped.");
					return;
				}

				var spvHeight = LocalSpvChain.Height;
				if (_prevSpvHeight != spvHeight)
				{
					_prevSpvHeight = spvHeight;
					Console.WriteLine($"Height of local SPV chain:  {spvHeight}");
				}

				var partialHeight = LocalPartialChain.BestHeight;
				if (_prevPartialHeight != partialHeight)
				{
					_prevPartialHeight = partialHeight;
					Console.WriteLine($"Height of local Partial chain:  {partialHeight}");
				}

				await Task.Delay(3000, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
			}
		}

		private static int timeoutDownSec = 10;
		private static async Task BlockPullerJobAsync(CancellationToken ctsToken)
		{
			while(true)
			{
				if (ctsToken.IsCancellationRequested)
				{
					Console.WriteLine($"{nameof(BlockPullerJobAsync)} is stopped.");
					return;
				}

				if (LocalSpvChain.Height < WalletCreationHeight)
				{
					await Task.Delay(1000, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
					continue;
				}

				int height;
				if(LocalPartialChain.BlockCount == 0)
				{
					height = WalletCreationHeight;
				}
				else if(LocalSpvChain.Height <= LocalPartialChain.BestHeight)
				{
					await Task.Delay(100, ctsToken).ContinueWith(tsk => { }).ConfigureAwait(false);
					continue;
				}
				else
				{
					height = LocalPartialChain.BestHeight + 1;
				}

				var chainedBlock = LocalSpvChain.GetBlock(height);
				BlockPuller.SetLocation(new ChainedBlock(chainedBlock.Previous.Header, chainedBlock.Previous.Height));
				Block block = null;
				CancellationTokenSource ctsBlockDownload = CancellationTokenSource.CreateLinkedTokenSource(
					new CancellationTokenSource(TimeSpan.FromSeconds(timeoutDownSec)).Token,
					ctsToken);
				try
				{
					block = await Task.Run(() => BlockPuller.NextBlock(ctsBlockDownload.Token)).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					if (ctsToken.IsCancellationRequested) return;
					Console.WriteLine($"Failed to download block {chainedBlock.Height} within {timeoutDownSec} seconds. Retry");
					timeoutDownSec = timeoutDownSec * 2; // adjust to the network speed
					continue;
				}

				//reorg test
				//if(new Random().Next(100) >= 60) block = null;

				if (block == null) // then reorg happened
				{
					Reorg();
					continue;
				}

				LocalPartialChain.Add(chainedBlock.Height, block);

				// check if chains are in sync, to be sure
				var bh = LocalPartialChain.BestHeight;
				for(int i = bh; i > bh - 6; i--)
				{
					if (!LocalPartialChain.Chain[i].MerkleProof.Header.GetHash()
					.Equals(LocalSpvChain.GetBlock(i).Header.GetHash()))
					{
						// something worng, reorg
						Reorg();
					}
				}

				Console.WriteLine($"Full blocks left to download:  {LocalSpvChain.Height - LocalPartialChain.BestHeight}");
			}
		}

		private static void Reorg()
		{
			Console.WriteLine($"Reorg detected at {LocalSpvChain.Height}. Handling reog.");
			LocalSpvChain.SetTip(LocalSpvChain.Tip.Previous);
			LocalPartialChain.ReorgOne();
		}

		private static AddressManager AddressManager
		{
			get
			{
				if(_connectionParameters != null)
				{
					foreach(var behavior in _connectionParameters.TemplateBehaviors)
					{
						var addressManagerBehavior = behavior as AddressManagerBehavior;
						if(addressManagerBehavior != null)
							return addressManagerBehavior.AddressManager;
					}
				}
				SemaphoreSave.Wait();
				try
				{
					return AddressManager.LoadPeerFile(_addressManagerFilePath);
				}
				catch
				{
					return new AddressManager();
				}
				finally
				{
					SemaphoreSave.Release();
				}
			}
		}
		private static ConcurrentChain LocalSpvChain
		{
			get
			{
				if(_connectionParameters != null)
					foreach(var behavior in _connectionParameters.TemplateBehaviors)
					{
						var chainBehavior = behavior as ChainBehavior;
						if(chainBehavior != null)
							return chainBehavior.Chain;
					}
				var chain = new ConcurrentChain(Network);
				SemaphoreSave.Wait();
				try
				{
					chain.Load(File.ReadAllBytes(_spvChainFilePath));
				}
				catch
				{
					// ignored
				}
				finally
				{
					SemaphoreSave.Release();
				}

				return chain;
			}
		}

		private static PartialBlockChain _localPartialChain = null;
		private static PartialBlockChain LocalPartialChain => GetLocalPartialChainAsync().Result;

		// This async getter is for clean exception handling
		private static async Task<PartialBlockChain> GetLocalPartialChainAsync()
		{
			if (_localPartialChain != null) return _localPartialChain;

			_localPartialChain = new PartialBlockChain(Network);
			try
			{
				await _localPartialChain.LoadAsync(_partialChainFolderPath).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Console.WriteLine("Blockchain synchronisation is needed. Reason:");
				Console.WriteLine(ex.Message);
				_localPartialChain = new PartialBlockChain(Network);
			}

			return _localPartialChain;
		}
	}
}
