using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadBuffer
{
    // the settings tabs, opened from the gear
    public sealed class SettingsPanel : Panel
    {
        public const int PageWidth = 540;
        public const int PageHeight = 460;

        private const int Inset = 24;
        private const int TabTop = 12;
        private const int TabHeight = 34;
        private const int PanelTop = 56;
        private const int PanelHeight = 324;
        private const int LabelWidth = 170;
        private const int ControlLeft = 180;
        private const int ControlWidth = 308;
        private const int RowHeight = 36;

        private readonly Context ctx;
        private readonly Settings draft;
        private readonly Companion owner;
        private readonly Action<bool> busyChanged;
        private readonly Action<bool> dialogChanged;
        private readonly Action<Settings> applied;
        private readonly Label messageLabel;
        private readonly ActionButton applyButton;
        private readonly List<LibraryTab> tabs = new List<LibraryTab>();
        private readonly List<Panel> pages = new List<Panel>();

        private Settings saved;
        private bool busy;
        private Label captureHelp;
        private FlatCombo monitorCombo;

        public SettingsPanel(Context ctx, Settings draft, Companion owner, Action<bool> busyChanged, Action<bool> dialogChanged, Action<Settings> applied)
        {
            this.ctx = ctx;
            this.draft = draft;
            this.owner = owner;
            this.busyChanged = busyChanged;
            this.dialogChanged = dialogChanged;
            this.applied = applied;
            saved = Settings.Load(ctx);
            BackColor = Theme.Background;
            Font = Ui.Text(13);

            AddTab("Clips", BuildClipsTab());
            AddTab("Video", BuildVideoTab());
            AddTab("Audio", BuildAudioTab());
            AddTab("Hotkeys", BuildHotkeysTab());
            AddTab("Startup", BuildStartupTab());
            SelectTab(0);

            messageLabel = NewLabel("", new Point(Ui.S(Inset), Ui.S(PanelTop + PanelHeight + 6)), new Size(Ui.S(360), Ui.S(44)), Theme.Muted, 12);
            Controls.Add(messageLabel);

            applyButton = new ActionButton("Apply", null, true)
            {
                Location = new Point(Ui.S(PageWidth - Inset - 120), Ui.S(PanelTop + PanelHeight + 8)),
                Size = new Size(Ui.S(120), Ui.S(40)),
            };
            applyButton.Click += (_, __) => _ = ApplyChanges();
            Controls.Add(applyButton);
            UpdateDirty();

            Ui.FontChanged += OnFontChanged;
        }

        private Panel BuildClipsTab()
        {
            Panel panel = NewPage();
            int y = 0;

            AddRow(panel, ref y, "Clip length", NewCombo(new[]
            {
                new Choice("30 seconds", 30),
                new Choice("1 minute", 60),
                new Choice("2 minutes", 120),
                new Choice("3 minutes", 180),
                new Choice("5 minutes", 300),
                new Choice("10 minutes", 600),
            }, draft.ClipSeconds, value => draft.ClipSeconds = (int)value));

            AddFolderRow(panel, ref y, "Save clips to", "Where should clips be saved?", draft.ClipsDir, value => draft.ClipsDir = value);
            AddFolderRow(panel, ref y, "Save recordings to", "Where should recordings be saved?", draft.RecordingsDir, value => draft.RecordingsDir = value);
            AddToggleRow(panel, ref y, "Sound when saved", draft.SaveSound, value => draft.SaveSound = value);
            AddToggleRow(panel, ref y, "Sort by game", draft.SortByGame, value => draft.SortByGame = value);
            return panel;
        }

        private Panel BuildVideoTab()
        {
            Panel panel = NewPage();
            int y = 0;

            AddRow(panel, ref y, "Capture", NewCombo(new[]
            {
                new Choice("Game capture", Settings.CaptureGame),
                new Choice("Desktop capture", Settings.CaptureDesktop),
            }, draft.Capture, value =>
            {
                draft.Capture = (string)value;
                UpdateCaptureHelp();
            }));

            captureHelp = NewLabel("", new Point(Ui.S(ControlLeft), y), new Size(Ui.S(ControlWidth), Ui.S(52)), Theme.Muted, 11.75F);
            panel.Controls.Add(captureHelp);
            y += Ui.S(54);

            List<DisplayInfo> displays = SystemInfo.AllDisplays();
            DisplayInfo primary = displays.FirstOrDefault(d => d.IsPrimary);
            Choice[] monitors = displays
                .Select(d => new Choice($"Display {d.Number} ({d.Width}x{d.Height}){(d.IsPrimary ? ", main" : "")}", d.MonitorId))
                .ToArray();
            string currentMonitor = string.IsNullOrEmpty(draft.MonitorId) ? primary?.MonitorId : draft.MonitorId;
            monitorCombo = NewCombo(monitors, currentMonitor, value => draft.MonitorId = (string)value);
            AddRow(panel, ref y, "Monitor to record", monitorCombo);

            AddRow(panel, ref y, "Quality", NewCombo(new[]
            {
                new Choice("Highest (big files)", 16),
                new Choice("High", 20),
                new Choice("Medium", 24),
                new Choice("Low (small files)", 28),
            }, draft.Quality, value => draft.Quality = (int)value));

            AddRow(panel, ref y, "Frame rate", NewCombo(new[]
            {
                new Choice("30 fps", 30),
                new Choice("60 fps", 60),
                new Choice("120 fps", 120),
                new Choice("144 fps", 144),
            }, draft.Fps, value => draft.Fps = (int)value));

            var resolutions = new List<Choice> { new Choice("Same as monitor", 0) };
            foreach (int height in new[] { 1440, 1080, 720 })
            {
                if (primary == null || height < primary.Height)
                {
                    resolutions.Add(new Choice(height + "p", height));
                }
            }
            AddRow(panel, ref y, "Resolution", NewCombo(resolutions.ToArray(), draft.OutputHeight, value => draft.OutputHeight = (int)value));

            State state = State.Load(ctx);
            var codecs = new List<Choice>();
            if (state.EncoderBrand != Encoders.BrandCpu)
            {
                codecs.Add(new Choice("H.264 (plays everywhere)", Encoders.CodecH264));
                if (state.SupportsHevc)
                {
                    codecs.Add(new Choice("HEVC (smaller files)", Encoders.CodecHevc));
                }
                if (state.SupportsAv1)
                {
                    codecs.Add(new Choice("AV1 (smallest, less compatible)", Encoders.CodecAv1));
                }
            }
            codecs.Add(new Choice("CPU x264 (slowest)", Encoders.CodecCpu));
            AddRow(panel, ref y, "Encoder", NewCombo(codecs.ToArray(), draft.Codec, value => draft.Codec = (string)value));

            UpdateCaptureHelp();
            return panel;
        }

        private Panel BuildAudioTab()
        {
            Panel panel = NewPage();
            int y = 0;

            var mics = new List<Choice> { new Choice("Windows default", Settings.DeviceDefault) };
            mics.AddRange(AudioDevices.Microphones().Select(d => new Choice(d.Name, d.Id)));
            AddRow(panel, ref y, "Microphone", NewCombo(mics.ToArray(), draft.MicDevice, value => draft.MicDevice = (string)value));

            AddRow(panel, ref y, "Mic noise suppression", NewCombo(new[]
            {
                new Choice("Off", Settings.NoiseOff),
                new Choice("RNNoise (better quality, more CPU)", Settings.NoiseRnnoise),
                new Choice("Speex (lower quality, less CPU)", Settings.NoiseSpeex),
            }, draft.NoiseSuppression, value => draft.NoiseSuppression = (string)value));

            var outputs = new List<Choice> { new Choice("Windows default", Settings.DeviceDefault) };
            outputs.AddRange(AudioDevices.Outputs().Select(d => new Choice(d.Name, d.Id)));
            outputs.Add(new Choice("Off", Settings.DeviceOff));
            AddRow(panel, ref y, "Game and desktop audio", NewCombo(outputs.ToArray(), draft.DesktopDevice, value => draft.DesktopDevice = (string)value));

            AddToggleRow(panel, ref y, "Separate audio tracks", draft.SeparateTracks, value => draft.SeparateTracks = value);
            panel.Controls.Add(NewLabel("Keeps a mix plus separate desktop and mic tracks for editing. Windows players and the preview may only play one of them, so leave this off unless you edit.", new Point(Ui.S(ControlLeft), y), new Size(Ui.S(ControlWidth), Ui.S(60)), Theme.Muted, 11.75F));
            return panel;
        }

        private Panel BuildHotkeysTab()
        {
            Panel panel = NewPage();
            int y = 0;

            AddHotkeyRow(panel, ref y, "Save clip", draft.SaveHotkey, false, false, value => draft.SaveHotkey = value);
            AddHotkeyRow(panel, ref y, "Open overlay", draft.OverlayHotkey, false, true, value => draft.OverlayHotkey = value);
            AddHotkeyRow(panel, ref y, "Record", draft.RecordHotkey, true, true, value => draft.RecordHotkey = value);
            AddHotkeyRow(panel, ref y, "Screenshot", draft.ScreenshotHotkey, true, true, value => draft.ScreenshotHotkey = value);
            AddHotkeyRow(panel, ref y, "Instant Replay on/off", draft.ReplayHotkey, true, true, value => draft.ReplayHotkey = value);
            AddHotkeyRow(panel, ref y, "Microphone on/off", draft.MicHotkey, true, true, value => draft.MicHotkey = value);
            panel.Controls.Add(NewLabel("While ShadBuffer runs, these combos belong to it, so a game that also uses one won't see the key. Press Delete in a box to turn one off.", new Point(0, y + Ui.S(8)), new Size(Ui.S(488), Ui.S(44)), Theme.Muted, 11.75F));
            return panel;
        }

        private Panel BuildStartupTab()
        {
            Panel panel = NewPage();
            int y = 0;

            AddToggleRow(panel, ref y, "Start at login", draft.StartAtLogin, value => draft.StartAtLogin = value);
            AddToggleRow(panel, ref y, "Run OBS as admin", draft.RunAsAdmin, value => draft.RunAsAdmin = value);
            panel.Controls.Add(NewLabel("Lets OBS capture games running as admin and gives it more GPU priority. Changing this asks for admin approval.", new Point(Ui.S(ControlLeft), y), new Size(Ui.S(ControlWidth), Ui.S(56)), Theme.Muted, 11.75F));
            y += Ui.S(58);
            AddToggleRow(panel, ref y, "Turn off Game Bar capture", draft.GameBarOff, value => draft.GameBarOff = value);
            AddFontRow(panel, ref y);
            return panel;
        }

        private void AddFontRow(Panel panel, ref int y)
        {
            var choices = new List<Choice>
            {
                new Choice("Default (Segoe UI)", Ui.ChoiceDefault),
                new Choice("Cascadia Code", Ui.ChoiceCascadia),
                new Choice("Segoe UI Variable", Ui.ChoiceSegoeVariable),
            };

            List<CustomFontItem> customFonts = Ui.GetAvailableCustomFonts(ctx);
            foreach (CustomFontItem font in customFonts)
            {
                choices.Add(new Choice(font.DisplayName, "custom:" + font.FileName));
            }

            choices.Add(new Choice("Import font...", "import"));

            string activeValue = draft.FontChoice ?? Ui.ChoiceDefault;
            if (activeValue == Ui.ChoiceCustom && !string.IsNullOrEmpty(draft.CustomFontFile))
            {
                activeValue = "custom:" + draft.CustomFontFile;
            }

            if (!choices.Any(c => c.Value.Equals(activeValue)))
            {
                activeValue = Ui.ChoiceDefault;
            }

            Action saveFontLive = () =>
            {
                draft.Save(ctx);
                saved.CopyLiveValues(draft);
                saved.Save(ctx);
                UpdateDirty();
            };

            FlatCombo combo = null;
            combo = NewCombo(choices.ToArray(), activeValue, val =>
            {
                string selected = (string)val;
                if (selected == "import")
                {
                    dialogChanged(true);
                    using (var dialog = new OpenFileDialog
                    {
                        Title = "Select a font file (.ttf, .otf)",
                        Filter = "Font Files (*.ttf;*.otf)|*.ttf;*.otf|All Files (*.*)|*.*",
                    })
                    {
                        if (dialog.ShowDialog() == DialogResult.OK && File.Exists(dialog.FileName))
                        {
                            Ui.SetFontChoice(Ui.ChoiceCustom, draft, ctx, dialog.FileName, true);
                            string key = "custom:" + draft.CustomFontFile;
                            string label = string.IsNullOrEmpty(draft.CustomFontFace) ? draft.CustomFontFile : draft.CustomFontFace;
                            var customChoice = new Choice(label, key);
                            int existingIdx = -1;
                            for (int i = 0; i < combo.Items.Count; i++)
                            {
                                if (((Choice)combo.Items[i]).Value.Equals(key))
                                {
                                    existingIdx = i;
                                    break;
                                }
                            }
                            if (existingIdx >= 0)
                            {
                                combo.Items[existingIdx] = customChoice;
                                combo.SelectedIndex = existingIdx;
                            }
                            else
                            {
                                combo.Items.Insert(combo.Items.Count - 1, customChoice);
                                combo.SelectedIndex = combo.Items.Count - 2;
                            }
                            saveFontLive();
                        }
                        else
                        {
                            string restoreVal = draft.FontChoice == Ui.ChoiceCustom ? ("custom:" + draft.CustomFontFile) : draft.FontChoice;
                            int prevIdx = -1;
                            for (int i = 0; i < combo.Items.Count; i++)
                            {
                                if (((Choice)combo.Items[i]).Value.Equals(restoreVal))
                                {
                                    prevIdx = i;
                                    break;
                                }
                            }
                            if (prevIdx >= 0)
                            {
                                combo.SelectedIndex = prevIdx;
                            }
                        }
                    }
                    dialogChanged(false);
                }
                else if (selected != null && selected.StartsWith("custom:"))
                {
                    string fontFile = selected.Substring("custom:".Length);
                    draft.FontChoice = Ui.ChoiceCustom;
                    draft.CustomFontFile = fontFile;
                    Ui.SetFontChoice(selected, draft, ctx, null, true);
                    saveFontLive();
                }
                else
                {
                    draft.FontChoice = selected;
                    Ui.SetFontChoice(selected, draft, ctx, null, true);
                    saveFontLive();
                }
            });

            AddRow(panel, ref y, "Font", combo);
        }

        private void OnFontChanged()
        {
            if (IsDisposed)
            {
                return;
            }
            RefreshFonts(this);
        }

        private static void RefreshFonts(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Label lbl && lbl.Tag is float fontSize)
                {
                    lbl.Font = Ui.Text(fontSize);
                }
                else if (c is FlatCombo || c is ActionButton || c is LibraryTab || c is HotkeyBox)
                {
                    c.Font = Ui.Text(13);
                }
                RefreshFonts(c);
            }
            parent.Invalidate(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Ui.FontChanged -= OnFontChanged;
            }
            base.Dispose(disposing);
        }

        private void UpdateCaptureHelp()
        {
            bool desktop = draft.Capture == Settings.CaptureDesktop;
            captureHelp.Text = desktop
                ? "Records the whole monitor without touching the game. Notifications and other windows show up in clips."
                : "Clean footage of just the game. It hooks into fullscreen games, so some anti-cheats may block it or object.";
            monitorCombo.Enabled = desktop;
        }

        private void AddTab(string title, Panel page)
        {
            int index = tabs.Count;
            int width = Ui.S(PageWidth - Inset * 2) / 5;
            var tab = new LibraryTab(title)
            {
                Location = new Point(Ui.S(Inset) + index * width, Ui.S(TabTop)),
                Size = new Size(width, Ui.S(TabHeight)),
            };
            tab.Click += (_, __) => SelectTab(index);
            tabs.Add(tab);
            pages.Add(page);
            Controls.Add(tab);
            Controls.Add(page);
        }

        private void SelectTab(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                tabs[i].Selected = i == index;
            }
        }

        private Panel NewPage()
        {
            return new Panel
            {
                Location = new Point(Ui.S(Inset), Ui.S(PanelTop)),
                Size = new Size(Ui.S(PageWidth - Inset * 2), Ui.S(PanelHeight)),
                BackColor = Theme.Background,
            };
        }

        private void AddRow(Panel panel, ref int y, string label, Control control, int width = ControlWidth)
        {
            panel.Controls.Add(NewLabel(label, new Point(0, y + Ui.S(6)), new Size(Ui.S(LabelWidth), Ui.S(24)), Theme.Text, 13));
            control.Location = new Point(Ui.S(ControlLeft), y + Ui.S(4));
            control.Width = Ui.S(width);
            panel.Controls.Add(control);
            y += Ui.S(RowHeight);
        }

        private void AddToggleRow(Panel panel, ref int y, string label, bool value, Action<bool> changed)
        {
            panel.Controls.Add(NewLabel(label, new Point(0, y + Ui.S(6)), new Size(Ui.S(LabelWidth), Ui.S(24)), Theme.Text, 13));
            var toggle = new ToggleSwitch { Location = new Point(Ui.S(ControlLeft), y + Ui.S(5)), Checked = value };
            toggle.Toggled += (_, __) =>
            {
                changed(toggle.Checked);
                UpdateDirty();
            };
            panel.Controls.Add(toggle);
            y += Ui.S(RowHeight);
        }

        private void AddFolderRow(Panel panel, ref int y, string label, string question, string current, Action<string> changed)
        {
            var folder = new TextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Ui.Text(13),
                Text = current,
            };
            var browse = new ActionButton("...")
            {
                Location = new Point(Ui.S(ControlLeft + ControlWidth - 40), y + Ui.S(4)),
                Size = new Size(Ui.S(40), Ui.S(26)),
            };
            browse.Click += (_, __) => PickFolder(folder, question, changed);
            panel.Controls.Add(browse);
            AddRow(panel, ref y, label, folder, ControlWidth - 48);
        }

        private void AddHotkeyRow(Panel panel, ref int y, string label, string current, bool clearable, bool mustBeFree, Action<string> changed)
        {
            string value = current;
            var box = new HotkeyBox(clearable) { Text = string.IsNullOrEmpty(current) ? HotkeyBox.NoneText : current };
            box.CaptureChanged += capturing => owner.PauseHotkeys(capturing);
            box.Captured += hotkey =>
            {
                ShowMessage("");
                if (hotkey != null && !owner.IsHotkeyFree(hotkey))
                {
                    if (mustBeFree)
                    {
                        ShowMessage("Another app already uses that combo, so pick a different one.");
                        box.Text = string.IsNullOrEmpty(value) ? HotkeyBox.NoneText : value;
                        return;
                    }
                    ShowMessage($"Another app also uses {hotkey}. It should still work, but that app may react to it too.");
                }
                value = hotkey?.ToString() ?? "";
                changed(value);
                UpdateDirty();
            };
            AddRow(panel, ref y, label, box);
        }

        private FlatCombo NewCombo(Choice[] choices, object current, Action<object> changed)
        {
            var combo = new FlatCombo();
            combo.Items.AddRange(choices.Cast<object>().ToArray());
            int index = Array.FindIndex(choices, choice => Equals(choice.Value, current));
            combo.SelectedIndex = index >= 0 ? index : 0;
            combo.SelectedIndexChanged += (_, __) =>
            {
                changed(((Choice)combo.SelectedItem).Value);
                UpdateDirty();
            };
            return combo;
        }

        private static Label NewLabel(string text, Point location, Size size, Color color, float fontSize)
        {
            return new Label
            {
                Text = text,
                Location = location,
                Size = size,
                ForeColor = color,
                Font = Ui.Text(fontSize),
                Tag = fontSize,
                BackColor = Color.Transparent,
            };
        }

        private void PickFolder(TextBox folder, string question, Action<string> changed)
        {
            dialogChanged(true);
            using (var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, Description = question })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    changed(dialog.SelectedPath);
                    folder.Text = dialog.SelectedPath;
                    UpdateDirty();
                }
            }
            dialogChanged(false);
            FindForm()?.Activate();
        }

        private string RepeatedHotkey()
        {
            string[] hotkeys = { draft.SaveHotkey, draft.OverlayHotkey, draft.RecordHotkey, draft.ScreenshotHotkey, draft.ReplayHotkey, draft.MicHotkey };
            return hotkeys
                .Where(hotkey => !string.IsNullOrEmpty(hotkey))
                .GroupBy(hotkey => hotkey)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private async Task ApplyChanges()
        {
            if (!applyButton.Enabled)
            {
                return;
            }
            string repeated = RepeatedHotkey();
            if (repeated != null)
            {
                ShowMessage($"{repeated} is set for two things. Pick a different combo for one of them.");
                return;
            }

            SetBusy(true);
            ShowMessage(saved.NeedsObsRestart(draft) ? "Applying. Recording restarts for a few seconds..." : "Applying...");

            Settings next = draft.Clone();
            string failure = await Ui.RunSafely(ctx, () => ObsControl.Apply(ctx, next));

            SetBusy(false);
            if (IsDisposed)
            {
                return;
            }
            if (failure == null)
            {
                saved = Settings.Load(ctx);
                applied(saved);
                ShowMessage("Saved.");
            }
            else
            {
                ShowMessage(failure);
            }
            UpdateDirty();
        }

        private void SetBusy(bool value)
        {
            busy = value;
            busyChanged(value);
            UpdateDirty();
        }

        private void UpdateDirty()
        {
            if (applyButton == null)
            {
                return;
            }
            bool dirty = !draft.SameAs(saved);
            applyButton.Enabled = dirty && !busy;
        }

        private void ShowMessage(string text)
        {
            messageLabel.Text = text;
        }
    }
}
