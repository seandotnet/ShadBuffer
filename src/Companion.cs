using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadBuffer
{
    // stays in the tray, listens for the overlay hotkey and watches obs
    public sealed class Companion : ApplicationContext
    {
        private const string MutexName = @"Local\ShadBuffer.Companion";
        private const string QuitEventName = @"Local\ShadBuffer.Quit";
        private const string ShowOverlayEventName = @"Local\ShadBuffer.ShowOverlay";
        private const int OverlayHotkeyId = 1;
        private const int TestHotkeyId = 2;
        private const int RecordHotkeyId = 3;
        private const int ScreenshotHotkeyId = 4;
        private const int ReplayHotkeyId = 5;
        private const int MicHotkeyId = 6;
        private const int WmHotkey = 0x0312;
        private const int QuitWaitMs = 5000;
        private const int SaveDelayMs = 300;

        private readonly Context ctx;
        private readonly NotifyIcon tray;
        private readonly HotkeyWindow window;
        private readonly System.Windows.Forms.Timer timer;
        private readonly EventWaitHandle quitEvent;
        private readonly EventWaitHandle showOverlayEvent;
        private readonly RegisteredWaitHandle showOverlayRegistration;
        private readonly SynchronizationContext syncContext;
        private readonly Icon iconOn = MakeIcon(Color.FromArgb(235, 64, 64));
        private readonly Icon iconOff = MakeIcon(Color.FromArgb(120, 120, 128));

        private readonly HashSet<string> takenHotkeys = new HashSet<string>();

        private OverlayForm overlay;
        private Settings draft;
        private int checkedReplayObsId;
        private int checkedMicObsId;

        public Uploader Uploads { get; }

        public Companion(Context ctx)
        {
            this.ctx = ctx;
            syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Ui.ApplySettings(Settings.Load(ctx), ctx);
            quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
            showOverlayEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowOverlayEventName);
            showOverlayRegistration = ThreadPool.RegisterWaitForSingleObject(showOverlayEvent, (_, __) =>
            {
                syncContext.Post(_ =>
                {
                    if (overlay == null)
                    {
                        ToggleOverlay();
                    }
                    else
                    {
                        overlay.Activate();
                    }
                }, null);
            }, null, -1, false);

            window = new HotkeyWindow(OnHotkey);
            tray = new NotifyIcon { Icon = iconOff, Text = Context.AppName, Visible = true };
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleOverlay();
                }
            };

            RegisterHotkeys();

            // the menu above is the first real control, so the ui context exists from here on
            Uploads = new Uploader(ctx);
            Uploads.Changed += OnUploadChanged;

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, __) => Tick();
            timer.Start();
        }

        public static int Run(Context ctx, bool atLogin, bool showOverlay = false)
        {
            using (var mutex = new Mutex(true, MutexName, out bool first))
            {
                if (!first)
                {
                    if (showOverlay)
                    {
                        SignalShowOverlay();
                    }
                    return 0;
                }
                using (var companion = new Companion(ctx))
                {
                    if (atLogin)
                    {
                        companion.StartObsAtLogin();
                    }
                    else if (showOverlay)
                    {
                        companion.ToggleOverlay();
                    }
                    Application.Run(companion);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        public static bool SignalShowOverlay()
        {
            if (EventWaitHandle.TryOpenExisting(ShowOverlayEventName, out EventWaitHandle handle))
            {
                using (handle)
                {
                    handle.Set();
                    return true;
                }
            }
            return false;
        }

        // left off stays off across a reboot, so obs only starts if recording is meant to be on
        public void StartObsAtLogin()
        {
            Task.Run(() =>
            {
                try
                {
                    if (Settings.Load(ctx).ReplayEnabled && !Obs.IsRunning(ctx))
                    {
                        Setup.StartObs(ctx, Obs.FindRoot(ctx));
                    }
                }
                catch (Exception e)
                {
                    ctx.Log(e.ToString());
                    tray.ShowBalloonTip(5000, Context.AppName, "OBS didn't start, so nothing is recording. Press the overlay hotkey to try again.", ToolTipIcon.Warning);
                }
            });
        }

        public static bool IsRunning()
        {
            return Mutex.TryOpenExisting(MutexName, out Mutex mutex) && SafeDispose(mutex);
        }

        public static void EnsureRunning(Context ctx, bool showOverlay = false)
        {
            if (!IsRunning() && File.Exists(ctx.InstalledExe))
            {
                string args = showOverlay ? "--tray --show-overlay" : "--tray";
                Process.Start(new ProcessStartInfo(ctx.InstalledExe, args)
                {
                    UseShellExecute = true,
                    WorkingDirectory = ctx.AppDir,
                })?.Dispose();
            }
            else if (showOverlay)
            {
                SignalShowOverlay();
            }
        }

        // setup has to overwrite my exe, so it asks the running one to get out of the way
        public static void RequestQuit()
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle quit))
            {
                return;
            }
            using (quit)
            {
                quit.Set();
            }
            Stopwatch wait = Stopwatch.StartNew();
            while (IsRunning() && wait.ElapsedMilliseconds < QuitWaitMs)
            {
                Thread.Sleep(100);
            }
        }

        public void ToggleOverlay()
        {
            if (overlay != null)
            {
                overlay.CloseIfIdle();
                return;
            }
            draft = draft ?? Settings.Load(ctx);
            overlay = new OverlayForm(ctx, draft, this);
            // closing the form skips the box's leave event, so the hotkey comes back here too
            overlay.FormClosed += (_, __) =>
            {
                overlay = null;
                RegisterHotkeys();
            };
            overlay.Show();
            overlay.Activate();
            overlay.BringToFront();
        }

        // windows swallows a registered combo, so a hotkey box couldn't see it otherwise
        public void PauseHotkeys(bool paused)
        {
            if (paused)
            {
                UnregisterHotkeys();
            }
            else
            {
                RegisterHotkeys();
            }
        }

        public bool HotkeyIsTaken(string hotkey)
        {
            return takenHotkeys.Contains(hotkey);
        }

        // tells the overlay if another app already owns the combo
        public bool IsHotkeyFree(Hotkey hotkey)
        {
            if (!RegisterHotKey(window.Handle, TestHotkeyId, hotkey.Modifiers, hotkey.VirtualKey))
            {
                return false;
            }
            UnregisterHotKey(window.Handle, TestHotkeyId);
            return true;
        }

        public void OnApplied(Settings saved)
        {
            draft = null;
            RegisterHotkeys();
        }

        public void SaveClip()
        {
            overlay?.CloseIfIdle();
            Task.Run(async () =>
            {
                // the game needs focus back first so the clip gets sorted under it
                await Task.Delay(SaveDelayMs);
                if (!ObsControl.Send(ctx, ObsControl.Save))
                {
                    tray.ShowBalloonTip(3000, Context.AppName, "Couldn't save a clip because OBS isn't recording on the ShadBuffer profile.", ToolTipIcon.Warning);
                }
            });
        }

        public void ToggleRecording()
        {
            bool starting = !ObsControl.ReadStatus(ctx).Recording;
            if (starting)
            {
                overlay?.CloseIfIdle();
            }
            RunAfterFocus(starting ? SaveDelayMs : 0, () => ObsControl.ToggleRecording(ctx));
        }

        public void TakeScreenshot()
        {
            overlay?.CloseIfIdle();
            RunAfterFocus(SaveDelayMs, () => ObsControl.TakeScreenshot(ctx));
        }

        public void OpenClips()
        {
            overlay?.CloseIfIdle();
            string folder = Settings.Load(ctx).ClipsDir;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                showOverlayRegistration?.Unregister(null);
                showOverlayEvent.Dispose();
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                UnregisterHotkeys();
                window.DestroyHandle();
                quitEvent.Dispose();
                iconOn.Dispose();
                iconOff.Dispose();
            }
            base.Dispose(disposing);
        }

        private static bool SafeDispose(IDisposable item)
        {
            item.Dispose();
            return true;
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open overlay", null, (_, __) => ToggleOverlay());
            menu.Items.Add("Save clip", null, (_, __) => SaveClip());
            menu.Items.Add("Open clips", null, (_, __) => OpenClips());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Set up or remove...", null, (_, __) => OpenSetupWindow());
            menu.Items.Add("Quit", null, (_, __) => QuitAll());
            return menu;
        }

        private void OpenSetupWindow()
        {
            if (File.Exists(ctx.InstalledExe))
            {
                Process.Start(new ProcessStartInfo(ctx.InstalledExe, "--setup") { UseShellExecute = true })?.Dispose();
            }
        }

        private void QuitAll()
        {
            if (Obs.IsRunning(ctx))
            {
                ObsControl.Send(ctx, ObsControl.Quit);
            }
            overlay?.Close();
            ExitThread();
        }

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            takenHotkeys.Clear();
            Settings settings = Settings.Load(ctx);

            Hotkey overlayKey = Hotkey.Parse(settings.OverlayHotkey);
            if (overlayKey == null || !RegisterHotKey(window.Handle, OverlayHotkeyId, overlayKey.Modifiers, overlayKey.VirtualKey))
            {
                tray.ShowBalloonTip(5000, Context.AppName, "The overlay hotkey is taken by another app. Open the overlay from this tray icon to change it.", ToolTipIcon.Warning);
            }
            RegisterOptional(RecordHotkeyId, settings.RecordHotkey);
            RegisterOptional(ScreenshotHotkeyId, settings.ScreenshotHotkey);
            RegisterOptional(ReplayHotkeyId, settings.ReplayHotkey);
            RegisterOptional(MicHotkeyId, settings.MicHotkey);
        }

        // these stay quiet at login, the overlay shows which ones another app got first
        private void RegisterOptional(int id, string combo)
        {
            Hotkey hotkey = Hotkey.Parse(combo);
            if (hotkey != null && !RegisterHotKey(window.Handle, id, hotkey.Modifiers, hotkey.VirtualKey))
            {
                takenHotkeys.Add(combo);
            }
        }

        // windows keeps the old combo if the same id registers twice, so these always clear first
        private void UnregisterHotkeys()
        {
            foreach (int id in new[] { OverlayHotkeyId, RecordHotkeyId, ScreenshotHotkeyId, ReplayHotkeyId, MicHotkeyId })
            {
                UnregisterHotKey(window.Handle, id);
            }
        }

        private void OnHotkey(int id)
        {
            switch (id)
            {
                case OverlayHotkeyId:
                    ToggleOverlay();
                    break;
                case RecordHotkeyId:
                    ToggleRecording();
                    break;
                case ScreenshotHotkeyId:
                    TakeScreenshot();
                    break;
                case ReplayHotkeyId:
                    ToggleReplayFromHotkey();
                    break;
                case MicHotkeyId:
                    ToggleMicFromHotkey();
                    break;
            }
        }

        private void ToggleReplayFromHotkey()
        {
            bool on = !Settings.Load(ctx).ReplayEnabled;
            RunAfterFocus(0, () =>
            {
                ObsControl.SetReplay(ctx, on);
                PlayToggleSound();
            });
        }

        private void ToggleMicFromHotkey()
        {
            bool on = !Settings.Load(ctx).RecordMic;
            RunAfterFocus(0, () =>
            {
                ObsControl.SetMic(ctx, on);
                PlayToggleSound();
            });
        }

        // there's nothing on screen in game, so a sound is the only way to know it worked
        private void PlayToggleSound()
        {
            if (Settings.Load(ctx).SaveSound && File.Exists(ctx.SoundPath))
            {
                using (var player = new SoundPlayer(ctx.SoundPath))
                {
                    player.Play();
                }
            }
        }

        private void RunAfterFocus(int delayMs, Action work)
        {
            Task.Run(async () =>
            {
                // the game needs focus back first so it gets sorted under the right name
                await Task.Delay(delayMs);
                try
                {
                    work();
                }
                catch (SetupException e)
                {
                    ctx.Log(e.Message);
                    tray.ShowBalloonTip(5000, Context.AppName, e.Message, ToolTipIcon.Warning);
                }
                catch (Exception e)
                {
                    ctx.Log(e.ToString());
                    tray.ShowBalloonTip(5000, Context.AppName, "Something went wrong: " + e.Message, ToolTipIcon.Warning);
                }
            });
        }

        private void OnUploadChanged(UploadJob job)
        {
            if (job.State == UploadState.Done && overlay == null)
            {
                tray.ShowBalloonTip(5000, Context.AppName, "Your upload is ready. Open the overlay to copy the link.", ToolTipIcon.Info);
            }
        }

        private void Tick()
        {
            if (quitEvent.WaitOne(0))
            {
                overlay?.Close();
                ExitThread();
                return;
            }

            if (showOverlayEvent.WaitOne(0))
            {
                if (overlay == null)
                {
                    ToggleOverlay();
                }
                else
                {
                    overlay.Activate();
                    overlay.BringToFront();
                }
            }

            ObsStatus status = ObsControl.ReadStatus(ctx);
            tray.Icon = status.Replay || status.Recording ? iconOn : iconOff;
            string text = status.Recording ? "Recording" : status.Replay ? "Instant Replay on" : status.ObsRunning ? "Instant Replay off" : "OBS isn't running";
            tray.Text = Truncate(Context.AppName + ": " + text);

            EnforceReplayOff(status);
            EnforceMic(status);
        }

        // obs always starts its buffer at login, so i turn it back off if the user left it off
        private void EnforceReplayOff(ObsStatus status)
        {
            if (!status.ScriptRunning || !status.Replay)
            {
                return;
            }
            int id = Obs.ProcessId(ctx);
            if (id == checkedReplayObsId)
            {
                return;
            }
            checkedReplayObsId = id;
            if (!Settings.Load(ctx).ReplayEnabled)
            {
                Task.Run(() => ObsControl.Send(ctx, ObsControl.Stop));
            }
        }

        // the mic can be flipped while obs is closed, and obs reopens the way it last saved it
        private void EnforceMic(ObsStatus status)
        {
            if (!status.ScriptRunning || status.Mic == null)
            {
                return;
            }
            int id = Obs.ProcessId(ctx);
            if (id == checkedMicObsId)
            {
                return;
            }
            checkedMicObsId = id;
            bool on = Settings.Load(ctx).RecordMic;
            if (status.Mic != on)
            {
                Task.Run(() => ObsControl.Send(ctx, on ? ObsControl.MicOn : ObsControl.MicOff));
            }
        }

        private static string Truncate(string text)
        {
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private static Icon MakeIcon(Color dot)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var ring = new SolidBrush(Color.FromArgb(39, 39, 42)))
            using (var fill = new SolidBrush(dot))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(ring, 1, 1, 30, 30);
                graphics.FillEllipse(fill, 8, 8, 16, 16);
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private sealed class HotkeyWindow : NativeWindow
        {
            private readonly Action<int> pressed;

            public HotkeyWindow(Action<int> pressed)
            {
                this.pressed = pressed;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WmHotkey)
                {
                    pressed(message.WParam.ToInt32());
                }
                base.WndProc(ref message);
            }
        }
    }
}
