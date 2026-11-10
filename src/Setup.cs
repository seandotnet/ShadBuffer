using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ShadBuffer
{
    public static class Setup
    {
        private const string ReplayStartedLine = "==== Replay Buffer Start";
        private const int ObsCloseTimeoutMs = 20000;
        private const int ReplayWaitMs = 45000;

        private static readonly string[][] UserIniKeys =
        {
            new[] { "General", "FirstRun" },
            new[] { "General", "ConfirmOnExit" },
            new[] { "BasicWindow", "SysTrayEnabled" },
            new[] { "BasicWindow", "SysTrayWhenStarted" },
            new[] { "Basic", "Profile" },
            new[] { "Basic", "ProfileDir" },
            new[] { "Basic", "SceneCollection" },
            new[] { "Basic", "SceneCollectionFile" },
        };

        public static string Install(Context ctx, Func<string, bool> confirm)
        {
            Directory.CreateDirectory(ctx.AppDir);
            State state = State.Load(ctx);
            Settings settings = Settings.Load(ctx);

            ctx.Report("Checking OBS");
            bool closeObs = AskToCloseObs(ctx, confirm);

            if (ctx.IsSandbox)
            {
                if (closeObs && !Obs.Close(ctx, ObsCloseTimeoutMs))
                {
                    throw new SetupException("OBS didn't close.");
                }
            }
            else
            {
                Companion.RequestQuit();
                ctx.Report("Waiting for admin approval (installing OBS can take a minute)");
                Elevated.Request(ctx, Elevated.ModeSetup, closeObs, settings);
            }

            string root = Obs.FindRoot(ctx);
            if (root == null)
            {
                throw new SetupException("Couldn't find OBS after installing it.");
            }
            state.ObsExe = Obs.ExePath(root);
            state.Save(ctx);

            ctx.Report("Backing up your OBS settings");
            BackUpConfig(ctx, state);

            ctx.Report("Checking your graphics card");
            DetectEncoder(ctx, state, root);

            ctx.Report("Creating the OBS profile");
            WriteObsConfig(ctx, settings, state, root);
            state.ProfileWritten = true;
            UpdateUserIni(ctx, state);
            settings.Save(ctx);
            state.Save(ctx);

            if (!ctx.IsSandbox)
            {
                ctx.Report("Applying Windows settings");
                if (settings.GameBarOff)
                {
                    WindowsTweaks.TurnOffGameBar(state);
                }
                WindowsTweaks.SetGpuPreference(state, state.ObsExe);
                state.Save(ctx);
                InstallApp(ctx, settings);
            }

            ctx.Report("Starting OBS");
            DateTime startedUtc = DateTime.UtcNow;
            StartObs(ctx, root);
            WaitForReplayBuffer(ctx, startedUtc);

            if (!ctx.IsSandbox)
            {
                Companion.EnsureRunning(ctx);
            }

            string message = $"Done. Press {settings.SaveHotkey} to save a clip and {settings.OverlayHotkey} for settings.";
            ctx.Report(message);
            return message;
        }

        public static string Remove(Context ctx, Func<string, bool> confirm)
        {
            State state = State.Load(ctx);
            Settings settings = Settings.Load(ctx);
            bool closeObs = AskToCloseObs(ctx, confirm);

            if (ctx.IsSandbox)
            {
                if (closeObs && !Obs.Close(ctx, ObsCloseTimeoutMs))
                {
                    throw new SetupException("OBS didn't close.");
                }
            }
            else
            {
                ctx.Report("Waiting for admin approval");
                Elevated.Request(ctx, Elevated.ModeRemove, closeObs, settings);
                Companion.RequestQuit();
                WindowsTweaks.RestoreGameBar(state);
                WindowsTweaks.RestoreGpuPreference(state);
                WindowsTweaks.SetTrayAtLogin(ctx.InstalledExe, false);
                WindowsTweaks.DeleteStartMenuShortcut();
            }

            ctx.Report("Removing the OBS profile");
            RestoreUserIni(ctx, state);
            if (state.ProfileWritten)
            {
                DeleteProfile(ctx);
            }
            foreach (string file in new[] { ctx.ScriptPath, ctx.SoundPath, ctx.StatePath, ctx.SettingsPath, ctx.StatusPath, ctx.CommandPath })
            {
                DeleteFile(file);
            }
            if (!ctx.IsSandbox)
            {
                DeleteInstalledExe(ctx);
            }

            string message = "Removed. Your clips, OBS itself and your other profiles were left alone.";
            ctx.Report(message);
            return message;
        }

        public static bool IsInstalled(Context ctx)
        {
            return File.Exists(ctx.StatePath);
        }

        public static void WriteObsConfig(Context ctx, Settings settings, State state, string root)
        {
            if (string.IsNullOrEmpty(state.EncoderBrand))
            {
                DetectEncoder(ctx, state, root);
            }
            EncoderChoice encoder = Encoders.Build(state.EncoderBrand, state.SupportsHevc, state.SupportsAv1, settings.Codec, settings.Quality);
            ctx.Log("Encoder " + encoder.Id);
            DisplayInfo canvas = SystemInfo.CanvasFor(settings);
            ProfileWriter.WriteAssets(ctx);
            ProfileWriter.WriteProfile(ctx, settings, encoder, canvas);
            ProfileWriter.WriteCollection(ctx, settings, canvas);
        }

        // nobody's there to answer obs's safe mode question at login, so a leftover marker would stop recording
        public static void ClearCrashMarker(Context ctx)
        {
            string folder = Path.Combine(ctx.ConfigDir, ".sentinel");
            if (Obs.IsRunning(ctx) || !Directory.Exists(folder))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(folder, "run_*"))
            {
                try
                {
                    File.Delete(file);
                    ctx.Log("OBS didn't shut down cleanly last time, skipping its safe mode question");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    ctx.Log("Couldn't clear the crash marker: " + e.Message);
                }
            }
        }

        public static void StartObs(Context ctx, string root)
        {
            ClearCrashMarker(ctx);
            if (ctx.IsSandbox)
            {
                string exe = Obs.ExePath(root);
                Process.Start(new ProcessStartInfo(exe, "--portable --disable-updater --multi " + Obs.LaunchArgs)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                })?.Dispose();
                return;
            }
            if (!StartupTask.Start())
            {
                throw new SetupException("OBS didn't start. It will start next time you log in.");
            }
        }

        // i only call it done once obs itself logs that the buffer is running
        public static void WaitForReplayBuffer(Context ctx, DateTime startedUtc)
        {
            string logs = Path.Combine(ctx.ConfigDir, "logs");
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < ReplayWaitMs)
            {
                Thread.Sleep(1000);
                if (!Directory.Exists(logs))
                {
                    continue;
                }
                FileInfo latest = new DirectoryInfo(logs).GetFiles("*.txt")
                    .Where(f => f.LastWriteTimeUtc >= startedUtc.AddSeconds(-2))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest != null && ReadShared(latest.FullName).Contains(ReplayStartedLine))
                {
                    return;
                }
            }
            throw new SetupException($"OBS opened but the replay buffer didn't start. The log is in {logs}.");
        }

        private static void DetectEncoder(Context ctx, State state, string root)
        {
            EncoderSupport support = Encoders.Detect(root, ctx);
            state.EncoderBrand = support.Brand;
            state.SupportsHevc = support.Hevc;
            state.SupportsAv1 = support.Av1;
            state.Save(ctx);
            ctx.Report(support.Brand == Encoders.BrandCpu ? "No hardware encoder found, using the CPU" : "Using your " + support.Brand + " encoder");
        }

        // the one-liner runs from a temp folder that gets deleted, so i keep my own copy
        private static void InstallApp(Context ctx, Settings settings)
        {
            string current = Assembly.GetEntryAssembly().Location;
            if (!string.Equals(current, ctx.InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(current, ctx.InstalledExe, true);
            }
            WindowsTweaks.SetTrayAtLogin(ctx.InstalledExe, settings.StartAtLogin);
            WindowsTweaks.CreateStartMenuShortcut(ctx.InstalledExe);
        }

        // a running exe can't delete itself so cmd does it a moment after i exit
        private static void DeleteInstalledExe(Context ctx)
        {
            string current = Assembly.GetEntryAssembly().Location;
            if (!string.Equals(current, ctx.InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(ctx.InstalledExe);
                return;
            }
            string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            Process.Start(new ProcessStartInfo(cmd, $"/c ping -n 4 127.0.0.1 >nul & del \"{ctx.InstalledExe}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }

        private static bool AskToCloseObs(Context ctx, Func<string, bool> confirm)
        {
            if (!Obs.IsRunning(ctx))
            {
                return false;
            }
            if (!confirm("OBS is open and has to close for this. Close it now?"))
            {
                throw new SetupException("Close OBS and run this again. Nothing was changed.", cancelled: true);
            }
            return true;
        }

        private static void BackUpConfig(Context ctx, State state)
        {
            if (!string.IsNullOrEmpty(state.BackupDir) || !Directory.Exists(ctx.ConfigDir))
            {
                return;
            }

            string target = Path.Combine(ctx.AppDir, "backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(target);
            CopyFolder(Path.Combine(ctx.ConfigDir, "basic"), Path.Combine(target, "basic"));
            foreach (string file in new[] { "user.ini", "global.ini" })
            {
                string source = Path.Combine(ctx.ConfigDir, file);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(target, file));
                }
            }
            state.BackupDir = target;
            state.Save(ctx);
        }

        private static void CopyFolder(string source, string target)
        {
            if (!Directory.Exists(source))
            {
                return;
            }
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            foreach (string folder in Directory.GetDirectories(source))
            {
                CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
            }
        }

        private static void UpdateUserIni(Context ctx, State state)
        {
            var ini = new IniFile(ctx.UserIniPath);
            foreach (string[] key in UserIniKeys)
            {
                string stateKey = key[0] + "|" + key[1];
                if (!state.UserIni.ContainsKey(stateKey))
                {
                    state.UserIni[stateKey] = ini.Get(key[0], key[1]);
                }
            }

            ini.Set("General", "FirstRun", "true");
            ini.Set("General", "ConfirmOnExit", "false");
            ini.Set("BasicWindow", "SysTrayEnabled", "true");
            ini.Set("BasicWindow", "SysTrayWhenStarted", "true");
            ini.Set("Basic", "Profile", Context.AppName);
            ini.Set("Basic", "ProfileDir", Context.AppName);
            ini.Set("Basic", "SceneCollection", Context.AppName);
            ini.Set("Basic", "SceneCollectionFile", Context.AppName);
            ini.Save();
        }

        private static void RestoreUserIni(Context ctx, State state)
        {
            if (state.UserIni.Count == 0 || !File.Exists(ctx.UserIniPath))
            {
                return;
            }
            var ini = new IniFile(ctx.UserIniPath);
            foreach (var entry in state.UserIni)
            {
                string[] parts = entry.Key.Split('|');
                if (entry.Value == null)
                {
                    ini.Remove(parts[0], parts[1]);
                }
                else
                {
                    ini.Set(parts[0], parts[1], entry.Value);
                }
            }
            ini.Save();
        }

        private static void DeleteProfile(Context ctx)
        {
            if (Directory.Exists(ctx.ProfileDir))
            {
                Directory.Delete(ctx.ProfileDir, true);
            }
            DeleteFile(ctx.CollectionPath);
            DeleteFile(ctx.CollectionPath + ".bak");
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static string ReadShared(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (IOException)
            {
                return "";
            }
        }
    }
}
