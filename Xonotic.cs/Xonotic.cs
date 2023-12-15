using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Query;
using WindowsGSM.GameServer.Engine;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace WindowsGSM.Plugins
{
    public class Xonotic
    {

        // - Plugin Details
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.Xonotic", // WindowsGSM.XXXX
            author = "Soul",
            description = "🧩 WindowsGSM plugin that provides Xonotic Dedicated server support!",
            version = "1.0",
            url = "https://github.com/Soulflare3/WindowsGSM.Xonotic", // Github repository link (Best practice)
            color = "#7a0101" // Color Hex
        };

        // - Standard Constructor and properties
        public Xonotic(ServerConfig serverData) => _serverData = serverData;
        private readonly ServerConfig _serverData;
        public string Error, Notice;

        // - Game server Fixed variables
        public string FullName = "Xonotic Dedicated Server"; //Do not change
        public string ServerName = "Xonotic $g_xonoticversion Server"; //In-Game Hostname
        public string StartPath = "Xonotic\\xonotic-dedicated.exe";
        public bool AllowsEmbedConsole = false;
        public int PortIncrements = 1;
        public dynamic QueryMethod = null;

        public string IP = "0.0.0.0";
        public string Port = "26000";
        public string QueryPort = "26000";
        public string Defaultmap = "afterslime atelier boil catharsis courtfun dance drain erbium finalrage fuse geoplanetary glowplant implosion leave_em_behind nexballarena oilrig runningman runningmanctf silentsiege solarium space-elevator stormkeep techassault vorix warfare xoylent"; //Maplist
        public string Maxplayers = "8";
        public string Additional = "-dedicated +serverconfig server.cfg %*";

        // - Create a default cfg for the game server after installation
        public async void CreateServerCFG()
        {
            //Download serverconfig.json
            var replaceValues = new List<(string, string)>()
            {
                ("{{ServerName}}", _serverData.ServerName),
                ("{{MaxClients}}", _serverData.ServerMaxPlayer),
                ("{{IP}}", _serverData.ServerIP),
                ("{{Port}}", _serverData.ServerPort),
                ("{{DefaultMap}}", _serverData.ServerMap)

            };

            await DownloadGameServerConfig(ServerPath.GetServersServerFiles(_serverData.ServerID, "Xonotic\\data", "server.cfg"), "Xonotic Dedicated Server", replaceValues);
        }

        // - Start server function, return its Process to WindowsGSM
        public async Task<Process> Start()
        {
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exePath))
            {
                Error = $"{Path.GetFileName(exePath)} not found ({exePath})";
                return null;
            }

            Process p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = Directory.GetParent(exePath).FullName,
                    FileName = exePath,
                    Arguments = _serverData.ServerParam,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            if (AllowsEmbedConsole)
            {
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                var serverConsole = new ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return p;
            }

            p.Start();
            return p;
        }

        // - Stop server function
        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                if (p.StartInfo.RedirectStandardInput)
                {
                    p.StandardInput.WriteLine("quit");
                }
                else
                {
                    ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "quit");
                }
            });
        }

        // - Install server function
        public async Task<Process> Install()
        {
            string version = await GetRemoteBuild();
            if (version == null) { return null; }
            string zipName = $"xonotic-{version}.zip"; //https://dl.xonotic.org/xonotic-0.8.6.zip
            string address = $"https://dl.xonotic.org/{zipName}"; //Download from official server
            //string address = $"http://localhost:8084/host/{zipName}"; //Download from local server
            string zipPath = ServerPath.GetServersServerFiles(_serverData.ServerID, zipName);

            // Download xonotic-{version}.zip from https://dl.xonotic.org/
            using (WebClient webClient = new WebClient())
            {
                try
                {
                    webClient.DownloadProgressChanged += new DownloadProgressChangedEventHandler(DownloadProgressCallback4);
                    await webClient.DownloadFileTaskAsync(address, zipPath);
                }
                catch
                {
                    Error = $"Fail to download {zipName}";
                    return null;
                }
            }

            // Extract xonotic-{version}.zip
            if (!await FileManagement.ExtractZip(zipPath, Directory.GetParent(zipPath).FullName))
            {
                Error = $"Fail to extract {zipName}";
                return null;
            }

            using (StreamWriter outFile = new StreamWriter(ServerPath.GetServersServerFiles(_serverData.ServerID, "Xonotic\\", "version.txt", "")))
            {
                await outFile.WriteAsync(version);
            }

            // Delete xonotic-{version}.zip, leave it if fail to delete
            await FileManagement.DeleteAsync(zipPath);

            return null;
        }

        //Source: https://learn.microsoft.com/en-us/dotnet/api/system.net.webclient.downloadprogresschanged?view=net-8.0
        private static void DownloadProgressCallback4(object sender, DownloadProgressChangedEventArgs e)
        {
            // Displays the operation identifier, and the transfer progress.
            Console.WriteLine("Downloaded {0} of {1} bytes. {2} % complete...",
                e.BytesReceived,
                e.TotalBytesToReceive,
                e.ProgressPercentage);
        }

        // - Update server function
        public async Task<Process> Update()
        {
            // Backup the data folder
            string dataPath = ServerPath.GetServersServerFiles(_serverData.ServerID, "Xonotic\\data");
            string tempPath = ServerPath.GetServers(_serverData.ServerID, "__temp");
            bool needBackup = Directory.Exists(dataPath);
            if (needBackup)
            {
                if (Directory.Exists(tempPath))
                {
                    if (!await DirectoryManagement.DeleteAsync(tempPath, true))
                    {
                        Error = "Fail to delete the temp folder";
                        return null;
                    }
                }

                if (!await Task.Run(() =>
                {
                    try
                    {
                        CopyDirectory(dataPath, tempPath, true);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Error = e.Message;
                        return false;
                    }
                }))
                {
                    return null;
                }
            }

            // Delete the serverfiles folder
            if (!await DirectoryManagement.DeleteAsync(ServerPath.GetServersServerFiles(_serverData.ServerID), true))
            {
                Error = "Fail to delete the serverfiles";
                return null;
            }

            // Recreate the serverfiles folder
            Directory.CreateDirectory(ServerPath.GetServersServerFiles(_serverData.ServerID));

            if (needBackup)
            {
                // Restore the data folder
                if (!await Task.Run(() =>
                {
                    try
                    {
                        CopyDirectory(tempPath, dataPath, true);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Error = e.Message;
                        return false;
                    }
                }))
                {
                    return null;
                }

                await DirectoryManagement.DeleteAsync(tempPath, true);
            }

            // Update the server by install again
            await Install();

            // Return is valid
            if (IsInstallValid())
            {
                return null;
            }

            Error = "Update fail";
            return null;
        }

        // - Check if the installation is successful
        public bool IsInstallValid()
        {
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            return File.Exists(exePath);
        }

        public bool IsImportValid(string path)
        {
            string exePath = Path.Combine(path, StartPath);
            Error = $"Invalid Path! Fail to find {StartPath}";
            return File.Exists(exePath);
        }

        // - Get Local server version
        public string GetLocalBuild()
        {
            // Get local version written to file
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exePath))
            {
                Error = $"{StartPath} is missing.";
                return string.Empty;
            }

            try
            {
                using (var sr = new StreamReader(ServerPath.GetServersServerFiles(_serverData.ServerID, "Xonotic\\", "version.txt", "")))
                {
                    return sr.ReadToEnd(); // return 0.8.6
                }
            }
            catch (Exception e)
            {
                Error = $"Unable to read local version file";
                return string.Empty;
            }
        }

        // - Get Latest server version
        public async Task<string> GetRemoteBuild()
        {
            // Get latest build in https://gitlab.com/xonotic/xonotic/-/tags?format=atom with regex
            try
            {
                using (WebClient webClient = new WebClient())
                {
                    string feed = await webClient.DownloadStringTaskAsync("https://gitlab.com/xonotic/xonotic/-/tags?format=atom");
                    Regex regex = new Regex(@"(\d{1,}\.\d{1,}\.\d{1,})"); // Match "0.8.6"
                    return regex.Match(feed).Groups[1].Value; // Get first group -> "0.8.6"
                }
            }
            catch
            {
                Error = "Fail to get remote build";
                return string.Empty;
            }
        }

        //Source: https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-copy-directories
        //Adding to replace reliance on VisualBasic library
        static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            // Get information about the source directory
            var dir = new DirectoryInfo(sourceDir);

            // Check if the source directory exists
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            // Cache directories before we start copying
            DirectoryInfo[] dirs = dir.GetDirectories();

            // Create the destination directory
            Directory.CreateDirectory(destinationDir);

            // Get the files in the source directory and copy to the destination directory
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            // If recursive and copying subdirectories, recursively call this method
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }

        // New function
        /// <summary>
        /// Download the config from https://github.com/WindowsGSM/Game-Server-Configs/ //development redirect
        /// </summary>
        /// <param name="configPath">Local config location</param>
        /// <param name="serverGame">Server Game FullName</param>
        /// <param name="replaceValues">Replace Values</param>
        /// <returns></returns>
        public static async Task<bool> DownloadGameServerConfig(string configPath, string serverGame, List<(string, string)> replaceValues)
        {
            // Create Directory for the config file
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));

            // Remove existing config file if exists
            if (File.Exists(configPath))
            {
                await Task.Run(() => File.Delete(configPath));
            }

            try
            {
                // Download config file from github
                using (WebClient webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync($"https://github.com/Soulflare3/Game-Server-Configs/raw/master/{serverGame.Replace(":", "")}/{Path.GetFileName(configPath)}", configPath);
                }

                // Replace values
                string configText = File.ReadAllText(configPath);
                replaceValues.ForEach(x => configText = configText.Replace(x.Item1, x.Item2));
                File.WriteAllText(configPath, configText);
            }
            catch
            {
                return false;
            }

            return File.Exists(configPath);
        }
    }
}