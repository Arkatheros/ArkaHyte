using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkaHyte;

public sealed record DisplaySettings(int Width = 682, int Height = 2560);

public sealed record HardwareSettings(
	string CpuMaker = "",
	string CpuModel = "",
	string GpuMaker = "",
	string GpuModel = "",
	double VramTotalGb = 24);

/// <summary>HWiNFO Gadget entry selector; both parts accept '*' wildcards and ignore case.</summary>
public sealed record SensorSelector(string Sensor = "*", string Label = "");

public sealed record HwInfoSettings(
	SensorSelector? CpuTemp = null,
	SensorSelector? CpuPower = null,
	SensorSelector? GpuTemp = null,
	SensorSelector? GpuPower = null,
	SensorSelector? GpuLoad = null,
	SensorSelector? GpuVramPercent = null);

public sealed record DriveSettings(string Name, SensorSelector? Temp = null);

public sealed record NetworkSettings(string Adapter = "", string PingHost = "1.1.1.1", string FallbackPingHost = "8.8.8.8");

/// <summary>
/// Dock button. Icon is either a built-in icon name (gamepad, music, chat, pulse, globe, folder,
/// video, code, headset, camera) or a path to an image (.png/.jpg/.svg/.webp/.ico) or to an
/// executable/DLL whose embedded icon is extracted. Empty: built-in "app" icon.
/// </summary>
public sealed record ShortcutSettings(string Label, string Target, string? Icon = null, string? Arguments = null);

public sealed record AppSettings(
	DisplaySettings Display,
	HardwareSettings Hardware,
	HwInfoSettings HwInfo,
	IReadOnlyList<DriveSettings> Drives,
	NetworkSettings Network,
	IReadOnlyList<ShortcutSettings> Shortcuts,
	string CpuClickTarget = "taskmgr.exe")
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static AppSettings Load(string path)
	{
		var settings = File.Exists(path)
			? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
			: null;

		return settings ?? new AppSettings(
			new DisplaySettings(),
			new HardwareSettings(),
			new HwInfoSettings(),
			[new DriveSettings("C:")],
			new NetworkSettings(),
			[new ShortcutSettings("Tâches", "taskmgr.exe", "pulse")]);
	}
}
