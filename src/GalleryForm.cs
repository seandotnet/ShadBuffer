using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadBuffer
{
    // the panel that floats beside the sidebar, holds the library and the settings
    public sealed class GalleryForm : Form
    {
        private const int Gap = 16;
        private const int BarHeight = 64;
        private const int CornerPreference = 33;
        private const int CornerRound = 2;
        private const int BorderColorAttribute = 34;

        private readonly Context ctx;
        private readonly Companion owner;
        private readonly OverlayForm dock;
        private readonly Settings draft;
        private readonly Rectangle area;
        private readonly MediaCache cache = new MediaCache();

        private readonly Panel bar = new Panel { BackColor = Theme.Bar };
        private readonly Panel barLine = new Panel { BackColor = Theme.Border };
        private readonly IconButton backButton = new IconButton(Icons.Left) { Boxed = true };
        private readonly IconButton closeButton = new IconButton(Icons.Close) { BackColor = Theme.Bar, IconColor = Theme.Muted, HoverColor = Theme.Panel, IconSize = 12 };
        private readonly Breadcrumb crumb = new Breadcrumb { BackColor = Theme.Bar };
        private readonly SegmentToggle sort = new SegmentToggle("Recently modified", "Biggest first") { BackColor = Theme.Bar };
        private readonly LibraryGrid grid;

        private PlayerPanel player;
        private SettingsPanel settings;
        private List<LibraryFolder> folders = new List<LibraryFolder>();
        private LibraryFolder openFolder;
        private LibraryFile openFile;
        private int scanNumber;
        private Rectangle preFullScreenBounds;

        public bool IsFullScreen { get; private set; }

        public LibraryKind Kind { get; private set; }
        public bool ShowingSettings { get; private set; }
        public bool AtLibraryRoot => !ShowingSettings && openFolder == null && openFile == null;

        public GalleryForm(Context ctx, Companion owner, OverlayForm dock, Settings draft, Rectangle screen)
        {
            this.ctx = ctx;
            this.owner = owner;
            this.dock = dock;
            this.draft = draft;

            Owner = dock;
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

            int left = screen.Left + dock.Width + Ui.S(Gap);
            area = new Rectangle(left, screen.Top + Ui.S(Gap), screen.Right - left - Ui.S(Gap), screen.Height - Ui.S(Gap) * 2);
            Bounds = area;

            backButton.BackColor = Theme.Bar;
            backButton.Click += (_, __) => GoBack();
            closeButton.Click += (_, __) => CloseGallery();
            sort.Changed += OnSortChanged;
            bar.Controls.AddRange(new Control[] { backButton, crumb, sort, closeButton });
            Controls.Add(bar);
            Controls.Add(barLine);

            grid = new LibraryGrid(cache);
            grid.FolderOpened += OpenFolder;
            grid.FileOpened += OpenFile;
            Controls.Add(grid);

            KeyDown += OnKey;
            Deactivate += (_, __) => dock.CheckFocusSoon();
            Ui.FontChanged += OnFontChanged;
        }

        public void ShowLibrary(LibraryKind kind)
        {
            ShowingSettings = false;
            player?.Unload();
            Kind = kind;
            openFolder = null;
            openFile = null;
            Bounds = area;
            ShowPanel();
            Render();
            _ = Rescan();
        }

        public void ShowSettings()
        {
            ShowingSettings = true;
            player?.Unload();
            openFile = null;
            openFolder = null;
            if (settings == null)
            {
                settings = new SettingsPanel(ctx, draft, owner, value => dock.Busy = value, value => dock.DialogOpen = value, dock.OnApplied);
                Controls.Add(settings);
            }
            int height = Math.Min(area.Height, Ui.S(BarHeight + SettingsPanel.PageHeight));
            Bounds = new Rectangle(area.X, area.Y, Ui.S(SettingsPanel.PageWidth), height);
            ShowPanel();
            Render();
        }

        public void CloseGallery()
        {
            if (IsFullScreen)
            {
                ToggleFullScreen();
            }
            // focus has to land before this hides, or the focus check closes the whole overlay
            player?.Unload();
            dock.Activate();
            Hide();
            dock.UpdateTabs();
        }

        // only the notice talks to the user here, and it's a normal modal window
        public bool ConfirmUpload()
        {
            dock.DialogOpen = true;
            bool confirmed;
            using (var notice = new UploadNoticeForm())
            {
                Rectangle screen = Screen.FromControl(this).Bounds;
                notice.Location = new Point(screen.Left + (screen.Width - notice.Width) / 2, screen.Top + (screen.Height - notice.Height) / 2);
                confirmed = notice.ShowDialog(this) == DialogResult.OK;
            }
            dock.DialogOpen = false;
            dock.CheckFocusSoon();
            return confirmed;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int corner = CornerRound;
            DwmSetWindowAttribute(Handle, CornerPreference, ref corner, sizeof(int));
            int border = ColorTranslator.ToWin32(Theme.Border);
            DwmSetWindowAttribute(Handle, BorderColorAttribute, ref border, sizeof(int));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutPage();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!IsFullScreen)
            {
                using (var pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Ui.FontChanged -= OnFontChanged;
            player?.Unload();
            cache.Dispose();
            base.OnFormClosed(e);
        }

        private void OnFontChanged()
        {
            if (IsDisposed)
            {
                return;
            }
            Font = Ui.Text(13);
            Invalidate(true);
        }

        private void ShowPanel()
        {
            if (!Visible)
            {
                Show();
            }
            Activate();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (IsFullScreen)
                {
                    ToggleFullScreen();
                    e.Handled = true;
                }
                else
                {
                    dock.CloseIfIdle();
                }
            }
            else if ((e.KeyCode == Keys.F || e.KeyCode == Keys.F11) && openFile != null)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.M && openFile != null && player != null)
            {
                player.ToggleMute();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Space && openFile != null && player != null)
            {
                player.Toggle();
                e.Handled = true;
            }
        }

        private async Task Rescan()
        {
            int number = ++scanNumber;
            Settings saved = Settings.Load(ctx);
            string root = Kind == LibraryKind.Clips ? saved.ClipsDir : saved.RecordingsDir;
            ObsStatus status = ObsControl.ReadStatus(ctx);
            DateTime? writing = Kind == LibraryKind.Recordings && status.Recording ? status.RecordingSinceUtc ?? DateTime.UtcNow : (DateTime?)null;

            List<LibraryFolder> found = await Task.Run(() => Library.Scan(root, writing));
            if (IsDisposed || number != scanNumber)
            {
                return;
            }
            folders = found;
            if (openFolder != null)
            {
                openFolder = folders.FirstOrDefault(folder => folder.Name == openFolder.Name);
            }
            ShowCurrentGrid();
            Render();
        }

        private bool BySize => sort.Selected == 1;
        private string Noun => Kind == LibraryKind.Clips ? "clip" : "recording";

        private string EmptyText()
        {
            Settings saved = Settings.Load(ctx);
            if (Kind == LibraryKind.Clips)
            {
                return "No clips yet. Press " + saved.SaveHotkey + " with Instant Replay on to save one.";
            }
            return string.IsNullOrEmpty(saved.RecordHotkey)
                ? "No recordings yet. Press Record to start one."
                : "No recordings yet. Press " + saved.RecordHotkey + " to start one.";
        }

        private void ShowCurrentGrid()
        {
            if (openFolder != null)
            {
                grid.ShowFiles(Library.Sorted(openFolder, BySize));
            }
            else
            {
                grid.ShowFolders(Library.Sorted(folders, BySize), Noun, EmptyText());
            }
        }

        private void OnSortChanged()
        {
            if (openFolder != null)
            {
                grid.UpdateFiles(Library.Sorted(openFolder, BySize));
            }
            else
            {
                grid.UpdateFolders(Library.Sorted(folders, BySize));
            }
        }

        private void OpenFolder(LibraryFolder folder)
        {
            openFolder = folder;
            grid.ShowFiles(Library.Sorted(folder, BySize));
            Render();
        }

        private void OpenFile(LibraryFile file)
        {
            openFile = file;
            if (player == null)
            {
                player = new PlayerPanel(ctx, owner.Uploads, cache, ConfirmUpload, dock.CloseForOtherApp);
                player.FilePicked += OpenFile;
                player.FullScreenRequested += ToggleFullScreen;
                Controls.Add(player);
                LayoutPage();
            }
            Render();
            player.Open(Library.Sorted(openFolder, BySize), file, openFolder.Name);
        }

        private void GoBack()
        {
            if (IsFullScreen)
            {
                ToggleFullScreen();
            }
            if (openFile != null)
            {
                player.Unload();
                openFile = null;
            }
            else
            {
                openFolder = null;
            }
            Render();
            _ = Rescan();
        }

        private void GoToRoot()
        {
            if (IsFullScreen)
            {
                ToggleFullScreen();
            }
            player?.Unload();
            openFile = null;
            openFolder = null;
            Render();
            _ = Rescan();
        }

        private void GoToFolder()
        {
            if (IsFullScreen)
            {
                ToggleFullScreen();
            }
            player?.Unload();
            openFile = null;
            Render();
            _ = Rescan();
        }

        private void Render()
        {
            bool inPlayer = !ShowingSettings && openFile != null;
            grid.Visible = !ShowingSettings && openFile == null;
            if (player != null)
            {
                player.Visible = inPlayer;
            }
            if (settings != null)
            {
                settings.Visible = ShowingSettings;
            }
            backButton.Visible = !ShowingSettings && openFolder != null;
            sort.Visible = !ShowingSettings && openFile == null;

            if (ShowingSettings)
            {
                crumb.Set(null, Part("Settings", null));
            }
            else
            {
                string root = Kind == LibraryKind.Clips ? "Clips" : "Recordings";
                if (openFile != null)
                {
                    string folderName = openFolder != null ? openFolder.Name : root;
                    crumb.Set(null, Part(root, GoToRoot), Part(folderName, GoToFolder), Part(Library.When(openFile.Modified), null));
                }
                else if (openFolder != null)
                {
                    crumb.Set(Library.Plural(openFolder.Files.Count, Noun) + ", " + Library.SizeText(openFolder.Size), Part(root, GoToRoot), Part(openFolder.Name, null));
                }
                else
                {
                    crumb.Set(Library.Plural(folders.Count, "folder"), Part(root, null));
                }
            }
            LayoutPage();
            dock.UpdateTabs();
        }

        private static KeyValuePair<string, Action> Part(string text, Action click)
        {
            return new KeyValuePair<string, Action>(text, click);
        }

        public void ToggleFullScreen()
        {
            if (player == null || openFile == null)
            {
                return;
            }

            if (!IsFullScreen)
            {
                preFullScreenBounds = Bounds;
                IsFullScreen = true;
                bar.Visible = false;
                barLine.Visible = false;
                Rectangle screen = Screen.FromControl(this).Bounds;
                Bounds = screen;
                player.Bounds = ClientRectangle;
                player.SetFullScreen(true);
                BringToFront();
            }
            else
            {
                IsFullScreen = false;
                bar.Visible = true;
                barLine.Visible = true;
                Bounds = preFullScreenBounds;
                player.SetFullScreen(false);
                LayoutPage();
            }
        }

        private void LayoutPage()
        {
            if (bar == null || grid == null)
            {
                return;
            }
            if (IsFullScreen)
            {
                if (player != null)
                {
                    player.Bounds = ClientRectangle;
                }
                return;
            }
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int barHeight = Ui.S(BarHeight);
            bar.SetBounds(1, 1, width - 2, barHeight - 1);
            barLine.SetBounds(1, barHeight, width - 2, 1);

            int square = Ui.S(44);
            int top = (bar.Height - square) / 2;
            int x = Ui.S(16);
            if (!ShowingSettings && openFolder != null)
            {
                backButton.SetBounds(x, top, square, square);
                x += square + Ui.S(14);
            }
            closeButton.SetBounds(bar.Width - square - Ui.S(12), top, square, square);
            int right = closeButton.Left - Ui.S(12);
            if (!ShowingSettings && openFile == null)
            {
                sort.Location = new Point(right - sort.Width, (bar.Height - sort.Height) / 2);
                right = sort.Left - Ui.S(12);
            }
            crumb.SetBounds(x, top, Math.Max(0, right - x), square);

            var body = new Rectangle(1, barHeight + 1, width - 2, height - barHeight - 2);
            grid.Bounds = body;
            if (player != null)
            {
                player.Bounds = body;
            }
            if (settings != null)
            {
                settings.Bounds = body;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    public sealed class UploadNoticeForm : Form
    {
        private const int NoticeWidth = 460;

        public UploadNoticeForm()
        {
            Text = Context.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Background;
            ClientSize = new Size(Ui.S(NoticeWidth), Ui.S(244));

            Controls.Add(NewLabel("Before you upload", 24, 22, Ui.Text(18, FontStyle.Bold), Theme.Text));
            Controls.Add(NewLabel("\u2022  Anyone with the link can watch this video.", 24, 66, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  kappa.lol stores your IP address and may review uploads.", 24, 92, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  There's no stated expiry, so delete it when you're done.", 24, 118, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  kappa.lol's terms say you must be 18 or over.", 24, 144, Ui.Text(14), Theme.Text));

            var cancel = new ActionButton("Cancel")
            {
                Location = new Point(Ui.S(NoticeWidth - 24 - 96 - 8 - 96), Ui.S(180)),
                Size = new Size(Ui.S(96), Ui.S(44)),
            };
            cancel.Click += (_, __) => DialogResult = DialogResult.Cancel;
            var upload = new ActionButton("Upload", null, true)
            {
                Location = new Point(Ui.S(NoticeWidth - 24 - 96), Ui.S(180)),
                Size = new Size(Ui.S(96), Ui.S(44)),
            };
            upload.Click += (_, __) => DialogResult = DialogResult.OK;
            Controls.Add(cancel);
            Controls.Add(upload);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                }
            };
            // closing deactivates it too, and that mustn't turn an upload click into a cancel
            Deactivate += (_, __) =>
            {
                if (DialogResult == DialogResult.None)
                {
                    DialogResult = DialogResult.Cancel;
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        private static Label NewLabel(string text, int x, int y, Font font, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(Ui.S(x), Ui.S(y)),
                Size = new Size(Ui.S(NoticeWidth - x * 2), font.Height + Ui.S(6)),
                Font = font,
                ForeColor = color,
                BackColor = Color.Transparent,
            };
        }
    }
}
