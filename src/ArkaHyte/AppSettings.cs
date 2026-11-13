using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
	SensorSelector? GpuVramPercent = null,
	// Game framerate: HWiNFO's PresentMon sensor (or RTSS), only present while a game runs
	SensorSelector? GpuFps = null,
	// Optional PresentMon values of the game view; computed from the framerate when not chosen
	SensorSelector? GpuFpsLow = null,
	SensorSelector? GpuFrameTime = null);

/// <summary>Display themes: colours, hilt, background and particles (console.css, console.js).</summary>
public static class Themes
{
	public const string Sith = "sith";
	public const string Jedi = "jedi";

	/// <summary>Theme id and the name shown in the tray menu.</summary>
	public static readonly IReadOnlyList<(string Id, string Name)> All = [(Sith, "Sith · sabre rouge"), (Jedi, "Jedi · sabre bleu")];

	public static string Normalize(string? theme) => All.Any(t => t.Id == theme) ? theme! : Sith;
}

/// <summary>Gauge range of a metric and its alert levels: warm tints the value, hot raises the alert.</summary>
public sealed record Threshold(double Min, double Max, double Warm, double Hot);

/// <summary>Per-metric thresholds; a metric left out uses <see cref="Defaults"/>.</summary>
public sealed record ThresholdSettings(
	Threshold? CpuTemp = null,
	Threshold? CpuPower = null,
	Threshold? GpuTemp = null,
	Threshold? GpuPower = null,
	Threshold? DiskTemp = null)
{
	public static readonly ThresholdSettings Defaults = new(
		new Threshold(30, 95, 75, 85),
		new Threshold(0, 160, 120, 150),
		new Threshold(30, 90, 70, 80),
		new Threshold(0, 450, 350, 420),
		new Threshold(0, 80, 50, 60));

	public ThresholdSettings WithDefaults() => new(
		CpuTemp ?? Defaults.CpuTemp,
		CpuPower ?? Defaults.CpuPower,
		GpuTemp ?? Defaults.GpuTemp,
		GpuPower ?? Defaults.GpuPower,
		DiskTemp ?? Defaults.DiskTemp);
}

/// <summary>Weather under the clock (Open-Meteo): the place picked in the setup wizard.</summary>
public sealed record WeatherSettings(bool Enabled = false, string Place = "", double? Latitude = null, double? Longitude = null);

/// <summary>
/// Game view: back to the normal view once the game has been in the background (another window has the
/// focus) for AutoNormalDelaySec, back to the game view when it returns to the foreground.
/// Transition: animation between the two views, one of <see cref="Transitions"/>.
/// </summary>
public sealed record GameViewSettings(bool AutoNormal = true, int AutoNormalDelaySec = 20, string Transition = "morph")
{
	public const int MinDelaySec = 5;
	public const int MaxDelaySec = 600;

	/// <summary>morph: widgets glide to their new place; slide: the views slide sideways; fade; none.</summary>
	public static readonly string[] Transitions = ["morph", "slide", "fade", "none"];
}

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
	string CpuClickTarget = "taskmgr.exe",
	bool SetupCompleted = false,
	string Theme = Themes.Sith,
	ThresholdSettings? Thresholds = null,
	WeatherSettings? Weather = null,
	GameViewSettings? GameView = null)
{
	public GameViewSettings EffectiveGameView => GameView ?? new GameViewSettings();

	/// <summary>Thresholds with the defaults filled in for the metrics the file leaves out.</summary>
	public ThresholdSettings EffectiveThresholds => (Thresholds ?? ThresholdSettings.Defaults).WithDefaults();

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public const string FileName = "appsettings.json";

	/// <summary>Copy kept in the data folder by installed versions, so updates never overwrite it.</summary>
	public static string UserFilePath => Path.Combine(Log.Directory, FileName);

	/// <summary>The user copy when there is one, otherwise the file shipped next to the executable.</summary>
	public static AppSettings Load() => Load(CurrentFilePath);

	private static string CurrentFilePath =>
		File.Exists(UserFilePath) ? UserFilePath : Path.Combine(AppContext.BaseDirectory, FileName);

	/// <summary>
	/// Applies changes to the settings file and writes the result to the user copy (created from the
	/// shipped file when missing). Edits the JSON tree so entries the change does not touch stay as written.
	/// </summary>
	public static void SaveUser(Action<JsonObject> change)
	{
		var source = CurrentFilePath;
		var root = File.Exists(source)
			? JsonNode.Parse(File.ReadAllText(source), new JsonNodeOptions { PropertyNameCaseInsensitive = true }, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
			: null;
		root ??= new JsonObject();
		change(root);
		Directory.CreateDirectory(Log.Directory);
		var json = root.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			// Keeps accents readable ("Tâches", "Température GPU") instead of \u escapes
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		File.WriteAllText(UserFilePath, json + Environment.NewLine);
		Log.Info($"Settings saved to {UserFilePath}");
	}

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
