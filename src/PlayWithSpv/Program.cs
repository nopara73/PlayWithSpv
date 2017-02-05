using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using NBitcoin.SPV;

namespace PlayWithSpv
{
	public class Program
	{
		public static SemaphoreSlim SemaphoreSave = new SemaphoreSlim(1, 1);
		private static string _addressManagerFilePath;
		private static string _chainFilePath;
		private static string _trackerFilePath;
		private const string SpvFolderPath = "Spv";

		public static void Main(string[] args)
		{
			Directory.CreateDirectory(SpvFolderPath);
			_addressManagerFilePath = Path.Combine(SpvFolderPath, $"AddressManager{Network.TestNet}.dat");
			_chainFilePath = Path.Combine(SpvFolderPath, $"LocalChain{Network.TestNet}.dat");
			_trackerFilePath = Path.Combine(SpvFolderPath, $"Tracker{Network.TestNet}.dat");

			_connectionParameters = new NodeConnectionParameters();

			//So we find nodes faster
			_connectionParameters.TemplateBehaviors.Add(new AddressManagerBehavior(AddressManager));
			//So we don't have to load the chain each time we start
			_connectionParameters.TemplateBehaviors.Add(new ChainBehavior(LocalChain));
			//Tracker knows which scriptPubKey and outpoints to track, it monitors all your wallets at the same
			_connectionParameters.TemplateBehaviors.Add(new TrackerBehavior(Tracker));

			_nodes = new NodesGroup(Network.TestNet, _connectionParameters,
				new NodeRequirement
				{
					RequiredServices = NodeServices.Network, // Needed for SPV
				})
			{
				MaximumNodeConnection = 8,
				AllowSameGroup = false,
			};

			Console.WriteLine("Start connecting to nodes...");
			_nodes.Connect();

			CancellationTokenSource cts = new CancellationTokenSource();

			var t1 = ReportConnectedNodeCountAsync(cts.Token);
			var t2 = ReportHeightAsync(cts.Token);
			var t3 = PeriodicSaveAsync(10000, cts.Token);

			Console.WriteLine("Press a key to exit...");
			Console.ReadKey();
			Console.WriteLine("Exiting...");

			cts.Cancel();
			Task.WhenAll(t1, t2, t3).Wait();
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
			bool filesOk = true;
			var c = new ConcurrentChain(Network.TestNet);
			await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
			{
				c.Load(File.ReadAllBytes(_chainFilePath));
			}
			catch
			{
				filesOk = false;
			}
			finally
			{
				SemaphoreSave.Release();
			}

			// If there is nothing to save don't save (can be improved by only saving what needs to be)
			var heightEquals = c.Height == LocalChain.Height;
			bool saveChain = !(filesOk && heightEquals);

			// If there is something to save then save
			await SemaphoreSave.WaitAsync().ConfigureAwait(false);
			try
			{
				await Task.Run(() =>
				{
					AddressManager.SavePeerFile(_addressManagerFilePath, Network.TestNet);

					using(var fs = File.Open(_trackerFilePath, FileMode.Create))
					{
						Tracker.Save(fs);
					}
					if(saveChain)
					{
						using(var fs = File.Open(_chainFilePath, FileMode.Create))
						{
							LocalChain.WriteTo(fs);
						}
						Console.WriteLine("Chain saved");
					}
				}).ConfigureAwait(false);
			}
			finally
			{
				SemaphoreSave.Release();
			}
		}

		private static NodesGroup _nodes;

		private static int currentNodeCount = 0;
		private static async Task ReportConnectedNodeCountAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested) return;

				var nodeCount = _nodes.ConnectedNodes.Count;
				if(currentNodeCount != nodeCount)
				{
					currentNodeCount = nodeCount;
					Console.WriteLine($"Number of connected nodes: {nodeCount}");
				}
				await Task.Delay(100).ConfigureAwait(false);
			}
		}
		private static int currentHeight = 0;
		private static async Task ReportHeightAsync(CancellationToken ctsToken)
		{
			while (true)
			{
				if (ctsToken.IsCancellationRequested) return;

				var height = LocalChain.Height;
				if (currentHeight != height)
				{
					currentHeight = height;
					Console.WriteLine($"Height of local chain:  {height}");
				}
				await Task.Delay(3000).ConfigureAwait(false);
			}
		}

		private static NodeConnectionParameters _connectionParameters;

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
		private static ConcurrentChain LocalChain
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
				var chain = new ConcurrentChain(Network.TestNet);
				SemaphoreSave.Wait();
				try
				{
					chain.Load(File.ReadAllBytes(_chainFilePath));
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
		private static Tracker Tracker
		{
			get
			{
				if(_connectionParameters != null)
					foreach(var behavior in _connectionParameters.TemplateBehaviors)
					{
						var trackerBehavior = behavior as TrackerBehavior;
						if(trackerBehavior != null)
							return trackerBehavior.Tracker;
					}
				SemaphoreSave.Wait();
				try
				{
					using(var fs = File.OpenRead(_trackerFilePath))
					{
						return Tracker.Load(fs);
					}
				}
				catch
				{
					return new Tracker();
				}
				finally
				{
					SemaphoreSave.Release();
				}
			}
		}
	}
}
