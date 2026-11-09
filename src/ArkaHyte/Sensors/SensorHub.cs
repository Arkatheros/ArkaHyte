namespace ArkaHyte.Sensors;

/// <summary>
/// Samples every source once per second on a background thread and publishes a full snapshot.
/// Each source is isolated so a failing one (HWiNFO closed, Wi-Fi off...) only blanks its own widgets.
/// </summary>
public sealed class SensorHub : IDisposable
{
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

	private readonly AppSettings _settings;
	private readonly HwInfoReader _hwInfo = new();
	private readonly SystemMetrics _system = new();
	private readonly WifiReader _wifi = new();
	private readonly NetworkMonitor _network;
	private readonly MediaMonitor _media = new();
	private readonly CancellationTokenSource _stop = new();
	private Task? _loop;

	public event Action<Snapshot>? SnapshotReady;
	public event Action<string, string>? CoverChanged;

	public Snapshot? Latest { get; private set; }
	public (string Key, string DataUrl)? LatestCover { get; private set; }
	public MediaMonitor Media => _media;
	public HwInfoReader HwInfo => _hwInfo;

	public SensorHub(AppSettings settings)
	{
		_settings = settings;
		_network = new NetworkMonitor(settings.Network);
		_media.CoverChanged += (key, dataUrl) =>
		{
			LatestCover = (key, dataUrl);
			CoverChanged?.Invoke(key, dataUrl);
		};
	}

	public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

	private async Task RunAsync(CancellationToken token)
	{
		using var timer = new PeriodicTimer(Interval);
		do
		{
			try
			{
				var snapshot = await SampleAsync();
				Latest = snapshot;
				SnapshotReady?.Invoke(snapshot);
			}
			catch (Exception exception)
			{
				Log.Error("Sampling failed", exception);
			}
		}
		while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
	}

	public async Task<Snapshot> SampleAsync()
	{
		_hwInfo.Refresh();
		var hw = _settings.HwInfo;

		var cpu = new CpuData(
			Safe(_system.ReadCpuLoad),
			_hwInfo.Get(hw.CpuTemp),
			_hwInfo.Get(hw.CpuPower));

		var gpu = new GpuData(
			_hwInfo.Get(hw.GpuLoad),
			_hwInfo.Get(hw.GpuTemp),
			_hwInfo.Get(hw.GpuPower),
			_hwInfo.Get(hw.GpuVramPercent),
			_settings.Hardware.VramTotalGb);

		var disks = _settings.Drives
			.Select(d => SystemMetrics.ReadDrive(d.Name, _hwInfo.Get(d.Temp)))
			.OfType<DiskData>()
			.ToList();

		var wifi = Safe(_wifi.Read);
		var network = _network.Read(wifi);

		MediaData? media = null;
		try
		{
			media = await _media.ReadAsync();
		}
		catch (Exception exception)
		{
			Log.Error("Media session read failed", exception);
		}

		return new Snapshot(cpu, gpu, SystemMetrics.ReadMemory(), disks, network, media);
	}

	private static T? Safe<T>(Func<T?> read)
	{
		try
		{
			return read();
		}
		catch (Exception exception)
		{
			Log.Error($"Sensor read failed ({read.Method.Name})", exception);
			return default;
		}
	}

	public void Dispose()
	{
		_stop.Cancel();
		try
		{
			_loop?.Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
			// Cancellation surfaces here; nothing left to clean up
		}
		_wifi.Dispose();
		_network.Dispose();
		_stop.Dispose();
	}
}
