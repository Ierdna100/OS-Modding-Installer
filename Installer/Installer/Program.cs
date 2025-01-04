using CommandLine;
using Octokit;
using Steamworks;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Installer
{
    public class CLIArgs
    {
        [Option('u', "uninstall", HelpText = "Uninstall the OS Modding environnement along with all mods and the mod manager.")]
        public bool Uninstall { get; set; }

        //[Option('v', "install-doorstop-verbose", HelpText = "Install the verbose version (generates logs) for Doorstop.")]
        //public bool InstallVerbose { get; set; }

        //[Option('l', "log-verbose", HelpText = "Sets logging level to verbose for the installer.")]
        //public bool Verbose {  get; set; }

        [Option('y', HelpText = "Answers the equivalent of \"yes\" to all questions the installer will ask.")]
        public bool SkipQuestions {  get; set; }

        [Option('d', "update", HelpText = "Update the OS Modding environnement along with all mods and the mod manager.")]
        public bool Update { get; set; }

        [Option('i', "check-integrity", HelpText = "Checks the integrity of this installer, the mod manager and the game.")]
        public bool CheckIntegrity { get; set; }

        [Option('p', "platform", HelpText = "Sets the platform for which Doorstop and BepInEx should be installed for. Possible values are 'win-x64', 'win-x86', 'macos', 'linux-x64' or 'linux-x86'")]
        public string Platform { get; set; }
        
        public override string ToString()
        {
            return "CLIArgs{Uninstall=" + Uninstall
                //+ ", InstallVerbose=" + InstallVerbose
                //+ ", Verbose=" + Verbose
                + ", SkipQuestions=" + SkipQuestions
                + ", Update=" + Update
                + ", CheckIntegrity=" + CheckIntegrity
                + ", Platform=" + Platform
                + "}";
        }
    }

    public class Program
    {
        public const uint obenseuerAppId = 951240;
        public const string gameName = "Obenseuer";

        public const string ghBIEOwner = "BepInEx";
        public const string ghBIERepo = "BepInEx";
        public const string ghProductHeaderValue = "obenseuer-modding-environment-installer";

        private static CLIArgs args;
        private static string installPath;
        private static GitHubClient ghClient;

        private static DateTime lastProgressReportTime = DateTime.Now;

        public async static Task<int> Main(string[] args)
        {
            var parsedArgs = Parser.Default.ParseArguments<CLIArgs>(args);
            Program.args = parsedArgs.Value;

            if (args.Where(s => s.Contains("-h") || s.Contains("--version")).Any() || parsedArgs.Errors.Any())
            {
                return 0;
            }

            installPath = ComputeAppInstallPath();

            if (installPath == null)
            {
                return 1;
            }

            ghClient = new GitHubClient(new Octokit.ProductHeaderValue(ghProductHeaderValue));

            // If we got here, everything was properly initialized
            if (Program.args.Uninstall)
            {
                Uninstall();
            } 
            else if (Program.args.Update)
            {
                await Install();
            }
            else if (Program.args.CheckIntegrity)
            {
                VerifyInstallerIntegrity();
                VerifyGameIntegrity();
            }
            else
            {
                await Install();
            }

            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();

            return 0;
        }

        private static string ComputeAppInstallPath()
        {
            try
            {
                SteamClient.Init(obenseuerAppId);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not run installer, SteamClient Initialization threw an error: {e}");
                return null;
            }

            if (!SteamApps.IsAppInstalled(obenseuerAppId))
            {
                Console.WriteLine("Could not run installer: Obenseuer is not installed!");
            }
            Console.WriteLine("Obenseuer is installed.");
            Console.WriteLine();

            string installPath = SteamApps.AppInstallDir(obenseuerAppId);
            SteamClient.Shutdown();

            return installPath;
        }

        private async static Task Install()
        {
            string targetPlatformName = args.Platform switch
            {
                "linux-x64" => "linux_x64",
                "linux-x86" => "linux_x86",
                "macos" => "macos_x64",
                "win-x64" => "win_x64",
                "win-x32" => "win_x32",
                _ => "win_x64"
            };
            string outputFilepath = Path.Combine(installPath, "bepinex-temp.zip");

            Console.WriteLine("Installing BepInEx from Github for platform {0} at filepath: {1}", targetPlatformName, outputFilepath);
            await DownloadFromGithub("BepInEx", ghBIEOwner, ghBIERepo, (r) => !r.Prerelease, (asset) => asset.Name.Contains(targetPlatformName), outputFilepath);

            Console.WriteLine("Extracting BepInEx compressed file into {0}", installPath);
            ZipFile.ExtractToDirectory(outputFilepath, installPath, true);

            Console.WriteLine("Removing temporary files...");
            File.Delete(outputFilepath);

            Console.WriteLine("Obenseuer modding environment installed!");
            Console.WriteLine("You should run the game once with the environment installed to ensure all files are correctly generated.");
        }

        private static void Uninstall()
        {
            File.Delete(Path.Combine(installPath, ".doorstop_version"));
            File.Delete(Path.Combine(installPath, "changelog.txt"));
            File.Delete(Path.Combine(installPath, "winhttp.dll"));
            File.Delete(Path.Combine(installPath, "doorstop_config.ini"));
            Directory.Delete(Path.Combine(installPath, "BepInEx"), true);

            Console.WriteLine("Successfully uninstalled Obenseuer modding environment!");
            Console.WriteLine("If you wish to remove the uninstaller, simply delete its parent folder, no other files exist.");
        }

        private static void VerifyInstallerIntegrity()
        {
            // doing this later, not important now
        }

        private static async Task DownloadFromGithub(
            string projectDisplayName, 
            string owner, 
            string repository, 
            Func<Release, bool> releasePredicate, 
            Func<ReleaseAsset, bool> assetPredicate,
            string outputFilepath)
        {
            var releases = await ghClient.Repository.Release.GetAll(owner, repository);

            Release target = releases.First(releasePredicate);
            ReleaseAsset downloadAsset = target.Assets.First(assetPredicate);

            var webClient = new HttpClient();
            var downloadStream = await webClient.GetStreamAsync(downloadAsset.BrowserDownloadUrl);

            FileStream outputFile = File.Create(outputFilepath);
            FileInfo outputFileInfo = new(outputFilepath);
            Task downloadTask = downloadStream.CopyToAsync(outputFile);

            while (!downloadTask.IsCompletedSuccessfully)
            {
                if (DateTime.Now > lastProgressReportTime + new TimeSpan(10_000_000))
                {
                    lastProgressReportTime = DateTime.Now;

                    Console.WriteLine("Downloading '{0}': {1:F2}%", projectDisplayName, (float)outputFileInfo.Length / downloadAsset.Size * 100);
                }
            }

            outputFile.Close();
            downloadStream.Close();

            Console.WriteLine("Successfully downloaded ");
        }

        private static void VerifyGameIntegrity()
        {
            var psi = new ProcessStartInfo
            {
                FileName = $@"steam://validate/{obenseuerAppId}",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            Process.Start(psi);
        }
    }
}
