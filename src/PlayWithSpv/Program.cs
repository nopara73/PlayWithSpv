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
		// TestNet addresses, first time used
		// 2Mz3BiReit6sNrSh9EMuhwUnhtqf2B35HpN, 1088037
		// mwiSUHLGngZd849Sz3TE6kRb7fHjJCuwKe, 1088031
		// muE3Z5Lhdk3WerqVevH49htmV96HJu4RLJ, 1088051

		private static readonly Network Network = Network.TestNet;
		private const int WalletCreationHeight = 1087900; // 451900;

		public static SemaphoreSlim SemaphoreSave = new SemaphoreSlim(1, 1);
		public static SemaphoreSlim SemaphoreSaveFullChain = new SemaphoreSlim(1, 1);
		private static string _addressManagerFilePath;
		private static string _spvChainFilePath;
		private static string _partialChainFilePath;
		private const string SpvFolderPath = "Spv";
		private static LookaheadBlockPuller BlockPuller;
		private static NodeConnectionParameters _connectionParameters;

		public static void Main(string[] args)
		{
			Directory.CreateDirectory(SpvFolderPath);
			_addressManagerFilePath = Path.Combine(SpvFolderPath, $"AddressManager{Network}.dat");
			_spvChainFilePath = Path.Combine(SpvFolderPath, $"LocalSpvChain{Network}.dat");
			_partialChainFilePath = Path.Combine(SpvFolderPath, $"LocalFullChain{Network}.dat");

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
			var t3 = PeriodicSaveAsync(10000, cts.Token);
			var t4 = BlockPullerJobAsync(cts.Token);

			Console.WriteLine("Press a key to exit...");
			Console.ReadKey();
			Console.WriteLine("Exiting...");

			cts.Cancel();
			Task.WhenAll(t1, t2, t3, t4).Wait();
			_nodes.Dispose();
			SaveAsync().Wait();
		}

		private static async Task PeriodicSaveAsync(int delay, CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested) return;
				await SaveAsync().ConfigureAwait(false);
				await Task.Delay(delay).ConfigureAwait(false);
			}
		}
		private static async Task SaveAsync()
		{
			// Check if there is something to save
			bool fileOk = true;
			var c = new ConcurrentChain(Network);
			await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
			{
				c.Load(File.ReadAllBytes(_spvChainFilePath));
			}
			catch
			{
				fileOk = false;
			}
			finally
			{
				SemaphoreSave.Release();
			}

			// If there is nothing to save don't save (can be improved by only saving what needs to be)
			var sameTip = c.SameTip(LocalSpvChain);
			bool saveSpvChain = !(fileOk && sameTip);

			// If there is something to save then save
			await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
			{
				await Task.Run(() =>
				{
					AddressManager.SavePeerFile(_addressManagerFilePath, Network);

					if(saveSpvChain)
					{
						using(var fs = File.Open(_spvChainFilePath, FileMode.Create))
						{
							LocalSpvChain.WriteTo(fs);
						}
						Console.WriteLine($"{nameof(LocalSpvChain)} saved");
					}

					LocalPartialChain.Flush(_partialChainFilePath);
					Console.WriteLine($"{nameof(LocalPartialChain)} saved");
				}).ConfigureAwait(false);
			}
			finally
			{
				SemaphoreSave.Release();
			}
		}

		private static NodesGroup _nodes;

		private static int prevNodeCount = -1;
		private static async Task ReportConnectedNodeCountAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested) return;

				var nodeCount = _nodes.ConnectedNodes.Count;
				if(prevNodeCount != nodeCount)
				{
					prevNodeCount = nodeCount;
					Console.WriteLine($"Number of connected nodes: {nodeCount}");
				}
				await Task.Delay(100).ConfigureAwait(false);
			}
		}
		private static int prevSpvHeight = -1;
		private static async Task ReportHeightAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested) return;

				var height = LocalSpvChain.Height;
				if (prevSpvHeight != height)
				{
					prevSpvHeight = height;
					Console.WriteLine($"Height of local SPV chain:  {height}");
				}

				await Task.Delay(3000).ConfigureAwait(false);
			}
		}

		private static async Task BlockPullerJobAsync(CancellationToken ctsToken)
		{
			while(true)
			{
				if(ctsToken.IsCancellationRequested) return;
				if(LocalSpvChain.Height < WalletCreationHeight)
				{
					await Task.Delay(1000).ConfigureAwait(false);
					continue;
				}

				int height;
				if(LocalPartialChain.BlockCount == 0)
				{
					height = WalletCreationHeight;
				}
				else if(LocalSpvChain.Height <= LocalPartialChain.BestHeight)
				{
					await Task.Delay(100).ConfigureAwait(false);
					continue;
				}
				else
				{
					height = LocalPartialChain.BestHeight + 1;
				}

				var chainedBlock = LocalSpvChain.GetBlock(height);
				BlockPuller.SetLocation(new ChainedBlock(chainedBlock.Previous.Header, chainedBlock.Previous.Height));
				Block block = null;
				const int timeoutSec = 60;
				CancellationTokenSource ctsBlockDownload = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
				try
				{
					block = await Task.Run(() => BlockPuller.NextBlock(ctsBlockDownload.Token)).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					if (ctsToken.IsCancellationRequested) return;
					Console.WriteLine($"Failed to download block {chainedBlock.Height} within {timeoutSec} seconds. Retry");
					continue;
				}
				if(block == null)
				{
					Console.WriteLine("Downloaded block is null. Retry");
					continue;
				}

				LocalPartialChain.Add(chainedBlock, block);

				Console.WriteLine($"Full blocks left to download:  {LocalSpvChain.Height - LocalPartialChain.BestHeight}");
			}
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

		private static PartialBlockChain _localMerkleChain = null;
		private static PartialBlockChain LocalPartialChain
		{
			get
			{
				if(_localMerkleChain != null) return _localMerkleChain;

				_localMerkleChain = new PartialBlockChain(Network);
				SemaphoreSave.Wait();
				try
				{
					_localMerkleChain.Load(_spvChainFilePath);
				}
				catch
				{
					// ignored
				}
				finally
				{
					SemaphoreSave.Release();
				}

				return _localMerkleChain;
			}
		}
	}
}
