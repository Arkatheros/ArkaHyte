using System.Diagnostics;
using System.Text.Json;
using ArkaHyte.Native;

namespace ArkaHyte;

/// <summary>An entry of the Start menu "All apps" list; Id is its shell:AppsFolder parsing name.</summary>
public sealed record AppEntry(string Id, string Name, string? Icon);

/// <summary>
/// Lists installed applications exactly like the Start menu does (desktop and Store apps)
/// through the shell:AppsFolder virtual folder, with their icons.
/// </summary>
public static class AppCatalog
{
	public const string AppsFolderPrefix = @"shell:AppsFolder\";
	public const string ExportArgument = "--export-apps";
	private const int IconSize = 64;
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(1);
	private static readonly string[] HiddenNameHints = ["uninstall", "désinstall", "desinstall"];
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly object Gate = new();

	private static string? _cachedJson;
	private static DateTime _cachedAt;
	private static Task<string>? _loading;

	/// <summary>
	/// The catalog as a JSON array, built by a short-lived child process: enumerating
	/// shell:AppsFolder loads dozens of shell extension DLLs (~50 MB) that would otherwise
	/// stay for good in the long-running display process.
	/// </summary>
	public static Task<string> ListJsonAsync()
	{
		lock (Gate)
		{
			if (_cachedJson is not null && DateTime.UtcNow - _cachedAt < CacheLifetime)
			{
				return Task.FromResult(_cachedJson);
			}
			return _loading ??= ExportInChildProcessAsync();
		}
	}

	private static async Task<string> ExportInChildProcessAsync()
	{
		// Leave the caller's lock first, so the cleanup below never runs before _loading is assigned
		await Task.Yield();
		var path = Path.Combine(Path.GetTempPath(), $"ArkaHyte-apps-{Environment.ProcessId}.json");
		try
		{
			var start = new ProcessStartInfo(Environment.ProcessPath!, [ExportArgument, path])
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using var process = Process.Start(start)
				?? throw new InvalidOperationException("App catalog process did not start");
			using var timeout = new CancellationTokenSource(ExportTimeout);
			try
			{
				await process.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				process.Kill();
				throw new TimeoutException("App catalog process timed out");
			}
			if (process.ExitCode != 0)
			{
				throw new InvalidOperationException($"App catalog process failed with code {process.ExitCode}");
			}
			var json = await File.ReadAllTextAsync(path);
			lock (Gate)
			{
				_cachedJson = json;
				_cachedAt = DateTime.UtcNow;
			}
			return json;
		}
		finally
		{
			lock (Gate)
			{
				_loading = null;
			}
			File.Delete(path);
		}
	}

	/// <summary>Child process side of <see cref="ListJsonAsync"/>: writes the full catalog to a file.</summary>
	public static int Export(string path)
	{
		var apps = EnumerateOnStaAsync().GetAwaiter().GetResult();
		File.WriteAllText(path, JsonSerializer.Serialize(apps, JsonOptions));
		return 0;
	}

	public static string? GetIcon(string parsingName, int size) => ShellIcons.GetPngDataUrl(parsingName, size);

	// The shell COM objects and icon extraction expect a single-threaded apartment
	public static Task<IReadOnlyList<AppEntry>> EnumerateOnStaAsync()
	{
		var completion = new TaskCompletionSource<IReadOnlyList<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				completion.SetResult(Enumerate());
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		})
		{
			IsBackground = true,
			Name = "AppCatalog"
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task;
	}

	private static IReadOnlyList<AppEntry> Enumerate()
	{
		var shellType = Type.GetTypeFromProgID("Shell.Application")
			?? throw new InvalidOperationException("Shell.Application is not available");
		dynamic shell = Activator.CreateInstance(shellType)!;
		dynamic folder = shell.NameSpace("shell:AppsFolder");

		var apps = new List<AppEntry>();
		var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
		foreach (var item in folder.Items())
		{
			string name = item.Name;
			string id = item.Path;
			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)
				|| id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				|| HiddenNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))
				|| !seenNames.Add(name))
			{
				continue;
			}
			apps.Add(new AppEntry(id, name, ShellIcons.GetPngDataUrl(AppsFolderPrefix + id, IconSize)));
		}
		apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
		Log.Info($"App catalog: {apps.Count} applications");
		return apps;
	}
}
