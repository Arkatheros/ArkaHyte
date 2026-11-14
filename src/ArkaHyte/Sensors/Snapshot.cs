namespace ArkaHyte.Sensors;

// Shapes serialized to the web UI (camelCase); null values render as "--"

/// <param name="ClockMhz">Average effective clock (HWiNFO).</param>
/// <param name="Voltage">Core voltage in volts (HWiNFO).</param>
/// <param name="PptLimit">Share of the package power limit in use, in percent (HWiNFO).</param>
/// <param name="ActiveCores">Average number of active cores (HWiNFO).</param>
/// <param name="CoreClocksMhz">Clock of each core, in core order (HWiNFO); empty when not reported.</param>
/// <param name="ThreadLoads">Load of each logical processor in percent (Windows).</param>
public sealed record CpuData(
	double? Load,
	double? Temp,
	double? Power,
	double? ClockMhz = null,
	double? Voltage = null,
	double? PptLimit = null,
	double? ActiveCores = null,
	IReadOnlyList<double>? CoreClocksMhz = null,
	IReadOnlyList<double>? ThreadLoads = null);

/// <param name="Fps">Framerate of the running game; null or 0 when none (the widget then hides).</param>
/// <param name="FpsLow">"1 % low" framerate when HWiNFO reports it (the page computes a fallback).</param>
/// <param name="FrameTimeMs">Frame time when HWiNFO reports it (the page derives it from the FPS otherwise).</param>
public sealed record GpuData(
	double? Load,
	double? Temp,
	double? Power,
	double? VramPercent,
	double VramTotalGb,
	double? Fps,
	double? FpsLow,
	double? FrameTimeMs);

/// <summary>Game being played (recognised by Windows), shown in the game view.</summary>
/// <param name="RefreshHz">Refresh rate of the monitor the game was seen on: top of the FPS scale.</param>
/// <param name="Foreground">The game has the focus; false while another window does (Alt+Tab, desktop).</param>
public sealed record GameData(string Name, int? RefreshHz, bool Foreground = true);

public sealed record RamData(double UsedGb, double TotalGb);

/// <param name="Disk">Physical disk number: drives sharing it are grouped under one temperature.</param>
public sealed record DiskData(string Name, string Label, double UsedGb, double TotalGb, double? Temp, int? Disk);

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
	string? ArtKey,
	// The player accepts a new position (the progress bar can then be dragged)
	bool CanSeek = false);

public sealed record Snapshot(
	CpuData Cpu,
	GpuData Gpu,
	RamData Ram,
	IReadOnlyList<DiskData> Disks,
	NetworkData Network,
	MediaData? Media,
	GameData? Game)
{
	public string Type => "snapshot";
}
