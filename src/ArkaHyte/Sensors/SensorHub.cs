namespace ArkaHyte.Sensors;

/// <summary>
/// Samples every source once per second on a background thread and publishes a full snapshot.
/// Each source is isolated so a failing one (HWiNFO closed, Wi-Fi off...) only blanks its own widgets.
/// </summary>
public sealed class SensorHub : IDisposable
{
	private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
	private const int CollectEverySamples = 30;

	// Replaced by UpdateSettings when the on-screen settings change a sensor; read once per sample
	private volatile AppSettings _settings;
	private readonly HwInfoReader _hwInfo = new();
	private readonly SystemMetrics _system = new();
	private readonly WifiReader _wifi = new();
	private readonly NetworkMonitor _network;
	private readonly MediaMonitor _media = new();
	private readonly CancellationTokenSource _stop = new();
	// Partition layout does not change while running: looked up once per drive
	private readonly Dictionary<string, int?> _diskNumbers = new(StringComparer.OrdinalIgnoreCase);
	private Task? _loop;

	public event Action<Snapshot>? SnapshotReady;
	public event Action<string, string>? CoverChanged;

	public Snapshot? Latest { get; private set; }
	public (string Key, string DataUrl)? LatestCover { get; private set; }
	public MediaMonitor Media => _media;
	public HwInfoReader HwInfo => _hwInfo;

	/// <summary>Skips sampling while nobody can see the display (monitors off, session locked).</summary>
	public bool Paused
	{
		get => _paused;
		set => _paused = value;
	}
	private volatile bool _paused;

	public SensorHub(AppSettings settings)
	{
		_settings = settings;
		ForegroundGame.Configure(settings.EffectiveGameView);
		_network = new NetworkMonitor(settings.Network);
		_media.CoverChanged += (key, dataUrl) =>
		{
			LatestCover = (key, dataUrl);
			CoverChanged?.Invoke(key, dataUrl);
		};
	}

	public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

	/// <summary>New sensor choices, taken from the next sample on.</summary>
	public void UpdateSettings(AppSettings settings)
	{
		_settings = settings;
		ForegroundGame.Configure(settings.EffectiveGameView);
	}

	private async Task RunAsync(CancellationToken token)
	{
		using var timer = new PeriodicTimer(Interval);
		var samples = 0;
		do
		{
			if (Paused)
			{
				continue;
			}
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
			if (++samples % CollectEverySamples == 0)
			{
				ReleaseNativeWrappers();
			}
		}
		while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
	}

	/// <summary>
	/// The media session API hands out new WinRT objects at every sample. Their managed wrappers are
	/// tiny but hold native memory until finalised, and this process allocates so little managed
	/// memory that the GC would almost never run: collect explicitly (a ~10 MB heap, a few ms).
	/// </summary>
	private static void ReleaseNativeWrappers()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	public async Task<Snapshot> SampleAsync()
	{
		_hwInfo.Refresh();
		var settings = _settings;
		var hw = settings.HwInfo;

		var cpu = new CpuData(
			Safe(_system.ReadCpuLoad),
			_hwInfo.Get(hw.CpuTemp),
			_hwInfo.Get(hw.CpuPower));

		// PresentMon measures whatever has the focus: only a fullscreen window or a known game counts
		var game = ForegroundGame.Read();
		double? Framerate(SensorSelector? selector) => game.FramerateCounts ? _hwInfo.Get(selector) : null;
		var gpu = new GpuData(
			_hwInfo.Get(hw.GpuLoad),
			_hwInfo.Get(hw.GpuTemp),
			_hwInfo.Get(hw.GpuPower),
			_hwInfo.Get(hw.GpuVramPercent),
			settings.Hardware.VramTotalGb,
			Framerate(hw.GpuFps),
			Framerate(hw.GpuFpsLow),
			Framerate(hw.GpuFrameTime));

		var disks = settings.Drives
			.Select(d => SystemMetrics.ReadDrive(d.Name, _hwInfo.Get(d.Temp), DiskNumber(d.Name)))
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

		return new Snapshot(cpu, gpu, SystemMetrics.ReadMemory(), disks, network, media, game.Game);
	}

	private int? DiskNumber(string driveName)
	{
		if (!_diskNumbers.TryGetValue(driveName, out var number))
		{
			number = HardwareDetector.ReadDiskNumber(driveName);
			_diskNumbers[driveName] = number;
		}
		return number;
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
		_hwInfo.Dispose();
		_network.Dispose();
		_stop.Dispose();
	}
}
