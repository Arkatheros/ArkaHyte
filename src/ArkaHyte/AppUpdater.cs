using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ArkaHyte;

/// <summary>Where the update flow stands, as shown in the tray menu.</summary>
public enum UpdateStage
{
	/// <summary>Not an installed copy (run from a build folder): updates are off.</summary>
	Unavailable,
	Idle,
	Checking,
	UpToDate,
	Available,
	Downloading,
	Failed
}

public sealed record UpdateState(UpdateStage Stage, string? Version = null, int Progress = 0);

/// <summary>
/// Checks the GitHub releases a minute after start then every 6 hours; downloading and installing
/// only happen when the user asks from the tray menu. Events are raised on background threads.
/// </summary>
public sealed class AppUpdater : IDisposable
{
	private const string RepositoryUrl = "https://github.com/Arkatheros/ArkaHyte";
	private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan CheckPeriod = TimeSpan.FromHours(6);

	private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, false));
	private readonly bool _simulate;
	private readonly SemaphoreSlim _busy = new(1, 1);
	private System.Threading.Timer? _timer;
	private UpdateInfo? _available;

	/// <summary>Raised whenever the state changes.</summary>
	public event Action<UpdateState>? StateChanged;

	public UpdateState State { get; private set; } = new(UpdateStage.Unavailable);

	/// <summary>Installed version, or the assembly version of a development build.</summary>
	public string CurrentVersion =>
		_manager.CurrentVersion?.ToString()
		?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
		?? "dev";

	public bool IsInstalled => _manager.IsInstalled;

	/// <param name="simulate">
	/// Development build (--simulate-updates): the tray entry works without an installed copy, a check
	/// pretends to take two seconds and finds nothing; nothing is ever downloaded.
	/// </param>
	public AppUpdater(bool simulate = false) => _simulate = simulate && !_manager.IsInstalled;

	public void Start()
	{
		if (_simulate)
		{
			Log.Info("Updates simulated (development build)");
			SetState(new UpdateState(UpdateStage.Idle));
			return;
		}
		if (!_manager.IsInstalled)
		{
			Log.Info($"Updates off: not an installed copy (version {CurrentVersion})");
			return;
		}
		SetState(new UpdateState(UpdateStage.Idle));
		_timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, FirstCheckDelay, CheckPeriod);
	}

	/// <summary>Tray action: installs the version found, or checks right away.</summary>
	public Task ActAsync() => _available is not null ? InstallAsync() : CheckAsync();

	public async Task CheckAsync()
	{
		if (_simulate)
		{
			SetState(new UpdateState(UpdateStage.Checking));
			await Task.Delay(TimeSpan.FromSeconds(2));
			SetState(new UpdateState(UpdateStage.UpToDate));
			return;
		}
		if (!_manager.IsInstalled || !await _busy.WaitAsync(0))
		{
			return;
		}
		try
		{
			SetState(new UpdateState(UpdateStage.Checking));
			var update = await _manager.CheckForUpdatesAsync();
			_available = update;
			if (update is null)
			{
				SetState(new UpdateState(UpdateStage.UpToDate));
			}
			else
			{
				Log.Info($"Update available: {update.TargetFullRelease.Version}");
				SetState(new UpdateState(UpdateStage.Available, update.TargetFullRelease.Version.ToString()));
			}
		}
		catch (Exception exception)
		{
			// Network down, GitHub rate limit (60 requests/hour per address)...: retried at the next period
			Log.Error("Update check failed", exception);
			SetState(_available is null
				? new UpdateState(UpdateStage.Failed)
				: new UpdateState(UpdateStage.Available, _available.TargetFullRelease.Version.ToString()));
		}
		finally
		{
			_busy.Release();
		}
	}

	private async Task InstallAsync()
	{
		if (_available is not { } update || !await _busy.WaitAsync(0))
		{
			return;
		}
		var version = update.TargetFullRelease.Version.ToString();
		try
		{
			SetState(new UpdateState(UpdateStage.Downloading, version));
			await _manager.DownloadUpdatesAsync(update, progress =>
				SetState(new UpdateState(UpdateStage.Downloading, version, progress)));
			Log.Info($"Update {version} downloaded, restarting to apply it");
			// The updater waits for this process to exit, swaps the files and starts the new version
			_manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, []);
			ExitRequested?.Invoke();
		}
		catch (Exception exception)
		{
			Log.Error($"Update {version} failed", exception);
			SetState(new UpdateState(UpdateStage.Failed, version));
			_busy.Release();
		}
	}

	/// <summary>Asks the app to close so the downloaded update can be applied.</summary>
	public event Action? ExitRequested;

	private void SetState(UpdateState state)
	{
		State = state;
		StateChanged?.Invoke(state);
	}

	public void Dispose()
	{
		_timer?.Dispose();
		_busy.Dispose();
	}
}
