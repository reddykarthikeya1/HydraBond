using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HydraBond {
    static class Program {
        [STAThread]
        static void Main() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string serverJs = Path.Combine(baseDir, "server.js");

            if (!File.Exists(serverJs)) {
                MessageBox.Show("Could not find server.js in: " + baseDir, "HydraBond Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 1. Launch background Node.js engine silently
            ProcessStartInfo nodePsi = new ProcessStartInfo {
                FileName = "node",
                Arguments = "\"" + serverJs + "\"",
                WorkingDirectory = baseDir,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            try {
                Process.Start(nodePsi);
            } catch (Exception ex) {
                MessageBox.Show("Node.js is required to run HydraBond.\nPlease install it from https://nodejs.org\n\nError: " + ex.Message, 
                    "Node.js Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Brief wait for local dashboard server to start listening
            Thread.Sleep(800);

            // 2. Locate browser (Brave, Chrome, or Edge)
            string bravePath = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
            string braveUserPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe");
            string chromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            string chromeUserPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe");
            string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            string edgePath64 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";

            string browserExe = null;
            if (File.Exists(bravePath)) browserExe = bravePath;
            else if (File.Exists(braveUserPath)) browserExe = braveUserPath;
            else if (File.Exists(chromePath)) browserExe = chromePath;
            else if (File.Exists(chromeUserPath)) browserExe = chromeUserPath;
            else if (File.Exists(edgePath)) browserExe = edgePath;
            else if (File.Exists(edgePath64)) browserExe = edgePath64;

            string appUrl = "http://127.0.0.1:4545";
            string tempProfile = Path.Combine(Path.GetTempPath(), "hydrabond_app_profile");

            if (browserExe != null) {
                ProcessStartInfo appPsi = new ProcessStartInfo {
                    FileName = browserExe,
                    Arguments = "--app=\"" + appUrl + "\" --window-size=1080,720 --user-data-dir=\"" + tempProfile + "\"",
                    UseShellExecute = true
                };
                try {
                    Process.Start(appPsi);
                    return;
                } catch { }
            }

            // Fallback: Open in default browser
            try {
                Process.Start(appUrl);
            } catch { }
        }
    }
}
