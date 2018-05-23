﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
//  Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
//  Contact: JustArchi@JustArchi.net
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
//      
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;
using ArchiSteamFarm.Localization;

namespace ArchiSteamFarm {
	internal static class ASF {
		internal static readonly ArchiLogger ArchiLogger = new ArchiLogger(SharedInfo.ASF);

		private static readonly ConcurrentDictionary<string, DateTime> LastWriteTimes = new ConcurrentDictionary<string, DateTime>();

		private static Timer AutoUpdatesTimer;
		private static FileSystemWatcher FileSystemWatcher;

		internal static async Task<Version> CheckAndUpdateProgram(bool updateOverride = false) {
			if (!SharedInfo.BuildInfo.CanUpdate || (Program.GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.None)) {
				return null;
			}

			if ((AutoUpdatesTimer == null) && (Program.GlobalConfig.UpdatePeriod > 0)) {
				TimeSpan autoUpdatePeriod = TimeSpan.FromHours(Program.GlobalConfig.UpdatePeriod);

				AutoUpdatesTimer = new Timer(
					async e => await CheckAndUpdateProgram().ConfigureAwait(false),
					null,
					autoUpdatePeriod, // Delay
					autoUpdatePeriod // Period
				);

				ArchiLogger.LogGenericInfo(string.Format(Strings.AutoUpdateCheckInfo, autoUpdatePeriod.ToHumanReadable()));
			}

			ArchiLogger.LogGenericInfo(Strings.UpdateCheckingNewVersion);

			string targetDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

			// Cleanup from previous update - update directory for old in-use runtime files
			string backupDirectory = Path.Combine(targetDirectory, SharedInfo.UpdateDirectory);
			if (Directory.Exists(backupDirectory)) {
				// It's entirely possible that old process is still running, wait a short moment for eventual cleanup
				await Task.Delay(5000).ConfigureAwait(false);

				try {
					Directory.Delete(backupDirectory, true);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
					return null;
				}
			}

			// Cleanup from previous update - old non-runtime in-use files
			try {
				foreach (string file in Directory.EnumerateFiles(targetDirectory, "*.old", SearchOption.AllDirectories)) {
					File.Delete(file);
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}

			string releaseURL = SharedInfo.GithubReleaseURL + (Program.GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable ? "/latest" : "?per_page=1");

			GitHub.ReleaseResponse releaseResponse;

			if (Program.GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable) {
				WebBrowser.ObjectResponse<GitHub.ReleaseResponse> objectResponse = await Program.WebBrowser.UrlGetToJsonObject<GitHub.ReleaseResponse>(releaseURL).ConfigureAwait(false);
				if (objectResponse?.Content == null) {
					ArchiLogger.LogGenericWarning(Strings.ErrorUpdateCheckFailed);
					return null;
				}

				releaseResponse = objectResponse.Content;
			} else {
				WebBrowser.ObjectResponse<List<GitHub.ReleaseResponse>> objectResponse = await Program.WebBrowser.UrlGetToJsonObject<List<GitHub.ReleaseResponse>>(releaseURL).ConfigureAwait(false);
				if ((objectResponse?.Content == null) || (objectResponse.Content.Count == 0)) {
					ArchiLogger.LogGenericWarning(Strings.ErrorUpdateCheckFailed);
					return null;
				}

				releaseResponse = objectResponse.Content[0];
			}

			if (string.IsNullOrEmpty(releaseResponse.Tag)) {
				ArchiLogger.LogGenericWarning(Strings.ErrorUpdateCheckFailed);
				return null;
			}

			Version newVersion = new Version(releaseResponse.Tag);

			ArchiLogger.LogGenericInfo(string.Format(Strings.UpdateVersionInfo, SharedInfo.Version, newVersion));

			if (SharedInfo.Version == newVersion) {
				return SharedInfo.Version;
			}

			if (SharedInfo.Version > newVersion) {
				ArchiLogger.LogGenericWarning(Strings.WarningPreReleaseVersion);
				await Task.Delay(15 * 1000).ConfigureAwait(false);
				return SharedInfo.Version;
			}

			if (!updateOverride && (Program.GlobalConfig.UpdatePeriod == 0)) {
				ArchiLogger.LogGenericInfo(Strings.UpdateNewVersionAvailable);
				await Task.Delay(5000).ConfigureAwait(false);
				return null;
			}

			// Auto update logic starts here
			if (releaseResponse.Assets == null) {
				ArchiLogger.LogGenericWarning(Strings.ErrorUpdateNoAssets);
				return null;
			}

			string targetFile = SharedInfo.ASF + "-" + SharedInfo.BuildInfo.Variant + ".zip";
			GitHub.ReleaseResponse.Asset binaryAsset = releaseResponse.Assets.FirstOrDefault(asset => asset.Name.Equals(targetFile, StringComparison.OrdinalIgnoreCase));

			if (binaryAsset == null) {
				ArchiLogger.LogGenericWarning(Strings.ErrorUpdateNoAssetForThisVersion);
				return null;
			}

			if (string.IsNullOrEmpty(binaryAsset.DownloadURL)) {
				ArchiLogger.LogNullError(nameof(binaryAsset.DownloadURL));
				return null;
			}

			ArchiLogger.LogGenericInfo(string.Format(Strings.UpdateDownloadingNewVersion, newVersion, binaryAsset.Size / 1024 / 1024));

			WebBrowser.BinaryResponse response = await Program.WebBrowser.UrlGetToBinaryWithProgress(binaryAsset.DownloadURL).ConfigureAwait(false);
			if (response?.Content == null) {
				return null;
			}

			try {
				using (ZipArchive zipArchive = new ZipArchive(new MemoryStream(response.Content))) {
					UpdateFromArchive(zipArchive, targetDirectory);
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}

			if (OS.IsUnix) {
				string executable = Path.Combine(targetDirectory, SharedInfo.AssemblyName);
				if (File.Exists(executable)) {
					OS.UnixSetFileAccessExecutable(executable);
				}
			}

			ArchiLogger.LogGenericInfo(Strings.UpdateFinished);
			await RestartOrExit().ConfigureAwait(false);
			return newVersion;
		}

		internal static async Task InitBots() {
			if (Bot.Bots.Count != 0) {
				return;
			}

			// Before attempting to connect, initialize our configuration
			await Bot.InitializeSteamConfiguration(Program.GlobalConfig.SteamProtocols, Program.GlobalDatabase.CellID, Program.GlobalDatabase.ServerListProvider).ConfigureAwait(false);

			try {
				IEnumerable<Task> tasks = Directory.EnumerateFiles(SharedInfo.ConfigDirectory, "*" + SharedInfo.ConfigExtension).Select(Path.GetFileNameWithoutExtension).Where(botName => !string.IsNullOrEmpty(botName) && IsValidBotName(botName)).OrderBy(botName => botName).Select(Bot.RegisterBot);

				switch (Program.GlobalConfig.OptimizationMode) {
					case GlobalConfig.EOptimizationMode.MinMemoryUsage:
						foreach (Task task in tasks) {
							await task.ConfigureAwait(false);
						}

						break;
					default:
						await Task.WhenAll(tasks).ConfigureAwait(false);
						break;
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return;
			}

			if (Bot.Bots.Count == 0) {
				ArchiLogger.LogGenericWarning(Strings.ErrorNoBotsDefined);
			}
		}

		internal static void InitEvents() {
			if (FileSystemWatcher != null) {
				return;
			}

			FileSystemWatcher = new FileSystemWatcher(SharedInfo.ConfigDirectory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };

			FileSystemWatcher.Changed += OnChanged;
			FileSystemWatcher.Created += OnCreated;
			FileSystemWatcher.Deleted += OnDeleted;
			FileSystemWatcher.Renamed += OnRenamed;

			FileSystemWatcher.EnableRaisingEvents = true;
		}

		private static bool IsValidBotName(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				ArchiLogger.LogNullError(nameof(botName));
				return false;
			}

			if (botName[0] == '.') {
				return false;
			}

			switch (botName) {
				case SharedInfo.ASF:
				case "example":
				case "minimal":
					return false;
				default:
					return true;
			}
		}

		private static async void OnChanged(object sender, FileSystemEventArgs e) {
			if ((sender == null) || (e == null)) {
				ArchiLogger.LogNullError(nameof(sender) + " || " + nameof(e));
				return;
			}

			await OnChangedFile(e.Name, e.FullPath).ConfigureAwait(false);
		}

		private static async Task OnChangedConfigFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			await OnCreatedConfigFile(name, fullPath).ConfigureAwait(false);
		}

		private static async Task OnChangedFile(string name, string fullPath) {
			string extension = Path.GetExtension(name);

			switch (extension) {
				case SharedInfo.ConfigExtension:
					await OnChangedConfigFile(name, fullPath).ConfigureAwait(false);
					break;
				case SharedInfo.KeysExtension:
					await OnChangedKeysFile(name, fullPath).ConfigureAwait(false);
					break;
			}
		}

		private static async Task OnChangedKeysFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			await OnCreatedKeysFile(name, fullPath).ConfigureAwait(false);
		}

