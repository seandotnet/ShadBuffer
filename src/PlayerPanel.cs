using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShadBuffer
{
    // the preview page: video and scrub bar on the left, file details and actions on the right
    public sealed class PlayerPanel : Panel
    {
        private const int Inset = 24;
        private const int SideWidth = 280;
        private const int SideGap = 24;
        private const int RowGap = 12;
        private const int ControlHeight = 44;
        private const int StripCount = 5;
        private const int StripGap = 10;
        private const int ConfirmMs = 2500;
        private const string FileHint = "the video file";
        private const string HostLine = "Hosted by kappa.lol. There's no stated expiry, so delete it when you're done.";
        private const string IdleLine = "Public link, up to 100 MB. " + HostLine;
        private const string UploadLabel = "Upload to kappa.lol";
        private const string DeleteLabel = "Delete from kappa.lol";

        private readonly Context ctx;
        private readonly Uploader uploads;
        private readonly MediaCache cache;
        private readonly Func<bool> confirmUpload;
        private readonly Action closeOverlay;

        private readonly Panel frame = new Panel { BackColor = Color.Black };
        private readonly IconButton playButton = new IconButton(Icons.Play) { Boxed = true };
        private readonly IconButton muteButton = new IconButton(Icons.Volume) { Boxed = true };
        private readonly IconButton fullscreenButton = new IconButton(Icons.FullScreen) { Boxed = true };
        private readonly ProgressLine scrub = new ProgressLine { Interactive = true };
        private readonly Label clock = NewLabel(13, Theme.Muted);
        private readonly ThumbStrip strip;
        private readonly Panel side = new Panel { BackColor = Theme.Background };

        private readonly Label nameLabel = new Label { Font = Ui.Text(18, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false };
        private readonly Label fileLabel = NewLabel(13, Theme.Muted);
        private readonly List<Label> keys = new List<Label>();
        private readonly List<Label> values = new List<Label>();
        private readonly ActionButton folderButton = new ActionButton("Show in folder", Icons.Folder);
        private readonly ActionButton copyButton = new ActionButton("Copy", Icons.Copy, false, FileHint);
        private readonly Panel rule = new Panel { BackColor = Theme.Border };

        private readonly Label uploadTitle = NewLabel(14, Theme.Text);
        private readonly Label uploadRight = NewLabel(13, Theme.Text);
        private readonly ProgressLine uploadBar = new ProgressLine();
        private readonly Label uploadDetail = NewLabel(12.5F, Theme.Muted);
        private readonly Label uploadError = NewLabel(13, Theme.ErrorText);
        private readonly TextBox linkBox = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Ui.Text(13),
        };
        private readonly ActionButton uploadButton = new ActionButton(UploadLabel, Icons.Upload, true);
        private readonly ActionButton deleteButton = new ActionButton(DeleteLabel, Icons.Delete);
        private readonly ActionButton cancelButton = new ActionButton("Cancel");
        private readonly Label uploadFine = NewLabel(12.5F, Theme.Muted);

        private readonly Timer tick = new Timer { Interval = 200 };
        private readonly Timer copyReset = new Timer { Interval = ConfirmMs };

        private VideoHost video;
        private List<LibraryFile> files = new List<LibraryFile>();
        private LibraryFile file;
        private UploadState shownState;
        private bool showError;
        private bool showDelete;

        public event Action<LibraryFile> FilePicked;
        public event Action FullScreenRequested;

        private static bool savedMuted;
        private bool isFullScreen;

        public PlayerPanel(Context ctx, Uploader uploads, MediaCache cache, Func<bool> confirmUpload, Action closeOverlay)
        {
            this.ctx = ctx;
            this.uploads = uploads;
            this.cache = cache;
            this.confirmUpload = confirmUpload;
            this.closeOverlay = closeOverlay;
            BackColor = Theme.Background;

            strip = new ThumbStrip(cache, StripCount);
            strip.Picked += picked => FilePicked?.Invoke(picked);

            playButton.Click += (_, __) => Toggle();
            muteButton.Click += (_, __) => ToggleMute();
            fullscreenButton.Click += (_, __) => FullScreenRequested?.Invoke();
            scrub.Seeking += value =>
            {
                if (video != null)
                {
                    video.Position = TimeSpan.FromTicks((long)(video.Length.Ticks * value));
                    UpdateClock();
                }
            };

            Controls.Add(frame);
            Controls.Add(playButton);
            Controls.Add(muteButton);
            Controls.Add(scrub);
            Controls.Add(clock);
            Controls.Add(fullscreenButton);
            Controls.Add(strip);
            Controls.Add(side);

            clock.TextAlign = ContentAlignment.MiddleRight;
            uploadRight.TextAlign = ContentAlignment.MiddleRight;
            side.Controls.Add(nameLabel);
            side.Controls.Add(fileLabel);
            foreach (string key in new[] { "Modified", "Size", "Length", "Video" })
            {
                Label left = NewLabel(13, Theme.Muted);
                left.Text = key;
                Label right = NewLabel(13, Theme.Text);
                right.TextAlign = ContentAlignment.TopRight;
                keys.Add(left);
                values.Add(right);
                side.Controls.Add(left);
                side.Controls.Add(right);
            }
            uploadTitle.Font = Ui.Strong(14);
            side.Controls.AddRange(new Control[]
            {
                folderButton, copyButton, rule, uploadTitle, uploadRight, uploadBar, uploadDetail, uploadError, linkBox, uploadButton, deleteButton, cancelButton, uploadFine,
            });

            folderButton.Click += (_, __) => ShowInFolder();
            copyButton.Click += (_, __) => CopyFile();
            uploadButton.Click += (_, __) => UploadClicked();
            cancelButton.Click += (_, __) => uploads.Cancel(file.Path);
            deleteButton.Click += (_, __) =>
            {
                uploads.Delete(file.Path);
                UpdateUpload();
            };

            tick.Tick += (_, __) => OnTick();
            copyReset.Tick += (_, __) =>
            {
                copyReset.Stop();
                copyButton.Hint = FileHint;
                UpdateUpload();
            };
            cache.Updated += UpdateDetails;
            uploads.Changed += OnUploadChanged;
        }

        public void Open(List<LibraryFile> files, LibraryFile file, string title)
        {
            this.files = files;
            this.file = file;

            if (video == null)
            {
                video = new VideoHost { Dock = DockStyle.Fill };
                video.Clicked += OnVideoClicked;
                video.DoubleClicked += () => FullScreenRequested?.Invoke();
                video.Finished += UpdatePlayState;
                video.Opened += UpdateClock;
                video.KeyPressed += OnVideoKey;
                frame.Padding = new Padding(1);
                frame.Controls.Add(video);
            }

            video.Load(file.Path);
            video.IsMuted = savedMuted;
            UpdateMuteState();
            scrub.Value = 0;
            copyReset.Stop();
            copyButton.Hint = FileHint;
            strip.Set(files, file);
            nameLabel.Text = title;
            fileLabel.Text = file.Name;
            UpdateDetails();
            UpdatePlayState();
            UpdateClock();
            UpdateUpload();
            LayoutAll();
            tick.Start();
        }

        public void Toggle()
        {
            if (video == null)
            {
                return;
            }
            video.Toggle();
            UpdatePlayState();
        }

        public void ToggleMute()
        {
            if (video == null)
            {
                return;
            }
            video.ToggleMute();
            savedMuted = video.IsMuted;
            UpdateMuteState();
        }

        private void UpdateMuteState()
        {
            muteButton.Icon = (video != null && video.IsMuted) ? Icons.Mute : Icons.Volume;
        }

        public void SetFullScreen(bool fullScreen)
        {
            isFullScreen = fullScreen;
            fullscreenButton.Icon = isFullScreen ? Icons.BackToWindow : Icons.FullScreen;
            side.Visible = !isFullScreen;
            strip.Visible = !isFullScreen;
            LayoutAll();
        }

        public void Unload()
        {
            tick.Stop();
            video?.Unload();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose();
                copyReset.Dispose();
                cache.Updated -= UpdateDetails;
                uploads.Changed -= OnUploadChanged;
                video?.Unload();
            }
            base.Dispose(disposing);
        }

        private static Label NewLabel(float size, Color color)
        {
            return new Label { Font = Ui.Text(size), ForeColor = color, BackColor = Theme.Background, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false };
        }

        private void LayoutAll()
        {
            int margin = Ui.S(Inset);
            int gap = Ui.S(RowGap);
            int controls = Ui.S(ControlHeight);
            int stripGap = Ui.S(StripGap);
            int clockWidth = Ui.S(110);

            if (isFullScreen)
            {
                int controlsY = Height - controls - margin;
                frame.SetBounds(0, 0, Width, Math.Max(0, controlsY - gap));

                playButton.SetBounds(margin, controlsY, controls, controls);
                muteButton.SetBounds(playButton.Right + gap, controlsY, controls, controls);
                fullscreenButton.SetBounds(Width - margin - controls, controlsY, controls, controls);
                clock.SetBounds(fullscreenButton.Left - gap - clockWidth, controlsY, clockWidth, controls);
                int scrubLeft = muteButton.Right + gap;
                int scrubRight = clock.Left - gap;
                scrub.SetBounds(scrubLeft, controlsY, Math.Max(0, scrubRight - scrubLeft), controls);
                return;
            }

            int width = Math.Max(Ui.S(200), Width - margin * 2 - Ui.S(SideWidth) - Ui.S(SideGap));

            // ultrawide monitors would make the video taller than the window, so width gives way
            int available = Height - margin * 2 - controls - gap * 2;
            int StripHeight(int w) => (w - stripGap * (StripCount - 1)) / StripCount * 9 / 16;
            if (width * 9 / 16 + StripHeight(width) > available)
            {
                width = (int)((available + stripGap * 0.45) * 16 / 9 / 1.2);
            }
            int videoHeight = width * 9 / 16;

            frame.SetBounds(margin, margin, width, videoHeight);
            int y = frame.Bottom + gap;
            playButton.SetBounds(margin, y, controls, controls);
            muteButton.SetBounds(playButton.Right + gap, y, controls, controls);
            fullscreenButton.SetBounds(margin + width - controls, y, controls, controls);
            clock.SetBounds(fullscreenButton.Left - gap - clockWidth, y, clockWidth, controls);
            int sLeft = muteButton.Right + gap;
            int sRight = clock.Left - gap;
            scrub.SetBounds(sLeft, y, Math.Max(0, sRight - sLeft), controls);
            y += controls + gap;
            strip.SetBounds(margin, y, width, StripHeight(width));
            strip.Gap = stripGap;

            side.SetBounds(margin + width + Ui.S(SideGap), margin, Ui.S(SideWidth), Height - margin * 2);
            LayoutSide();
        }

        private void LayoutSide()
        {
            int width = side.Width;
            int y = 0;
            nameLabel.SetBounds(0, y, width, Ui.S(28));
            y += Ui.S(30);
            int fileHeight = TextRenderer.MeasureText(fileLabel.Text, fileLabel.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            fileLabel.SetBounds(0, y, width, fileHeight + 2);
            y += fileHeight + Ui.S(14);

            for (int i = 0; i < keys.Count; i++)
            {
                keys[i].SetBounds(0, y, Ui.S(90), Ui.S(20));
                values[i].SetBounds(Ui.S(90), y, width - Ui.S(90), Ui.S(20));
                y += Ui.S(25);
            }
            y += Ui.S(8);

            folderButton.SetBounds(0, y, width, Ui.S(44));
            y += Ui.S(52);
            copyButton.SetBounds(0, y, width, Ui.S(44));
            y += Ui.S(60);
            rule.SetBounds(0, y, width, 1);
            y += Ui.S(14);

            // visible reads false while the page is hidden, so this goes off the state instead
            bool uploading = shownState == UploadState.Uploading;
            bool hasLink = shownState == UploadState.Done || shownState == UploadState.Deleting;
            if (uploading || hasLink)
            {
                uploadTitle.SetBounds(0, y, Ui.S(170), Ui.S(22));
                uploadRight.SetBounds(Ui.S(170), y, width - Ui.S(170), Ui.S(22));
                y += Ui.S(28);
            }
            if (uploading)
            {
                uploadBar.SetBounds(0, y, width, Ui.S(12));
                y += Ui.S(18);
                uploadDetail.SetBounds(0, y, width, Ui.S(20));
                y += Ui.S(24);
            }
            if (hasLink)
            {
                linkBox.SetBounds(0, y, width, Ui.S(30));
                y += linkBox.Height + Ui.S(8);
            }
            if (showError)
            {
                int height = TextRenderer.MeasureText(uploadError.Text, uploadError.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
                uploadError.SetBounds(0, y, width, height + 2);
                y += height + Ui.S(8);
            }
            if (uploading)
            {
                cancelButton.SetBounds(0, y, width, Ui.S(36));
                y += Ui.S(44);
            }
            else
            {
                uploadButton.SetBounds(0, y, width, Ui.S(44));
                y += Ui.S(52);
            }
            if (showDelete)
            {
                deleteButton.SetBounds(0, y, width, Ui.S(36));
                y += Ui.S(44);
            }
            int fineHeight = TextRenderer.MeasureText(uploadFine.Text, uploadFine.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            uploadFine.SetBounds(0, y, width, fineHeight + 2);
        }

        private void UpdateDetails()
        {
            if (file == null)
            {
                return;
            }
            MediaInfo info = cache.Get(file.Path);
            values[0].Text = Library.When(file.Modified);
            values[1].Text = Library.SizeText(file.Size);
            values[2].Text = info?.Length != null ? Library.LengthText(info.Length.Value) : "-";
            values[3].Text = info?.VideoText() ?? "-";
            strip.Invalidate();
        }

        private void OnTick()
        {
            if (video == null)
            {
                return;
            }
            if (!scrub.Dragging && video.Length > TimeSpan.Zero)
            {
                scrub.Value = (double)video.Position.Ticks / video.Length.Ticks;
            }
            UpdateClock();
        }

        private void UpdateClock()
        {
            if (video == null)
            {
                return;
            }
            string text = Library.LengthText(video.Position) + " / " + Library.LengthText(video.Length);
            if (clock.Text != text)
            {
                clock.Text = text;
            }
        }

        private void UpdatePlayState()
        {
            playButton.Icon = video != null && video.Playing ? Icons.Pause : Icons.Play;
        }

        private void OnVideoClicked()
        {
            if (video.HasFailed)
            {
                // it can fail because the file got deleted, and then there's nothing to open either
                if (File.Exists(file.Path))
                {
                    Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true })?.Dispose();
                    closeOverlay();
                }
                return;
            }
            Toggle();
        }

        private void OnVideoKey(System.Windows.Input.Key key)
        {
            if (key == System.Windows.Input.Key.Space)
            {
                Toggle();
            }
            else if (key == System.Windows.Input.Key.M)
            {
                ToggleMute();
            }
            else if (key == System.Windows.Input.Key.F || key == System.Windows.Input.Key.F11)
            {
                FullScreenRequested?.Invoke();
            }
            else if (key == System.Windows.Input.Key.Escape)
            {
                if (isFullScreen)
                {
                    FullScreenRequested?.Invoke();
                }
                else
                {
                    closeOverlay();
                }
            }
        }

        private void ShowInFolder()
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(explorer, "/select,\"" + file.Path + "\"")?.Dispose();
            closeOverlay();
        }

        private void CopyFile()
        {
            if (TryCopy(() => Clipboard.SetFileDropList(new StringCollection { file.Path })))
            {
                copyButton.Hint = "copied, paste it anywhere";
                copyReset.Stop();
                copyReset.Start();
            }
        }

        private static bool TryCopy(Action copy)
        {
            try
            {
                copy();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private void UploadClicked()
        {
            UploadJob job = uploads.Get(file.Path);
            if (job != null && job.State == UploadState.Deleting)
            {
                return;
            }
            if (job != null && job.State == UploadState.Done)
            {
                if (TryCopy(() => Clipboard.SetText(job.Link)))
                {
                    uploadButton.Label = "Link copied";
                    copyReset.Stop();
                    copyReset.Start();
                }
                return;
            }
            if (!Settings.Load(ctx).KappaNoticeSeen)
            {
                if (!confirmUpload())
                {
                    return;
                }
                Settings settings = Settings.Load(ctx);
                settings.KappaNoticeSeen = true;
                settings.Save(ctx);
            }
            uploads.Start(file.Path, file.Size);
            UpdateUpload();
        }

        private void OnUploadChanged(UploadJob job)
        {
            if (file != null && job.Path == file.Path)
            {
                UpdateUpload();
            }
        }


        private void UpdateUpload()
        {
            UploadJob job = uploads.Get(file.Path);
            UploadState state = job?.State ?? UploadState.Idle;
            bool tooBig = file.Size > Uploader.MaxBytes;
            bool hasLink = state == UploadState.Done || state == UploadState.Deleting;
            shownState = state;
            showError = !string.IsNullOrEmpty(job?.Error) && (hasLink || state == UploadState.Failed);
            showDelete = hasLink && !string.IsNullOrEmpty(job.Key);

            uploadTitle.Visible = state == UploadState.Uploading || hasLink;
            uploadRight.Visible = uploadTitle.Visible;
            uploadBar.Visible = state == UploadState.Uploading;
            uploadDetail.Visible = state == UploadState.Uploading;
            uploadError.Visible = showError;
            linkBox.Visible = hasLink;
            uploadButton.Visible = state != UploadState.Uploading;
            deleteButton.Visible = showDelete;
            deleteButton.Enabled = state == UploadState.Done;
            deleteButton.Label = state == UploadState.Deleting ? "Deleting..." : DeleteLabel;
            cancelButton.Visible = state == UploadState.Uploading;
            uploadButton.Enabled = state == UploadState.Done || (!tooBig && state != UploadState.Deleting);
            uploadFine.Text = tooBig && state == UploadState.Idle ? "This file is too big, kappa.lol only takes up to 100 MB." : hasLink ? HostLine : IdleLine;
            uploadError.Text = job?.Error ?? "";

            switch (state)
            {
                case UploadState.Uploading:
                    int percent = job.Total > 0 ? (int)(job.Sent * 100 / job.Total) : 0;
                    uploadTitle.Text = "Uploading to kappa.lol";
                    uploadRight.Text = percent + "%";
                    uploadBar.Value = percent / 100.0;
                    uploadDetail.Text = Library.SizeText(job.Sent) + " of " + Library.SizeText(job.Total);
                    break;
                case UploadState.Done:
                case UploadState.Deleting:
                    uploadTitle.Text = "Link ready";
                    uploadRight.Text = "";
                    linkBox.Text = job.Link;
                    SetUploadButton("Copy link", Icons.Link);
                    break;
                case UploadState.Failed:
                    SetUploadButton("Retry", Icons.Retry);
                    break;
                default:
                    SetUploadButton(UploadLabel, Icons.Upload);
                    break;
            }
            LayoutSide();
        }

        private void SetUploadButton(string label, string icon)
        {
            uploadButton.Label = label;
            uploadButton.SetIcon(icon);
        }
    }

    // the row of neighbouring clips under the video
    public sealed class ThumbStrip : PaintedControl
    {
        private readonly MediaCache cache;
        private readonly int slots;
        private List<LibraryFile> files = new List<LibraryFile>();
        private LibraryFile current;
        private int first;
        private int hover = -1;

        public event Action<LibraryFile> Picked;

        public int Gap { get; set; }

        public ThumbStrip(MediaCache cache, int slots)
        {
            this.cache = cache;
            this.slots = slots;
        }

        public void Set(List<LibraryFile> files, LibraryFile current)
        {
            this.files = files;
            this.current = current;
            int index = files.FindIndex(file => file.Path == current.Path);
            first = Math.Max(0, Math.Min(index - slots / 2, files.Count - slots));
            Invalidate();
        }

        private Rectangle Slot(int position)
        {
            int width = (Width - Gap * (slots - 1)) / slots;
            return new Rectangle(position * (width + Gap), 0, width, Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            for (int i = 0; i < slots && first + i < files.Count; i++)
            {
                LibraryFile file = files[first + i];
                bool selected = current != null && file.Path == current.Path;
                Color border = selected || i == hover ? Theme.Accent : Theme.Border;
                VideoThumb.Draw(graphics, Slot(i), cache.Get(file.Path), border, selected ? Ui.S(2) : 1F, 8, 5, 11);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int found = -1;
            for (int i = 0; i < slots && first + i < files.Count; i++)
            {
                if (Slot(i).Contains(e.Location))
                {
                    found = i;
                }
            }
            Change(ref hover, found);
            Cursor = found >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (hover >= 0 && first + hover < files.Count)
            {
                Picked?.Invoke(files[first + hover]);
            }
        }
    }
}
