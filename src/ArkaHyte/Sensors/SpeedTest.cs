using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ArkaHyte.Sensors;

/// <summary>Progress of a speed test as sent to the web UI ("type": "speedtest").</summary>
/// <param name="Phase">install, ping, download, upload, done, cancelled or error.</param>
/// <param name="Progress">Overall progress from 0 to 1.</param>
/// <param name="Mbps">Live throughput of the running phase.</param>
/// <param name="Server">Test server, e.g. "Iliad Free SAS · Vitry-sur-Seine".</param>
/// <param name="Reason">On error: "install" (CLI download failed) or "test".</param>
public sealed record SpeedTestUpdate(
	string Phase,
	double Progress,
	double? Mbps = null,
	double? PingMs = null,
	double? DownMbps = null,
	double? UpMbps = null,
	string? Server = null,
	string? Reason = null)
{
	public string Type => "speedtest";
}

/// <summary>
/// Internet speed test through the official Speedtest by Ookla CLI (nearest Ookla server,
/// no rate limiting for occasional tests, unlike Cloudflare's endpoints which blocked the
/// address for an hour after two runs). The CLI is downloaded from Ookla on first use into
/// %LOCALAPPDATA%\ArkaHyte\tools (its licence does not allow bundling it in the repository).
/// </summary>
public sealed class SpeedTest
{
	private const string CliDownloadUrl = "https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip";
	private const string CliArguments = "--format=jsonl --progress=yes --accept-license --accept-gdpr";

	private static string CliPath => Path.Combine(Log.Directory, "tools", "speedtest.exe");

	public async Task RunAsync(Action<SpeedTestUpdate> report, CancellationToken token)
	{
		await EnsureCliAsync(report, token);
		report(new SpeedTestUpdate("ping", 0));

		var startInfo = new ProcessStartInfo(CliPath, CliArguments)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8
		};
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Speedtest CLI did not start");
		using var killOnCancel = token.Register(() => TryKill(process));
		// Drain stderr so a chatty CLI can never block on a full pipe
		var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

		var state = new RunState();
		string? line;
		while ((line = await process.StandardOutput.ReadLineAsync(token)) is not null)
		{
			var update = Parse(line, state);
			if (update is not null)
			{
				report(update);
			}
		}
		await process.WaitForExitAsync(token);
		token.ThrowIfCancellationRequested();

		if (!state.Completed)
		{
			var details = state.LastError ?? (await stderr).Trim();
			throw new InvalidOperationException($"Speedtest CLI ended without a result (exit {process.ExitCode}): {details}");
		}
		Log.Info($"Speed test ({state.Server}): ping {state.PingMs:0.0} ms, down {state.DownMbps:0.0} Mbit/s, up {state.UpMbps:0.0} Mbit/s");
	}

	private sealed class RunState
	{
		public string? Server;
		public double? PingMs;
		public double? DownMbps;
		public double? UpMbps;
		public string? LastError;
		public bool Completed;
	}

	// One JSON object per line: testStart, ping, download, upload, result, log. The licence
	// banner on first run is plain text and is skipped.
	private static SpeedTestUpdate? Parse(string line, RunState state)
	{
		if (!line.StartsWith('{'))
		{
			return null;
		}
		try
		{
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			switch (root.GetProperty("type").GetString())
			{
				case "testStart":
					if (root.TryGetProperty("server", out var server))
					{
						var name = server.GetProperty("name").GetString();
						var location = server.TryGetProperty("location", out var place) ? place.GetString() : null;
						state.Server = string.IsNullOrWhiteSpace(location) ? name : $"{name} · {location}";
					}
					return new SpeedTestUpdate("ping", 0, Server: state.Server);

				case "ping":
					var ping = root.GetProperty("ping");
					state.PingMs = ping.GetProperty("latency").GetDouble();
					return new SpeedTestUpdate("ping", 0.1 * Progress(ping), PingMs: state.PingMs, Server: state.Server);

				case "download":
					var download = root.GetProperty("download");
					// Kept so the upload phase can already show the download figure
					state.DownMbps = Mbps(download);
					return new SpeedTestUpdate("download", 0.1 + 0.45 * Progress(download), state.DownMbps, state.PingMs, Server: state.Server);

				case "upload":
					var upload = root.GetProperty("upload");
					return new SpeedTestUpdate("upload", 0.55 + 0.45 * Progress(upload), Mbps(upload), state.PingMs, state.DownMbps, Server: state.Server);

				case "result":
					state.PingMs = root.GetProperty("ping").GetProperty("latency").GetDouble();
					state.DownMbps = Mbps(root.GetProperty("download"));
					state.UpMbps = Mbps(root.GetProperty("upload"));
					state.Completed = true;
					return new SpeedTestUpdate("done", 1, PingMs: state.PingMs, DownMbps: state.DownMbps, UpMbps: state.UpMbps, Server: state.Server);

				case "log" when root.TryGetProperty("level", out var level) && level.GetString() == "error":
					// The CLI logs a harmless "[0] Unknown error" even on successful runs; keep it only for diagnostics
					state.LastError = root.TryGetProperty("message", out var message) ? message.GetString() : null;
					return null;
			}
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			Log.Error($"Unreadable Speedtest CLI line: {line}", exception);
		}
		return null;
	}

	// Ookla reports bandwidth in bytes per second
	private static double Mbps(JsonElement phase) => phase.GetProperty("bandwidth").GetDouble() * 8 / 1e6;

	private static double Progress(JsonElement phase) =>
		phase.TryGetProperty("progress", out var progress) ? Math.Clamp(progress.GetDouble(), 0, 1) : 1;

	private static async Task EnsureCliAsync(Action<SpeedTestUpdate> report, CancellationToken token)
	{
		if (File.Exists(CliPath))
		{
			return;
		}
		report(new SpeedTestUpdate("install", 0));
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(CliPath)!);
			using var http = new HttpClient();
			await using var archive = await http.GetStreamAsync(CliDownloadUrl, token);
			using var buffer = new MemoryStream();
			await archive.CopyToAsync(buffer, token);
			buffer.Position = 0;
			using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
			var entry = zip.GetEntry("speedtest.exe") ?? throw new InvalidDataException("speedtest.exe missing from the Ookla archive");
			entry.ExtractToFile(CliPath, overwrite: true);
			Log.Info($"Speedtest CLI installed to {CliPath}");
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
		{
			throw new SpeedTestInstallException(exception);
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
			// Already gone
		}
	}
}

/// <summary>The Speedtest CLI could not be downloaded or extracted.</summary>
public sealed class SpeedTestInstallException(Exception inner)
	: Exception("Could not install the Speedtest CLI", inner);
