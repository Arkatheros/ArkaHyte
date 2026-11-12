using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ArkaHyte;

/// <summary>
/// What the installer adds around the app files (Velopack hooks): start at logon and a user copy of
/// the settings. HYTE Nexus is left alone: disabling or uninstalling it is up to the user.
/// </summary>
/// <remarks>
/// Start at logon is a scheduled task (no rights needed for the user's own logon): Explorer starts the
/// Run key entries one after another once the desktop is up, which put the case screen ~35 s behind
/// the logon. The Run key stays as a fallback when the task cannot be registered.
/// </remarks>
public static class Installation
{
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string RunValueName = TrayIcon.AppName;
	// Same name as the development task of scripts\Install.ps1: one start entry, the last installed wins
	private const string TaskName = TrayIcon.AppName;

	/// <summary>Velopack after-install hook (30 s budget, then the process exits).</summary>
	public static void OnInstalled()
	{
		RegisterStartup();
		KeepUserSettings();
	}

	/// <summary>Velopack after-update hook: the executable path is stable, but re-assert it.</summary>
	public static void OnUpdated() => RegisterStartup();

	/// <summary>Velopack before-uninstall hook.</summary>
	public static void OnUninstalling()
	{
		RemoveRunValue();
		RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
		Log.Info("Start at logon removed");
	}

	private static void RegisterStartup()
	{
		var path = Environment.ProcessPath!;
		if (RegisterLogonTask(path))
		{
			RemoveRunValue();
			Log.Info($"Start at logon registered (scheduled task): {path}");
			return;
		}
		using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
		key.SetValue(RunValueName, $"\"{path}\"");
		Log.Info($"Start at logon registered (Run key fallback): {path}");
	}

	private static void RemoveRunValue()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
		key?.DeleteValue(RunValueName, throwOnMissingValue: false);
	}

	/// <summary>
	/// Logon task for the current user only, run with the user's limited token. Task defaults are wrong
	/// for an always-on display: below-normal priority (7, here 4) and a 3-day time limit (here none).
	/// </summary>
	private static bool RegisterLogonTask(string executablePath)
	{
		var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
		var xml = $"""
			<?xml version="1.0" encoding="UTF-16"?>
			<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
				<RegistrationInfo>
					<Description>Starts the Arka Hyte case display at logon.</Description>
				</RegistrationInfo>
				<Triggers>
					<LogonTrigger>
						<Enabled>true</Enabled>
						<UserId>{user}</UserId>
					</LogonTrigger>
				</Triggers>
				<Principals>
					<Principal id="Author">
						<UserId>{user}</UserId>
						<LogonType>InteractiveToken</LogonType>
						<RunLevel>LeastPrivilege</RunLevel>
					</Principal>
				</Principals>
				<Settings>
					<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
					<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
					<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
					<AllowHardTerminate>true</AllowHardTerminate>
					<StartWhenAvailable>false</StartWhenAvailable>
					<IdleSettings>
						<StopOnIdleEnd>false</StopOnIdleEnd>
						<RestartOnIdle>false</RestartOnIdle>
					</IdleSettings>
					<AllowStartOnDemand>true</AllowStartOnDemand>
					<Enabled>true</Enabled>
					<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
					<Priority>4</Priority>
				</Settings>
				<Actions Context="Author">
					<Exec>
						<Command>{SecurityElement.Escape(executablePath)}</Command>
						<WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(executablePath))}</WorkingDirectory>
					</Exec>
				</Actions>
			</Task>
			""";
		var xmlPath = Path.Combine(Path.GetTempPath(), $"ArkaHyte-task-{Environment.ProcessId}.xml");
		try
		{
			File.WriteAllText(xmlPath, xml, Encoding.Unicode);
			return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
		}
		catch (Exception exception)
		{
			Log.Error("Logon task registration failed", exception);
			return false;
		}
		finally
		{
			File.Delete(xmlPath);
		}
	}

	private static bool RunSchtasks(string arguments)
	{
		var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardError = true,
		};
		using var process = Process.Start(startInfo)!;
		var error = process.StandardError.ReadToEndAsync();
		// The Velopack hooks have 30 s in all
		if (!process.WaitForExit(10_000))
		{
			process.Kill();
			Log.Info($"schtasks {arguments.Split(' ')[0]} timed out");
			return false;
		}
		if (process.ExitCode != 0)
		{
			Log.Info($"schtasks {arguments.Split(' ')[0]} failed ({process.ExitCode}): {error.Result.Trim()}");
		}
		return process.ExitCode == 0;
	}

	/// <summary>
	/// Updates replace the app folder: the settings the user edits live in the data folder,
	/// seeded once from the shipped defaults (see <see cref="AppSettings.Load()"/>).
	/// </summary>
	private static void KeepUserSettings()
	{
		var shipped = Path.Combine(AppContext.BaseDirectory, AppSettings.FileName);
		if (!File.Exists(AppSettings.UserFilePath) && File.Exists(shipped))
		{
			Directory.CreateDirectory(Log.Directory);
			File.Copy(shipped, AppSettings.UserFilePath);
			Log.Info($"Settings copied to {AppSettings.UserFilePath}");
		}
	}
}
