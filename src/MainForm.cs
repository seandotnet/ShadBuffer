using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadBuffer
{
    public sealed class MainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(24, 24, 27);
        private static readonly Color Panel = Color.FromArgb(39, 39, 42);
        private static readonly Color Text1 = Color.FromArgb(244, 244, 245);
        private static readonly Color Muted = Color.FromArgb(161, 161, 170);
        private static readonly Color Accent = Color.FromArgb(109, 94, 245);

        private readonly Context ctx;
        private readonly string autoRun;
        private readonly TextBox log;
        private readonly Button setup;
        private readonly Button remove;
        private readonly Button clips;

        public MainForm(Context ctx, string autoRun)
        {
            this.ctx = ctx;
            this.autoRun = autoRun;
            Settings settings = Settings.Load(ctx);

            Text = Context.AppName;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(480, 340);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.75F);

            Controls.Add(new Label
            {
                Text = Context.AppName,
                Font = new Font("Segoe UI Semibold", 20F),
                Location = new Point(24, 18),
                AutoSize = true,
            });
            Controls.Add(new Label
            {
                Text = $"Records in the background with OBS, like ShadowPlay.\nPress {settings.SaveHotkey} to save a clip and {settings.OverlayHotkey} for settings.",
                ForeColor = Muted,
                Location = new Point(26, 64),
                Size = new Size(430, 44),
            });

            log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Muted,
                Location = new Point(26, 118),
                Size = new Size(428, 136),
                TabStop = false,
                Text = "This adds a new OBS profile, starts OBS at login in the tray, and turns off Game Bar capture and the OBS exit warning. Your mic is recorded in clips. Your other OBS profiles aren't changed." + Environment.NewLine + Environment.NewLine + "Game capture hooks into fullscreen games, so some anti-cheats may block it or object. You can switch to desktop capture in settings.",
            };
            Controls.Add(log);

            setup = NewButton(Setup.IsInstalled(ctx) ? "Set up again" : "Set up", new Point(26, 272), new Size(150, 42));
            setup.BackColor = Accent;
            setup.FlatAppearance.BorderSize = 0;
            setup.Font = new Font("Segoe UI Semibold", 10.5F);
            setup.Click += (_, __) => RunSetup();

            clips = NewButton("Open clips", new Point(186, 272), new Size(130, 42));
            clips.Visible = Setup.IsInstalled(ctx);
            clips.Click += (_, __) => OpenClips();

            remove = NewButton("Remove", new Point(326, 272), new Size(128, 42));
            remove.Visible = Setup.IsInstalled(ctx);
            remove.Click += (_, __) => RunRemove();

            Controls.AddRange(new Control[] { setup, clips, remove });

            ctx.Progress = Append;
            Shown += (_, __) =>
            {
                if (autoRun == "setup")
                {
                    RunSetup();
                }
                else if (autoRun == "remove")
                {
                    RunRemove();
                }
            };
        }

        private Button NewButton(string text, Point location, Size size)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel,
                ForeColor = Text1,
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
            return button;
        }

        private void RunSetup()
        {
            _ = Execute(() => Setup.Install(ctx, Confirm));
        }

        private void RunRemove()
        {
            if (MessageBox.Show(this, "Remove the OBS profile, the startup task and the Windows changes?\nYour clips and other OBS profiles stay.", Context.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            _ = Execute(() => Setup.Remove(ctx, Confirm));
        }

        private async Task Execute(Func<string> work)
        {
            setup.Enabled = remove.Enabled = clips.Enabled = false;
            log.Clear();

            string failure = null;
            try
            {
                await Task.Run(work);
            }
            catch (SetupException e)
            {
                failure = e.Message;
            }
            catch (Exception e)
            {
                ctx.Log(e.ToString());
                failure = "Something went wrong: " + e.Message;
            }

            if (failure != null)
            {
                ctx.Log(failure);
                Append(failure);
            }

            bool installed = Setup.IsInstalled(ctx);
            setup.Text = installed ? "Set up again" : "Set up";
            clips.Visible = remove.Visible = installed;
            setup.Enabled = remove.Enabled = clips.Enabled = true;
        }

        private bool Confirm(string question)
        {
            return (bool)Invoke(new Func<bool>(() =>
                MessageBox.Show(this, question, Context.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
        }

        private void Append(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Append), line);
                return;
            }
            log.ForeColor = Text1;
            log.AppendText((log.TextLength > 0 ? Environment.NewLine : "") + line);
        }

        private void OpenClips()
        {
            Directory.CreateDirectory(ctx.ClipsDir);
            Process.Start(new ProcessStartInfo(ctx.ClipsDir) { UseShellExecute = true })?.Dispose();
        }
    }
}
