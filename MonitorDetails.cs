using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Traymetry
{
    internal sealed partial class MonitorForm
    {
        private const int DetailPanelWidth = 460;
        private const int DetailPanelHeight = 270;
        private MetricDetailPanel _inlineDetails;
        private MetricDetailPanel _popupDetails;
        private MetricDetailPanel _metricDetails;
        private Form _detailPopup;
        private readonly System.Windows.Forms.Timer _detailClickTimer = new System.Windows.Forms.Timer();
        private int _pendingDetailSlot = -1;
        private CompactCardKind _pendingDetailKind;
        private bool _detailRightUp;
        private int _detailSlot = -1;
        private int _hoverDetailSlot = -1;
        private int _detailFramePid;
        private string _detailFrameSource;
        private CompactCardPresentation _lastFrameCard;
        private SensorSnapshot _lastChartSnapshot;
        private readonly Queue<double> _detailFrameTimes = new Queue<double>();
        private CancellationTokenSource _detailCancel;
        private Task _detailWorker;
        private string _detailGpu;
        private DetailSample _detailSample;

        private void InitializeMetricDetails()
        {
            _inlineDetails = new MetricDetailPanel { Visible = false };
            _inlineDetails.CloseRequested += CloseMetricDetails;
            Controls.Add(_inlineDetails);
            _metricDetails = _inlineDetails;
            _detailClickTimer.Tick += delegate { FinishDetailClick(); };
        }

        private void EnsureDetailPopup()
        {
            if (_detailPopup != null)
                return;
            _detailPopup = new DetailPopupWindow { Opacity = _opacityPercent / 100.0 };
            _popupDetails = new MetricDetailPanel { Dock = DockStyle.Fill };
            _popupDetails.CloseRequested += CloseMetricDetails;
            _detailPopup.Controls.Add(_popupDetails);
        }

        private sealed class DetailPopupWindow : Form
        {
            internal DetailPopupWindow()
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(27, 32, 40);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams options = base.CreateParams;
                    options.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    return options;
                }
            }
        }

        private static bool SupportsDetails(CompactCardKind kind)
        {
            return kind == CompactCardKind.Cpu || kind == CompactCardKind.Gpu ||
                kind == CompactCardKind.Network || kind == CompactCardKind.Fps;
        }

        private int DetailSlotAt(Point client)
        {
            if (_pinned || !Visible || GetResizeHitTest(client, ResizeEdge) != 1)
                return -1;
            for (int index = 0; index < _compactSlots.Length; index++)
                if (_compactSlots[index].Card.Visible && _compactSlots[index].Card.Bounds.Contains(client) &&
                    SupportsDetails(_compactSlotKinds[index]))
                    return index;
            return -1;
        }

        private void UpdateDetailHover()
        {
            int slot = IsMenuOpen() ? -1 : DetailSlotAt(PointToClient(Cursor.Position));
            if (slot == _hoverDetailSlot)
                return;
            _hoverDetailSlot = slot;
            RefreshDetailCardBorders();
        }

        private void RefreshDetailCardBorders()
        {
            for (int index = 0; index < _compactSlots.Length; index++)
            {
                CompactCardSlotView slot = _compactSlots[index];
                bool active = _detailSlot == index;
                bool hover = _hoverDetailSlot == index;
                Color accent = active || hover ? GetCardAccent(_compactSlotKinds[index]) : Color.Empty;
                slot.Card.SetDetailHighlight(accent, active);
                if (hover || slot.Card.Cursor != Cursors.Default)
                {
                    Cursor cursor = hover ? Cursors.Hand : Cursors.Default;
                    slot.Card.Cursor = cursor;
                    slot.Caption.Cursor = cursor;
                    slot.Value.Cursor = cursor;
                    slot.Column.Cursor = cursor;
                }
            }
        }

        private bool TryHandleDetailPointerDown(Point client, bool nativeDoubleClick)
        {
            if (_pinned || _metricDetails == null || GetResizeHitTest(client, ResizeEdge) != 1)
                return false;
            if (_detailSlot >= 0 && _metricDetails == _inlineDetails &&
                _inlineDetails.Visible && _inlineDetails.Bounds.Contains(client))
                return TryHandleDetailPress(client);

            int slot = DetailSlotAt(client);
            if (slot < 0)
                return false;
            int now = Environment.TickCount;
            Point screen = PointToScreen(client);
            if (HandleDetailDoubleClick(slot, screen, now, nativeDoubleClick))
                return true;

            CancelDetailClick();
            _dragClickPending = false;
            MoveWidgetFromPress();
            if (!_windowMovedDuringDragClick && DetailSlotAt(client) == slot)
                QueueDetailClick(slot, screen, now);
            return true;
        }

        private bool HandleDetailDoubleClick(int slot, Point screen, int now, bool nativeDoubleClick)
        {
            bool secondPress = (_pendingDetailSlot < 0 || (_pendingDetailSlot == slot &&
                _pendingDetailKind == _compactSlotKinds[slot])) && IsWidgetDoubleClick(screen, now);
            if (!secondPress && !nativeDoubleClick)
                return false;
            CancelDetailClick();
            CloseMetricDetails();
            CloseOpacityPopup();
            _dragClickPending = false;
            ToggleViewByDoubleClick();
            return true;
        }

        private void QueueDetailClick(int slot, Point screen, int beganAt)
        {
            _pendingDetailSlot = slot;
            _pendingDetailKind = _compactSlotKinds[slot];
            RememberWidgetPress(screen, beganAt);
            int elapsed = Math.Max(0, unchecked(Environment.TickCount - beganAt));
            _detailClickTimer.Interval = Math.Max(1, SystemInformation.DoubleClickTime - elapsed + 1);
            _detailClickTimer.Start();
        }

        private void CancelDetailClick()
        {
            if (_pendingDetailSlot < 0)
                return;
            _detailClickTimer.Stop();
            _pendingDetailSlot = -1;
        }

        private void FinishDetailClick()
        {
            int slot = _pendingDetailSlot;
            CancelDetailClick();
            if (slot >= 0)
                _dragClickPending = false;
            if (slot < 0 || _stopping || _pinned || !Visible || IsMenuOpen())
                return;
            Control card = _compactSlots[slot].Card;
            if (card.Visible && _pendingDetailKind == _compactSlotKinds[slot])
                TryHandleDetailPress(new Point(card.Left + card.Width / 2, card.Top + card.Height / 2));
        }

        private bool TryHandleDetailPress(Point client)
        {
            if (_pinned || _metricDetails == null)
                return false;
            if (_detailSlot >= 0 && _metricDetails == _inlineDetails &&
                _inlineDetails.Visible && _inlineDetails.Bounds.Contains(client))
            {
                _metricDetails.HandlePress(new Point(client.X - _metricDetails.Left,
                    client.Y - _metricDetails.Top));
                return true;
            }

            int slot = DetailSlotAt(client);
            if (slot < 0)
                return false;
            bool close = slot == _detailSlot;
            CloseMetricDetails();
            _dragClickPending = false;
            if (close)
                return true;

            _detailSlot = slot;
            Rectangle inlineBounds = GetInlineDetailBounds(_compactSlots[slot].Card);
            if (inlineBounds.IsEmpty)
            {
                EnsureDetailPopup();
                _metricDetails = _popupDetails;
            }
            else
            {
                _metricDetails = _inlineDetails;
            }
            _metricDetails.Kind = _compactSlotKinds[slot];
            _detailFramePid = _metricDetails.Kind == CompactCardKind.Fps && _lastFrameCard != null
                ? _lastFrameCard.FrameProcessId : 0;
            _detailFrameSource = _detailFramePid > 0 ? _lastFrameCard.Values[3] : null;
            LayoutMetricDetails();
            if (_detailSlot < 0)
                return true;
            RefreshDetailCardBorders();
            RefreshMetricDetails(_lastSnapshot);
            if (_metricDetails == _popupDetails)
            {
                _detailPopup.TopMost = TopMost;
                _detailPopup.Show(this);
                ApplyDisplayAffinity();
            }
            else
            {
                _inlineDetails.BringToFront();
                _inlineDetails.Show();
            }
            return true;
        }

        private Rectangle GetInlineDetailBounds(Control anchor)
        {
            int width = Math.Min(DetailPanelWidth, ClientSize.Width - 20);
            int y = anchor.Bottom + 8;
            int height = Math.Min(DetailPanelHeight, LayoutContentBottom - ChromeBandReserve - y - 4);
            if (!_expanded || width < 350 || height < 225)
                return Rectangle.Empty;
            int x = Math.Max(10, Math.Min(anchor.Left, ClientSize.Width - 10 - width));
            return new Rectangle(x, y, width, height);
        }

        private void LayoutMetricDetails()
        {
            if (_detailSlot < 0)
                return;
            Control anchor = _compactSlots[_detailSlot].Card;
            if (_pinned || !anchor.Visible ||
                _compactSlotKinds[_detailSlot] != _metricDetails.Kind)
            {
                CloseMetricDetails();
                return;
            }
            if (_metricDetails == _popupDetails)
            {
                Rectangle card = anchor.RectangleToScreen(anchor.ClientRectangle);
                _detailPopup.Bounds = GetDetailPopupBounds(Bounds, card,
                    Screen.FromRectangle(card).WorkingArea);
                return;
            }
            Rectangle bounds = GetInlineDetailBounds(anchor);
            if (bounds.IsEmpty)
                CloseMetricDetails();
            else
                _inlineDetails.Bounds = bounds;
        }

        internal static Rectangle GetDetailPopupBounds(Rectangle widget, Rectangle anchor, Rectangle area)
        {
            const int gap = 8;
            int width = Math.Min(DetailPanelWidth, area.Width);
            int height = Math.Min(DetailPanelHeight, area.Height);
            Point below = new Point(anchor.Left, widget.Bottom + gap);
            Point above = new Point(anchor.Left, widget.Top - gap - height);
            Point right = new Point(widget.Right + gap, anchor.Top);
            Point left = new Point(widget.Left - gap - width, anchor.Top);
            bool vertical = widget.Height > widget.Width;
            Point[] positions = vertical ? new[] { right, left, below, above } : new[] { below, above, right, left };
            for (int index = 0; index < positions.Length; index++)
            {
                Point candidate = positions[index];
                bool beside = vertical ? index < 2 : index >= 2;
                if (beside && candidate.X >= area.Left && candidate.X + width <= area.Right)
                    return new Rectangle(candidate.X, Math.Max(area.Top, Math.Min(candidate.Y, area.Bottom - height)), width, height);
                if (!beside && candidate.Y >= area.Top && candidate.Y + height <= area.Bottom)
                    return new Rectangle(Math.Max(area.Left, Math.Min(candidate.X, area.Right - width)), candidate.Y, width, height);
            }

            int spaceRight = area.Right - widget.Right - gap;
            int spaceLeft = widget.Left - area.Left - gap;
            int sideWidth = Math.Min(width, Math.Max(spaceRight, spaceLeft));
            if (sideWidth >= 270)
            {
                int x = spaceRight >= spaceLeft ? widget.Right + gap : widget.Left - gap - sideWidth;
                int y = Math.Max(area.Top, Math.Min(anchor.Top, area.Bottom - height));
                return new Rectangle(x, y, sideWidth, height);
            }

            int spaceBelow = area.Bottom - anchor.Bottom - gap;
            int spaceAbove = anchor.Top - area.Top - gap;
            int popupY = spaceBelow >= spaceAbove ? anchor.Bottom + gap : anchor.Top - gap - height;
            return new Rectangle(Math.Max(area.Left, Math.Min(anchor.Left, area.Right - width)),
                Math.Max(area.Top, Math.Min(popupY, area.Bottom - height)), width, height);
        }

        private void CloseMetricDetails()
        {
            CancelDetailClick();
            if (_detailSlot < 0)
                return;
            _detailSlot = -1;
            StopDetailCollection();
            _detailSample = null;
            _detailFrameTimes.Clear();
            _lastChartSnapshot = null;
            if (_metricDetails == _popupDetails)
            {
                if (!_detailPopup.IsDisposed)
                    _detailPopup.Hide();
            }
            else
                _inlineDetails.Hide();
            RefreshDetailCardBorders();
            Invalidate();
        }

        private void DismissDetailsFromOutside(bool mouseDown, Point screen)
        {
            if (_detailSlot < 0 && _pendingDetailSlot < 0)
                return;
            if ((NativeUi.GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0 ||
                (mouseDown && !OverWidget(screen) &&
                    (_detailPopup == null || !_detailPopup.Visible || !_detailPopup.Bounds.Contains(screen))))
                CloseMetricDetails();
        }

        private void StopDetailCollection()
        {
            CancellationTokenSource previous = _detailCancel;
            if (previous == null)
                return;
            _detailCancel = null;
            previous.Cancel();
            _detailWorker.ContinueWith(delegate { previous.Dispose(); }, TaskScheduler.Default);
            _detailWorker = null;
        }

        private void StartDetailCollection(CompactCardKind kind, string gpuName)
        {
            StopDetailCollection();
            _detailGpu = gpuName;
            _detailSample = null;
            CancellationTokenSource session = new CancellationTokenSource();
            _detailCancel = session;
            CancellationToken token = session.Token;
            _detailWorker = Task.Run(async delegate
            {
                if (token.IsCancellationRequested)
                    return;
                CpuDetailSampler cpu = kind == CompactCardKind.Cpu ? new CpuDetailSampler() : null;
                try
                {
                    using (GpuDetailSampler gpu = kind == CompactCardKind.Gpu ? new GpuDetailSampler() : null)
                    {
                        if (kind == CompactCardKind.Network)
                        {
                            await CollectNetworkDetails(session, token).ConfigureAwait(false);
                            return;
                        }
                        while (!token.IsCancellationRequested)
                        {
                            DetailSample sample = cpu != null ? cpu.Read() : gpu.Read(gpuName);
                            PublishDetailSample(session, token, sample);
                            await Task.Delay(1250, token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    DiagLog.Write("detail telemetry " + kind + ": " + error.GetType().Name);
                    PublishDetailSample(session, token, new DetailSample { StatusKey = "detail.unavailable" });
                }
            });
        }

        private async Task CollectNetworkDetails(CancellationTokenSource session, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                DetailSample sample;
                if (!SensorServiceClient.TryReadNetworkRates(out sample))
                    sample = NetworkDetailSampler.ReadConnectionCounts();
                PublishDetailSample(session, token, sample);
                await Task.Delay(1250, token).ConfigureAwait(false);
            }
        }

        private void PublishDetailSample(CancellationTokenSource session, CancellationToken token, DetailSample sample)
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (_detailCancel != session)
                        return;
                    _detailSample = sample;
                    RefreshMetricDetails(_lastSnapshot);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void RefreshMetricDetails(SensorSnapshot snapshot)
        {
            if (_detailSlot < 0)
                return;
            MetricDetailPanel panel = _metricDetails;
            CompactCardKind kind = panel.Kind;
            panel.Accent = GetCardAccent(kind);
            if (kind == CompactCardKind.Fps)
            {
                RefreshFrameDetails(snapshot);
                panel.Invalidate();
                return;
            }

            string gpuName = kind == CompactCardKind.Gpu && snapshot != null ? snapshot.GpuName : null;
            if (_detailCancel == null || !String.Equals(gpuName, _detailGpu, StringComparison.Ordinal))
                StartDetailCollection(kind, gpuName);
            ProcessDetail[] rows = _detailSample != null ? _detailSample.Rows : new ProcessDetail[0];
            panel.Rows = rows;
            panel.ConnectionCounts = _detailSample != null && _detailSample.ConnectionCounts;
            panel.Summary = Loc.T("state.noData");
            panel.TotalFraction = 0;
            panel.RowScale = 1;
            panel.Hint = Loc.T(_detailSample != null ? _detailSample.StatusKey : "state.collecting");

            if (kind == CompactCardKind.Cpu)
            {
                panel.RowScale = 100;
                double? sampledTotal = _detailSample == null ? null : _detailSample.CpuTotalPercent;
                double? total = sampledTotal ?? (snapshot != null && snapshot.Usage >= 0
                    ? (double?)snapshot.Usage : null);
                if (total.HasValue)
                {
                    panel.Summary = Loc.T("detail.cpuTotal", MetricDetailPanel.FormatPercent(total.Value));
                    panel.TotalFraction = total.Value / 100;
                }
                if (rows.Length > 0)
                    panel.Hint = Loc.T("detail.cpu.scale");
            }
            else if (kind == CompactCardKind.Gpu)
            {
                const double gib = 1024.0 * 1024 * 1024;
                double? adapterBytes = _detailSample == null ? null : _detailSample.GpuAdapterBytes;
                if (snapshot != null && snapshot.GpuMemoryTotalGb > 0)
                {
                    panel.RowScale = snapshot.GpuMemoryTotalGb * gib;
                    double usedBytes = adapterBytes ?? snapshot.GpuMemoryUsedGb * gib;
                    panel.TotalFraction = usedBytes / panel.RowScale;
                    panel.Summary = Loc.T(adapterBytes.HasValue ? "detail.vramWindowsTotal" : "detail.vramTotal",
                        MetricDetailPanel.FormatMemory(usedBytes, true),
                        MetricDetailPanel.FormatMemory(snapshot.GpuMemoryTotalGb * gib));
                }
                else if (rows.Length > 0)
                    panel.RowScale = rows[0].Value;
                if (rows.Length > 0)
                {
                    double shown = rows.Sum(row => row.Value);
                    string subtotal = Loc.T("detail.gpu.shown", rows.Length,
                        MetricDetailPanel.FormatMemory(shown, true));
                    if (_detailSample.GpuOtherProcessCount > 0)
                        subtotal += " · " + Loc.T("detail.gpu.others", _detailSample.GpuOtherProcessCount,
                            MetricDetailPanel.FormatMemory(_detailSample.GpuOtherProcessBytes, true));
                    string note = Loc.T("detail.gpu.note");
                    if (adapterBytes.HasValue)
                    {
                        double gap = adapterBytes.Value - shown - _detailSample.GpuOtherProcessBytes;
                        if (Math.Abs(gap) >= 1024 * 1024)
                            note = Loc.T(gap > 0 ? "detail.gpu.gap" : "detail.gpu.overlap",
                                MetricDetailPanel.FormatMemory(Math.Abs(gap), true));
                    }
                    panel.Hint = subtotal + Environment.NewLine + note;
                }
            }
            else
            {
                if (snapshot != null)
                    panel.Summary = Loc.T("detail.network.total",
                        MetricDetailPanel.FormatRate(snapshot.NetworkDownloadKbps),
                        MetricDetailPanel.FormatRate(snapshot.NetworkUploadKbps));
                if (rows.Length > 0)
                {
                    panel.RowScale = rows[0].Value + rows[0].Secondary;
                    panel.Hint = Loc.T(panel.ConnectionCounts ? "detail.network.connections" : "detail.network.note");
                }
            }
            panel.Invalidate();
        }

        private void RefreshFrameDetails(SensorSnapshot snapshot)
        {
            CompactCardPresentation fps = CreateFrameCompactPresentation(snapshot, snapshot != null,
                Color.FromArgb(147, 158, 173), _detailFramePid);
            if (fps.FrameProcessId <= 0)
            {
                _metricDetails.Summary = _detailFrameSource ?? fps.Captions[3];
                _metricDetails.Hint = _detailFrameSource != null
                    ? Loc.T("detail.fps.noRecent") : String.Empty;
                _metricDetails.FrameFooter = String.Empty;
            }
            else
            {
                _detailFramePid = fps.FrameProcessId;
                _detailFrameSource = fps.Values[3];
                _metricDetails.Summary = _detailFrameSource;
                _metricDetails.Hint = fps.Values[0] + "   ·   " + fps.Values[1] +
                    "   ·   1% low " + fps.Values[2];
                _metricDetails.FrameFooter = Loc.T(fps.Values[2] == "—"
                    ? "detail.fps.lowPending" : "detail.fps.low");
            }
            if (!Object.ReferenceEquals(snapshot, _lastChartSnapshot))
            {
                _lastChartSnapshot = snapshot;
                if (fps.FrameDuration > 0)
                {
                    if (_detailFrameTimes.Count >= 40)
                        _detailFrameTimes.Dequeue();
                    _detailFrameTimes.Enqueue(fps.FrameDuration);
                }
                else
                    _detailFrameTimes.Clear();
            }
            _metricDetails.FrameTimes = _detailFrameTimes.ToArray();
        }
    }
}