		private static async void OnCreated(object sender, FileSystemEventArgs e) {
			if ((sender == null) || (e == null)) {
				ArchiLogger.LogNullError(nameof(sender) + " || " + nameof(e));
				return;
			}

			await OnCreatedFile(e.Name, e.FullPath).ConfigureAwait(false);
		}

		private static async Task OnCreatedConfigFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			string botName = Path.GetFileNameWithoutExtension(name);
			if (string.IsNullOrEmpty(botName) || (botName[0] == '.')) {
				return;
			}

			DateTime lastWriteTime = DateTime.UtcNow;

			if (LastWriteTimes.TryGetValue(name, out DateTime savedLastWriteTime)) {
				if (savedLastWriteTime >= lastWriteTime) {
					return;
				}
			}

			LastWriteTimes[name] = lastWriteTime;

			// It's entirely possible that some process is still accessing our file, allow at least a second before trying to read it
			await Task.Delay(1000).ConfigureAwait(false);

			// It's also possible that we got some other event in the meantime
			if (LastWriteTimes.TryGetValue(name, out savedLastWriteTime)) {
				if (lastWriteTime != savedLastWriteTime) {
					return;
				}

				if (LastWriteTimes.TryRemove(name, out savedLastWriteTime)) {
					if (lastWriteTime != savedLastWriteTime) {
						return;
					}
				}
			}

