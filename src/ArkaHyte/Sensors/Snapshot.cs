namespace ArkaHyte.Sensors;

// Shapes serialized to the web UI (camelCase); null values render as "--"

public sealed record CpuData(double? Load, double? Temp, double? Power);

/// <param name="Fps">Framerate of the running game; null or 0 when none (the widget then hides).</param>
public sealed record GpuData(double? Load, double? Temp, double? Power, double? VramPercent, double VramTotalGb, double? Fps);

public sealed record RamData(double UsedGb, double TotalGb);

public sealed record DiskData(string Name, string Label, double UsedGb, double TotalGb, double? Temp);

public sealed record WifiData(
	string Ssid,
	int Signal,
	int? Rssi,
	int? Channel,
	double? BandGhz,
	string Standard,
	string Generation,
	string Security,
	double LinkMbps);

/// <param name="Status">"online", "no-internet" (link up, no ping answer) or "offline" (no usable adapter).</param>
/// <param name="OfflineSec">How long the connection has been down, null while online.</param>
public sealed record NetworkData(
	string? Adapter,
	double? DownBytesPerSec,
	double? UpBytesPerSec,
	double? LinkMbps,
	double? PingMs,
	WifiData? Wifi,
	string Status,
	double? OfflineSec);

public sealed record MediaData(
	string Source,
	string Kind,
	string Title,
	string Artist,
	double? PositionSec,
	double? DurationSec,
	bool Playing,
	bool Live,
	string? ArtKey);

public sealed record Snapshot(
	CpuData Cpu,
	GpuData Gpu,
	RamData Ram,
	IReadOnlyList<DiskData> Disks,
	NetworkData Network,
	MediaData? Media)
{
	public string Type => "snapshot";
}
