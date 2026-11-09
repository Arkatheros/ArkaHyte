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
	private const int IconSize = 64;
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
	private static readonly string[] HiddenNameHints = ["uninstall", "désinstall", "desinstall"];

	private static IReadOnlyList<AppEntry>? _cache;
	private static DateTime _cachedAt;

	public static Task<IReadOnlyList<AppEntry>> ListAsync()
	{
		if (_cache is not null && DateTime.UtcNow - _cachedAt < CacheLifetime)
		{
			return Task.FromResult(_cache);
		}
		return RunOnStaThread(() =>
		{
			var apps = Enumerate();
			_cache = apps;
			_cachedAt = DateTime.UtcNow;
			return apps;
		});
	}

	public static string? GetIcon(string parsingName, int size) => ShellIcons.GetPngDataUrl(parsingName, size);

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

	// The shell COM objects and icon extraction expect a single-threaded apartment
	private static Task<T> RunOnStaThread<T>(Func<T> work)
	{
		var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				completion.SetResult(work());
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
}
