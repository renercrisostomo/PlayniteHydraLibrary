using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace HydraPlaynite.Client
{
    public class HydraClient : LibraryClient
    {
        private readonly string iconPath;

        public HydraClient(string iconPath)
        {
            this.iconPath = iconPath;
        }

        public override string Icon
        {
            get { return iconPath; }
        }

        public override bool IsInstalled
        {
            get
            {
                if (!string.IsNullOrEmpty(FindHydraExecutable()))
                    return true;

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Directory.Exists(Path.Combine(appData, "hydra")) ||
                       Directory.Exists(Path.Combine(appData, "Hydra"));
            }
        }

        public override void Open()
        {
            string executable = FindHydraExecutable();
            if (!string.IsNullOrEmpty(executable))
            {
                Process.Start(executable);
                return;
            }
            Process.Start("hydralauncher://");
        }

        private static string FindHydraExecutable()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = new string[]
            {
                Path.Combine(localAppData, "Programs", "hydra", "Hydra.exe"),
                Path.Combine(localAppData, "Programs", "Hydra", "Hydra.exe"),
                Path.Combine(localAppData, "hydra", "Hydra.exe")
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
