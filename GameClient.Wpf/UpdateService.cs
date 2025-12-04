using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;


namespace GameClient.Wpf{

public static class UpdateService
{
	// TODO: put your actual raw GitHub URL here
	private const string VersionInfoUrl =
		"https://raw.githubusercontent.com/Jarias11/GameHub/main/latest.json";

	// Get current app version from assembly
	private static Version CurrentVersion =>
		Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

	public static async Task CheckForUpdatesAsync(Window owner)
	{
		try
		{
			using var client = new HttpClient();

			// 1) Download latest.json
			var json = await client.GetStringAsync(VersionInfoUrl);

			// 2) Parse it
			var info = JsonSerializer.Deserialize<UpdateInfo>(
    json,
    new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });
			if (info == null || string.IsNullOrWhiteSpace(info.Version))
				return;

			var latest = new Version(info.Version);



			MessageBox.Show(
	owner,
	$"Current App Version: {CurrentVersion}\n" +
	$"Latest GitHub Version: {info.Version}\n" +
	$"Installer URL: {info.Url}",
	"Update Debug Info",
	MessageBoxButton.OK,
	MessageBoxImage.Information
);

			// 3) Compare versions
			if (latest > CurrentVersion)
			{
				// Build message
				var msg = $"A new version ({latest}) is available.\n" +
						  $"You are currently on {CurrentVersion}.\n\n" +
						  "Download and install now?";

				if (!string.IsNullOrWhiteSpace(info.Notes))
				{
					msg += $"\n\nWhat's new:\n{info.Notes}";
				}

				var result = MessageBox.Show(
					owner,
					msg,
					"Update available",
					MessageBoxButton.YesNo,
					MessageBoxImage.Information);

				if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(info.Url))
				{
					await DownloadAndInstallAsync(info.Url, owner);
				}
			}
		}
		catch (Exception)
		{
			
		}
	}

	private static async Task DownloadAndInstallAsync(string installerUrl, Window owner)
	{
		try
		{
			using var client = new HttpClient();

			// Simple "please wait" message (no progress bar)
			MessageBox.Show(owner,
				"Downloading the update. This may take a moment...",
				"Downloading",
				MessageBoxButton.OK,
				MessageBoxImage.Information);

			// 1) Download installer bytes
			var response = await client.GetAsync(installerUrl);
			response.EnsureSuccessStatusCode();

			var bytes = await response.Content.ReadAsByteArrayAsync();

			// 2) Save to temp folder
			var tempPath = Path.GetTempPath();
			var installerPath = Path.Combine(tempPath, "GameHubSetup_latest.exe");

			await File.WriteAllBytesAsync(installerPath, bytes);

			// 3) Ask one last time before running installer (optional)
			var result = MessageBox.Show(owner,
				"The update has been downloaded.\n\n" +
				"The installer will now run and this app will close.\n" +
				"After installation, you can reopen GameHub.",
				"Ready to install",
				MessageBoxButton.OKCancel,
				MessageBoxImage.Question);

			if (result == MessageBoxResult.OK)
			{
				// 4) Run installer
				Process.Start(new ProcessStartInfo
				{
					FileName = installerPath,
					UseShellExecute = true
				});

				// 5) Close current app
				Application.Current.Shutdown();
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(owner,
				"Failed to download or run the update:\n" + ex.Message,
				"Update error",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
		}
	}
}
}