			if (botName.Equals(SharedInfo.ASF)) {
				ArchiLogger.LogGenericInfo(Strings.GlobalConfigChanged);
				await RestartOrExit().ConfigureAwait(false);
				return;
			}

			if (!IsValidBotName(botName)) {
				return;
			}

			if (Bot.Bots.TryGetValue(botName, out Bot bot)) {
				await bot.OnConfigChanged(false).ConfigureAwait(false);
			} else {
				await Bot.RegisterBot(botName).ConfigureAwait(false);
			}
		}

		private static async Task OnCreatedFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			string extension = Path.GetExtension(name);

			switch (extension) {
				case SharedInfo.ConfigExtension:
					await OnCreatedConfigFile(name, fullPath).ConfigureAwait(false);
					break;
				case SharedInfo.KeysExtension:
					await OnCreatedKeysFile(name, fullPath).ConfigureAwait(false);
					break;
			}
		}

		private static async Task OnCreatedKeysFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			string botName = Path.GetFileNameWithoutExtension(name);
			if (string.IsNullOrEmpty(botName) || (botName[0] == '.')) {
				return;
			}

			DateTime lastWriteTime = DateTime.UtcNow;

			if (LastWriteTimes.TryGetValue(name, out DateTime savedLastWriteTime)) {
				if (savedLastWriteTime >= lastWriteTime) {
					return;
				}
			}

			LastWriteTimes[name] = lastWriteTime;

			// It's entirely possible that some process is still accessing our file, allow at least a second before trying to read it
			await Task.Delay(1000).ConfigureAwait(false);

			// It's also possible that we got some other event in the meantime
			if (LastWriteTimes.TryGetValue(name, out savedLastWriteTime)) {
				if (lastWriteTime != savedLastWriteTime) {
					return;
				}

				if (LastWriteTimes.TryRemove(name, out savedLastWriteTime)) {
					if (lastWriteTime != savedLastWriteTime) {
						return;
					}
				}
			}

			if (!Bot.Bots.TryGetValue(botName, out Bot bot)) {
				return;
			}

			await bot.ImportKeysToRedeem(fullPath).ConfigureAwait(false);
		}

		private static async void OnDeleted(object sender, FileSystemEventArgs e) {
			if ((sender == null) || (e == null)) {
				ArchiLogger.LogNullError(nameof(sender) + " || " + nameof(e));
				return;
			}

			await OnDeletedFile(e.Name, e.FullPath).ConfigureAwait(false);
		}

		private static async Task OnDeletedConfigFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			string botName = Path.GetFileNameWithoutExtension(name);
			if (string.IsNullOrEmpty(botName)) {
				return;
			}

			DateTime lastWriteTime = DateTime.UtcNow;

			if (LastWriteTimes.TryGetValue(name, out DateTime savedLastWriteTime)) {
				if (savedLastWriteTime >= lastWriteTime) {
					return;
				}
			}

			LastWriteTimes[name] = lastWriteTime;

			// It's entirely possible that some process is still accessing our file, allow at least a second before trying to read it
			await Task.Delay(1000).ConfigureAwait(false);

			// It's also possible that we got some other event in the meantime
			if (LastWriteTimes.TryGetValue(name, out savedLastWriteTime)) {
				if (lastWriteTime != savedLastWriteTime) {
					return;
				}

				if (LastWriteTimes.TryRemove(name, out savedLastWriteTime)) {
					if (lastWriteTime != savedLastWriteTime) {
						return;
					}
				}
			}

			if (botName.Equals(SharedInfo.ASF)) {
				if (File.Exists(fullPath)) {
					return;
				}

				// Some editors might decide to delete file and re-create it in order to modify it
				// If that's the case, we wait for maximum of 5 seconds before shutting down
				await Task.Delay(5000).ConfigureAwait(false);
				if (File.Exists(fullPath)) {
					return;
				}

				ArchiLogger.LogGenericError(Strings.ErrorGlobalConfigRemoved);
				await Program.Exit(1).ConfigureAwait(false);
				return;
			}

			if (!IsValidBotName(botName)) {
				return;
			}

			if (Bot.Bots.TryGetValue(botName, out Bot bot)) {
				await bot.OnConfigChanged(true).ConfigureAwait(false);
			}
		}

		private static async Task OnDeletedFile(string name, string fullPath) {
			if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fullPath)) {
				ArchiLogger.LogNullError(nameof(name) + " || " + nameof(fullPath));
				return;
			}

			string extension = Path.GetExtension(name);

			switch (extension) {
				case SharedInfo.ConfigExtension:
					await OnDeletedConfigFile(name, fullPath).ConfigureAwait(false);
					break;
			}
		}

		private static async void OnRenamed(object sender, RenamedEventArgs e) {
			if ((sender == null) || (e == null)) {
				ArchiLogger.LogNullError(nameof(sender) + " || " + nameof(e));
				return;
			}

			await OnDeletedFile(e.OldName, e.OldFullPath).ConfigureAwait(false);
			await OnCreatedFile(e.Name, e.FullPath).ConfigureAwait(false);
		}

		private static async Task RestartOrExit() {
			if (Program.RestartAllowed && Program.GlobalConfig.AutoRestart) {
				ArchiLogger.LogGenericInfo(Strings.Restarting);
				await Task.Delay(5000).ConfigureAwait(false);
				await Program.Restart().ConfigureAwait(false);
			} else {
				ArchiLogger.LogGenericInfo(Strings.Exiting);
				await Task.Delay(5000).ConfigureAwait(false);
				await Program.Exit().ConfigureAwait(false);
			}
		}

		private static void UpdateFromArchive(ZipArchive archive, string targetDirectory) {
			if ((archive == null) || string.IsNullOrEmpty(targetDirectory)) {
				ArchiLogger.LogNullError(nameof(archive) + " || " + nameof(targetDirectory));
				return;
			}

			string backupDirectory = Path.Combine(targetDirectory, SharedInfo.UpdateDirectory);
			Directory.CreateDirectory(backupDirectory);

			// Move top-level runtime in-use files to other directory
			// We must do it in order to not crash at later stage - all libraries/executables must keep original names
			foreach (string file in Directory.EnumerateFiles(targetDirectory)) {
				string fileName = Path.GetFileName(file);
				switch (fileName) {
					// Files that we want to keep in original directory
					case "NLog.config":
						continue;
				}

				string target = Path.Combine(backupDirectory, fileName);
				File.Move(file, target);
			}

			// In generic ASF variant there can also be "runtimes" directory in need of same approach
			string runtimesDirectory = Path.Combine(targetDirectory, "runtimes");
			if (Directory.Exists(runtimesDirectory)) {
				foreach (string file in Directory.EnumerateFiles(runtimesDirectory, "*", SearchOption.AllDirectories)) {
					string directory = Path.Combine(backupDirectory, Path.GetDirectoryName(Path.GetRelativePath(targetDirectory, file)));
					Directory.CreateDirectory(directory);

					string target = Path.Combine(directory, Path.GetFileName(file));
					File.Move(file, target);
				}
			}

			foreach (ZipArchiveEntry zipFile in archive.Entries) {
				string file = Path.Combine(targetDirectory, zipFile.FullName);
				string directory = Path.GetDirectoryName(file);

				if (!Directory.Exists(directory)) {
					Directory.CreateDirectory(directory);
				}

				if (string.IsNullOrEmpty(zipFile.Name) || zipFile.Name.Equals(SharedInfo.GlobalConfigFileName)) {
					continue;
				}

				string backupFile = file + ".old";

				if (File.Exists(file)) {
					// This is non-runtime file to be replaced, probably in use, move it away
					File.Move(file, backupFile);
				}

				zipFile.ExtractToFile(file);

				try {
					File.Delete(backupFile);
				} catch {
					// Ignored - that file is indeed in use, it will be deleted after restart
				}
			}
		}

		internal enum EUserInputType : byte {
			Unknown,
			DeviceID,
			Login,
			Password,
			SteamGuard,
			SteamParentalPIN,
			TwoFactorAuthentication
		}
	}
}