using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Traymetry
{
    /// <summary>
    /// One line of the cheat sheet: a gesture, key or button on the left and
    /// what it does on the right.  A row with no shortcut is a plain paragraph,
    /// which is how the "how it works" advice is written.
    /// </summary>
    internal sealed class HelpEntry
    {
        public HelpEntry(string shortcut, string description)
        {
            Shortcut = shortcut ?? String.Empty;
            Description = description ?? String.Empty;
        }

        public string Shortcut { get; private set; }
        public string Description { get; private set; }
    }

    internal sealed class HelpSection
    {
        public HelpSection(string title, params HelpEntry[] entries)
        {
            Title = title ?? String.Empty;
            Entries = entries ?? new HelpEntry[0];
        }

        public string Title { get; private set; }
        public HelpEntry[] Entries { get; private set; }
    }

    /// <summary>
    /// The controls cheat sheet.  A single instance is reused, because the point
    /// of the window is to be opened next to the widget and read, not collected.
    /// </summary>
    internal sealed class HelpWindow : Form
    {
        private static HelpWindow _instance;

        private static readonly Color Background = Color.FromArgb(22, 26, 32);
        private static readonly Color Surface = Color.FromArgb(29, 33, 40);
        private static readonly Color Line = Color.FromArgb(49, 55, 65);
        private static readonly Color Accent = Color.FromArgb(73, 190, 198);
        private static readonly Color TextPrimary = Color.FromArgb(224, 230, 238);
        private static readonly Color TextMuted = Color.FromArgb(146, 156, 169);

        // The scroller's own padding, top and bottom, plus a hairline so the
        // last row never sits flush against the frame.  The top half is the
        // band the language button sits in.
        private const int ContentTop = 40;
        private const int ContentMargin = ContentTop + 20;

        // Shared for the life of the process.  The shortcut font used to be
        // built inside the row painter, which meant one font handle per row per
        // repaint, and the heading font was handed to a label that never gave
        // it back when the sheet was rebuilt in the other language.
        private static readonly Font HeadingFont =
            new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Point);
        private static readonly Font ShortcutFont =
            new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
        private static readonly Font BodyFont =
            new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        private readonly Panel _scroller = new Panel();
        private readonly Panel _content = new Panel();
        private readonly Button _languageButton = new Button();
        private bool _manuallySized;
        private Size _sizeBeforeUserDrag;

        /// <summary>
        /// What the language button runs.  The sheet does not own the setting -
        /// the widget does, and it is the one that has to replay every caption
        /// it holds - so the switch is handed in rather than reimplemented.
        /// </summary>
        private static Action _toggleLanguage;

        private HelpWindow()
        {
            Text = Loc.T("help.title");
            // Manual: the fit pass centres the window itself, and CenterScreen
            // would recompute the position from the pre-fit size on first show.
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimumSize = new Size(430, 320);
            ClientSize = new Size(560, 620);
            BackColor = Background;
            ForeColor = TextPrimary;
            ShowInTaskbar = false;
            KeyPreview = true;
            Font = BodyFont;

            _scroller.Dock = DockStyle.Fill;
            _scroller.AutoScroll = true;
            _scroller.BackColor = Background;
            _scroller.Padding = new Padding(16, ContentTop, 16, 16);

            _content.Location = new Point(16, ContentTop);
            _content.AutoSize = false;
            _content.BackColor = Background;

            _scroller.Controls.Add(_content);
            Controls.Add(_scroller);

            BuildLanguageButton();

            BuildContent();
            _scroller.Resize += delegate { LayoutContent(); };
            LayoutContent();
            FitToContent();
            // Only a resize the user performed counts.  The programmatic ones
            // done by the fit itself must not lock it out - and neither must a
            // move, which ends with the same WM_EXITSIZEMOVE a resize does and
            // used to leave the sheet stuck at its English width for good.
            ResizeBegin += delegate { _sizeBeforeUserDrag = Size; };
            ResizeEnd += delegate
            {
                if (Size != _sizeBeforeUserDrag)
                    _manuallySized = true;
            };
        }

        /// <summary>
        /// The language switch, in the sheet itself.  Someone who opened the
        /// wrong language is reading the one screen in the program that explains
        /// where every button is - including the one that would fix it - and it
        /// is the screen they can least afford to have to guess their way out
        /// of.
        /// </summary>
        private void BuildLanguageButton()
        {
            _languageButton.Text = Loc.Code.ToUpperInvariant();
            _languageButton.AccessibleName = Loc.T("access.language");
            _languageButton.Font = HeadingFont;
            _languageButton.FlatStyle = FlatStyle.Flat;
            _languageButton.FlatAppearance.BorderColor = Line;
            _languageButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 48, 57);
            // Opaque, because the sheet scrolls underneath it.
            _languageButton.BackColor = Surface;
            _languageButton.ForeColor = Accent;
            _languageButton.Size = new Size(46, 22);
            _languageButton.Location = new Point(ClientSize.Width - 62, 10);
            _languageButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _languageButton.TabStop = false;
            _languageButton.Click += delegate
            {
                Action toggle = _toggleLanguage;
                if (toggle != null)
                    toggle();
            };
            Controls.Add(_languageButton);
            _languageButton.BringToFront();
        }

        /// <summary>
        /// Opens at the size its own text needs.  A cheat sheet that has to be
        /// scrolled or dragged wider before it can be read is one that gets read
        /// once and never opened again.
        /// </summary>
        private void FitToContent()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maximumWidth = Math.Max(MinimumSize.Width, area.Width - 80);
            int maximumHeight = Math.Max(MinimumSize.Height, area.Height - 80);
            // The scroll bar would take its width out of the text while it is
            // being measured, and every measurement after the first would be of
            // a narrower column than the one the window ends up with.
            bool scrolled = _scroller.AutoScroll;
            _scroller.AutoScroll = false;
            try
            {
                int width = Math.Max(MinimumSize.Width, Math.Min(620, maximumWidth));
                int height = MeasuredHeightAt(width);
                // A sheet too tall for the screen is widened rather than left to
                // scroll: the same text wraps over fewer lines and gets shorter.
                // A window that is read where it opens beats one that has to be
                // dragged open first.
                while (height > maximumHeight && width < maximumWidth)
                {
                    width = Math.Min(maximumWidth, width + 60);
                    height = MeasuredHeightAt(width);
                }
                ClientSize = new Size(width,
                    Math.Max(MinimumSize.Height, Math.Min(maximumHeight, height)));
            }
            finally
            {
                _scroller.AutoScroll = scrolled;
            }
            LayoutContent();
            Location = new Point(
                area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        /// <summary>
        /// How tall the window has to be for the text to fit at this width.
        /// The wrapping depends on the width, so the width is committed and the
        /// content laid out again before anything is measured.
        /// </summary>
        private int MeasuredHeightAt(int width)
        {
            ClientSize = new Size(width, ClientSize.Height);
            LayoutContent();
            return _content.Height + ContentMargin;
        }

        internal static void ShowSingleton(IWin32Window owner, Action toggleLanguage)
        {
            _toggleLanguage = toggleLanguage;
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new HelpWindow();
                _instance.FormClosed += delegate { _instance = null; };
                _instance.TopMost = IsOwnerTopMost(owner);
                _instance.Show(owner);
            }
            else
            {
                // A second request means "I want to look at it", so an instance
                // that ended up behind the game or the browser comes forward.
                if (_instance.WindowState == FormWindowState.Minimized)
                    _instance.WindowState = FormWindowState.Normal;
                _instance.TopMost = IsOwnerTopMost(owner);
                _instance.Activate();
            }
            _instance.BringToFront();
        }

        /// <summary>
        /// Being owned by the widget is not enough to stay in front of it: a
        /// topmost owner sits in a band of its own, and an ordinary owned window
        /// ends up underneath it - which is the widget covering the very text it
        /// was asked to explain.  So the sheet joins whichever band its owner is
        /// in.
        /// </summary>
        private static bool IsOwnerTopMost(IWin32Window owner)
        {
            Form form = owner as Form;
            return form != null && form.TopMost;
        }

        /// <summary>
        /// Rebuilds the sheet in the language that is current now.  Called from
        /// the language switch, so an open window does not keep the old text.
        /// </summary>
        internal static void RefreshIfOpen()
        {
            if (_instance == null || _instance.IsDisposed)
                return;
            _instance.Text = Loc.T("help.title");
            _instance._languageButton.Text = Loc.Code.ToUpperInvariant();
            _instance._languageButton.AccessibleName = Loc.T("access.language");
            _instance.BuildContent();
            _instance.LayoutContent();
            // The other language is not the same length, so a window still at
            // its automatic size is re-fitted to the text it now holds.
            if (!_instance._manuallySized)
                _instance.FitToContent();
            else
                _instance.GrowToContent();
        }

        /// <summary>
        /// Takes the height the text now needs, and only ever more of it.  A
        /// size the user chose is theirs to keep, but Russian runs longer than
        /// English and a switch that leaves the last two sections below the
        /// frame looks like the switch broke the window.
        /// </summary>
        private void GrowToContent()
        {
            int needed = _content.Height + ContentMargin;
            if (needed <= ClientSize.Height)
                return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(ClientSize.Width,
                Math.Max(MinimumSize.Height, Math.Min(needed, area.Height - 60)));
            LayoutContent();
            // Growing downwards can push the frame off the bottom of the screen,
            // which hides the very rows that were just made room for.
            if (Bottom > area.Bottom)
                Top = Math.Max(area.Top, area.Bottom - Height);
        }

        /// <summary>
        /// True if there was a sheet to close.  The caller uses that to make one
        /// key both open and close it.
        /// </summary>
        internal static bool CloseIfOpen()
        {
            if (_instance == null || _instance.IsDisposed)
                return false;
            _instance.Close();
            return true;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape || keyData == Keys.F1)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void BuildContent()
        {
            // Clear() only detaches; the labels keep their window handles until
            // something disposes them, and this runs again on every language
            // switch.
            Control[] previous = new Control[_content.Controls.Count];
            _content.Controls.CopyTo(previous, 0);
            _content.Controls.Clear();
            foreach (Control control in previous)
                control.Dispose();

            foreach (HelpSection section in BuildSections())
            {
                Label heading = new Label();
                heading.Text = section.Title.ToUpperInvariant();
                heading.Font = HeadingFont;
                heading.ForeColor = Accent;
                heading.AutoSize = false;
                heading.Tag = "heading";
                _content.Controls.Add(heading);

                foreach (HelpEntry entry in section.Entries)
                {
                    Label row = new Label();
                    row.Tag = entry;
                    row.AutoSize = false;
                    row.BackColor = String.IsNullOrEmpty(entry.Shortcut) ? Background : Surface;
                    _content.Controls.Add(row);
                    row.Paint += PaintRow;
                }
            }
        }

        private void PaintRow(object sender, PaintEventArgs e)
        {
            Label row = (Label)sender;
            HelpEntry entry = (HelpEntry)row.Tag;
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (String.IsNullOrEmpty(entry.Shortcut))
            {
                TextRenderer.DrawText(e.Graphics, entry.Description, Font,
                    new Rectangle(2, 0, row.Width - 4, row.Height), TextMuted,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }

            using (Pen border = new Pen(Line))
                e.Graphics.DrawRectangle(border, 0, 0,
                    Math.Max(0, row.Width - 1), Math.Max(0, row.Height - 1));

            int shortcutWidth = ShortcutColumnWidth(row.Width);
            TextRenderer.DrawText(e.Graphics, entry.Shortcut, ShortcutFont,
                new Rectangle(9, 6, shortcutWidth - 12, row.Height - 10), Accent,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, entry.Description, Font,
                new Rectangle(shortcutWidth, 6, row.Width - shortcutWidth - 10, row.Height - 10),
                TextPrimary, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        private static int ShortcutColumnWidth(int rowWidth)
        {
            // Wide enough for "Двойной клик" without stealing the description's
            // room on a narrow window.
            return Math.Max(88, Math.Min(150, rowWidth / 3));
        }

        private void LayoutContent()
        {
            int width = Math.Max(200, _scroller.ClientSize.Width - 32);
            _content.Width = width;
            _content.SuspendLayout();
            int y = 0;
            using (Graphics graphics = CreateGraphics())
            {
                foreach (Control control in _content.Controls)
                {
                    Label label = control as Label;
                    if (label == null)
                        continue;

                    if ("heading".Equals(label.Tag))
                    {
                        if (y > 0)
                            y += 14;
                        label.Bounds = new Rectangle(2, y, width - 4, 18);
                        y += 22;
                        continue;
                    }

                    HelpEntry entry = (HelpEntry)label.Tag;
                    int height;
                    if (String.IsNullOrEmpty(entry.Shortcut))
                    {
                        height = MeasureHeight(graphics, entry.Description, Font, width - 4) + 6;
                    }
                    else
                    {
                        int shortcutWidth = ShortcutColumnWidth(width);
                        int left = MeasureHeight(graphics, entry.Shortcut, ShortcutFont, shortcutWidth - 12);
                        int right = MeasureHeight(graphics, entry.Description, Font, width - shortcutWidth - 10);
                        height = Math.Max(left, right) + 12;
                    }
                    label.Bounds = new Rectangle(0, y, width, height);
                    y += height + 4;
                }
            }
            _content.Height = y;
            _content.ResumeLayout();
            _content.Invalidate(true);
        }

        private static int MeasureHeight(Graphics graphics, string text, Font font, int width)
        {
            return TextRenderer.MeasureText(graphics, text, font,
                new Size(Math.Max(20, width), Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            using (Brush brush = new SolidBrush(Background))
                e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        private static IEnumerable<HelpSection> BuildSections()
        {
            return new[]
            {
                new HelpSection(Loc.T("help.section.mouse"),
                    new HelpEntry(Loc.T("help.key.leftDrag"), Loc.T("help.text.leftDrag")),
                    new HelpEntry(Loc.T("help.key.doubleClick"), Loc.T("help.text.doubleClick")),
                    new HelpEntry(Loc.T("help.key.rightClick"), Loc.T("help.text.rightClick")),
                    new HelpEntry(Loc.T("help.key.middleClick"), Loc.T("help.text.middleClick")),
                    new HelpEntry(Loc.T("help.key.wheel"), Loc.T("help.text.wheel")),
                    new HelpEntry(Loc.T("help.key.storageClick"), Loc.T("help.text.storageClick")),
                    new HelpEntry(Loc.T("help.key.edges"), Loc.T("help.text.edges")),
                    new HelpEntry(Loc.T("help.key.showHide"), Loc.T("help.text.showHide"))),

                // The two configurable combinations are printed as they are set,
                // not as they ship: a sheet that still says Alt+Ё after the user
                // moved it is worse than no sheet.
                new HelpSection(Loc.T("help.section.keys"),
                    new HelpEntry(HotkeyDisplay.Help, Loc.T("help.text.f1")),
                    new HelpEntry(HotkeyDisplay.Dismiss, Loc.T("help.text.hide")),
                    new HelpEntry(HotkeyDisplay.Pin, Loc.T("help.text.pinHotkey")),
                    new HelpEntry(HotkeyDisplay.Hide, Loc.T("help.text.hideHotkey")),
                    new HelpEntry(String.Empty, Loc.T("help.text.keysNote"))),

                // The ▾ button belongs with the other buttons of the row it sits
                // in, not under the key that happens to do the same thing.
                new HelpSection(Loc.T("help.section.buttons"),
                    new HelpEntry("↻", Loc.T("help.text.cycleButton")),
                    new HelpEntry("%", Loc.T("help.text.opacityButton")),
                    new HelpEntry("◐", Loc.T("help.text.backgroundButton")),
                    new HelpEntry("RU / EN", Loc.T("help.text.languageButton")),
                    new HelpEntry(Loc.T("help.key.pinButton"), Loc.T("help.text.pinButton")),
                    new HelpEntry("▾", Loc.T("help.text.arrowButton"))),

                new HelpSection(Loc.T("help.section.tips"),
                    new HelpEntry(String.Empty, Loc.T("help.tip.resize")),
                    new HelpEntry(String.Empty, Loc.T("help.tip.header")),
                    new HelpEntry(String.Empty, Loc.T("help.tip.customize")),
                    new HelpEntry(String.Empty, Loc.T("help.tip.gaming")),
                    new HelpEntry(String.Empty, Loc.T("help.tip.streaming")),
                    new HelpEntry(String.Empty, Loc.T("help.tip.sensors")))
            };
        }
    }
}
