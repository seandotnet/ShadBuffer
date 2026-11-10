using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadBuffer
{
    // a normal solid window that only exists while it's open, it never touches the game
    public sealed class OverlayForm : Form
    {
        private const int DockWidth = 378;
        private const int HeaderHeight = 56;
        private const int TabsTop = 60;
        private const int TabsHeight = 52;
        private const int RowsTop = 121;
        private const int RowHeight = 62;
        private const int SaveBarHeight = 44;
        private const int SaveBarGap = 10;
        private const int MicHeight = 64;
        private const int MessageHeight = 72;
        private const int CornerPreference = 33;
        private const int CornerNone = 1;
        private const int StatusGraceSeconds = 15;
        private const int MessageSeconds = 5;

        private readonly Context ctx;
        private readonly Settings draft;
        private readonly Companion owner;
        private readonly IntPtr previousWindow;
        private readonly Rectangle screen;
        private readonly Timer statusTimer;
        private readonly LibraryTab clipsTab = new LibraryTab("Clips", Icons.Film);
        private readonly LibraryTab recordingsTab = new LibraryTab("Recordings", Icons.Video);
        private readonly SidebarRow recordRow = new SidebarRow("Record") { Clickable = true };
        private readonly SidebarRow replayRow = new SidebarRow("Instant Replay") { Icon = Icons.History };
        private readonly SidebarRow shotRow = new SidebarRow("Screenshot") { Icon = Icons.Photo, Clickable = true, Trail = Icons.Camera };
        private readonly SidebarRow micRow = new SidebarRow("Microphone") { Icon = Icons.Mic };
        private readonly ToggleSwitch replaySwitch = new ToggleSwitch();
        private readonly ActionButton saveButton = new ActionButton("Save clip", null, true);
        private readonly Label micValue = new Label { ForeColor = Theme.Text, BackColor = Theme.Background, TextAlign = ContentAlignment.MiddleCenter };
        private readonly Label messageLabel = new Label { ForeColor = Theme.Muted, BackColor = Theme.Background, TextAlign = ContentAlignment.BottomLeft };

        private Settings saved;
        private GalleryForm gallery;
        private bool recording;
        private bool micOn;
        private bool switching;
        private bool restoreFocus = true;
        private DateTime ignoreReplayUntil = DateTime.MinValue;
        private DateTime ignoreMicUntil = DateTime.MinValue;
        private DateTime messageUntil = DateTime.MinValue;
        private readonly DateTime openedAtUtc = DateTime.UtcNow;

        public bool Busy { get; set; }
        public bool DialogOpen { get; set; }

        public OverlayForm(Context ctx, Settings draft, Companion owner)
        {
            this.ctx = ctx;
            this.draft = draft;
            this.owner = owner;
            saved = Settings.Load(ctx);
            previousWindow = GetForegroundWindow();

            // opening on a different monitor than the main one needs that monitor's scale from the start
            Ui.UseMonitorOf(previousWindow);
            screen = (previousWindow != IntPtr.Zero ? Screen.FromHandle(previousWindow) : Screen.PrimaryScreen).Bounds;

            Text = Context.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Ui.Text(13);
            Bounds = new Rectangle(screen.Left, screen.Top, Ui.S(DockWidth), screen.Height);
            MaximumSize = Size;

            BuildHeader();
            BuildTabs();
            BuildRows();
            BuildMic();
            Controls.Add(messageLabel);
            messageLabel.Font = Ui.Text(12.5F);
            messageLabel.SetBounds(Ui.S(16), ClientSize.Height - Ui.S(MicHeight + MessageHeight) - 1, ClientSize.Width - Ui.S(32), Ui.S(MessageHeight - 8));

            micOn = saved.RecordMic;
            replaySwitch.Checked = saved.ReplayEnabled;
            UpdateHotkeyText();

            statusTimer = new Timer { Interval = 1000 };
            statusTimer.Tick += (_, __) => UpdateStatus();
            statusTimer.Start();
            UpdateStatus();

            Ui.FontChanged += OnFontChanged;

            Deactivate += (_, __) => CheckFocusSoon();
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseIfIdle();
                }
            };
        }

        public void CloseIfIdle()
        {
            if (!Busy && !DialogOpen)
            {
                Close();
            }
        }

        // show in folder and the like, the new window should stay on top of the game
        public void CloseForOtherApp()
        {
            restoreFocus = false;
            CloseIfIdle();
        }

        // focus moving to the gallery counts as a deactivate too, so this waits to see where it landed
        public void CheckFocusSoon()
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke(new Action(() =>
            {
                if ((DateTime.UtcNow - openedAtUtc).TotalMilliseconds < 500)
                {
                    return;
                }
                if (!IsDisposed && !Busy && !DialogOpen && ActiveForm == null)
                {
                    Close();
                }
            }));
        }

        public void UpdateTabs()
        {
            bool open = gallery != null && gallery.Visible && !gallery.ShowingSettings;
            clipsTab.Selected = open && gallery.Kind == LibraryKind.Clips;
            recordingsTab.Selected = open && gallery.Kind == LibraryKind.Recordings;
        }

        public void OnApplied(Settings applied)
        {
            saved = applied;
            owner.OnApplied(applied);
            UpdateHotkeyText();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int corner = CornerNone;
            DwmSetWindowAttribute(Handle, CornerPreference, ref corner, sizeof(int));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            BringToFront();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                int right = ClientSize.Width - 1;
                e.Graphics.DrawLine(pen, right, 0, right, ClientSize.Height);
                e.Graphics.DrawLine(pen, Ui.S(12), Ui.S(TabsTop + TabsHeight), right - Ui.S(12), Ui.S(TabsTop + TabsHeight));
                int micTop = ClientSize.Height - Ui.S(MicHeight) - 1;
                e.Graphics.DrawLine(pen, Ui.S(12), micTop, right - Ui.S(12), micTop);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            gallery?.Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Ui.FontChanged -= OnFontChanged;
            statusTimer.Dispose();
            if (restoreFocus && previousWindow != IntPtr.Zero)
            {
                SetForegroundWindow(previousWindow);
            }
            base.OnFormClosed(e);
        }

        private void OnFontChanged()
        {
            if (IsDisposed)
            {
                return;
            }
            Font = Ui.Text(13);
            messageLabel.Font = Ui.Text(12.5F);
            micValue.Font = Ui.Text(14);
            saveButton.Font = Ui.Strong(15);
            RefreshOverlayFonts(this);
            Invalidate(true);
        }

        private static void RefreshOverlayFonts(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Label lbl && lbl.Tag is float fontSize)
                {
                    lbl.Font = Ui.Text(fontSize, lbl.Font.Style);
                }
                RefreshOverlayFonts(c);
            }
        }

        private void BuildHeader()
        {
            var header = new Panel { BackColor = Theme.Panel, Bounds = new Rectangle(0, 0, ClientSize.Width - 1, Ui.S(HeaderHeight)) };
            header.Controls.Add(new Label
            {
                Text = Context.AppName,
                Font = Ui.Text(20, FontStyle.Bold),
                Tag = 20F,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(Ui.S(14), 0, Ui.S(240), header.Height),
            });

            int square = Ui.S(44);
            int top = (header.Height - square) / 2;
            var close = new IconButton(Icons.Close) { BackColor = Theme.Panel, IconSize = 12, Bounds = new Rectangle(header.Width - square - Ui.S(6), top, square, square) };
            var gear = new IconButton(Icons.Gear) { BackColor = Theme.Panel, IconSize = 16, Bounds = new Rectangle(close.Left - square - Ui.S(2), top, square, square) };
            close.Click += (_, __) => CloseIfIdle();
            gear.Click += (_, __) => ToggleSettings();
            header.Controls.Add(gear);
            header.Controls.Add(close);
            Controls.Add(header);
        }

        private void BuildTabs()
        {
            int half = (ClientSize.Width - 1 - Ui.S(24)) / 2;
            clipsTab.SetBounds(Ui.S(12), Ui.S(TabsTop), half, Ui.S(TabsHeight));
            recordingsTab.SetBounds(Ui.S(12) + half, Ui.S(TabsTop), half, Ui.S(TabsHeight));
            clipsTab.Click += (_, __) => OpenTab(LibraryKind.Clips);
            recordingsTab.Click += (_, __) => OpenTab(LibraryKind.Recordings);
            Controls.Add(clipsTab);
            Controls.Add(recordingsTab);
        }

        private void BuildRows()
        {
            int width = ClientSize.Width - 1;
            int y = Ui.S(RowsTop);

            recordRow.IconPainter = PaintRecordIcon;
            recordRow.SetBounds(0, y, width, Ui.S(RowHeight));
            recordRow.Click += (_, __) => owner.ToggleRecording();
            y += Ui.S(RowHeight);

            replayRow.SetBounds(0, y, width, Ui.S(RowHeight));
            replaySwitch.Location = new Point(width - Ui.S(16) - replaySwitch.Width, (replayRow.Height - replaySwitch.Height) / 2);
            replaySwitch.Toggled += (_, __) => _ = ToggleReplay();
            replayRow.Controls.Add(replaySwitch);
            y += Ui.S(RowHeight);

            saveButton.SetBounds(Ui.S(54), y, width - Ui.S(54 + 16), Ui.S(SaveBarHeight));
            saveButton.Click += (_, __) => SaveClip();
            y += Ui.S(SaveBarHeight + SaveBarGap);

            shotRow.SetBounds(0, y, width, Ui.S(RowHeight));
            shotRow.Click += (_, __) => owner.TakeScreenshot();

            Controls.Add(recordRow);
            Controls.Add(replayRow);
            Controls.Add(saveButton);
            Controls.Add(shotRow);
        }

        private void BuildMic()
        {
            int width = ClientSize.Width - 1;
            micRow.SetBounds(0, ClientSize.Height - Ui.S(MicHeight), width, Ui.S(MicHeight));

            int arrow = Ui.S(36);
            int value = Ui.S(34);
            int height = Ui.S(44);
            int top = (micRow.Height - height) / 2;
            var next = new IconButton(Icons.Right) { IconSize = 11, HoverColor = Theme.Panel, Bounds = new Rectangle(width - Ui.S(16) - arrow, top, arrow, height) };
            micValue.Font = Ui.Text(14);
            micValue.SetBounds(next.Left - value, top, value, height);
            var previous = new IconButton(Icons.Left) { IconSize = 11, HoverColor = Theme.Panel, Bounds = new Rectangle(micValue.Left - arrow, top, arrow, height) };
            next.Click += (_, __) => _ = ToggleMic();
            previous.Click += (_, __) => _ = ToggleMic();
            micRow.Controls.Add(previous);
            micRow.Controls.Add(micValue);
            micRow.Controls.Add(next);
            Controls.Add(micRow);
        }

        private void PaintRecordIcon(Graphics graphics, Rectangle box)
        {
            Color color = recording ? Theme.Danger : Theme.Text;
            int outer = Ui.S(16);
            int inner = Ui.S(6);
            int centerX = box.X + box.Width / 2;
            int centerY = box.Y + box.Height / 2;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, Ui.Scale * 1.6F))
            using (var brush = new SolidBrush(color))
            {
                graphics.DrawEllipse(pen, centerX - outer / 2, centerY - outer / 2, outer, outer);
                graphics.FillEllipse(brush, centerX - inner / 2, centerY - inner / 2, inner, inner);
            }
        }

        private void OpenTab(LibraryKind kind)
        {
            EnsureGallery();
            if (gallery.Visible && !gallery.ShowingSettings && gallery.Kind == kind && gallery.AtLibraryRoot)
            {
                gallery.CloseGallery();
                return;
            }
            gallery.ShowLibrary(kind);
            UpdateTabs();
        }

        private void ToggleSettings()
        {
            EnsureGallery();
            if (gallery.Visible && gallery.ShowingSettings)
            {
                gallery.CloseGallery();
                return;
            }
            gallery.ShowSettings();
            UpdateTabs();
        }

        private void EnsureGallery()
        {
            if (gallery == null)
            {
                gallery = new GalleryForm(ctx, owner, this, draft, screen);
            }
        }

        private void SaveClip()
        {
            if (!replaySwitch.Checked)
            {
                ShowMessage("Turn on Instant Replay to save a clip.");
                return;
            }
            owner.SaveClip();
        }

        private async Task ToggleReplay()
        {
            bool on = replaySwitch.Checked;
            switching = true;
            replaySwitch.Enabled = false;
            ignoreReplayUntil = DateTime.UtcNow.AddSeconds(StatusGraceSeconds);
            string failure = await Ui.RunSafely(ctx, () => ObsControl.SetReplay(ctx, on));
            switching = false;
            draft.ReplayEnabled = on;
            saved.ReplayEnabled = on;
            if (IsDisposed)
            {
                return;
            }
            if (failure != null)
            {
                ignoreReplayUntil = DateTime.MinValue;
                ShowMessage(failure);
            }
            UpdateStatus();
        }

        private async Task ToggleMic()
        {
            bool on = !micOn;
            micOn = on;
            micValue.Text = on ? "On" : "Off";
            ignoreMicUntil = DateTime.UtcNow.AddSeconds(StatusGraceSeconds);
            string failure = await Ui.RunSafely(ctx, () => ObsControl.SetMic(ctx, on));
            draft.RecordMic = on;
            saved.RecordMic = on;
            if (IsDisposed)
            {
                return;
            }
            if (failure != null)
            {
                ignoreMicUntil = DateTime.MinValue;
                ShowMessage(failure);
            }
            UpdateStatus();
        }

        private void UpdateHotkeyText()
        {
            replayRow.Subtitle = HotkeyLine(saved.ReplayHotkey, "Toggle on/off");
            shotRow.Subtitle = HotkeyLine(saved.ScreenshotHotkey, "Save a screenshot");
            micRow.Subtitle = HotkeyLine(saved.MicHotkey, "Toggle on/off");
            saveButton.Hint = saved.SaveHotkey;
            UpdateRecordRow(null);
        }

        private string HotkeyLine(string hotkey, string action)
        {
            if (string.IsNullOrEmpty(hotkey))
            {
                return action;
            }
            return owner.HotkeyIsTaken(hotkey) ? hotkey + " is in use" : hotkey + " - " + action;
        }

        private void UpdateRecordRow(ObsStatus status)
        {
            bool wasRecording = recording;
            recording = status?.Recording ?? recording;
            string action = "Start";
            if (recording)
            {
                TimeSpan elapsed = DateTime.UtcNow - (status?.RecordingSinceUtc ?? DateTime.UtcNow);
                action = "Stop, recording " + Library.LengthText(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
            }
            recordRow.Subtitle = HotkeyLine(saved.RecordHotkey, action);
            recordRow.Trail = recording ? Icons.Stop : Icons.Play;
            recordRow.TrailColor = recording ? Theme.Danger : Theme.Text;
            if (wasRecording != recording)
            {
                recordRow.Invalidate();
            }
        }

        private void ShowMessage(string text)
        {
            messageLabel.Text = text;
            messageUntil = DateTime.UtcNow.AddSeconds(MessageSeconds);
        }

        private void UpdateStatus()
        {
            ObsStatus status = ObsControl.ReadStatus(ctx);
            UpdateRecordRow(status);

            // obs takes a few seconds to catch up after a click, so the switch doesn't flick back
            if (DateTime.UtcNow >= ignoreReplayUntil || status.Replay == replaySwitch.Checked)
            {
                replaySwitch.Checked = status.ObsRunning ? status.Replay : saved.ReplayEnabled;
            }
            replaySwitch.Enabled = !switching && (!status.ObsRunning || status.ScriptRunning);

            if (status.Mic != null && (DateTime.UtcNow >= ignoreMicUntil || status.Mic == micOn))
            {
                micOn = status.Mic.Value;
            }
            micValue.Text = micOn ? "On" : "Off";

            if (DateTime.UtcNow >= messageUntil)
            {
                messageLabel.Text = ProblemText(status);
            }
        }

        private string ProblemText(ObsStatus status)
        {
            if (!status.ObsRunning)
            {
                return "OBS isn't running. Record and Instant Replay will start it.";
            }
            if (!status.ScriptRunning)
            {
                return Obs.VisiblePopup(ctx) != null ? "OBS is waiting on a popup." : "OBS is on another profile, so ShadBuffer can't control it.";
            }
            return "";
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
