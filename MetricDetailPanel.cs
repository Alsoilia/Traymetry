using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Traymetry
{
    internal sealed class MetricDetailPanel : Control
    {
        private readonly Font _title = new Font("Segoe UI", 12F, FontStyle.Bold);
        private readonly Font _body = new Font("Segoe UI", 11F);
        private readonly Font _bold = new Font("Segoe UI", 11F, FontStyle.Bold);
        private readonly Font _small = new Font("Segoe UI", 9.5F);
        private readonly Color _ink = Color.FromArgb(241, 244, 248);
        private readonly Color _muted = Color.FromArgb(190, 200, 213);
        private readonly Color _track = Color.FromArgb(47, 56, 68);
        private bool _closeHover;

        internal CompactCardKind Kind;
        internal Color Accent;
        internal string Summary;
        internal string Hint;
        internal string FrameFooter;
        internal double TotalFraction;
        internal double RowScale;
        internal bool ConnectionCounts;
        internal ProcessDetail[] Rows = new ProcessDetail[0];
        internal double[] FrameTimes = new double[0];
        internal event Action CloseRequested;

        internal MetricDetailPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(27, 32, 40);
        }

        private Rectangle CloseBounds { get { return new Rectangle(Width - 38, 4, 32, 28); } }

        internal void HandlePress(Point point)
        {
            if (CloseBounds.Contains(point) && CloseRequested != null)
                CloseRequested();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
                HandlePress(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetCloseHover(CloseBounds.Contains(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetCloseHover(false);
        }

        private void SetCloseHover(bool value)
        {
            if (_closeHover == value)
                return;
            _closeHover = value;
            Cursor = value ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (Brush background = new SolidBrush(BackColor))
                g.FillRectangle(background, ClientRectangle);
            using (Pen border = new Pen(_track))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            string label = Kind == CompactCardKind.Cpu ? "CPU" :
                Kind == CompactCardKind.Gpu ? "GPU · " + Loc.T("detail.vram") :
                Kind == CompactCardKind.Fps ? "FPS" :
                Loc.T(ConnectionCounts ? "detail.network.tcpTitle" : "card.network");
            DrawText(g, label, _title, Accent, new Rectangle(14, 5, Width - 58, 28), false);
            if (_closeHover)
                using (Brush hover = new SolidBrush(_track))
                    g.FillRectangle(hover, CloseBounds);
            using (Pen cross = new Pen(_ink, 1.35F))
            {
                int x = CloseBounds.Left + CloseBounds.Width / 2;
                int y = CloseBounds.Top + CloseBounds.Height / 2;
                g.DrawLine(cross, x - 4, y - 4, x + 4, y + 4);
                g.DrawLine(cross, x - 4, y + 4, x + 4, y - 4);
            }

            DrawText(g, Summary, _bold, _ink, new Rectangle(14, 35, Width - 28, 24), false);
            if (Kind == CompactCardKind.Fps)
                PaintFps(g);
            else
                PaintProcesses(g);
        }

        private void PaintProcesses(Graphics g)
        {
            bool network = Kind == CompactCardKind.Network;
            if (!network)
                DrawBar(g, new Rectangle(14, 62, Width - 28, 7), TotalFraction, 0);
            int first = network ? 66 : 77;
            int footer = Height - 46;
            int lineHeight = Math.Min(28, Math.Max(20, (footer - first) / 5));
            bool roomForBars = Width >= 270;
            int rightWidth = network && !ConnectionCounts ? 174 : 98;
            int nameWidth = roomForBars ? Math.Max(64, (Width - rightWidth - 28) * 45 / 100) :
                Width - rightWidth - 28;
            for (int index = 0; index < Rows.Length && first + (index + 1) * lineHeight <= footer; index++)
            {
                ProcessDetail row = Rows[index];
                int y = first + index * lineHeight;
                string amount = Kind == CompactCardKind.Cpu ? FormatPercent(row.Value) :
                    Kind == CompactCardKind.Gpu ? FormatMemory(row.Value) :
                    ConnectionCounts ? Loc.T("detail.network.connectionCount",
                        row.Value.ToString("0", CultureInfo.CurrentCulture)) :
                    "↓ " + FormatRate(row.Value) + "  ↑ " + FormatRate(row.Secondary);
                DrawText(g, row.Name, _body, _ink, new Rectangle(14, y, nameWidth, lineHeight), false);
                int barWidth = Width - rightWidth - nameWidth - 34;
                if (roomForBars && barWidth > 0)
                    DrawBar(g, new Rectangle(18 + nameWidth, y + (lineHeight - 5) / 2, barWidth, 5),
                        row.Value / RowScale, row.Secondary / RowScale);
                DrawText(g, amount, network ? _small : _bold, _ink,
                    new Rectangle(Width - 12 - rightWidth, y, rightWidth, lineHeight), true);
            }
            int hintY = Rows.Length == 0 ? first + 5 : footer;
            DrawText(g, Hint, _small, _muted, new Rectangle(14, hintY, Width - 28, 42), false, true);
        }

        private void PaintFps(Graphics g)
        {
            DrawText(g, Hint, _body, _muted, new Rectangle(14, 60, Width - 28, 24), false);
            DrawText(g, FrameFooter, _small, _muted,
                new Rectangle(14, Height - 34, Width - 28, 32), false, true);
            if (FrameTimes.Length < 2 || Height < 145)
                return;
            Rectangle chart = new Rectangle(14, 110, Width - 28, Height - 151);
            double peak = FrameTimes.Max();
            if (peak <= 0 || chart.Width < 2 || chart.Height < 2)
                return;
            DrawText(g, Loc.T("detail.frameHistory"), _small, _muted,
                new Rectangle(14, 87, Width - 110, 20), false);
            DrawText(g, peak.ToString("0.#", CultureInfo.CurrentCulture) + " ms", _small, _muted,
                new Rectangle(Width - 92, 87, 78, 20), true);
            using (Pen grid = new Pen(_track))
                g.DrawRectangle(grid, chart);
            PointF[] points = new PointF[FrameTimes.Length];
            for (int index = 0; index < points.Length; index++)
                points[index] = new PointF(chart.Left + index * chart.Width / (float)(points.Length - 1),
                    chart.Bottom - (float)(FrameTimes[index] / peak) * (chart.Height - 1));
            using (Pen line = new Pen(Accent, 1.5F))
                g.DrawLines(line, points);
        }

        private void DrawBar(Graphics graphics, Rectangle track, double fraction, double secondary)
        {
            using (Brush background = new SolidBrush(_track))
                graphics.FillRectangle(background, track);
            int first = BarWidth(track.Width, fraction);
            int next = Math.Min(track.Width - first, BarWidth(track.Width, secondary));
            using (Brush fill = new SolidBrush(Accent))
                graphics.FillRectangle(fill, track.X, track.Y, first, track.Height);
            if (next > 0)
                using (Brush fill = new SolidBrush(Color.FromArgb(115, 150, 212)))
                    graphics.FillRectangle(fill, track.X + first, track.Y, next, track.Height);
        }

        private static int BarWidth(int width, double fraction)
        {
            return fraction > 0 ? Math.Max(1, (int)Math.Round(width * Math.Min(1, fraction))) : 0;
        }

        private static void DrawText(Graphics g, string text, Font font, Color color,
            Rectangle bounds, bool right, bool wrap = false)
        {
            if (String.IsNullOrEmpty(text) || bounds.Width <= 0)
                return;
            using (Brush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap))
            {
                format.Alignment = right ? StringAlignment.Far : StringAlignment.Near;
                format.LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center;
                format.Trimming = wrap ? StringTrimming.EllipsisWord : StringTrimming.EllipsisCharacter;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        internal static string FormatPercent(double value)
        {
            if (value > 0 && value < 0.1)
                return "<0.1%";
            return value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        }

        internal static string FormatMemory(double bytes, bool precise = false)
        {
            const double mib = 1024 * 1024;
            return bytes >= mib * 1024
                ? (bytes / (mib * 1024)).ToString(precise ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " " + Loc.T("detail.gb")
                : (bytes / mib).ToString("0.#", CultureInfo.CurrentCulture) + " " + Loc.T("detail.mb");
        }

        internal static string FormatRate(double kib)
        {
            if (kib > 0 && kib < 0.1)
                return "<" + 0.1.ToString("0.0", CultureInfo.CurrentCulture) + " " + Loc.T("detail.kbRate");
            return kib >= 1024
                ? (kib / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " " + Loc.T("detail.mbRate")
                : kib.ToString("0.#", CultureInfo.CurrentCulture) + " " + Loc.T("detail.kbRate");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _title.Dispose();
                _body.Dispose();
                _bold.Dispose();
                _small.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
