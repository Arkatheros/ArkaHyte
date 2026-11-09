using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ArkaHyte.Sensors;

/// <summary>
/// Throughput of the internet-facing adapter, a periodic latency probe, and the resulting
/// connectivity state (a ping answer from either host within the grace period means online).
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
	private static readonly string[] VirtualAdapterHints = ["virtual", "hyper-v", "tap-", "vpn", "loopback", "wan miniport", "bluetooth"];

	private const double PingIntervalSeconds = 2;
	private const int PingTimeoutMs = 1000;
	// Three missed probes before raising the alarm, so a single dropped packet stays silent
	private const double InternetGraceSeconds = 6;

	private readonly NetworkSettings _settings;
	private readonly Ping _ping = new();
	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private NetworkInterface? _adapter;
	private long _lastReceived;
	private long _lastSent;
	private double _lastSampleSeconds;
	private double? _pingMs;
	private int _pingInFlight;
	private double _lastPingSeconds = double.MinValue;
	private long _lastPingSuccessMs;
	private double? _downSinceSeconds;

	public NetworkMonitor(NetworkSettings settings)
	{
		_settings = settings;
	}

	public NetworkData Read(WifiData? wifi)
	{
		var adapter = ResolveAdapter();
		double? down = null;
		double? up = null;
		double? linkMbps = null;

		if (adapter is not null)
		{
			try
			{
				var stats = adapter.GetIPStatistics();
				var now = _clock.Elapsed.TotalSeconds;
				var elapsed = now - _lastSampleSeconds;
				if (_lastSampleSeconds > 0 && elapsed > 0)
				{
					down = Math.Max(0, (stats.BytesReceived - _lastReceived) / elapsed);
					up = Math.Max(0, (stats.BytesSent - _lastSent) / elapsed);
				}
				_lastReceived = stats.BytesReceived;
				_lastSent = stats.BytesSent;
				_lastSampleSeconds = now;
				linkMbps = adapter.Speed > 0 ? adapter.Speed / 1_000_000d : null;
			}
			catch (NetworkInformationException)
			{
				_adapter = null;
				adapter = null;
			}
		}

		ProbeLatency();
		var (status, offlineSec) = EvaluateStatus(adapter is not null);
		return new NetworkData(adapter?.Name, down, up, linkMbps, status == "online" ? _pingMs : null, wifi, status, offlineSec);
	}

	private (string Status, double? OfflineSec) EvaluateStatus(bool hasAdapter)
	{
		var now = _clock.Elapsed.TotalSeconds;
		var sinceLastAnswer = now - Volatile.Read(ref _lastPingSuccessMs) / 1000d;
		var status = !hasAdapter
			? "offline"
			: sinceLastAnswer > InternetGraceSeconds ? "no-internet" : "online";

		if (status == "online")
		{
			_downSinceSeconds = null;
			return (status, null);
		}
		_downSinceSeconds ??= now;
		return (status, now - _downSinceSeconds.Value);
	}

	private NetworkInterface? ResolveAdapter()
	{
		if (_adapter is { OperationalStatus: OperationalStatus.Up })
		{
			return _adapter;
		}

		var candidates = NetworkInterface.GetAllNetworkInterfaces()
			.Where(n => n.OperationalStatus == OperationalStatus.Up
				&& n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel);

		_adapter = string.IsNullOrWhiteSpace(_settings.Adapter)
			? candidates
				.Where(n => !VirtualAdapterHints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase)))
				.Where(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
				.OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
				.FirstOrDefault()
			: candidates.FirstOrDefault(n => n.Name.Equals(_settings.Adapter, StringComparison.OrdinalIgnoreCase));

		// Reset counters so the first delta on a new adapter is not a huge spike
		_lastSampleSeconds = 0;
		return _adapter;
	}

	// Fire-and-forget probe every 2 s; the latest result is reported on the next refresh
	private void ProbeLatency()
	{
		var now = _clock.Elapsed.TotalSeconds;
		if (now - _lastPingSeconds < PingIntervalSeconds || Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0)
		{
			return;
		}
		_lastPingSeconds = now;
		_ = Task.Run(async () =>
		{
			try
			{
				var roundTrip = await TryPingAsync(_settings.PingHost) ?? await TryPingAsync(_settings.FallbackPingHost);
				_pingMs = roundTrip;
				if (roundTrip is not null)
				{
					Volatile.Write(ref _lastPingSuccessMs, _clock.ElapsedMilliseconds);
				}
			}
			finally
			{
				Volatile.Write(ref _pingInFlight, 0);
			}
		});
	}

	private async Task<double?> TryPingAsync(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			return null;
		}
		try
		{
			var reply = await _ping.SendPingAsync(host, PingTimeoutMs);
			return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
		}
		catch (Exception exception) when (exception is PingException or InvalidOperationException)
		{
			return null;
		}
	}

	public void Dispose() => _ping.Dispose();
}
