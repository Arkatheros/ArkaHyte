using System.Security.Cryptography;
using System.Text;

namespace ArkaHyte.Home;

/// <summary>
/// Home Assistant long-lived access token, encrypted with DPAPI for the current Windows user in the data
/// folder. Never in appsettings.json, never exported (unreadable on another machine anyway), never logged.
/// </summary>
public static class HomeToken
{
	private static string FilePath => Path.Combine(DataFolder.Root, "home-token.bin");

	// Ties the encrypted bytes to this use, on top of the user's DPAPI key
	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArkaHyte.HomeAssistant");

	public static bool Exists => File.Exists(FilePath);

	public static string? Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return null;
			}
			var clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(clear);
		}
		catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
		{
			Log.Error("Home Assistant token unreadable", exception);
			return null;
		}
	}

	public static void Save(string token)
	{
		Directory.CreateDirectory(DataFolder.Root);
		var sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
		File.WriteAllBytes(FilePath, sealedBytes);
	}

	public static void Delete()
	{
		if (File.Exists(FilePath))
		{
			File.Delete(FilePath);
		}
	}
}
