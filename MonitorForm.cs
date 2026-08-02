using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Traymetry
{
    internal enum CompactCardKind
    {
        Cpu,
        Gpu,
        Memory,
        Network,
        Storage,
        Fans,
        Fps
    }

    internal enum CompactCardLayoutFlavor
    {
        Normal,
        Rate
    }

    internal sealed class CompactCardPresentation
    {
        public string Caption;
        public string Primary;
        public string Secondary;
        public string[] Values;
        public string[] Captions;
        public Color Accent;
        public CompactCardLayoutFlavor Flavor;
    }

    internal sealed class CompactCardSlotView
    {
        public MonitorCard Card;
        public Label Caption;
        public Label Value;
        public CompactMetricColumn Column;
        public CompactCardLayoutFlavor Flavor;
    }

    internal sealed class CompactPresetMenuTag
    {
        public CompactPresetMenuTag(CompactCardKind[] kinds)
        {
            Kinds = kinds;
        }

        public CompactCardKind[] Kinds { get; private set; }
    }

    internal sealed class CompactSlotMenuTag
    {
        public CompactSlotMenuTag(int slotIndex, CompactCardKind kind)
        {
            SlotIndex = slotIndex;
            Kind = kind;
        }

        public int SlotIndex { get; private set; }
        public CompactCardKind Kind { get; private set; }
    }

    internal sealed class MonitorForm : Form, IMessageFilter
    {
        private const int WindowWidth = 430;
        private const int CompactHeight = 96;
        private const int ExpandedHeight = 360;
        private const int SuperExpandedWidth = 760;
        private const int SuperExpandedHeight = 760;
        private const int MinimumCompactWidth = 96;
        private const int CompactHeaderDelta = 22;
        private const int HeaderlessCompactMinimumHeight = 56;
        private const int CompactHeaderRevealHeight = 77;
        private const string AppRegistryPath = @"Software\Traymetry";
        private const string StartupValueName = "Traymetry";
        private const string OpacityTooltip = "Клик / средняя кнопка — показать или скрыть\nКолесо мыши — изменить прозрачность";
        private static readonly Color NormalBackground = Color.FromArgb(20, 23, 28);
        private static readonly Color BackgroundKey = Color.FromArgb(1, 2, 3);
        private static readonly Color GpuAccent = Color.FromArgb(24, 124, 82);

        private readonly Label _compactCpu;
        private readonly Label _compactGpu;
        private readonly Label _compactNetwork;
        private readonly Label _compactMemory;
        private readonly CompactMetricColumn _compactCpuColumn;
        private readonly CompactMetricColumn _compactGpuColumn;
        private readonly CompactMetricColumn _compactNetworkColumn;
        private readonly CompactMetricColumn _compactMemoryColumn;
        private readonly CompactCardSlotView[] _compactSlots;
        private readonly Label _title;
        private readonly Label _cpuName;
        private readonly Label _gpuName;
        private readonly Label _gpuMemory;
        private readonly Label _opacityLabel;
        private readonly MetricReadout _cpuTemperature;
        private readonly MetricReadout _cpuUsage;
        private readonly MetricReadout _cpuClock;
        private readonly MetricReadout _cpuPower;
        private readonly MetricReadout _gpuTemperature;
        private readonly MetricReadout _gpuUsage;
        private readonly MetricReadout _gpuClock;
        private readonly MetricReadout _gpuPower;
        private readonly Panel _detailsArea;
        private readonly MonitorCard _cpuCompactCard;
        private readonly MonitorCard _gpuCompactCard;
        private readonly MonitorCard _networkCompactCard;
        private readonly MonitorCard _memoryCompactCard;
        private readonly MonitorCard _cpuCard;
        private readonly MonitorCard _gpuCard;
        private readonly OpacityPopupForm _opacityCard;
        private readonly ExpandableStrip _superToggleButton;
        private readonly Panel _superArea;
        private readonly RingGauge _cpuGauge;
        private readonly RingGauge _gpuGauge;
        private readonly SensorHistoryControl _cpuHistory;
        private readonly SensorHistoryControl _gpuHistory;
        private readonly ResourceSummaryControl _memorySummary;
        private readonly ResourceSummaryControl _storageSummary;
        private readonly FanSummaryControl _fanSummary;
        private readonly ContextMenuStrip _storageMenu;
        private readonly SlimOpacitySlider _opacitySlider;
        private readonly CheckBox _backgroundCheckBox;
        private readonly Button _opacityButton;
        private readonly Button _backgroundButton;
        private readonly Button _cycleButton;
        private readonly Button _pinButton;
        private readonly Button _expandButton;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _viewItem;
        private readonly ToolStripMenuItem _headerVisibilityItem;
        private readonly ToolStripMenuItem _pinItem;
        private readonly ToolStripMenuItem _topMostItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _backgroundItem;
        private readonly List<ToolStripMenuItem> _opacityItems = new List<ToolStripMenuItem>();
        private readonly List<MonitorCard> _cards = new List<MonitorCard>();
        private readonly List<Button> _headerButtons = new List<Button>();
        private readonly ToolTip _tips = new ToolTip();
        private readonly ResizeGripControl _topLeftResizeGrip;
        private readonly ResizeGripControl _leftResizeGrip;
        private readonly ResizeGripControl _resizeGrip;
        private readonly BackgroundHitForm _backgroundHitForm;

        private volatile bool _stopping;
        private bool _expanded;
        private bool _superExpanded;
        private bool _backgroundless;
        private bool _opacityPopupVisible;
        private bool _loadingSettings;
        private bool _switchingView;
        private bool _automaticTransition;
        private bool _pinned;
        private bool _interactiveResize;
        private bool _resizeFromLeftEdge;
        private bool _backgroundHitSuspended;
        private bool _applyingSizeLimits;
        private bool _layoutInProgress;
        private bool _compactLocationKnown;
        private bool _headerManuallyHidden;
        private bool _restoredAutomaticHeaderHidden;
        private Point _compactLocation;
        private int _compactPageIndex;
        private CompactCardKind[] _compactSlotKinds = CreateSystemCompactPreset();
        private int _currentCompactVisibleCards = 1;
        private int _currentCompactCardCount = 4;
        private int _lastCompactVisibleCards = -1;
        private int _lastCompactCardCount = -1;
        private Size _compactSize = new Size(WindowWidth, CompactHeight);
        private Size _expandedSize = new Size(WindowWidth, ExpandedHeight);
        private Size _superExpandedSize = new Size(SuperExpandedWidth, SuperExpandedHeight);
        private bool _superReturnStateKnown;
        private bool _superReturnExpanded;
        private Size _superReturnSize;
        private Point _superReturnLocation;
        private SensorSnapshot _lastSnapshot;
        private string _selectedStorageDrive = String.Empty;
        private string _storageMenuSignature = String.Empty;
        private Thread _worker;
        private IntPtr _pinnedClickTarget;
        private bool _dragClickPending;
        private int _lastDragClickTick;
        private Point _lastDragClickPosition;
        private bool _windowMovedDuringDragClick;

        public MonitorForm()
        {
            StartupTrace.Write("form-constructor-enter");
            Text = "Traymetry";
            ClientSize = new Size(WindowWidth, CompactHeight);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = NormalBackground;
            ForeColor = Color.White;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            MaximumSize = new Size(1000, 760);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            Label title = _title = MakeLabel("TRAYMETRY", new Point(12, 4), new Size(180, 22), 8F, FontStyle.Bold, Color.FromArgb(125, 135, 148));
            Controls.Add(title);
            _tips.SetToolTip(_title, "Двойной клик — максимальный вид / вернуться");

            _opacityButton = MakeHeaderButton("%", 312);
            _opacityButton.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
            _opacityButton.AccessibleName = "Настроить прозрачность";
            _backgroundButton = MakeHeaderButton("◐", 336);
            _backgroundButton.AccessibleName = "Включить или отключить фон";
            _cycleButton = MakeHeaderButton("↻", 360);
            _cycleButton.AccessibleName = "Следующий показатель";
            _pinButton = MakeHeaderButton("\uE718", 384);
            _pinButton.Font = new Font("Segoe MDL2 Assets", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _pinButton.AccessibleName = "Закрепить положение";
            _expandButton = MakeHeaderButton("▾", 408);
            _expandButton.Location = new Point(404, 1);
            _expandButton.Size = new Size(24, 25);
            _expandButton.Font = new Font("Segoe UI Symbol", 13F, FontStyle.Bold, GraphicsUnit.Point);
            _expandButton.AccessibleName = "Скрыть в область уведомлений";
            _headerButtons.Add(_opacityButton);
            _headerButtons.Add(_backgroundButton);
            _headerButtons.Add(_cycleButton);
            _headerButtons.Add(_pinButton);
            _headerButtons.Add(_expandButton);
            Controls.Add(_opacityButton);
            Controls.Add(_backgroundButton);
            Controls.Add(_cycleButton);
            Controls.Add(_pinButton);
            Controls.Add(_expandButton);

            _tips.InitialDelay = 650;
            _tips.ReshowDelay = 150;
            _tips.AutoPopDelay = 5000;
            _tips.ShowAlways = true;
            _tips.SetToolTip(_opacityButton, OpacityTooltip);
            _tips.SetToolTip(_backgroundButton, "Показать или убрать фон");
            _tips.SetToolTip(_cycleButton, "Листать или переставлять карточки: CPU → GPU → Сеть");
            _tips.SetToolTip(_pinButton, "Закрепить положение и отключить клики по утилите");
            _tips.SetToolTip(_expandButton, "Скрыть в область уведомлений");
            _opacityButton.Click += delegate { ToggleOpacityPopup(); };
            _backgroundButton.Click += delegate { ApplyBackgroundMode(!_backgroundless, true); };
            _cycleButton.Click += delegate { CycleCompactCards(); };
            _pinButton.Click += delegate { ApplyPinnedMode(!_pinned, true); };
            _expandButton.Click += delegate { CloseOpacityPopup(); Hide(); };
            AddHeaderHover(_opacityButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_backgroundButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_cycleButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_pinButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_expandButton, Color.FromArgb(43, 48, 57));

            MonitorCard cpuCompactCard = _cpuCompactCard = new MonitorCard();
            _cards.Add(cpuCompactCard);
            cpuCompactCard.Location = new Point(10, 29);
            cpuCompactCard.Size = new Size(125, 58);
            Label cpuCompactCaption = MakeLabel("CPU", new Point(9, 4), new Size(105, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
            cpuCompactCard.Controls.Add(cpuCompactCaption);
            _compactCpu = MakeLabel("—°C   —%", new Point(8, 21), new Size(110, 30), 15F, FontStyle.Bold, Color.FromArgb(150, 158, 169));
            cpuCompactCard.Controls.Add(_compactCpu);
            _compactCpuColumn = new CompactMetricColumn();
            _compactCpuColumn.Visible = false;
            cpuCompactCard.Controls.Add(_compactCpuColumn);

            MonitorCard gpuCompactCard = _gpuCompactCard = new MonitorCard();
            _cards.Add(gpuCompactCard);
            gpuCompactCard.Location = new Point(143, 29);
            gpuCompactCard.Size = new Size(125, 58);
            Label gpuCompactCaption = MakeLabel("GPU", new Point(9, 4), new Size(105, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
            gpuCompactCard.Controls.Add(gpuCompactCaption);
            _compactGpu = MakeLabel("—°C   —%", new Point(8, 21), new Size(110, 30), 15F, FontStyle.Bold, Color.FromArgb(150, 158, 169));
            gpuCompactCard.Controls.Add(_compactGpu);
            _compactGpuColumn = new CompactMetricColumn();
            _compactGpuColumn.Visible = false;
            gpuCompactCard.Controls.Add(_compactGpuColumn);

            MonitorCard networkCompactCard = _networkCompactCard = new MonitorCard();
            _cards.Add(networkCompactCard);
            networkCompactCard.Location = new Point(276, 29);
            networkCompactCard.Size = new Size(144, 58);
            Label networkCompactCaption = MakeLabel("СЕТЬ", new Point(9, 4), new Size(125, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
            networkCompactCard.Controls.Add(networkCompactCaption);
            _compactNetwork = MakeLabel("▼ —   ▲ —", new Point(8, 23), new Size(128, 27), 11F, FontStyle.Bold, Color.FromArgb(210, 216, 224));
            networkCompactCard.Controls.Add(_compactNetwork);
            _compactNetworkColumn = new CompactMetricColumn();
            _compactNetworkColumn.Visible = false;
            networkCompactCard.Controls.Add(_compactNetworkColumn);

            MonitorCard memoryCompactCard = _memoryCompactCard = new MonitorCard();
            _cards.Add(memoryCompactCard);
            memoryCompactCard.Location = new Point(10, 95);
            memoryCompactCard.Size = new Size(144, 58);
            Label memoryCompactCaption = MakeLabel("ПАМЯТЬ", new Point(9, 4), new Size(125, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
            memoryCompactCard.Controls.Add(memoryCompactCaption);
            _compactMemory = MakeLabel("—%   — / —", new Point(8, 21), new Size(128, 30), 13F, FontStyle.Bold, Color.FromArgb(92, 170, 255));
            memoryCompactCard.Controls.Add(_compactMemory);
            _compactMemoryColumn = new CompactMetricColumn();
            _compactMemoryColumn.Visible = false;
            memoryCompactCard.Controls.Add(_compactMemoryColumn);

            Controls.Add(cpuCompactCard);
            Controls.Add(gpuCompactCard);
            Controls.Add(networkCompactCard);
            Controls.Add(memoryCompactCard);
            _compactSlots = new[]
            {
                new CompactCardSlotView
                {
                    Card = cpuCompactCard,
                    Caption = cpuCompactCaption,
                    Value = _compactCpu,
                    Column = _compactCpuColumn
                },
                new CompactCardSlotView
                {
                    Card = gpuCompactCard,
                    Caption = gpuCompactCaption,
                    Value = _compactGpu,
                    Column = _compactGpuColumn
                },
                new CompactCardSlotView
                {
                    Card = memoryCompactCard,
                    Caption = memoryCompactCaption,
                    Value = _compactMemory,
                    Column = _compactMemoryColumn
                },
                new CompactCardSlotView
                {
                    Card = networkCompactCard,
                    Caption = networkCompactCaption,
                    Value = _compactNetwork,
                    Column = _compactNetworkColumn
                }
            };
            _compactCpuColumn.SetMetrics(new[] { "—°C", "—%", "—", "—" },
                new[] { "ТЕМП.", "НАГРУЗКА", "ЧАСТОТА", "МОЩНОСТЬ" }, Color.FromArgb(150, 158, 169));
            _compactGpuColumn.SetMetrics(new[] { "—°C", "—%", "—", "—", "—" },
                new[] { "ТЕМП.", "НАГРУЗКА", "ЧАСТОТА", "МОЩНОСТЬ", "VRAM" }, Color.FromArgb(150, 158, 169));
            _compactNetworkColumn.SetMetrics(new[] { "—", "—" },
                new[] { "ЗАГРУЗКА", "ОТДАЧА" }, Color.FromArgb(150, 158, 169));
            _compactMemoryColumn.SetMetrics(new[] { "—%", "— / —", "—" },
                new[] { "ЗАНЯТО", "ИСПОЛЬЗОВАНО", "ЧАСТОТА" }, Color.FromArgb(92, 170, 255));

            _detailsArea = new BufferedPanel();
            _detailsArea.Location = new Point(0, CompactHeight);
            _detailsArea.Size = new Size(WindowWidth, ExpandedHeight - CompactHeight);
            _detailsArea.BackColor = BackColor;
            _detailsArea.Visible = false;
            Controls.Add(_detailsArea);

            MonitorCard cpuCard = _cpuCard = new MonitorCard();
            _cards.Add(cpuCard);
            cpuCard.Location = new Point(10, 0);
            cpuCard.Size = new Size(200, 172);
            cpuCard.Controls.Add(MakeLabel("CPU", new Point(10, 7), new Size(42, 17), 7.5F, FontStyle.Bold, Color.FromArgb(73, 190, 198)));
            _cpuName = MakeLabel("Ожидание данных…", new Point(10, 23), new Size(180, 22), 8.5F, FontStyle.Regular, Color.FromArgb(195, 202, 211));
            _cpuName.AutoEllipsis = true;
            cpuCard.Controls.Add(_cpuName);
            _cpuTemperature = AddMetric(cpuCard, "ТЕМПЕРАТУРА", 10, 49);
            _cpuUsage = AddMetric(cpuCard, "НАГРУЗКА", 106, 49);
            _cpuClock = AddMetric(cpuCard, "ЧАСТОТА", 10, 102);
            _cpuPower = AddMetric(cpuCard, "МОЩНОСТЬ", 106, 102);

            MonitorCard gpuCard = _gpuCard = new MonitorCard();
            _cards.Add(gpuCard);
            gpuCard.Location = new Point(220, 0);
            gpuCard.Size = new Size(200, 172);
            gpuCard.Controls.Add(MakeLabel("GPU", new Point(10, 7), new Size(42, 17), 7.5F, FontStyle.Bold, GpuAccent));
            _gpuName = MakeLabel("Ожидание данных…", new Point(10, 23), new Size(180, 22), 8.5F, FontStyle.Regular, Color.FromArgb(195, 202, 211));
            _gpuName.AutoEllipsis = true;
            gpuCard.Controls.Add(_gpuName);
            _gpuTemperature = AddMetric(gpuCard, "ТЕМПЕРАТУРА", 10, 49);
            _gpuUsage = AddMetric(gpuCard, "НАГРУЗКА", 106, 49);
            _gpuClock = AddMetric(gpuCard, "ЧАСТОТА", 10, 102);
            _gpuPower = AddMetric(gpuCard, "МОЩНОСТЬ", 106, 102);
            _gpuMemory = MakeLabel("VRAM  — / —", new Point(10, 149), new Size(180, 17), 7.8F, FontStyle.Regular, Color.FromArgb(145, 155, 168));
            gpuCard.Controls.Add(_gpuMemory);

            OpacityPopupForm opacityCard = _opacityCard = new OpacityPopupForm();
            opacityCard.BackColor = Color.FromArgb(29, 33, 40);
            opacityCard.ClientSize = new Size(250, 32);
            opacityCard.Visible = false;
            _opacityLabel = MakeLabel("ПРОЗРАЧНОСТЬ  90%", new Point(8, 5), new Size(130, 22), 7.5F, FontStyle.Bold, Color.FromArgb(145, 155, 168));
            opacityCard.Controls.Add(_opacityLabel);
            _backgroundCheckBox = new CheckBox();
            _backgroundCheckBox.Text = "БЕЗ ФОНА";
            _backgroundCheckBox.Location = new Point(10, 24);
            _backgroundCheckBox.Size = new Size(125, 21);
            _backgroundCheckBox.Font = new Font("Segoe UI", 7.5F, FontStyle.Bold, GraphicsUnit.Point);
            _backgroundCheckBox.ForeColor = Color.FromArgb(178, 186, 197);
            _backgroundCheckBox.BackColor = Color.Transparent;
            _backgroundCheckBox.FlatStyle = FlatStyle.Flat;
            _backgroundCheckBox.CheckedChanged += delegate { if (!_loadingSettings) ApplyBackgroundMode(_backgroundCheckBox.Checked, true); };
            _backgroundCheckBox.Visible = false;
            opacityCard.Controls.Add(_backgroundCheckBox);
            _opacitySlider = new SlimOpacitySlider();
            _opacitySlider.Location = new Point(137, 5);
            _opacitySlider.Size = new Size(105, 22);
            _opacitySlider.Minimum = 10;
            _opacitySlider.Maximum = 100;
            _opacitySlider.Value = 90;
            _opacitySlider.BackColor = opacityCard.BackColor;
            _opacitySlider.ValueChanged += delegate { if (!_loadingSettings) SetOpacityPercent(_opacitySlider.Value, false); };
            _opacitySlider.MouseUp += delegate { SaveSettings(); };
            _opacitySlider.MouseWheel += delegate { SaveSettings(); };
            opacityCard.Controls.Add(_opacitySlider);
            opacityCard.Deactivate += delegate
            {
                // Deactivation is raised before the button click that may have
                // caused it. Defer closing so clicking the % button still acts
                // as a normal toggle instead of closing and immediately reopening.
                if (!_opacityPopupVisible || _stopping || !IsHandleCreated)
                    return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    bool pointerIsOverToggle = _opacityButton.IsHandleCreated &&
                        _opacityButton.RectangleToScreen(_opacityButton.ClientRectangle)
                            .Contains(Cursor.Position);
                    if (_opacityPopupVisible && !_opacityCard.ContainsFocus &&
                        !pointerIsOverToggle)
                        CloseOpacityPopup();
                }));
            };
            opacityCard.MouseDown += ToggleOpacityWithMiddleMouse;
            AssignMiddleOpacityToggle(opacityCard.Controls);

            _superToggleButton = new ExpandableStrip();
            _superToggleButton.BackColor = Color.FromArgb(29, 33, 40);
            _superToggleButton.ForeColor = Color.FromArgb(145, 155, 168);
            _superToggleButton.AccessibleName = "Дополнительная статистика";
            _superToggleButton.Visible = false;
            _superToggleButton.Click += delegate { ToggleSuperExpanded(); };

            _superArea = new BufferedPanel();
            _superArea.BackColor = BackColor;
            _superArea.Visible = false;
            _cpuGauge = new RingGauge("CPU");
            _gpuGauge = new RingGauge("GPU");
            _cpuHistory = new SensorHistoryControl("CPU", Color.FromArgb(82, 218, 145));
            _gpuHistory = new SensorHistoryControl("GPU", GpuAccent);
            _memorySummary = new ResourceSummaryControl("ПАМЯТЬ", false);
            _storageSummary = new ResourceSummaryControl("ХРАНИЛИЩЕ", false);
            _fanSummary = new FanSummaryControl();
            _storageSummary.Cursor = Cursors.Hand;
            _storageMenu = new ContextMenuStrip();
            _storageMenu.ShowImageMargin = false;
            _storageMenu.BackColor = Color.FromArgb(29, 33, 40);
            _storageMenu.ForeColor = Color.FromArgb(225, 230, 236);
            _storageSummary.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || _storageMenu.Items.Count == 0)
                    return;
                int menuWidth = Math.Max(54, _storageMenu.PreferredSize.Width);
                int maximumX = Math.Max(4, _storageSummary.Width - menuWidth - 4);
                int x = Math.Max(4, Math.Min(e.X - 10, maximumX));
                int y = Math.Max(0, Math.Min(24, _storageSummary.Height - 4));
                _storageMenu.Show(_storageSummary, new Point(x, y),
                    ToolStripDropDownDirection.BelowRight);
            };
            _superArea.Controls.Add(_cpuGauge);
            _superArea.Controls.Add(_gpuGauge);
            _superArea.Controls.Add(_cpuHistory);
            _superArea.Controls.Add(_gpuHistory);
            _superArea.Controls.Add(_memorySummary);
            _superArea.Controls.Add(_storageSummary);
            _superArea.Controls.Add(_fanSummary);

            _detailsArea.Controls.Add(cpuCard);
            _detailsArea.Controls.Add(gpuCard);
            _detailsArea.Controls.Add(_superArea);
            Controls.Add(_superToggleButton);

            _topLeftResizeGrip = new ResizeGripControl(true, true);
            _topLeftResizeGrip.Size = new Size(15, 15);
            _topLeftResizeGrip.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            _topLeftResizeGrip.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_pinned)
                    BeginResize(13);
            };
            Controls.Add(_topLeftResizeGrip);

            _leftResizeGrip = new ResizeGripControl(true, false);
            _leftResizeGrip.Size = new Size(15, 15);
            _leftResizeGrip.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _leftResizeGrip.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_pinned)
                    BeginResize(16);
            };
            Controls.Add(_leftResizeGrip);

            _resizeGrip = new ResizeGripControl(false, false);
            _resizeGrip.Size = new Size(15, 15);
            _resizeGrip.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _resizeGrip.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_pinned)
                    BeginResize(17);
            };
            Controls.Add(_resizeGrip);
            _topLeftResizeGrip.BringToFront();
            _leftResizeGrip.BringToFront();
            _resizeGrip.BringToFront();

            ContextMenuStrip menu = new ContextMenuStrip();
            _viewItem = new ToolStripMenuItem("Показать подробности");
            _viewItem.Click += delegate { ToggleSuperExpanded(); };
            _headerVisibilityItem = new ToolStripMenuItem("Скрыть верхнюю панель");
            _headerVisibilityItem.CheckOnClick = true;
            _headerVisibilityItem.Click += delegate { ToggleCompactHeader(); };
            _pinItem = new ToolStripMenuItem("Закрепить положение");
            _pinItem.CheckOnClick = true;
            _pinItem.Click += delegate { ApplyPinnedMode(_pinItem.Checked, true); };
            _topMostItem = new ToolStripMenuItem("Поверх всех окон");
            _topMostItem.CheckOnClick = true;
            _topMostItem.Click += delegate
            {
                TopMost = _topMostItem.Checked;
                SyncBackgroundHitForm();
                SaveSettings();
            };
            _startupItem = new ToolStripMenuItem("Запускать вместе с Windows");
            _startupItem.CheckOnClick = true;
            _startupItem.Click += delegate { SetStartup(_startupItem.Checked); };
            ToolStripMenuItem compactCardsMenu = CreateCompactCardsMenu();

            ToolStripMenuItem opacityMenu = new ToolStripMenuItem("Прозрачность");
            foreach (int percent in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 })
            {
                int selectedPercent = percent;
                ToolStripMenuItem item = new ToolStripMenuItem(percent.ToString(CultureInfo.InvariantCulture) + "%");
                item.Click += delegate { SetOpacityPercent(selectedPercent, true); };
                _opacityItems.Add(item);
                opacityMenu.DropDownItems.Add(item);
            }
            _backgroundItem = new ToolStripMenuItem("Без фона");
            _backgroundItem.CheckOnClick = true;
            _backgroundItem.Click += delegate { ApplyBackgroundMode(_backgroundItem.Checked, true); };

            ToolStripMenuItem resetItem = new ToolStripMenuItem("В правый верхний угол");
            resetItem.Click += delegate { MoveToDefaultPosition(); SaveSettings(); };
            ToolStripMenuItem hideItem = new ToolStripMenuItem("Скрыть в область уведомлений");
            hideItem.Click += delegate { CloseOpacityPopup(); Hide(); };
            ToolStripMenuItem updateItem = new ToolStripMenuItem("Проверить обновления…");
            updateItem.Click += delegate { UpdateManager.CheckForUpdatesAsync(this, true); };
            ToolStripMenuItem supportItem = new ToolStripMenuItem("Поддержать Traymetry ♥");
            supportItem.Click += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ReleaseConfiguration.SupportUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    MessageBox.Show(this,
                        "Не удалось открыть страницу поддержки.",
                        "Traymetry",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            };
            ToolStripMenuItem repairServiceItem = new ToolStripMenuItem("Проверить и починить датчики…");
            repairServiceItem.Click += delegate
            {
                bool repaired = MachineBootstrap.RequestRepair();
                MessageBox.Show(repaired
                        ? "Сервис датчиков работает. Показания появятся в Traymetry через несколько секунд."
                        : "Не удалось настроить сервис датчиков Traymetry.",
                    "Traymetry",
                    MessageBoxButtons.OK,
                    repaired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            ToolStripMenuItem removeServiceItem = new ToolStripMenuItem("Удалить системный сервис датчиков…");
            removeServiceItem.Click += delegate
            {
                DialogResult answer = MessageBox.Show(
                    "Traymetry остановит и удалит свой системный сервис датчиков. Сам файл Traymetry и драйвер PawnIO удалены не будут.\r\n\r\n" +
                    "Продолжить?",
                    "Traymetry — удаление сервиса",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                    return;
                bool removed = MachineBootstrap.RequestUninstall();
                MessageBox.Show(removed
                        ? "Системный сервис Traymetry удалён. Без него часть показателей CPU может быть недоступна."
                        : "Не удалось удалить системный сервис Traymetry.",
                    "Traymetry",
                    MessageBoxButtons.OK,
                    removed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += delegate { Close(); };

            menu.Items.Add(_viewItem);
            menu.Items.Add(_headerVisibilityItem);
            menu.Items.Add(_pinItem);
            menu.Items.Add(_topMostItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(compactCardsMenu);
            menu.Items.Add(opacityMenu);
            menu.Items.Add(_backgroundItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(resetItem);
            menu.Items.Add(hideItem);
            menu.Items.Add(updateItem);
            menu.Items.Add(supportItem);
            menu.Items.Add(repairServiceItem);
            menu.Items.Add(removeServiceItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            ContextMenuStrip = menu;
            AssignContextMenu(Controls, menu);
            _opacityCard.ContextMenuStrip = menu;
            AssignContextMenu(_opacityCard.Controls, menu);

            _backgroundHitForm = new BackgroundHitForm();
            _backgroundHitForm.MouseDown += BackgroundHitMouseDown;
            _backgroundHitForm.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && !_pinned)
                    menu.Show(Cursor.Position);
            };

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Information;
            _tray.Text = "Traymetry";
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate
            {
                if (Visible)
                {
                    CloseOpacityPopup();
                    Hide();
                }
                else
                {
                    Show();
                    Activate();
                }
            };

            LoadSettings();
            ApplyRoundedCorners();
            MouseDown += DragWindow;
            MouseDown += ToggleOpacityWithMiddleMouse;
            AssignDrag(Controls);
            AssignMiddleOpacityToggle(Controls);

            Resize += delegate
            {
                SyncBackgroundHitForm();
                if (_layoutInProgress || _switchingView || _loadingSettings)
                    return;
                // An automatically hidden header is restored after restart as
                // a temporary visual latch.  The first deliberate resize back
                // into a roomy layout releases that latch immediately, keeping
                // the original automatic reveal behaviour.
                if (_interactiveResize && _restoredAutomaticHeaderHidden &&
                    (_expanded || ClientSize.Height >= CompactHeaderRevealHeight))
                    _restoredAutomaticHeaderHidden = false;
                RunLayoutPass(!_interactiveResize);
            };
            ResizeEnd += delegate { RememberCurrentSize(); SaveSettings(); };
            LocationChanged += delegate
            {
                SyncBackgroundHitForm();
                if (_opacityPopupVisible)
                    LayoutOpacityPopup();
            };
            VisibleChanged += delegate { SyncBackgroundHitForm(); };

            Shown += delegate
            {
                StartupTrace.Write("form-shown");
                RunLayoutPass(true);
                SyncBackgroundHitForm();
                StartWorker();
                UpdateManager.CheckAutomaticallyIfDue(this);
            };
            FormClosed += OnFormClosed;
            Application.AddMessageFilter(this);
            StartupTrace.Write("form-constructor-exit");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int DropShadow = 0x00020000;
                const int ToolWindow = 0x00000080;
                const int AppWindow = 0x00040000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= DropShadow;
                parameters.ExStyle |= ToolWindow;
                parameters.ExStyle &= ~AppWindow;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int NonClientHitTest = 0x0084;
            const int WindowMoving = 0x0216;
            const int WindowSizing = 0x0214;
            const int EnterSizeMove = 0x0231;
            const int ExitSizeMove = 0x0232;
            if (message.Msg == EnterSizeMove)
            {
                CloseOpacityPopup();
                _interactiveResize = true;
                _resizeFromLeftEdge = false;
                SuspendBackgroundHitForm();
                ClearRoundedCorners();
            }
            if (message.Msg == WindowSizing)
            {
                int edge = message.WParam.ToInt32();
                _resizeFromLeftEdge = edge == 1 || edge == 4 || edge == 7;
            }
            if (message.Msg == NonClientHitTest)
            {
                base.WndProc(ref message);
                if (_pinned)
                {
                    message.Result = (IntPtr)1;
                    return;
                }
                Point point = PointToClient(Cursor.Position);
                if (IsPointOverResizeExcludedControl(point))
                {
                    message.Result = (IntPtr)1;
                    return;
                }
                const int edge = 7;
                bool left = point.X <= edge;
                bool right = point.X >= ClientSize.Width - edge;
                bool top = point.Y <= edge;
                bool bottom = point.Y >= ClientSize.Height - edge;

                if (left && top) message.Result = (IntPtr)13;
                else if (right && top) message.Result = (IntPtr)14;
                else if (left && bottom) message.Result = (IntPtr)16;
                else if (right && bottom) message.Result = (IntPtr)17;
                else if (left) message.Result = (IntPtr)10;
                else if (right) message.Result = (IntPtr)11;
                else if (top) message.Result = (IntPtr)12;
                else if (bottom) message.Result = (IntPtr)15;
                return;
            }
            if (message.Msg == WindowMoving && message.LParam != IntPtr.Zero)
            {
                _windowMovedDuringDragClick = true;
                NativeRect rectangle = (NativeRect)System.Runtime.InteropServices.Marshal.PtrToStructure(message.LParam, typeof(NativeRect));
                int width = rectangle.Right - rectangle.Left;
                int height = rectangle.Bottom - rectangle.Top;
                Rectangle proposed = new Rectangle(rectangle.Left, rectangle.Top, width, height);

                if (!IsRectangleInsideDesktop(proposed))
                {
                    Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                    int left = Math.Max(area.Left, Math.Min(rectangle.Left, area.Right - width));
                    int top = Math.Max(area.Top, Math.Min(rectangle.Top, area.Bottom - height));
                    rectangle.Left = left;
                    rectangle.Top = top;
                    rectangle.Right = left + width;
                    rectangle.Bottom = top + height;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(rectangle, message.LParam, true);
                }
            }
            base.WndProc(ref message);
            if (message.Msg == ExitSizeMove)
            {
                _interactiveResize = false;
                _resizeFromLeftEdge = false;
                RunLayoutPass(true);
                ResumeBackgroundHitForm();
            }
        }

        public bool PreFilterMessage(ref Message message)
        {
            bool belongsToWindow = BelongsToWindow(message.HWnd);
            if (_opacityPopupVisible && message.Msg == 0x020A)
            {
                int delta = unchecked((short)(((long)message.WParam >> 16) & 0xFFFF));
                if (delta != 0)
                {
                    _opacitySlider.Value += delta > 0 ? 5 : -5;
                    SaveSettings();
                }
                return true;
            }

            if (_opacityPopupVisible && message.Msg == 0x0100 &&
                (Keys)(int)message.WParam == Keys.Escape)
            {
                CloseOpacityPopup();
                return true;
            }

            if (_opacityPopupVisible && IsMouseButtonDownMessage(message.Msg) &&
                !IsMiddleMouseDownMessage(message.Msg) &&
                !BelongsToControl(message.HWnd, _opacityCard) &&
                !BelongsToControl(message.HWnd, _opacityButton))
            {
                CloseOpacityPopup();
            }

            if (!belongsToWindow)
                return false;

            if (_pinned && IsMiddleMouseDownMessage(message.Msg))
            {
                ToggleOpacityPopup();
                return true;
            }

            if (_pinned && IsLeftMouseMessage(message.Msg))
            {
                if (BelongsToControl(message.HWnd, _pinButton))
                    return false;
                ForwardPinnedMouse(message.Msg);
                return true;
            }

            if (!_pinned && message.Msg == 0x0201)
            {
                IntPtr messageHandle = message.HWnd;
                if (_headerButtons.Any(delegate(Button button)
                    {
                        return button.Enabled && button.Visible && BelongsToControl(messageHandle, button);
                    }) || (_superToggleButton.Enabled && _superToggleButton.Visible &&
                           BelongsToControl(messageHandle, _superToggleButton)))
                    return false;
                if (BelongsToControl(message.HWnd, _topLeftResizeGrip))
                {
                    BeginResize(13);
                    return true;
                }
                if (BelongsToControl(message.HWnd, _leftResizeGrip))
                {
                    BeginResize(16);
                    return true;
                }
                if (BelongsToControl(message.HWnd, _resizeGrip))
                {
                    BeginResize(17);
                    return true;
                }
                int hitTest = GetResizeHitTest(PointToClient(Cursor.Position), 7);
                if (hitTest != 1)
                {
                    BeginResize(hitTest);
                    return true;
                }
            }
            return false;
        }

        private bool IsPointOverResizeExcludedControl(Point point)
        {
            if (_headerButtons.Any(delegate(Button button)
                {
                    return button.Enabled && button.Visible && button.Bounds.Contains(point);
                }))
                return true;
            return _superToggleButton.Enabled && _superToggleButton.Visible &&
                _superToggleButton.Bounds.Contains(point);
        }

        private bool BelongsToWindow(IntPtr handle)
        {
            Control target = Control.FromHandle(handle);
            while (target != null)
            {
                if (ReferenceEquals(target, this))
                    return true;
                target = target.Parent;
            }
            return false;
        }

        private static bool BelongsToControl(IntPtr handle, Control expected)
        {
            Control target = Control.FromHandle(handle);
            while (target != null)
            {
                if (ReferenceEquals(target, expected))
                    return true;
                target = target.Parent;
            }
            return false;
        }

        private int GetResizeHitTest(Point point, int edge)
        {
            bool left = point.X <= edge;
            bool right = point.X >= ClientSize.Width - edge;
            bool top = point.Y <= edge;
            bool bottom = point.Y >= ClientSize.Height - edge;

            if (left && top) return 13;
            if (right && top) return 14;
            if (left && bottom) return 16;
            if (right && bottom) return 17;
            if (left) return 10;
            if (right) return 11;
            if (top) return 12;
            if (bottom) return 15;
            return 1;
        }

        private void BeginResize(int hitTest)
        {
            NativeUi.ReleaseCapture();
            NativeUi.SendMessage(Handle, 0x00A1, (IntPtr)hitTest, IntPtr.Zero);
        }

        private static bool IsLeftMouseMessage(int message)
        {
            return message == 0x0201 || message == 0x0202 || message == 0x0203 ||
                   message == 0x00A1 || message == 0x00A2 || message == 0x00A3;
        }

        private static bool IsMouseButtonDownMessage(int message)
        {
            return message == 0x0201 || message == 0x0204 || message == 0x0207 ||
                   message == 0x00A1 || message == 0x00A4 || message == 0x00A7;
        }

        private static bool IsMiddleMouseDownMessage(int message)
        {
            return message == 0x0207 || message == 0x00A7;
        }

        private void ForwardPinnedMouse(int message)
        {
            const int clientLeftDown = 0x0201;
            const int clientLeftUp = 0x0202;
            const int clientLeftDoubleClick = 0x0203;
            int forwardedMessage = message == 0x00A1
                ? clientLeftDown
                : message == 0x00A2 ? clientLeftUp
                : message == 0x00A3 ? clientLeftDoubleClick
                : message;

            Point cursor = Cursor.Position;
            if (forwardedMessage == clientLeftDown || forwardedMessage == clientLeftDoubleClick ||
                _pinnedClickTarget == IntPtr.Zero)
            {
                _pinnedClickTarget = FindWindowBelow(cursor);
                if (_pinnedClickTarget != IntPtr.Zero)
                {
                    IntPtr root = GetAncestor(_pinnedClickTarget, 2); // GA_ROOT
                    if (root != IntPtr.Zero)
                        SetForegroundWindow(root);
                }
            }

            IntPtr target = _pinnedClickTarget;
            if (target != IntPtr.Zero)
            {
                NativePoint point = new NativePoint { X = cursor.X, Y = cursor.Y };
                if (ScreenToClient(target, ref point))
                {
                    int packed = (point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16);
                    IntPtr buttons = forwardedMessage == clientLeftUp ? IntPtr.Zero : (IntPtr)1;
                    PostMessage(target, forwardedMessage, buttons, (IntPtr)packed);
                }
            }

            if (forwardedMessage == clientLeftUp)
                _pinnedClickTarget = IntPtr.Zero;
        }

        private IntPtr FindWindowBelow(Point screenPoint)
        {
            const uint nextWindow = 2; // GW_HWNDNEXT
            IntPtr candidate = GetWindow(Handle, nextWindow);
            while (candidate != IntPtr.Zero)
            {
                NativeRect bounds;
                if (IsWindowVisible(candidate) && IsWindowEnabled(candidate) &&
                    GetWindowRect(candidate, out bounds) &&
                    screenPoint.X >= bounds.Left && screenPoint.X < bounds.Right &&
                    screenPoint.Y >= bounds.Top && screenPoint.Y < bounds.Bottom)
                {
                    return FindDeepestChild(candidate, screenPoint);
                }
                candidate = GetWindow(candidate, nextWindow);
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindDeepestChild(IntPtr parent, Point screenPoint)
        {
            const uint skipInvisible = 0x0001;
            const uint skipDisabled = 0x0002;
            const uint skipTransparent = 0x0004;
            IntPtr current = parent;
            for (int depth = 0; depth < 16; depth++)
            {
                NativePoint point = new NativePoint { X = screenPoint.X, Y = screenPoint.Y };
                if (!ScreenToClient(current, ref point))
                    break;
                IntPtr child = ChildWindowFromPointEx(current, point,
                    skipInvisible | skipDisabled | skipTransparent);
                if (child == IntPtr.Zero || child == current)
                    break;
                current = child;
            }
            return current;
        }

        private static bool IsRectangleInsideDesktop(Rectangle rectangle)
        {
            Point[] corners =
            {
                new Point(rectangle.Left, rectangle.Top),
                new Point(rectangle.Right - 1, rectangle.Top),
                new Point(rectangle.Left, rectangle.Bottom - 1),
                new Point(rectangle.Right - 1, rectangle.Bottom - 1)
            };

            foreach (Point corner in corners)
            {
                if (!Screen.AllScreens.Any(delegate(Screen screen) { return screen.WorkingArea.Contains(corner); }))
                    return false;
            }
            return true;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        private Label MakeLabel(string text, Point location, Size size, float fontSize, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = location;
            label.Size = size;
            label.Font = new Font("Segoe UI", fontSize, style, GraphicsUnit.Point);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private Button MakeHeaderButton(string text, int x)
        {
            Button button = new HeaderButton();
            button.Text = text;
            button.Location = new Point(x, 1);
            button.Size = new Size(20, 25);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = BackColor;
            button.ForeColor = Color.FromArgb(165, 173, 184);
            button.Font = new Font("Segoe UI Symbol", 10F, FontStyle.Regular, GraphicsUnit.Point);
            button.TabStop = false;
            button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            return button;
        }

        private void AddHeaderHover(Button button, Color hoverColor)
        {
            button.MouseEnter += delegate
            {
                if (button.Enabled)
                    button.BackColor = hoverColor;
            };
            button.MouseLeave += delegate { button.BackColor = BackColor; };
        }

        private void LayoutHeaderButtons()
        {
            // Keep one compact spacing scheme at every width.  Switching
            // between a loose and a compact scheme at 140 px made the whole
            // group jump while the window crossed that breakpoint.
            const int smallWidth = 17;
            const int arrowWidth = 20;
            const int totalWidth = smallWidth * 4 + arrowWidth;
            int x = Math.Max(0, ClientSize.Width - totalWidth);
            Button[] compactButtons =
            {
                _opacityButton, _backgroundButton, _cycleButton, _pinButton
            };
            foreach (Button button in compactButtons)
            {
                button.Bounds = new Rectangle(x, 1, smallWidth, 25);
                x += smallWidth;
            }
            _expandButton.Bounds = new Rectangle(x, 1, arrowWidth, 25);
        }

        private MetricReadout AddMetric(Control parent, string caption, int x, int y)
        {
            MetricReadout metric = new MetricReadout(caption);
            metric.Location = new Point(x, y);
            metric.Size = new Size(84, 48);
            parent.Controls.Add(metric);
            return metric;
        }

        private void StartWorker()
        {
            _worker = new Thread(SensorLoop);
            _worker.IsBackground = true;
            _worker.Name = "Traymetry sensor reader";
            _worker.Start();
        }

        private void SensorLoop()
        {
            while (!_stopping)
            {
                try
                {
                    using (HardwareTelemetrySession session = new HardwareTelemetrySession())
                    {
                        while (!_stopping)
                        {
                            PostSnapshot(session.ReadSnapshot());
                            for (int part = 0; part < 10 && !_stopping; part++)
                                Thread.Sleep(100);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PostOffline(ex.Message);
                    for (int part = 0; part < 15 && !_stopping; part++)
                        Thread.Sleep(100);
                }
            }
        }

        private void PostSnapshot(SensorSnapshot snapshot)
        {
            if (_stopping || snapshot == null || !IsHandleCreated)
                return;
            try { BeginInvoke(new Action<SensorSnapshot>(UpdateSnapshot), snapshot); }
            catch (InvalidOperationException) { }
        }

        private void PostOffline(string reason)
        {
            if (_stopping || !IsHandleCreated)
                return;
            try { BeginInvoke(new Action<string>(UpdateOffline), reason); }
            catch (InvalidOperationException) { }
        }

        private void UpdateSnapshot(SensorSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            Color cpuColor = TemperatureColor(snapshot.Temperature);
            bool gpuDetected = !String.IsNullOrWhiteSpace(snapshot.GpuName);
            Color gpuColor = GpuStatusColor(snapshot.GpuTemperature, gpuDetected);
            double memoryPercent = snapshot.MemoryTotalGb > 0 ? snapshot.MemoryUsedGb / snapshot.MemoryTotalGb : 0;
            RenderCompactCards(snapshot, true);

            _cpuName.Text = String.IsNullOrWhiteSpace(snapshot.CpuName) ? "CPU" : snapshot.CpuName;
            _cpuTemperature.SetValue(FormatTemperature(snapshot.Temperature), cpuColor);
            _cpuUsage.SetValue(Math.Round(snapshot.Usage).ToString("0", CultureInfo.InvariantCulture) + "%", Color.White);
            _cpuClock.SetValue(FormatClockGhz(snapshot.ClockMhz), Color.White);
            _cpuPower.SetValue(FormatPower(snapshot.PowerWatts), Color.White);

            _gpuName.Text = String.IsNullOrWhiteSpace(snapshot.GpuName) ? "GPU не обнаружен" : snapshot.GpuName;
            _gpuTemperature.SetValue(snapshot.GpuTemperature > 0 ? Math.Round(snapshot.GpuTemperature).ToString("0", CultureInfo.InvariantCulture) + "°C" : "—", gpuColor);
            _gpuUsage.SetValue(gpuDetected ? Math.Round(snapshot.GpuUsage).ToString("0", CultureInfo.InvariantCulture) + "%" : "—", Color.White);
            _gpuClock.SetValue(snapshot.GpuClockMhz > 0 ? snapshot.GpuClockMhz.ToString("0", CultureInfo.InvariantCulture) + " MHz" : "—", Color.White);
            _gpuPower.SetValue(snapshot.GpuPowerWatts > 0 ? snapshot.GpuPowerWatts.ToString("0", CultureInfo.InvariantCulture) + " W" : "—", Color.White);
            _gpuMemory.Text = snapshot.GpuMemoryTotalGb > 0
                ? "VRAM   " + snapshot.GpuMemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + snapshot.GpuMemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : "VRAM   —";

            _cpuGauge.SetData(snapshot.Usage / 100.0,
                Math.Round(snapshot.Usage).ToString("0", CultureInfo.InvariantCulture) + "%",
                "LOAD",
                cpuColor);
            _cpuGauge.SetAuxiliary(
                FormatClockGhz(snapshot.ClockMhz), "ЧАСТОТА",
                FormatTemperature(snapshot.Temperature), "ТЕМПЕРАТУРА",
                FormatPower(snapshot.PowerWatts), "МОЩНОСТЬ");
            _gpuGauge.SetData(snapshot.GpuUsage / 100.0,
                snapshot.GpuTemperature > 0 ? Math.Round(snapshot.GpuUsage).ToString("0", CultureInfo.InvariantCulture) + "%" : "—",
                "LOAD",
                gpuColor);
            _gpuGauge.SetAuxiliary(
                FormatClockMhz(snapshot.GpuClockMhz), "ЧАСТОТА",
                FormatTemperature(snapshot.GpuTemperature), "ТЕМПЕРАТУРА",
                snapshot.GpuMemoryTotalGb > 0
                    ? snapshot.GpuMemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + snapshot.GpuMemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                    : "—", "ПАМЯТЬ GPU");
            _cpuHistory.AddSample(snapshot.Temperature, snapshot.Usage);
            _gpuHistory.AddSample(snapshot.GpuTemperature, snapshot.GpuUsage);
            string memoryDetails = snapshot.MemoryClockMhz > 0
                ? snapshot.MemoryClockMhz.ToString("0", CultureInfo.InvariantCulture) + " MHz  ·  " +
                    (memoryPercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "ИСПОЛЬЗОВАНИЕ  " + (memoryPercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            _memorySummary.SetUsage(memoryPercent,
                snapshot.MemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + snapshot.MemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB",
                memoryDetails,
                Color.FromArgb(92, 170, 255));
            UpdateStorageSummary(snapshot);
            bool fanLayoutChanged = _fanSummary.SetFans(
                snapshot.FanNames, snapshot.FanRpm, snapshot.FanControlPercent);
            if (fanLayoutChanged)
                RunLayoutPass(false);

            string tooltip = "CPU " + FormatTemperature(snapshot.Temperature) + " " + snapshot.Usage.ToString("0", CultureInfo.InvariantCulture) +
                "% · GPU " + FormatTemperature(snapshot.GpuTemperature) + " " + snapshot.GpuUsage.ToString("0", CultureInfo.InvariantCulture) + "%";
            _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip.Substring(0, 63);
        }

        private void UpdateStorageSummary(SensorSnapshot snapshot)
        {
            string[] names = snapshot.StorageDriveNames ?? new string[0];
            double[] used = snapshot.StorageDriveUsedGb ?? new double[0];
            double[] totals = snapshot.StorageDriveTotalGb ?? new double[0];
            int count = Math.Min(names.Length, Math.Min(used.Length, totals.Length));
            if (count <= 0)
            {
                _storageSummary.SetTitle("ХРАНИЛИЩЕ");
                double aggregatePercent = snapshot.StorageTotalGb > 0 ? snapshot.StorageUsedGb / snapshot.StorageTotalGb : 0;
                _storageSummary.SetUsage(aggregatePercent,
                    snapshot.StorageUsedGb.ToString("0", CultureInfo.InvariantCulture) + " / " + snapshot.StorageTotalGb.ToString("0", CultureInfo.InvariantCulture) + " GB",
                    "ЗАНЯТО  " + (aggregatePercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                    Color.FromArgb(182, 133, 255));
                EnsureStorageMenu(new string[0]);
                return;
            }

            int selectedIndex = Array.IndexOf(names, _selectedStorageDrive, 0, count);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                _selectedStorageDrive = names[0];
            }
            double percent = totals[selectedIndex] > 0 ? used[selectedIndex] / totals[selectedIndex] : 0;
            _storageSummary.SetTitle("ХРАНИЛИЩЕ   " + names[selectedIndex] + "   ▾");
            _storageSummary.SetUsage(percent,
                used[selectedIndex].ToString("0", CultureInfo.InvariantCulture) + " / " + totals[selectedIndex].ToString("0", CultureInfo.InvariantCulture) + " GB",
                "ЗАНЯТО  " + (percent * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                Color.FromArgb(182, 133, 255));
            EnsureStorageMenu(names.Take(count).ToArray());
        }

        private void EnsureStorageMenu(string[] names)
        {
            string signature = String.Join("|", names ?? new string[0]);
            if (_storageMenuSignature != signature)
            {
                _storageMenuSignature = signature;
                _storageMenu.Items.Clear();
                foreach (string name in names)
                {
                    string driveName = name;
                    ToolStripMenuItem item = new ToolStripMenuItem(driveName);
                    item.Tag = driveName;
                    item.Click += delegate
                    {
                        _selectedStorageDrive = driveName;
                        if (_lastSnapshot != null)
                        {
                            UpdateStorageSummary(_lastSnapshot);
                            RenderCompactCards(_lastSnapshot, true);
                        }
                        SaveSettings();
                    };
                    _storageMenu.Items.Add(item);
                }
            }

            foreach (ToolStripItem rawItem in _storageMenu.Items)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                if (item != null)
                    item.Checked = String.Equals(Convert.ToString(item.Tag, CultureInfo.InvariantCulture),
                        _selectedStorageDrive, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void UpdateOffline(string reason)
        {
            Color muted = Color.FromArgb(150, 158, 169);
            _lastSnapshot = null;
            RenderCompactCards(null, false);
            _cpuName.Text = "Ожидание данных датчиков…";
            _gpuName.Text = "Ожидание данных датчиков…";
            foreach (MetricReadout metric in new[] { _cpuTemperature, _cpuUsage, _cpuClock, _cpuPower, _gpuTemperature, _gpuUsage, _gpuClock, _gpuPower })
                metric.SetValue("—", muted);
            _gpuMemory.Text = "VRAM   —";
            _cpuGauge.SetData(0, "—", "ОЖИДАНИЕ", Color.FromArgb(100, 110, 124));
            _gpuGauge.SetData(0, "—", "ОЖИДАНИЕ", Color.FromArgb(100, 110, 124));
            _memorySummary.SetUsage(0, "—", "ОЖИДАНИЕ", Color.FromArgb(100, 110, 124));
            _storageSummary.SetUsage(0, "—", "ОЖИДАНИЕ", Color.FromArgb(100, 110, 124));
            _tray.Text = "Traymetry — ожидание данных";
        }

        private static Color TemperatureColor(double temperature)
        {
            if (temperature <= 0)
                return Color.FromArgb(150, 158, 169);
            if (temperature < 70)
                return Color.FromArgb(85, 209, 135);
            if (temperature < 85)
                return Color.FromArgb(255, 184, 77);
            return Color.FromArgb(255, 93, 108);
        }

        private static Color GpuStatusColor(double temperature, bool detected)
        {
            if (!detected)
                return Color.FromArgb(150, 158, 169);
            if (temperature >= 85)
                return Color.FromArgb(255, 93, 108);
            if (temperature >= 70)
                return Color.FromArgb(255, 184, 77);
            return GpuAccent;
        }

        private static string FormatTemperature(double value)
        {
            return value > 0
                ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "°C"
                : "—°C";
        }

        private static string FormatClockGhz(double value)
        {
            return value > 0
                ? (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " GHz"
                : "—";
        }

        private static string FormatClockMhz(double value)
        {
            return value > 0
                ? value.ToString("0", CultureInfo.InvariantCulture) + " MHz"
                : "—";
        }

        private static string FormatPower(double value)
        {
            return value > 0
                ? value.ToString("0", CultureInfo.InvariantCulture) + " W"
                : "—";
        }

        private static string FormatRate(double kilobytesPerSecond)
        {
            if (kilobytesPerSecond >= 1024)
                return (kilobytesPerSecond / 1024.0).ToString(kilobytesPerSecond >= 10240 ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (kilobytesPerSecond >= 1)
                return kilobytesPerSecond.ToString(kilobytesPerSecond >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " KB/s";
            return (kilobytesPerSecond * 1024.0).ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        private static string FormatCompactRate(double kilobytesPerSecond)
        {
            if (kilobytesPerSecond >= 1024)
                return (kilobytesPerSecond / 1024.0).ToString(kilobytesPerSecond >= 10240 ? "0" : "0.0", CultureInfo.InvariantCulture) + "M";
            return kilobytesPerSecond.ToString(kilobytesPerSecond >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + "K";
        }

        private static CompactCardKind[] CreateSystemCompactPreset()
        {
            return new[]
            {
                CompactCardKind.Cpu,
                CompactCardKind.Gpu,
                CompactCardKind.Memory,
                CompactCardKind.Network
            };
        }

        private static CompactCardKind[] CreateGamingCompactPreset()
        {
            return new[]
            {
                CompactCardKind.Fps,
                CompactCardKind.Gpu,
                CompactCardKind.Cpu,
                CompactCardKind.Memory
            };
        }

        private static CompactCardKind[] CreateCoolingCompactPreset()
        {
            return new[]
            {
                CompactCardKind.Cpu,
                CompactCardKind.Gpu,
                CompactCardKind.Fans,
                CompactCardKind.Memory
            };
        }

        private ToolStripMenuItem CreateCompactCardsMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Карточки");
            root.DropDownItems.Add(CreateCompactPresetMenuItem("Пресет: Система", CreateSystemCompactPreset()));
            root.DropDownItems.Add(CreateCompactPresetMenuItem("Пресет: Игры", CreateGamingCompactPreset()));
            root.DropDownItems.Add(CreateCompactPresetMenuItem("Пресет: Охлаждение", CreateCoolingCompactPreset()));
            root.DropDownItems.Add(new ToolStripSeparator());

            CompactCardKind[] availableKinds =
            {
                CompactCardKind.Cpu,
                CompactCardKind.Gpu,
                CompactCardKind.Memory,
                CompactCardKind.Network,
                CompactCardKind.Storage,
                CompactCardKind.Fans,
                CompactCardKind.Fps
            };
            for (int slotIndex = 0; slotIndex < 4; slotIndex++)
            {
                int selectedSlot = slotIndex;
                ToolStripMenuItem slotMenu = new ToolStripMenuItem();
                slotMenu.Tag = selectedSlot;
                foreach (CompactCardKind kindValue in availableKinds)
                {
                    CompactCardKind selectedKind = kindValue;
                    ToolStripMenuItem choice = new ToolStripMenuItem(GetCompactCardDisplayName(selectedKind));
                    choice.Tag = new CompactSlotMenuTag(selectedSlot, selectedKind);
                    choice.Click += delegate
                    {
                        CompactCardKind[] next = (CompactCardKind[])_compactSlotKinds.Clone();
                        next[selectedSlot] = selectedKind;
                        ApplyCompactSlots(next, true);
                    };
                    slotMenu.DropDownItems.Add(choice);
                }
                root.DropDownItems.Add(slotMenu);
            }

            root.DropDownOpening += delegate { RefreshCompactCardsMenu(root); };
            RefreshCompactCardsMenu(root);
            return root;
        }

        private ToolStripMenuItem CreateCompactPresetMenuItem(string text, CompactCardKind[] kinds)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = new CompactPresetMenuTag(kinds);
            item.Click += delegate { ApplyCompactSlots(kinds, true); };
            return item;
        }

        private void RefreshCompactCardsMenu(ToolStripMenuItem root)
        {
            foreach (ToolStripItem rawItem in root.DropDownItems)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                if (item == null)
                    continue;
                CompactPresetMenuTag preset = item.Tag as CompactPresetMenuTag;
                if (preset != null)
                {
                    item.Checked = CompactSlotKindsEqual(_compactSlotKinds, preset.Kinds);
                    continue;
                }

                if (item.Tag is int)
                {
                    int slotIndex = (int)item.Tag;
                    item.Text = "Слот " + (slotIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        ": " + GetCompactCardDisplayName(_compactSlotKinds[slotIndex]);
                    foreach (ToolStripItem rawChoice in item.DropDownItems)
                    {
                        ToolStripMenuItem choice = rawChoice as ToolStripMenuItem;
                        CompactSlotMenuTag slot = choice != null ? choice.Tag as CompactSlotMenuTag : null;
                        if (slot != null)
                            choice.Checked = _compactSlotKinds[slot.SlotIndex] == slot.Kind;
                    }
                }
            }
        }

        private void ApplyCompactSlots(CompactCardKind[] kinds, bool save)
        {
            CompactCardKind[] normalized = NormalizeCompactSlotKinds(kinds);
            _compactSlotKinds = normalized;
            _compactPageIndex = 0;
            UpdateCompactCycleTooltip();

            Action refresh = delegate
            {
                RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
                LayoutResponsive();
            };
            if (IsHandleCreated)
                PerformAtomicLayout(refresh);
            else
                refresh();
            if (save)
                SaveSettings();
        }

        private static CompactCardKind[] NormalizeCompactSlotKinds(CompactCardKind[] kinds)
        {
            CompactCardKind[] defaults = CreateSystemCompactPreset();
            if (kinds == null || kinds.Length != defaults.Length)
                return defaults;
            CompactCardKind[] result = new CompactCardKind[defaults.Length];
            for (int index = 0; index < result.Length; index++)
                result[index] = Enum.IsDefined(typeof(CompactCardKind), kinds[index]) ? kinds[index] : defaults[index];
            return result;
        }

        private static bool CompactSlotKindsEqual(CompactCardKind[] left, CompactCardKind[] right)
        {
            return left != null && right != null && left.Length == right.Length && left.SequenceEqual(right);
        }

        private void UpdateCompactCycleTooltip()
        {
            if (_pinned)
            {
                _tips.SetToolTip(_cycleButton, String.Empty);
                return;
            }
            string order = String.Join(" → ", _compactSlotKinds
                .Select(GetCompactCardDisplayName)
                .ToArray());
            _tips.SetToolTip(_cycleButton, "Листать или переставлять карточки: " + order);
        }

        private static string GetCompactCardDisplayName(CompactCardKind kind)
        {
            switch (kind)
            {
                case CompactCardKind.Cpu: return "CPU";
                case CompactCardKind.Gpu: return "GPU";
                case CompactCardKind.Memory: return "Память";
                case CompactCardKind.Network: return "Сеть";
                case CompactCardKind.Storage: return "Хранилище";
                case CompactCardKind.Fans: return "Вентиляторы";
                case CompactCardKind.Fps: return "FPS (скоро)";
                default: return "Показатель";
            }
        }

        private static string GetCompactCardId(CompactCardKind kind)
        {
            switch (kind)
            {
                case CompactCardKind.Cpu: return "cpu";
                case CompactCardKind.Gpu: return "gpu";
                case CompactCardKind.Memory: return "memory";
                case CompactCardKind.Network: return "network";
                case CompactCardKind.Storage: return "storage";
                case CompactCardKind.Fans: return "fans";
                case CompactCardKind.Fps: return "fps";
                default: return "cpu";
            }
        }

        private static bool TryParseCompactCardId(string id, out CompactCardKind kind)
        {
            switch ((id ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "cpu": kind = CompactCardKind.Cpu; return true;
                case "gpu": kind = CompactCardKind.Gpu; return true;
                case "memory": kind = CompactCardKind.Memory; return true;
                case "network": kind = CompactCardKind.Network; return true;
                case "storage": kind = CompactCardKind.Storage; return true;
                case "fans": kind = CompactCardKind.Fans; return true;
                case "fps": kind = CompactCardKind.Fps; return true;
                default: kind = CompactCardKind.Cpu; return false;
            }
        }

        private static string SerializeCompactSlotKinds(CompactCardKind[] kinds)
        {
            CompactCardKind[] normalized = NormalizeCompactSlotKinds(kinds);
            return String.Join(";", normalized.Select(GetCompactCardId).ToArray());
        }

        private static CompactCardKind[] ParseCompactSlotKinds(string value)
        {
            CompactCardKind[] defaults = CreateSystemCompactPreset();
            string[] parts = (value ?? String.Empty).Split(new[] { ';' }, StringSplitOptions.None);
            if (parts.Length != defaults.Length)
                return defaults;
            CompactCardKind[] result = new CompactCardKind[defaults.Length];
            for (int index = 0; index < result.Length; index++)
            {
                CompactCardKind parsed;
                result[index] = TryParseCompactCardId(parts[index], out parsed) ? parsed : defaults[index];
            }
            return result;
        }

        private void RenderCompactCards(SensorSnapshot snapshot, bool available)
        {
            for (int index = 0; index < _compactSlots.Length; index++)
                ApplyCompactPresentation(_compactSlots[index],
                    CreateCompactPresentation(_compactSlotKinds[index], snapshot, available));
            RefreshCompactValueLayouts();
        }

        private static void ApplyCompactPresentation(CompactCardSlotView slot,
            CompactCardPresentation presentation)
        {
            slot.Caption.Tag = presentation.Caption;
            slot.Caption.Text = presentation.Caption;
            SetCompactValue(slot.Value, presentation.Primary, presentation.Secondary);
            slot.Value.ForeColor = presentation.Accent;
            slot.Column.SetMetrics(presentation.Values, presentation.Captions, presentation.Accent);
            slot.Flavor = presentation.Flavor;
        }

        private CompactCardPresentation CreateCompactPresentation(CompactCardKind kind,
            SensorSnapshot snapshot, bool available)
        {
            Color muted = Color.FromArgb(150, 158, 169);
            switch (kind)
            {
                case CompactCardKind.Cpu:
                {
                    Color accent = available ? TemperatureColor(snapshot.Temperature) : muted;
                    string temperature = available ? FormatTemperature(snapshot.Temperature) : "—°C";
                    string usage = available
                        ? Math.Round(snapshot.Usage).ToString("0", CultureInfo.InvariantCulture) + "%"
                        : "—%";
                    return MakeCompactPresentation("CPU", temperature, usage,
                        new[]
                        {
                            temperature,
                            usage,
                            available ? FormatClockGhz(snapshot.ClockMhz) : "—",
                            available ? FormatPower(snapshot.PowerWatts) : "—"
                        },
                        new[] { "ТЕМП.", "НАГРУЗКА", "ЧАСТОТА", "МОЩНОСТЬ" }, accent,
                        CompactCardLayoutFlavor.Normal);
                }
                case CompactCardKind.Gpu:
                {
                    bool detected = available && !String.IsNullOrWhiteSpace(snapshot.GpuName);
                    Color accent = available ? GpuStatusColor(snapshot.GpuTemperature, detected) : muted;
                    string temperature = detected ? FormatTemperature(snapshot.GpuTemperature) : "—°C";
                    string usage = detected
                        ? Math.Round(snapshot.GpuUsage).ToString("0", CultureInfo.InvariantCulture) + "%"
                        : "—%";
                    string memory = detected && snapshot.GpuMemoryTotalGb > 0
                        ? snapshot.GpuMemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                            snapshot.GpuMemoryTotalGb.ToString("0", CultureInfo.InvariantCulture) + "G"
                        : "—";
                    return MakeCompactPresentation("GPU", detected ? temperature : "—", detected ? usage : String.Empty,
                        new[]
                        {
                            temperature,
                            usage,
                            detected ? FormatClockMhz(snapshot.GpuClockMhz) : "—",
                            detected ? FormatPower(snapshot.GpuPowerWatts) : "—",
                            memory
                        },
                        new[] { "ТЕМП.", "НАГРУЗКА", "ЧАСТОТА", "МОЩНОСТЬ", "VRAM" }, accent,
                        CompactCardLayoutFlavor.Normal);
                }
                case CompactCardKind.Memory:
                {
                    Color accent = available ? Color.FromArgb(92, 170, 255) : muted;
                    double percent = available && snapshot.MemoryTotalGb > 0
                        ? snapshot.MemoryUsedGb / snapshot.MemoryTotalGb
                        : 0;
                    string percentText = available && snapshot.MemoryTotalGb > 0
                        ? (percent * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                        : "—%";
                    string usage = available && snapshot.MemoryTotalGb > 0
                        ? snapshot.MemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + "/" +
                            snapshot.MemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + "G"
                        : "— / —";
                    return MakeCompactPresentation("ПАМЯТЬ", percentText, usage,
                        new[]
                        {
                            percentText,
                            usage,
                            available && snapshot.MemoryClockMhz > 0
                                ? snapshot.MemoryClockMhz.ToString("0", CultureInfo.InvariantCulture) + " MHz"
                                : "—"
                        },
                        new[] { "ЗАНЯТО", "ИСПОЛЬЗОВАНО", "ЧАСТОТА" }, accent,
                        CompactCardLayoutFlavor.Normal);
                }
                case CompactCardKind.Network:
                {
                    Color accent = available ? Color.FromArgb(213, 219, 227) : muted;
                    string download = available ? FormatCompactRate(snapshot.NetworkDownloadKbps) : "—";
                    string upload = available ? FormatCompactRate(snapshot.NetworkUploadKbps) : "—";
                    return MakeCompactPresentation("СЕТЬ", "▼ " + download, "▲ " + upload,
                        new[]
                        {
                            available ? FormatRate(snapshot.NetworkDownloadKbps) : "—",
                            available ? FormatRate(snapshot.NetworkUploadKbps) : "—"
                        },
                        new[] { "ЗАГРУЗКА", "ОТДАЧА" }, accent,
                        CompactCardLayoutFlavor.Rate);
                }
                case CompactCardKind.Storage:
                    return CreateStorageCompactPresentation(snapshot, available, muted);
                case CompactCardKind.Fans:
                    return CreateFansCompactPresentation(snapshot, available, muted);
                case CompactCardKind.Fps:
                    return MakeCompactPresentation("FPS", "— FPS", String.Empty,
                        new[] { "— FPS", "— ms", "—" },
                        new[] { "FPS", "ВРЕМЯ КАДРА", "1% LOW" },
                        available ? Color.FromArgb(73, 190, 198) : muted,
                        CompactCardLayoutFlavor.Rate);
                default:
                    return MakeCompactPresentation("ПОКАЗАТЕЛЬ", "—", String.Empty,
                        new[] { "—" }, new[] { "НЕТ ДАННЫХ" }, muted,
                        CompactCardLayoutFlavor.Normal);
            }
        }

        private CompactCardPresentation CreateStorageCompactPresentation(SensorSnapshot snapshot,
            bool available, Color muted)
        {
            double used = 0;
            double total = 0;
            string drive = "ВСЕ ДИСКИ";
            if (available)
            {
                string[] names = snapshot.StorageDriveNames ?? new string[0];
                double[] usedValues = snapshot.StorageDriveUsedGb ?? new double[0];
                double[] totalValues = snapshot.StorageDriveTotalGb ?? new double[0];
                int count = Math.Min(names.Length, Math.Min(usedValues.Length, totalValues.Length));
                int selectedIndex = count > 0
                    ? Array.IndexOf(names, _selectedStorageDrive, 0, count)
                    : -1;
                if (count > 0)
                {
                    if (selectedIndex < 0)
                        selectedIndex = 0;
                    used = usedValues[selectedIndex];
                    total = totalValues[selectedIndex];
                    drive = names[selectedIndex];
                }
                else
                {
                    used = snapshot.StorageUsedGb;
                    total = snapshot.StorageTotalGb;
                }
            }

            double percent = total > 0 ? used / total : 0;
            string percentText = available && total > 0
                ? (percent * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "—%";
            string usage = available && total > 0
                ? used.ToString("0", CultureInfo.InvariantCulture) + "/" +
                    total.ToString("0", CultureInfo.InvariantCulture) + "G"
                : "— / —";
            Color accent = available ? Color.FromArgb(182, 133, 255) : muted;
            return MakeCompactPresentation("ХРАНИЛИЩЕ", percentText, usage,
                new[] { percentText, usage, available ? drive : "—" },
                new[] { "ЗАНЯТО", "ИСПОЛЬЗОВАНО", "ДИСК" }, accent,
                CompactCardLayoutFlavor.Normal);
        }

        private static CompactCardPresentation CreateFansCompactPresentation(SensorSnapshot snapshot,
            bool available, Color muted)
        {
            string[] names = available && snapshot != null ? snapshot.FanNames ?? new string[0] : new string[0];
            double[] rpm = available && snapshot != null ? snapshot.FanRpm ?? new double[0] : new double[0];
            double[] control = available && snapshot != null ? snapshot.FanControlPercent ?? new double[0] : new double[0];
            int count = Math.Min(names.Length, rpm.Length);
            if (count <= 0)
            {
                return MakeCompactPresentation("ВЕНТИЛЯТОРЫ", "— RPM", String.Empty,
                    new[] { "— RPM" }, new[] { "НЕТ ДАННЫХ" }, muted,
                    CompactCardLayoutFlavor.Normal);
            }

            string[] values = new string[Math.Min(5, count)];
            string[] captions = new string[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                double percent = index < control.Length ? control[index] : -1;
                values[index] = FormatCompactFan(rpm[index], percent);
                captions[index] = String.IsNullOrWhiteSpace(names[index]) ? "ВЕНТИЛЯТОР" : names[index];
            }
            string secondary = control.Length > 0 && control[0] >= 0
                ? Math.Round(control[0]).ToString("0", CultureInfo.InvariantCulture) + "%"
                : String.Empty;
            return MakeCompactPresentation("ВЕНТИЛЯТОРЫ",
                Math.Round(Math.Max(0, rpm[0])).ToString("0", CultureInfo.InvariantCulture) + " RPM",
                secondary, values, captions, Color.FromArgb(73, 190, 198),
                CompactCardLayoutFlavor.Normal);
        }

        private static string FormatCompactFan(double rpm, double control)
        {
            string value = rpm >= 0
                ? Math.Round(rpm).ToString("0", CultureInfo.InvariantCulture) + " RPM"
                : "— RPM";
            if (control >= 0)
                value += " · " + Math.Round(control).ToString("0", CultureInfo.InvariantCulture) + "%";
            return value;
        }

        private static CompactCardPresentation MakeCompactPresentation(string caption,
            string primary, string secondary, string[] values, string[] captions,
            Color accent, CompactCardLayoutFlavor flavor)
        {
            return new CompactCardPresentation
            {
                Caption = caption,
                Primary = primary,
                Secondary = secondary,
                Values = values,
                Captions = captions,
                Accent = accent,
                Flavor = flavor
            };
        }

        private static void SetCompactValue(Label label, string primary, string secondary)
        {
            string[] values = { primary ?? "—", secondary ?? String.Empty };
            label.Tag = values;
            label.Text = values[1].Length > 0 ? values[0] + "   " + values[1] : values[0];
        }

        private void CycleCompactCards()
        {
            int pageCount = Math.Max(1, _currentCompactCardCount);
            PerformAtomicLayout(delegate
            {
                _compactPageIndex = (_compactPageIndex + 1) % pageCount;
                LayoutResponsive();
            });
        }

        private void PerformAtomicLayout(Action action)
        {
            bool redrawWasDisabled = IsHandleCreated;
            if (redrawWasDisabled)
                NativeUi.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
            SuspendLayout();
            try
            {
                action();
            }
            finally
            {
                ResumeLayout(false);
                if (redrawWasDisabled)
                {
                    NativeUi.SendMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
                    Invalidate(true);
                    Update();
                }
            }
        }

        private void RunLayoutPass(bool updateCorners)
        {
            if (_layoutInProgress)
                return;
            _layoutInProgress = true;
            try
            {
                if (_interactiveResize)
                {
                    SuspendLayout();
                    try
                    {
                        HandleAutomaticViewTransition();
                        LayoutResponsive();
                    }
                    finally { ResumeLayout(false); }
                    Invalidate(true);
                    // Resizing from the left changes both the origin and the
                    // width. DWM may briefly reuse the previous client bitmap,
                    // leaving duplicated header icons and cards. Repaint that
                    // edge synchronously without slowing the normal right-edge
                    // resize path.
                    if (_resizeFromLeftEdge)
                        Update();
                }
                else
                {
                    PerformAtomicLayout(delegate
                    {
                        HandleAutomaticViewTransition();
                        LayoutResponsive();
                    });
                }
                if (updateCorners)
                    ApplyRoundedCorners();
            }
            finally
            {
                _layoutInProgress = false;
            }
        }

        private void PrepareCompactPaging(int visibleCards, int cardCount)
        {
            cardCount = Math.Max(1, cardCount);
            visibleCards = Math.Max(1, Math.Min(cardCount, visibleCards));
            if (_lastCompactVisibleCards != visibleCards || _lastCompactCardCount != cardCount)
            {
                _compactPageIndex = 0;
                _lastCompactVisibleCards = visibleCards;
                _lastCompactCardCount = cardCount;
            }

            _currentCompactVisibleCards = visibleCards;
            _currentCompactCardCount = cardCount;
            _compactPageIndex = Math.Max(0, Math.Min(_compactPageIndex, cardCount - 1));
            _cycleButton.Enabled = !_pinned;
        }

        private int[] GetCompactVisibleIndices()
        {
            int count = Math.Max(1, Math.Min(_currentCompactCardCount, _currentCompactVisibleCards));
            int start = Math.Max(0, Math.Min(_currentCompactCardCount - 1, _compactPageIndex));
            int[] indices = new int[count];
            for (int index = 0; index < count; index++)
                indices[index] = (start + index) % _currentCompactCardCount;
            return indices;
        }

        private void SetExpanded(bool expanded, bool save)
        {
            if (!_loadingSettings && !_switchingView)
            {
                RememberCurrentSize();
                if (!_expanded)
                {
                    _compactLocation = Location;
                    _compactLocationKnown = true;
                }
            }

            Point targetLocation = !expanded && _compactLocationKnown ? _compactLocation : Location;
            _switchingView = true;
            _expanded = expanded;
            _superExpanded = false;
            _superToggleButton.Expanded = false;
            _detailsArea.Visible = expanded;
            _superArea.Visible = false;
            // Keep the native tracking minimum stable for the whole resize
            // gesture.  Programmatic view changes are clamped below, while a
            // manual drag must be able to cross every responsive breakpoint
            // without releasing and grabbing the edge again.
            MinimumSize = new Size(MinimumCompactWidth,
                HeaderlessCompactMinimumHeight);
            Size target = expanded ? _expandedSize : _compactSize;
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int minimumWidth = expanded ? 220 : MinimumCompactWidth;
            int minimumHeight = expanded ? 278 : HeaderlessCompactMinimumHeight;
            target.Width = Math.Max(minimumWidth, Math.Min(target.Width, area.Width));
            target.Height = Math.Max(minimumHeight, Math.Min(target.Height, area.Height));
            ClientSize = target;
            Location = targetLocation;
            _expandButton.Text = "▾";
            UpdateViewMenuState();
            _tips.SetToolTip(_expandButton, "Скрыть в область уведомлений");
            _switchingView = false;
            RunLayoutPass(false);
            ApplyRoundedCorners();
            EnsureWindowVisible();
            if (!expanded)
            {
                _compactLocation = Location;
                _compactLocationKnown = true;
            }
            if (save)
                SaveSettings();
        }

        private void ToggleSuperExpanded()
        {
            if (!_superExpanded)
            {
                SetSuperExpanded(true, true);
                return;
            }

            if (!_superReturnStateKnown)
            {
                SetSuperExpanded(false, true);
                return;
            }

            RestorePreSuperState(true);
        }

        private void CapturePreSuperState()
        {
            _superReturnStateKnown = true;
            _superReturnExpanded = _expanded;
            _superReturnSize = ClientSize;
            _superReturnLocation = Location;
        }

        private void RestorePreSuperState(bool save)
        {
            bool returnExpanded = _superReturnExpanded;
            Size target = _superReturnSize;
            Point targetLocation = _superReturnLocation;
            Rectangle area = Screen.FromRectangle(new Rectangle(targetLocation, target)).WorkingArea;
            int minimumWidth = returnExpanded ? 220 : MinimumCompactWidth;
            int minimumHeight = returnExpanded ? 278 : HeaderlessCompactMinimumHeight;
            target.Width = Math.Max(minimumWidth, Math.Min(target.Width, area.Width));
            target.Height = Math.Max(minimumHeight, Math.Min(target.Height, area.Height));

            _switchingView = true;
            _superExpanded = false;
            _expanded = returnExpanded;
            _detailsArea.Visible = returnExpanded;
            _superArea.Visible = false;
            _superToggleButton.Expanded = false;
            // Never let the previous layout leave a native height constraint
            // behind.  The target itself has already been clamped above.
            MinimumSize = new Size(MinimumCompactWidth,
                HeaderlessCompactMinimumHeight);
            ClientSize = target;
            Location = targetLocation;
            _expandButton.Text = "▾";
            UpdateViewMenuState();
            _tips.SetToolTip(_expandButton, "Скрыть в область уведомлений");
            if (returnExpanded)
                _expandedSize = target;
            else
            {
                _compactSize = target;
                _compactLocation = targetLocation;
                _compactLocationKnown = true;
            }
            _superReturnStateKnown = false;
            // Preserve the captured visual mode for this pass. Otherwise the
            // responsive breakpoint can immediately reinterpret a custom-sized
            // compact window as an intermediate expanded layout.
            RunLayoutPass(false);
            _switchingView = false;
            ApplyRoundedCorners();
            EnsureWindowVisible();
            if (!returnExpanded)
                _compactLocation = Location;
            if (save)
                SaveSettings();
        }

        private void SetSuperExpanded(bool enabled, bool save)
        {
            if (enabled && !_superExpanded && !_superReturnStateKnown)
                CapturePreSuperState();
            if (enabled && !_expanded)
                SetExpanded(true, false);
            if (!_expanded)
                return;

            RememberCurrentSize();
            _switchingView = true;
            _superExpanded = enabled;
            UpdateViewMenuState();
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            Size target = enabled ? _superExpandedSize : _expandedSize;
            int minimumWidth = enabled ? SuperExpandedWidth : WindowWidth;
            int minimumHeight = enabled ? SuperExpandedHeight : ExpandedHeight;
            int maximumWidth = Math.Max(1, Math.Min(area.Width, MaximumSize.Width));
            int maximumHeight = Math.Max(1, Math.Min(area.Height, MaximumSize.Height));
            int effectiveMinimumWidth = Math.Min(minimumWidth, maximumWidth);
            int effectiveMinimumHeight = Math.Min(minimumHeight, maximumHeight);
            target.Width = Math.Max(effectiveMinimumWidth, Math.Min(target.Width, maximumWidth));
            target.Height = Math.Max(effectiveMinimumHeight, Math.Min(target.Height, maximumHeight));
            ClientSize = target;
            _superToggleButton.Expanded = enabled;
            _switchingView = false;
            RunLayoutPass(false);
            ApplyRoundedCorners();
            EnsureWindowVisible();
            if (save)
                SaveSettings();
        }

        private void UpdateViewMenuState()
        {
            _viewItem.Text = _superExpanded
                ? "Скрыть подробности"
                : "Показать подробности";
            _viewItem.Checked = _superExpanded;
        }

        private bool IsCompactHeaderHidden()
        {
            // Pinning changes input behaviour only.  It must not reveal the
            // header over compact cards or otherwise alter the current layout.
            return !_expanded && ClientSize.Height < CompactHeaderRevealHeight;
        }

        private bool IsHeaderHidden()
        {
            return _headerManuallyHidden || _restoredAutomaticHeaderHidden ||
                IsCompactHeaderHidden();
        }

        private void ToggleCompactHeader()
        {
            CloseOpacityPopup();
            bool manuallyHidden = _headerManuallyHidden;
            bool automaticallyHidden = IsCompactHeaderHidden();
            bool revealing = manuallyHidden || automaticallyHidden ||
                _restoredAutomaticHeaderHidden;
            _headerManuallyHidden = !revealing;
            _restoredAutomaticHeaderHidden = false;

            // In compact mode the manual command also consumes or restores the
            // physical header height.  Larger views keep their exact bounds:
            // only their top row is hidden until the user explicitly restores it.
            bool resizeCompactWindow = !_expanded && !_pinned &&
                (ClientSize.Height <= CompactHeight || automaticallyHidden);
            if (resizeCompactWindow)
            {
                int targetHeight;
                if (revealing)
                {
                    targetHeight = ClientSize.Height < CompactHeaderRevealHeight
                        ? Math.Max(CompactHeaderRevealHeight,
                            ClientSize.Height + CompactHeaderDelta)
                        : ClientSize.Height;
                }
                else
                {
                    targetHeight = Math.Min(CompactHeaderRevealHeight - 1,
                        Math.Max(HeaderlessCompactMinimumHeight,
                            ClientSize.Height - CompactHeaderDelta));
                }

                _switchingView = true;
                ClientSize = new Size(ClientSize.Width, targetHeight);
                _compactSize = ClientSize;
                _switchingView = false;
            }
            RunLayoutPass(false);
            ApplyRoundedCorners();
            EnsureWindowVisible();
            SaveSettings();
        }

        private void RememberCurrentSize()
        {
            if (_loadingSettings || _switchingView || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                return;
            if (_superExpanded)
                _superExpandedSize = ClientSize;
            else if (_expanded)
                _expandedSize = ClientSize;
            else
                _compactSize = ClientSize;
        }

        private void ToggleOpacityPopup()
        {
            if (_opacityPopupVisible)
            {
                CloseOpacityPopup();
                return;
            }

            _opacityPopupVisible = true;
            _opacitySlider.Enabled = true;
            _opacityButton.ForeColor = Color.FromArgb(73, 190, 198);
            _tips.SetToolTip(_opacityButton, OpacityTooltip);
            LayoutOpacityPopup();
            _opacityCard.Show(this);
            _opacityCard.BringToFront();
        }

        private void LayoutOpacityPopup()
        {
            _opacityCard.Bounds = GetOpacityPopupBounds();
            LayoutOpacityCard();
        }

        private Rectangle GetOpacityPopupBounds()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int popupWidth = Math.Max(140, Math.Min(250, area.Width - 8));
            const int popupHeight = 32;
            const int gap = 6;
            int x = Left + (Width - popupWidth) / 2;
            x = Math.Max(area.Left + 4, Math.Min(x, area.Right - popupWidth - 4));
            int y = Top - popupHeight - gap;
            if (y < area.Top + 4)
                y = Bottom + gap;
            if (y + popupHeight > area.Bottom - 4)
                y = Math.Max(area.Top + 4, Math.Min(Top, area.Bottom - popupHeight - 4));
            return new Rectangle(x, y, popupWidth, popupHeight);
        }

        private bool CanShowOpacityPopup()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            return area.Width >= 148 && area.Height >= 40;
        }

        private void CloseOpacityPopup()
        {
            _opacityPopupVisible = false;
            _opacityCard.Hide();
            _opacityButton.ForeColor = Color.FromArgb(165, 173, 184);
            if (!_pinned)
                _tips.SetToolTip(_opacityButton, OpacityTooltip);
        }

        private void SetOpacityPercent(int percent, bool save)
        {
            percent = Math.Max(10, Math.Min(100, percent));
            Opacity = percent / 100.0;
            _opacityLabel.Text = _opacityCard.Width >= 220
                ? "ПРОЗРАЧНОСТЬ  " + percent.ToString(CultureInfo.InvariantCulture) + "%"
                : percent.ToString(CultureInfo.InvariantCulture) + "%";
            if (_opacitySlider.Value != percent)
            {
                _loadingSettings = true;
                _opacitySlider.Value = percent;
                _loadingSettings = false;
            }
            foreach (ToolStripMenuItem item in _opacityItems)
                item.Checked = item.Text == percent.ToString(CultureInfo.InvariantCulture) + "%";
            if (save)
                SaveSettings();
        }

        private void ApplyBackgroundMode(bool enabled, bool save)
        {
            _backgroundless = enabled;
            bool wasLoading = _loadingSettings;
            _loadingSettings = true;
            _backgroundCheckBox.Checked = enabled;
            _backgroundItem.Checked = enabled;
            _loadingSettings = wasLoading;

            if (enabled)
            {
                BackColor = BackgroundKey;
                TransparencyKey = BackgroundKey;
            }
            else
            {
                TransparencyKey = Color.Empty;
                BackColor = NormalBackground;
            }

            _detailsArea.BackColor = BackColor;
            _superArea.BackColor = BackColor;
            foreach (MonitorCard card in _cards)
                card.SetBackgroundless(enabled, BackgroundKey);
            _opacityCard.BackColor = Color.FromArgb(29, 33, 40);
            foreach (Button button in _headerButtons)
                button.BackColor = BackColor;
            _backgroundButton.ForeColor = enabled
                ? Color.FromArgb(73, 190, 198)
                : Color.FromArgb(165, 173, 184);
            _tips.SetToolTip(_backgroundButton, _pinned
                ? String.Empty
                : enabled ? "Вернуть фон" : "Убрать фон");
            _superToggleButton.BackColor = enabled ? BackgroundKey : Color.FromArgb(29, 33, 40);
            _opacitySlider.BackColor = Color.FromArgb(29, 33, 40);
            Invalidate(true);
            SyncBackgroundHitForm();

            if (save)
                SaveSettings();
        }

        private void ApplyPinnedMode(bool enabled, bool save)
        {
            _pinned = enabled;
            if (!enabled)
                _pinnedClickTarget = IntPtr.Zero;
            _pinItem.Checked = enabled;
            _topMostItem.Enabled = true;
            Button[] lockedHeaderButtons =
            {
                _opacityButton, _backgroundButton, _cycleButton, _expandButton
            };
            foreach (Button button in lockedHeaderButtons)
            {
                button.Enabled = !enabled;
                button.BackColor = BackColor;
            }
            _opacityButton.Enabled = !enabled && CanShowOpacityPopup();
            _pinButton.Enabled = true;
            _superToggleButton.Enabled = !enabled;
            // The popup is a separate tool window. Keep its slider interactive
            // even while the monitor itself is pinned and click-through.
            _opacitySlider.Enabled = true;
            _storageSummary.Enabled = !enabled;
            _storageSummary.Cursor = enabled ? Cursors.Default : Cursors.Hand;

            _topMostItem.Checked = TopMost;
            _pinButton.ForeColor = enabled
                ? Color.FromArgb(73, 190, 198)
                : Color.FromArgb(165, 173, 184);
            _tips.SetToolTip(_pinButton, enabled
                ? "Положение закреплено. Нажмите пин, чтобы разблокировать"
                : "Закрепить положение и отключить клики по утилите");

            if (enabled)
            {
                _tips.SetToolTip(_opacityButton, String.Empty);
                _tips.SetToolTip(_backgroundButton, String.Empty);
                _tips.SetToolTip(_cycleButton, String.Empty);
                _tips.SetToolTip(_expandButton, String.Empty);
            }
            else
            {
                _opacityButton.ForeColor = _opacityPopupVisible
                    ? Color.FromArgb(73, 190, 198)
                    : Color.FromArgb(165, 173, 184);
                _backgroundButton.ForeColor = _backgroundless
                    ? Color.FromArgb(73, 190, 198)
                    : Color.FromArgb(165, 173, 184);
                _cycleButton.ForeColor = Color.FromArgb(165, 173, 184);
                _expandButton.ForeColor = Color.FromArgb(165, 173, 184);
                _tips.SetToolTip(_opacityButton, OpacityTooltip);
                _tips.SetToolTip(_backgroundButton, _backgroundless ? "Вернуть фон" : "Убрать фон");
                UpdateCompactCycleTooltip();
                _tips.SetToolTip(_expandButton, "Скрыть в область уведомлений");
            }
            _superToggleButton.Invalidate();
            SyncBackgroundHitForm();
            if (!_loadingSettings)
                RunLayoutPass(false);
            if (save)
                SaveSettings();
        }

        private void HandleAutomaticViewTransition()
        {
            if (_loadingSettings || _switchingView || _automaticTransition)
                return;

            // A vertical stack stops gaining useful information sooner because
            // additional width only creates empty space to the right.  Switch
            // it to gauges earlier, while keeping the wider landscape boundary
            // that gives compact metric columns room to scale naturally.
            bool verticalWindow = ClientSize.Height > ClientSize.Width;
            int requiredWidth = verticalWindow ? 210 : 260;
            bool shouldBeExpanded = ClientSize.Width >= requiredWidth && ClientSize.Height >= 300;
            if (_expanded == shouldBeExpanded)
                return;

            _automaticTransition = true;
            if (shouldBeExpanded)
            {
                _expanded = true;
            }
            else
            {
                _expanded = false;
                _superExpanded = false;
            }
            _detailsArea.Visible = _expanded;
            _superArea.Visible = _expanded && _superExpanded;
            MinimumSize = new Size(MinimumCompactWidth,
                HeaderlessCompactMinimumHeight);
            _expandButton.Text = "▾";
            UpdateViewMenuState();
            _tips.SetToolTip(_expandButton, "Скрыть в область уведомлений");
            _superToggleButton.Expanded = _superExpanded;
            if (_superExpanded)
                _superExpandedSize = ClientSize;
            else if (_expanded)
                _expandedSize = ClientSize;
            else
                _compactSize = ClientSize;
            _automaticTransition = false;
        }

        private void LayoutResponsive()
        {
            if (ClientSize.Width < 1 || ClientSize.Height < 1)
                return;

            ApplyDynamicSizeLimits();
            LayoutHeaderButtons();

            bool headerHidden = IsHeaderHidden();
            _headerVisibilityItem.Enabled = true;
            _headerVisibilityItem.Text = "Скрыть верхнюю панель";
            _headerVisibilityItem.Checked = headerHidden;
            foreach (Button button in _headerButtons)
                button.Visible = !headerHidden;
            _title.Visible = !headerHidden && ClientSize.Width >= 200;
            _title.Text = "TRAYMETRY";
            _title.Size = new Size(Math.Max(42, ClientSize.Width - 136), 22);
            _topLeftResizeGrip.Location = Point.Empty;
            _leftResizeGrip.Location = new Point(0, ClientSize.Height - _leftResizeGrip.Height);
            _resizeGrip.Location = new Point(ClientSize.Width - _resizeGrip.Width, ClientSize.Height - _resizeGrip.Height);

            int compactTop = headerHidden ? 29 - CompactHeaderDelta : 29;
            int compactBottom = _expanded
                ? CompactHeight - 9
                : ClientSize.Height - 20;
            int compactAvailableHeight = Math.Max(28, compactBottom - compactTop);
            int compactSideMargin = ClientSize.Width < 120 ? 6 : 10;
            int availableWidth = Math.Max(1, ClientSize.Width - compactSideMargin * 2);
            int gap = 8;
            MonitorCard[] allCompactCards = _compactSlots.Select(delegate(CompactCardSlotView slot)
            {
                return slot.Card;
            }).ToArray();
            int compactCardCount = allCompactCards.Length;

            bool verticalCompactLayout = !_expanded && ClientSize.Height > ClientSize.Width;
            if (verticalCompactLayout)
            {
                // Two concise cards remain readable at 54 px each.  Using the
                // full 58 px card height as the paging threshold left a narrow
                // band where one oversized CPU card occupied the whole window
                // even though CPU and GPU already fitted comfortably.
                const int preferredCardHeight = 54;
                int visibleCards = Math.Max(1, Math.Min(compactCardCount,
                    (compactAvailableHeight + gap) / (preferredCardHeight + gap)));
                PrepareCompactPaging(visibleCards, compactCardCount);
                int[] visibleIndices = GetCompactVisibleIndices();
                int compactCardHeight = Math.Max(28, (compactAvailableHeight - gap * (visibleCards - 1)) / visibleCards);
                for (int slot = 0; slot < visibleCards; slot++)
                {
                    MonitorCard card = allCompactCards[visibleIndices[slot]];
                    int top = compactTop + slot * (compactCardHeight + gap);
                    int height = slot == visibleCards - 1
                        ? compactBottom - top
                        : compactCardHeight;
                    card.Location = new Point(compactSideMargin, top);
                    card.Size = new Size(availableWidth, Math.Max(28, height));
                }
                ApplyCompactVisibility(allCompactCards, visibleIndices);
            }
            else
            {
                int visibleCards = ClientSize.Width >= 400
                    ? 4
                    : ClientSize.Width >= 300 ? 3 : ClientSize.Width >= 200 ? 2 : 1;
                visibleCards = Math.Min(compactCardCount, visibleCards);
                PrepareCompactPaging(visibleCards, compactCardCount);
                int[] visibleIndices = GetCompactVisibleIndices();
                int compactCardWidth = Math.Max(1, (availableWidth - gap * (visibleCards - 1)) / visibleCards);
                for (int slot = 0; slot < visibleCards; slot++)
                {
                    MonitorCard card = allCompactCards[visibleIndices[slot]];
                    card.Location = new Point(compactSideMargin + slot * (compactCardWidth + gap), compactTop);
                    int width = slot == visibleCards - 1
                        ? ClientSize.Width - compactSideMargin - card.Left
                        : compactCardWidth;
                    card.Size = new Size(width, compactAvailableHeight);
                }
                ApplyCompactVisibility(allCompactCards, visibleIndices);
            }

            RefreshCompactValueLayouts();

            _detailsArea.Location = new Point(0, CompactHeight);
            int detailsBottom = ClientSize.Height - 20;
            _detailsArea.Size = new Size(ClientSize.Width,
                Math.Max(0, detailsBottom - CompactHeight));
            if (_expanded)
                LayoutDetailedCards();

            if (!_pinned)
            {
                _opacityButton.Enabled = true;
                _tips.SetToolTip(_opacityButton, OpacityTooltip);
            }
            if (_opacityPopupVisible)
            {
                LayoutOpacityPopup();
                _opacityCard.BringToFront();
            }

            _superToggleButton.Visible = true;
            _superToggleButton.Expanded = _superExpanded;
            _superToggleButton.Bounds = new Rectangle(compactSideMargin,
                Math.Max(0, ClientSize.Height - 19), availableWidth, 10);
            _superToggleButton.BringToFront();

            bool absoluteMinimalCompact = !_expanded &&
                ClientSize.Width <= 120 && ClientSize.Height <= CompactHeight;
            Rectangle topLeftGripBounds = new Rectangle(Point.Empty, _topLeftResizeGrip.Size);
            // Header buttons are right-anchored. At 132 px the leftmost one
            // first enters the 15 px resize marker, even before WinForms has
            // completed the native anchor pass for the current resize frame.
            bool topLeftGripOverlapsButton = ClientSize.Width <= 132 ||
                _headerButtons.Any(delegate(Button button)
                {
                    return button.Visible && button.Bounds.IntersectsWith(topLeftGripBounds);
                });
            _topLeftResizeGrip.Visible = !_pinned && !headerHidden &&
                !absoluteMinimalCompact && !topLeftGripOverlapsButton;
            if (_topLeftResizeGrip.Visible)
                _topLeftResizeGrip.BringToFront();
            _leftResizeGrip.Visible = !_pinned;
            if (_leftResizeGrip.Visible)
                _leftResizeGrip.BringToFront();
            _resizeGrip.Visible = !_pinned;
            if (_resizeGrip.Visible)
                _resizeGrip.BringToFront();
        }

        private static void ApplyCompactVisibility(MonitorCard[] cards, int[] visibleIndices)
        {
            for (int index = 0; index < cards.Length; index++)
            {
                bool shouldBeVisible = visibleIndices.Contains(index);
                // Visible inherits the state of every parent.  Assign it even
                // while the form is hidden, otherwise a card can retain a
                // stale internal state and reappear over another card later.
                cards[index].Visible = shouldBeVisible;
            }
        }

        private void ApplyDynamicSizeLimits()
        {
            if (_applyingSizeLimits)
                return;
            Size desiredMaximum = new Size(1000, SuperExpandedHeight);
            _applyingSizeLimits = true;
            try
            {
                if (MaximumSize != desiredMaximum)
                    MaximumSize = desiredMaximum;
            }
            finally
            {
                _applyingSizeLimits = false;
            }
        }

        private void LayoutCompactValue(MonitorCard card, Label value, CompactMetricColumn column, bool compactRate)
        {
            // Never infer semantic roles from z-order.  BringToFront() and the
            // vertical metric-column mode legitimately reorder child controls;
            // using Controls[0] then made a later pass treat the value itself
            // as the caption and produced the clipped fragments seen after a
            // few resize cycles.
            Label caption = card.Controls.OfType<Label>()
                .FirstOrDefault(delegate(Label label) { return !ReferenceEquals(label, value); });
            if (caption != null)
            {
                if (caption.Tag == null)
                    caption.Tag = caption.Text;
            }

            // Once a compact card becomes tall, use its height for the full
            // metric list at every width.  This keeps the same visual language
            // while horizontal resizing only scales text instead of switching
            // back to two oversized values.
            bool useColumn = card.Height >= 100;
            value.Visible = !useColumn;
            column.Visible = useColumn;
            if (useColumn)
            {
                column.Bounds = new Rectangle(8, 21, Math.Max(1, card.Width - 16), Math.Max(1, card.Height - 25));
                SetCompactCaptionLayout(caption, true,
                    new Rectangle(9, 4, Math.Max(1, card.Width - 18), 17),
                    ContentAlignment.MiddleLeft);
                int rowsThatFit = Math.Max(2, column.Height / 38);
                column.VisibleMetricCount = Math.Min(column.MetricCount, rowsThatFit);
                column.BringToFront();
                return;
            }

            string[] storedValues = value.Tag as string[];
            string primary = storedValues != null && storedValues.Length > 0 ? storedValues[0] : value.Text;
            string secondary = storedValues != null && storedValues.Length > 1 ? storedValues[1] : String.Empty;
            bool showOnlyPrimary = card.Width < 112;
            // A tall compact card has enough vertical room to become a real
            // glanceable display.  Keep both readings, stack them and let the
            // type grow with the card instead of leaving a small label in a
            // large empty rectangle.
            bool spaciousStack = !showOnlyPrimary && secondary.Length > 0 &&
                card.Height >= 86 && card.Height > card.Width * 0.72F;
            string singleLineText = showOnlyPrimary || secondary.Length == 0
                ? primary
                : primary + "   " + secondary;
            bool wrapValues = !showOnlyPrimary && secondary.Length > 0 &&
                (card.Width < 132 || spaciousStack);
            value.Text = wrapValues ? primary + Environment.NewLine + secondary : singleLineText;
            int valueTop = card.Height < 38 ? 1 : card.Height < 48 ? 18 : 21;
            float fontSize = Math.Max(10.5F, Math.Min(21F, 11F + (card.Height - 40) * 0.22F));
            if (spaciousStack)
            {
                float widthScale = Math.Max(16F, (card.Width - 16) / (compactRate ? 4.4F : 3.7F));
                float heightScale = Math.Max(16F, (card.Height - valueTop - 4) / 3.2F);
                fontSize = Math.Min(34F, Math.Min(widthScale, heightScale));
            }
            if (card.Height < 38)
                fontSize = Math.Min(fontSize, 12F);
            if (compactRate)
                fontSize = Math.Max(8.5F, spaciousStack ? fontSize : fontSize - 3F);
            if (wrapValues && !spaciousStack)
                fontSize = Math.Min(fontSize, compactRate ? 10.5F : 12.5F);
            if (showOnlyPrimary)
                fontSize = Math.Max(fontSize, compactRate ? 12F : 13.5F);
            fontSize = (float)Math.Round(fontSize * 2F) / 2F;

            int valueWidth = Math.Max(1, card.Width - 16);
            if (card.Height >= 38)
            {
                SetCompactCaptionLayout(caption, true,
                    new Rectangle(9, card.Height < 48 ? 1 : 4,
                        Math.Max(1, card.Width - 18), 17),
                    ContentAlignment.MiddleLeft);
            }
            else
            {
                bool sideCaption = false;
                if (!showOnlyPrimary && secondary.Length > 0 && caption != null)
                {
                    string captionText = caption.Tag as string ?? caption.Text;
                    using (Font probe = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        int primaryWidth = MeasureSingleLine(primary, probe);
                        int secondaryWidth = MeasureSingleLine(secondary, probe);
                        int measuredValueWidth = wrapValues
                            ? Math.Max(primaryWidth, secondaryWidth)
                            : MeasureSingleLine(singleLineText, probe);
                        int captionWidth = Math.Max(24,
                            MeasureSingleLine(captionText, caption.Font) + 2);
                        int innerWidth = Math.Max(1, card.Width - 16);
                        sideCaption = measuredValueWidth + 6 + captionWidth <= innerWidth;
                        if (sideCaption)
                        {
                            Rectangle captionBounds = new Rectangle(
                                card.Width - 8 - captionWidth,
                                Math.Max(0, (card.Height - 17) / 2), captionWidth, 17);
                            SetCompactCaptionLayout(caption, true, captionBounds,
                                ContentAlignment.MiddleRight);
                            valueWidth = Math.Max(1, captionBounds.Left - 14);
                        }
                    }
                }
                if (!sideCaption)
                    SetCompactCaptionLayout(caption, false, Rectangle.Empty,
                        ContentAlignment.MiddleLeft);
            }

            value.Location = new Point(8, valueTop);
            value.Size = new Size(valueWidth, Math.Max(1, card.Height - valueTop - 2));
            value.TextAlign = spaciousStack ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft;
            FitLabelFont(value, fontSize, compactRate ? 7.5F : 8F, FontStyle.Bold);
            // Values are the primary content.  This is also a final safety net
            // against a transient native-label repaint during live resizing.
            value.BringToFront();
            card.Invalidate();
        }

        private static int MeasureSingleLine(string text, Font font)
        {
            return TextRenderer.MeasureText(text ?? String.Empty, font,
                new Size(Int32.MaxValue, Int32.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine).Width;
        }

        private static void SetCompactCaptionLayout(Label caption, bool visible,
            Rectangle bounds, ContentAlignment alignment)
        {
            if (caption == null)
                return;
            string originalText = caption.Tag as string ?? caption.Text;
            string nextText = visible ? originalText : String.Empty;
            Rectangle nextBounds = visible ? bounds : caption.Bounds;
            Rectangle dirtyBounds = Rectangle.Union(caption.Bounds, nextBounds);
            caption.Bounds = nextBounds;
            caption.TextAlign = alignment;
            caption.Text = nextText;
            // Do not use caption.Visible as an early-out here.  Its getter is
            // false whenever a parent card/form is hidden, even if the label's
            // own visibility bit is still true.  An unconditional assignment
            // prevents old captions from returning after paging or resizing.
            caption.Visible = visible;
            Control parent = caption.Parent;
            if (parent != null && parent.IsHandleCreated)
                parent.Invalidate(dirtyBounds, true);
        }

        private void RefreshCompactValueLayouts()
        {
            foreach (CompactCardSlotView slot in _compactSlots)
                LayoutCompactValue(slot.Card, slot.Value, slot.Column,
                    slot.Flavor == CompactCardLayoutFlavor.Rate);
        }

        private void LayoutDetailedCards()
        {
            int availableWidth = Math.Max(1, _detailsArea.ClientSize.Width - 20);
            int availableHeight = Math.Max(0, _detailsArea.ClientSize.Height);

            _superArea.Location = new Point(10, 0);
            _superArea.Size = new Size(availableWidth, Math.Max(1, availableHeight));
            LayoutSuperArea();
            ApplyDetailedVisibility(false, false, true);
        }

        private void ApplyDetailedVisibility(bool cpu, bool gpu, bool super)
        {
            _cpuCard.Visible = cpu;
            _gpuCard.Visible = gpu;
            _superArea.Visible = super;
        }

        private void LayoutHardwareCard(MonitorCard card, Label name, MetricReadout[] metrics, Label footer)
        {
            name.Size = new Size(Math.Max(1, card.Width - 20), 22);
            bool showName = card.Width >= 320;
            name.Visible = showName;
            metrics[0].SetCaption("ТЕМПЕРАТУРА");
            if (card.Width < 150)
            {
                metrics[0].SetCaption("ТЕМП.");
                bool showLoad = card.Height >= 135;
                for (int index = 0; index < metrics.Length; index++)
                    metrics[index].Visible = index == 0 || (index == 1 && showLoad);

                int narrowMetricTop = showName ? 49 : 26;
                int narrowMetricWidth = Math.Max(1, card.Width - 16);
                int availableMetricHeight = Math.Max(40, card.Height - narrowMetricTop - 4);
                int narrowMetricHeight = showLoad
                    ? Math.Max(42, (availableMetricHeight - 4) / 2)
                    : availableMetricHeight;
                metrics[0].Location = new Point(8, narrowMetricTop);
                metrics[0].Size = new Size(narrowMetricWidth, narrowMetricHeight);
                float narrowScale = Math.Max(1.15F, Math.Min(1.55F, card.Width / 82F));
                metrics[0].SetScale(narrowScale);
                if (showLoad)
                {
                    metrics[1].Location = new Point(8, narrowMetricTop + narrowMetricHeight + 4);
                    metrics[1].Size = new Size(narrowMetricWidth, narrowMetricHeight);
                    metrics[1].SetScale(narrowScale);
                }
                if (footer != null)
                    footer.Visible = false;
                return;
            }

            int metricTop = showName ? 49 : 26;
            int metricWidth = Math.Max(45, (card.Width - 30) / 2);
            int secondX = 20 + metricWidth;
            bool showSecondRow = card.Height >= (showName ? 138 : 112);
            bool showFooter = footer != null && card.Height >= 165;
            int metricBottom = card.Height - (showFooter ? 20 : 4);
            int metricHeight = showSecondRow
                ? Math.Max(40, Math.Min(58, (metricBottom - metricTop - 4) / 2))
                : Math.Max(40, metricBottom - metricTop);
            int secondRowY = metricTop + metricHeight + 4;
            metrics[0].Location = new Point(10, metricTop);
            metrics[1].Location = new Point(secondX, metricTop);
            metrics[0].Size = new Size(metricWidth, metricHeight);
            metrics[1].Size = new Size(metricWidth, metricHeight);
            metrics[2].Visible = showSecondRow;
            metrics[3].Visible = showSecondRow;
            if (showSecondRow)
            {
                metrics[2].Location = new Point(10, secondRowY);
                metrics[3].Location = new Point(secondX, secondRowY);
                metrics[2].Size = new Size(metricWidth, metricHeight);
                metrics[3].Size = new Size(metricWidth, metricHeight);
            }

            float scale = Math.Max(0.9F, Math.Min(1.35F, card.Width / 200F));
            if (!showName)
                scale = Math.Max(1.15F, scale);
            scale = (float)Math.Round(scale * 10F) / 10F;
            foreach (MetricReadout metric in metrics)
                metric.SetScale(scale);
            if (showName)
                SetLabelFont(name, Math.Min(11.5F, 8.5F * scale), FontStyle.Regular);
            if (footer != null)
            {
                footer.Visible = showFooter;
                footer.Location = new Point(10, card.Height - 22);
                footer.Size = new Size(Math.Max(1, card.Width - 20), 17);
            }
        }

        private void LayoutSuperArea()
        {
            const int gap = 10;
            int actualHeight = Math.Max(1, _superArea.ClientSize.Height);
            const int wideColumnMinimum = 135;
            const int wideColumnGap = 8;
            int wideLayoutMinimum = wideColumnMinimum * 4 + wideColumnGap * 3;
            if (_superArea.ClientSize.Width >= wideLayoutMinimum)
            {
                LayoutWideSuperArea(actualHeight);
                return;
            }

            int halfWidth = Math.Max(1, (_superArea.ClientSize.Width - gap) / 2);
            int gaugeHeight = Math.Max(64, Math.Min(190, actualHeight));
            _cpuGauge.Bounds = new Rectangle(0, 0, halfWidth, gaugeHeight);
            _gpuGauge.Bounds = new Rectangle(halfWidth + gap, 0,
                _superArea.ClientSize.Width - halfWidth - gap, gaugeHeight);
            _cpuGauge.Visible = true;
            _gpuGauge.Visible = true;

            const int summaryHeight = 68;
            int summariesTop = gaugeHeight + gap;
            bool showSummaries = actualHeight - summariesTop >= 18;
            _memorySummary.Visible = showSummaries;
            _storageSummary.Visible = showSummaries;
            if (showSummaries)
            {
                _memorySummary.Bounds = new Rectangle(0, summariesTop, halfWidth, summaryHeight);
                _storageSummary.Bounds = new Rectangle(halfWidth + gap, summariesTop,
                    _superArea.ClientSize.Width - halfWidth - gap, summaryHeight);
            }

            int historyTop = summariesTop + summaryHeight + gap;
            int fanHeight = _fanSummary.GetPreferredHeight(_superArea.ClientSize.Width);
            bool hasFans = _fanSummary.HasFans;
            // Fans have a stable place before history.  When the viewport is
            // shortened, the parent clips their lower edge instead of swapping
            // the entire fan block for graphs at a one-pixel breakpoint.
            _fanSummary.Visible = hasFans && actualHeight > historyTop;
            if (hasFans)
            {
                _fanSummary.Bounds = new Rectangle(0, historyTop,
                    _superArea.ClientSize.Width, fanHeight);
                historyTop += fanHeight + gap;
            }
            int historyViewportHeight = actualHeight - historyTop;
            bool showHistory = historyViewportHeight >= 24;
            int historyHeight = Math.Max(90, historyViewportHeight);
            _cpuHistory.Bounds = new Rectangle(0, historyTop, halfWidth, historyHeight);
            _gpuHistory.Bounds = new Rectangle(halfWidth + gap, historyTop,
                _superArea.ClientSize.Width - halfWidth - gap, historyHeight);
            _cpuHistory.Visible = showHistory;
            _gpuHistory.Visible = showHistory;
        }

        private void LayoutWideSuperArea(int actualHeight)
        {
            const int columnGap = 8;
            const int historyGap = 10;
            int availableWidth = Math.Max(1, _superArea.ClientSize.Width);
            int columnWidth = Math.Max(1, (availableWidth - columnGap * 3) / 4);
            int rowHeight = Math.Max(72, Math.Min(190, actualHeight));
            int fourthLeft = (columnWidth + columnGap) * 3;

            _cpuGauge.Bounds = new Rectangle(0, 0, columnWidth, rowHeight);
            _gpuGauge.Bounds = new Rectangle(columnWidth + columnGap, 0, columnWidth, rowHeight);
            _memorySummary.Bounds = new Rectangle((columnWidth + columnGap) * 2, 0,
                columnWidth, rowHeight);
            _storageSummary.Bounds = new Rectangle(fourthLeft, 0,
                Math.Max(1, availableWidth - fourthLeft), rowHeight);
            _cpuGauge.Visible = true;
            _gpuGauge.Visible = true;
            _memorySummary.Visible = true;
            _storageSummary.Visible = true;

            int historyTop = rowHeight + historyGap;
            int fanHeight = _fanSummary.GetPreferredHeight(availableWidth);
            bool hasFans = _fanSummary.HasFans;
            // Preserve the same vertical order at every height.  Partial fan
            // rows are intentionally clipped by _superArea; history begins
            // only below the complete fan block.
            _fanSummary.Visible = hasFans && actualHeight > historyTop;
            if (hasFans)
            {
                _fanSummary.Bounds = new Rectangle(0, historyTop,
                    availableWidth, fanHeight);
                historyTop += fanHeight + historyGap;
            }
            int historyViewportHeight = actualHeight - historyTop;
            bool showHistory = historyViewportHeight >= 24;
            int historyHeight = Math.Max(90, historyViewportHeight);
            int historyWidth = Math.Max(1, (availableWidth - historyGap) / 2);
            _cpuHistory.Bounds = new Rectangle(0, historyTop, historyWidth, historyHeight);
            _gpuHistory.Bounds = new Rectangle(historyWidth + historyGap, historyTop,
                Math.Max(1, availableWidth - historyWidth - historyGap), historyHeight);
            _cpuHistory.Visible = showHistory;
            _gpuHistory.Visible = showHistory;
        }

        private void LayoutOpacityCard()
        {
            _backgroundCheckBox.Visible = false;
            int labelWidth = _opacityCard.Width >= 220 ? 130 : _opacityCard.Width >= 140 ? 70 : 34;
            _opacityLabel.Text = _opacityCard.Width >= 220
                ? "ПРОЗРАЧНОСТЬ  " + _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%";
            _opacityLabel.Location = new Point(8, 5);
            _opacityLabel.Size = new Size(labelWidth, 22);
            _opacitySlider.Location = new Point(labelWidth + 9, 5);
            _opacitySlider.Size = new Size(Math.Max(1, _opacityCard.Width - labelWidth - 17), 22);
        }

        private static void SetLabelFont(Label label, float size, FontStyle style)
        {
            if (Math.Abs(label.Font.Size - size) < 0.15F && label.Font.Style == style)
                return;
            Font oldFont = label.Font;
            label.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            oldFont.Dispose();
        }

        private static void FitLabelFont(Label label, float maximumSize, float minimumSize, FontStyle style)
        {
            float size = maximumSize;
            bool multiline = label.Text.IndexOf('\n') >= 0 || label.Text.IndexOf('\r') >= 0;
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                (multiline
                    ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
                    : TextFormatFlags.SingleLine);
            while (size > minimumSize)
            {
                using (Font probe = new Font("Segoe UI", size, style, GraphicsUnit.Point))
                {
                    Size measured = TextRenderer.MeasureText(label.Text, probe,
                        new Size(Math.Max(1, label.Width), Int32.MaxValue), flags);
                    if (measured.Height <= label.Height && measured.Width <= label.Width)
                        break;
                }
                size -= 0.5F;
            }
            SetLabelFont(label, Math.Max(minimumSize, size), style);
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _pinned)
                return;
            if (TryHandleDragDoubleClick())
                return;
            _windowMovedDuringDragClick = false;
            NativeUi.ReleaseCapture();
            NativeUi.SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
            if (_windowMovedDuringDragClick)
                _dragClickPending = false;
        }

        private bool TryHandleDragDoubleClick()
        {
            int now = Environment.TickCount;
            Point current = Cursor.Position;
            int elapsed = unchecked(now - _lastDragClickTick);
            Size tolerance = SystemInformation.DoubleClickSize;
            bool doubleClick = _dragClickPending && elapsed >= 0 &&
                elapsed <= SystemInformation.DoubleClickTime &&
                Math.Abs(current.X - _lastDragClickPosition.X) <= Math.Max(1, tolerance.Width / 2) &&
                Math.Abs(current.Y - _lastDragClickPosition.Y) <= Math.Max(1, tolerance.Height / 2);

            _lastDragClickTick = now;
            _lastDragClickPosition = current;
            _dragClickPending = !doubleClick;
            if (!doubleClick)
                return false;

            CloseOpacityPopup();
            ToggleSuperExpanded();
            return true;
        }

        private void BackgroundHitMouseDown(object sender, MouseEventArgs e)
        {
            if (_pinned)
                return;
            if (e.Button == MouseButtons.Middle)
            {
                ToggleOpacityPopup();
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;

            Point point = PointToClient(Cursor.Position);
            foreach (Button button in _headerButtons)
            {
                if (button.Visible && button.Enabled && button.Bounds.Contains(point))
                {
                    button.PerformClick();
                    return;
                }
            }

            int hitTest = GetResizeHitTest(point, 7);
            if (hitTest != 1)
                BeginResize(hitTest);
            else
                DragWindow(this, e);
        }

        private void SyncBackgroundHitForm()
        {
            if (_backgroundHitForm == null || _backgroundHitForm.IsDisposed)
                return;

            bool shouldShow = _backgroundless && !_pinned && !_backgroundHitSuspended &&
                Visible && WindowState != FormWindowState.Minimized;
            if (!shouldShow)
            {
                if (_backgroundHitForm.Visible)
                    _backgroundHitForm.Hide();
                return;
            }

            _backgroundHitForm.TopMost = TopMost;
            if (!_backgroundHitForm.Visible)
            {
                _backgroundHitForm.Bounds = Bounds;
                _backgroundHitForm.Show();
            }
            NativeUi.SetWindowPos(_backgroundHitForm.Handle, Handle,
                Left, Top, Width, Height, 0x0010);
        }

        private void SuspendBackgroundHitForm()
        {
            _backgroundHitSuspended = true;
            if (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                _backgroundHitForm.Visible)
                _backgroundHitForm.Hide();
        }

        private void ResumeBackgroundHitForm()
        {
            _backgroundHitSuspended = false;
            SyncBackgroundHitForm();
        }

        private void ToggleOpacityWithMiddleMouse(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
                ToggleOpacityPopup();
        }

        private void AssignDrag(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                if (!(control is Button) && !(control is CheckBox) && !(control is TrackBar) &&
                    !(control is SlimOpacitySlider) &&
                    !(control is ResizeGripControl) && !(control is ResourceSummaryControl))
                    control.MouseDown += DragWindow;
                if (control.HasChildren)
                    AssignDrag(control.Controls);
            }
        }

        private void AssignMiddleOpacityToggle(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                control.MouseDown += ToggleOpacityWithMiddleMouse;
                if (control.HasChildren)
                    AssignMiddleOpacityToggle(control.Controls);
            }
        }

        private void AssignContextMenu(Control.ControlCollection controls, ContextMenuStrip menu)
        {
            foreach (Control control in controls)
            {
                control.ContextMenuStrip = menu;
                if (control.HasChildren)
                    AssignContextMenu(control.Controls, menu);
            }
        }

        private void ApplyRoundedCorners()
        {
            IntPtr regionHandle = NativeUi.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 16, 16);
            Region oldRegion = Region;
            Region = Region.FromHrgn(regionHandle);
            NativeUi.DeleteObject(regionHandle);
            if (oldRegion != null)
                oldRegion.Dispose();
        }

        private void ClearRoundedCorners()
        {
            Region oldRegion = Region;
            Region = null;
            if (oldRegion != null)
                oldRegion.Dispose();
        }

        private void MoveToDefaultPosition()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Top + 16);
        }

        private void EnsureWindowVisible()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        private void LoadSettings()
        {
            _loadingSettings = true;
            bool positioned = false;
            int opacityPercent = 90;
            bool expanded = false;
            bool superExpanded = false;
            bool backgroundless = false;
            bool pinned = false;
            bool headerManuallyHidden = false;
            bool headerAutomaticallyHidden = false;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppRegistryPath))
            {
                if (key != null)
                {
                    object xValue = key.GetValue("X");
                    object yValue = key.GetValue("Y");
                    if (xValue is int && yValue is int)
                    {
                        Point point = new Point((int)xValue, (int)yValue);
                        Rectangle proposed = new Rectangle(point, Size);
                        if (Screen.AllScreens.Any(delegate(Screen screen) { return screen.WorkingArea.IntersectsWith(proposed); }))
                        {
                            Location = point;
                            positioned = true;
                        }
                    }
                    TopMost = Convert.ToInt32(key.GetValue("TopMost", 1), CultureInfo.InvariantCulture) != 0;
                    opacityPercent = Convert.ToInt32(key.GetValue("Opacity", 90), CultureInfo.InvariantCulture);
                    expanded = Convert.ToInt32(key.GetValue("Expanded", 0), CultureInfo.InvariantCulture) != 0;
                    superExpanded = Convert.ToInt32(key.GetValue("SuperExpanded", 0), CultureInfo.InvariantCulture) != 0;
                    backgroundless = Convert.ToInt32(key.GetValue("Backgroundless", 0), CultureInfo.InvariantCulture) != 0;
                    pinned = Convert.ToInt32(key.GetValue("Pinned", 0), CultureInfo.InvariantCulture) != 0;
                    headerManuallyHidden = Convert.ToInt32(
                        key.GetValue("HeaderManuallyHidden", 0), CultureInfo.InvariantCulture) != 0;
                    headerAutomaticallyHidden = Convert.ToInt32(
                        key.GetValue("HeaderAutomaticallyHidden", 0), CultureInfo.InvariantCulture) != 0;
                    _selectedStorageDrive = Convert.ToString(key.GetValue("StorageDrive", String.Empty), CultureInfo.InvariantCulture) ?? String.Empty;
                    _compactSlotKinds = ParseCompactSlotKinds(Convert.ToString(
                        key.GetValue("CompactSlotsV1", SerializeCompactSlotKinds(CreateSystemCompactPreset())),
                        CultureInfo.InvariantCulture));
                    _compactSize = new Size(
                        Math.Max(MinimumCompactWidth, Convert.ToInt32(key.GetValue("CompactWidth", WindowWidth), CultureInfo.InvariantCulture)),
                        Math.Max(HeaderlessCompactMinimumHeight,
                            Convert.ToInt32(key.GetValue("CompactHeight", CompactHeight), CultureInfo.InvariantCulture)));
                    int storedExpandedHeight = Convert.ToInt32(
                        key.GetValue("ExpandedHeight", ExpandedHeight), CultureInfo.InvariantCulture);
                    // Migrate the former defaults that reserved a separate
                    // opacity row to the clipped, continuous card layout.
                    if (storedExpandedHeight == 444 || storedExpandedHeight == 396)
                        storedExpandedHeight = ExpandedHeight;
                    _expandedSize = new Size(
                        Math.Max(220, Convert.ToInt32(key.GetValue("ExpandedWidth", WindowWidth), CultureInfo.InvariantCulture)),
                        Math.Max(278, storedExpandedHeight));
                    _superExpandedSize = new Size(
                        Math.Max(SuperExpandedWidth, Convert.ToInt32(key.GetValue("SuperWidth", SuperExpandedWidth), CultureInfo.InvariantCulture)),
                        Math.Max(SuperExpandedHeight, Convert.ToInt32(key.GetValue("SuperHeight", SuperExpandedHeight), CultureInfo.InvariantCulture)));
                    object compactXValue = key.GetValue("CompactX");
                    object compactYValue = key.GetValue("CompactY");
                    if (compactXValue is int && compactYValue is int)
                    {
                        Point compactPoint = new Point((int)compactXValue, (int)compactYValue);
                        Rectangle compactBounds = new Rectangle(compactPoint, _compactSize);
                        if (Screen.AllScreens.Any(delegate(Screen screen) { return screen.WorkingArea.IntersectsWith(compactBounds); }))
                        {
                            _compactLocation = compactPoint;
                            _compactLocationKnown = true;
                        }
                    }
                    bool storedReturnKnown = Convert.ToInt32(
                        key.GetValue("SuperReturnKnown", 0), CultureInfo.InvariantCulture) != 0;
                    if (storedReturnKnown)
                    {
                        Point returnPoint = new Point(
                            Convert.ToInt32(key.GetValue("SuperReturnX", Location.X), CultureInfo.InvariantCulture),
                            Convert.ToInt32(key.GetValue("SuperReturnY", Location.Y), CultureInfo.InvariantCulture));
                        Size returnSize = new Size(
                            Math.Max(MinimumCompactWidth, Convert.ToInt32(
                                key.GetValue("SuperReturnWidth", _compactSize.Width), CultureInfo.InvariantCulture)),
                            Math.Max(HeaderlessCompactMinimumHeight, Convert.ToInt32(
                                key.GetValue("SuperReturnHeight", _compactSize.Height), CultureInfo.InvariantCulture)));
                        Rectangle returnBounds = new Rectangle(returnPoint, returnSize);
                        if (Screen.AllScreens.Any(delegate(Screen screen)
                            {
                                return screen.WorkingArea.IntersectsWith(returnBounds);
                            }))
                        {
                            _superReturnStateKnown = true;
                            _superReturnExpanded = Convert.ToInt32(
                                key.GetValue("SuperReturnExpanded", 0), CultureInfo.InvariantCulture) != 0;
                            _superReturnSize = returnSize;
                            _superReturnLocation = returnPoint;
                        }
                    }
                }
                else
                {
                    TopMost = true;
                }
            }

            if (!positioned)
                MoveToDefaultPosition();
            if (!expanded && _compactLocationKnown)
                Location = _compactLocation;
            if (!_compactLocationKnown)
            {
                _compactLocation = Location;
                _compactLocationKnown = true;
            }
            _topMostItem.Checked = TopMost;
            _startupItem.Checked = IsStartupEnabled();
            SetOpacityPercent(opacityPercent, false);
            _headerManuallyHidden = headerManuallyHidden;
            _restoredAutomaticHeaderHidden = headerAutomaticallyHidden &&
                !headerManuallyHidden;
            SetExpanded(expanded, false);
            if (expanded && superExpanded)
                SetSuperExpanded(true, false);
            ApplyBackgroundMode(backgroundless, false);
            ApplyPinnedMode(pinned, false);
            _loadingSettings = false;
            UpdateCompactCycleTooltip();
            RenderCompactCards(null, false);
        }

        private void SaveSettings()
        {
            if (_loadingSettings)
                return;
            RememberCurrentSize();
            if (!_expanded)
            {
                _compactLocation = Location;
                _compactLocationKnown = true;
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppRegistryPath))
            {
                key.SetValue("X", Left, RegistryValueKind.DWord);
                key.SetValue("Y", Top, RegistryValueKind.DWord);
                key.SetValue("TopMost", TopMost ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Opacity", (int)Math.Round(Opacity * 100), RegistryValueKind.DWord);
                key.SetValue("Expanded", _expanded ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("SuperExpanded", _superExpanded ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Backgroundless", _backgroundless ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Pinned", _pinned ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("HeaderManuallyHidden", _headerManuallyHidden ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("HeaderAutomaticallyHidden",
                    !_headerManuallyHidden && IsHeaderHidden() ? 1 : 0,
                    RegistryValueKind.DWord);
                key.SetValue("StorageDrive", _selectedStorageDrive ?? String.Empty, RegistryValueKind.String);
                key.SetValue("CompactSlotsV1", SerializeCompactSlotKinds(_compactSlotKinds), RegistryValueKind.String);
                key.SetValue("CompactWidth", _compactSize.Width, RegistryValueKind.DWord);
                key.SetValue("CompactHeight", _compactSize.Height, RegistryValueKind.DWord);
                key.SetValue("ExpandedWidth", _expandedSize.Width, RegistryValueKind.DWord);
                key.SetValue("ExpandedHeight", _expandedSize.Height, RegistryValueKind.DWord);
                key.SetValue("SuperWidth", _superExpandedSize.Width, RegistryValueKind.DWord);
                key.SetValue("SuperHeight", _superExpandedSize.Height, RegistryValueKind.DWord);
                key.SetValue("SuperReturnKnown", _superReturnStateKnown ? 1 : 0, RegistryValueKind.DWord);
                if (_superReturnStateKnown)
                {
                    key.SetValue("SuperReturnExpanded", _superReturnExpanded ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("SuperReturnWidth", _superReturnSize.Width, RegistryValueKind.DWord);
                    key.SetValue("SuperReturnHeight", _superReturnSize.Height, RegistryValueKind.DWord);
                    key.SetValue("SuperReturnX", _superReturnLocation.X, RegistryValueKind.DWord);
                    key.SetValue("SuperReturnY", _superReturnLocation.Y, RegistryValueKind.DWord);
                }
                if (_compactLocationKnown)
                {
                    key.SetValue("CompactX", _compactLocation.X, RegistryValueKind.DWord);
                    key.SetValue("CompactY", _compactLocation.Y, RegistryValueKind.DWord);
                }
            }
        }

        private static bool IsStartupEnabled()
        {
            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return runKey != null && runKey.GetValue(StartupValueName) != null;
        }

        private void SetStartup(bool enabled)
        {
            using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled)
                    runKey.SetValue(StartupValueName, "\"" + Application.ExecutablePath + "\"");
                else
                    runKey.DeleteValue(StartupValueName, false);
            }
            _startupItem.Checked = enabled;
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            _stopping = true;
            Application.RemoveMessageFilter(this);
            if (_worker != null && _worker.IsAlive)
                _worker.Join(2000);
            try
            {
                SaveSettings();
            }
            catch { }
            finally
            {
                if (_backgroundHitForm != null)
                {
                    _backgroundHitForm.Hide();
                    _backgroundHitForm.Dispose();
                }
                _opacityCard.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _storageMenu.Dispose();
                _tips.Dispose();
            }
        }
    }

    internal sealed class HeaderButton : Button
    {
        public HeaderButton()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
        }

        protected override bool ShowFocusCues
        {
            get { return false; }
        }

        public override void NotifyDefault(bool value)
        {
            base.NotifyDefault(false);
        }
    }

    internal sealed class BackgroundHitForm : Form
    {
        public BackgroundHitForm()
        {
            Text = String.Empty;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Opacity = 0.01;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int ToolWindow = 0x00000080;
                const int AppWindow = 0x00040000;
                const int NoActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= ToolWindow | NoActivate;
                parameters.ExStyle &= ~AppWindow;
                return parameters;
            }
        }
    }

    internal sealed class OpacityPopupForm : Form
    {
        public OpacityPopupForm()
        {
            Text = "Прозрачность";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(29, 33, 40);
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int ToolWindow = 0x00000080;
                const int AppWindow = 0x00040000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= ToolWindow;
                parameters.ExStyle &= ~AppWindow;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(Color.FromArgb(58, 66, 78)))
                e.Graphics.DrawRectangle(border, 0, 0,
                    Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
        }
    }

    internal sealed class RingGauge : Control
    {
        private readonly string _title;
        private double _progress;
        private string _value = "—";
        private string _detail = "ОЖИДАНИЕ";
        private Color _accent = Color.FromArgb(73, 190, 198);
        private readonly string[] _auxiliaryValues = { String.Empty, String.Empty, String.Empty };
        private readonly string[] _auxiliaryLabels = { String.Empty, String.Empty, String.Empty };

        public RingGauge(string title)
        {
            _title = title;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public void SetData(double progress, string value, string detail, Color accent)
        {
            _progress = Math.Max(0, Math.Min(1, progress));
            _value = value ?? "—";
            _detail = detail ?? String.Empty;
            _accent = accent;
            Invalidate();
        }

        public void SetAuxiliary(string value1, string label1, string value2, string label2, string value3, string label3)
        {
            _auxiliaryValues[0] = value1 ?? String.Empty;
            _auxiliaryLabels[0] = label1 ?? String.Empty;
            _auxiliaryValues[1] = value2 ?? String.Empty;
            _auxiliaryLabels[1] = label2 ?? String.Empty;
            _auxiliaryValues[2] = value3 ?? String.Empty;
            _auxiliaryLabels[2] = label3 ?? String.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Base the inset on the shortest side.  Using the full width made
            // the gauge collapse into a tiny dot in wide, shallow layouts.
            int padding = Math.Max(8, Math.Min(18, Math.Min(Width, Height) / 12));
            int titleHeight = Math.Max(18, Height / 9);
            int proposedGaugeWidth = Math.Min(Height, Math.Max(72, (int)(Width * 0.52F)));
            float proposedAuxiliaryWidth = Width - proposedGaugeWidth - 12;
            float auxiliaryValueSize = Math.Max(8.5F, Height / 17F);
            bool showAuxiliaryValues = Width >= 175 && proposedAuxiliaryWidth >= 68;
            if (showAuxiliaryValues)
            {
                while (auxiliaryValueSize > 7F)
                {
                    using (Font probe = new Font("Segoe UI", auxiliaryValueSize, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        bool fits = _auxiliaryValues.All(delegate(string text)
                        {
                            return TextRenderer.MeasureText(text ?? String.Empty, probe,
                                new Size(Int32.MaxValue, Int32.MaxValue),
                                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                TextFormatFlags.SingleLine).Width <= proposedAuxiliaryWidth;
                        });
                        if (fits)
                            break;
                    }
                    auxiliaryValueSize -= 0.5F;
                }
                if (auxiliaryValueSize <= 7F)
                {
                    using (Font probe = new Font("Segoe UI", 7F, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        showAuxiliaryValues = _auxiliaryValues.All(delegate(string text)
                        {
                            return TextRenderer.MeasureText(text ?? String.Empty, probe,
                                new Size(Int32.MaxValue, Int32.MaxValue),
                                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                                TextFormatFlags.SingleLine).Width <= proposedAuxiliaryWidth;
                        });
                    }
                }
            }
            bool showAuxiliaryLabels = showAuxiliaryValues;
            int gaugeWidth = showAuxiliaryValues ? proposedGaugeWidth : Width;
            int diameter = Math.Max(20, Math.Min(gaugeWidth - padding * 2, Height - padding * 2 - titleHeight));
            Rectangle arc = new Rectangle((gaugeWidth - diameter) / 2, titleHeight + padding / 2, diameter, diameter);
            float penWidth = Math.Max(5F, diameter / 18F);

            using (Pen backgroundPen = new Pen(Color.FromArgb(45, 51, 61), penWidth))
            using (Pen valuePen = new Pen(_accent, penWidth))
            {
                backgroundPen.StartCap = backgroundPen.EndCap = LineCap.Round;
                valuePen.StartCap = valuePen.EndCap = LineCap.Round;
                e.Graphics.DrawArc(backgroundPen, arc, 135, 270);
                if (_progress > 0)
                    e.Graphics.DrawArc(valuePen, arc, 135, (float)(270 * _progress));
            }

            using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (StringFormat left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            using (Font titleFont = new Font("Segoe UI", Math.Max(7F, Height / 19F), FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", Math.Max(13F, diameter / 8F), FontStyle.Bold, GraphicsUnit.Point))
            using (Font detailFont = new Font("Segoe UI", Math.Max(6.5F, diameter / 23F), FontStyle.Bold, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush valueBrush = new SolidBrush(Color.White))
            using (Brush detailBrush = new SolidBrush(Color.FromArgb(145, 155, 168)))
            {
                e.Graphics.DrawString(_title, titleFont, titleBrush,
                    new RectangleF(10, 0, Math.Max(1, Width - 20), titleHeight), left);
                e.Graphics.DrawString(_value, valueFont, valueBrush,
                    new RectangleF(arc.Left, arc.Top + diameter * 0.27F, diameter, diameter * 0.30F), centered);
                e.Graphics.DrawString(_detail, detailFont, detailBrush,
                    new RectangleF(arc.Left + 4, arc.Top + diameter * 0.57F, diameter - 8, diameter * 0.22F), centered);

                if (showAuxiliaryValues)
                {
                    float auxiliaryLeft = gaugeWidth + 4;
                    float auxiliaryWidth = Width - auxiliaryLeft - 8;
                    float auxiliaryLabelSize = Math.Max(5.3F,
                        Math.Min(7F, proposedAuxiliaryWidth / 14F));
                    using (Font auxiliaryValueFont = new Font("Segoe UI", auxiliaryValueSize, FontStyle.Bold, GraphicsUnit.Point))
                    using (Font auxiliaryLabelFont = new Font("Segoe UI", auxiliaryLabelSize, FontStyle.Regular, GraphicsUnit.Point))
                    {
                        bool horizontalRows = Height < 135 && auxiliaryWidth >= 210;
                        if (horizontalRows)
                        {
                            float columnWidth = auxiliaryWidth / 3F;
                            float valueTop = titleHeight + Math.Max(5, (Height - titleHeight) * 0.15F);
                            float valueHeight = Math.Max(20, (Height - titleHeight) * 0.38F);
                            for (int index = 0; index < 3; index++)
                            {
                                float columnLeft = auxiliaryLeft + index * columnWidth;
                                e.Graphics.DrawString(_auxiliaryValues[index], auxiliaryValueFont, valueBrush,
                                    new RectangleF(columnLeft, valueTop, columnWidth - 5, valueHeight), left);
                                if (showAuxiliaryLabels)
                                    e.Graphics.DrawString(_auxiliaryLabels[index], auxiliaryLabelFont, detailBrush,
                                        new RectangleF(columnLeft, valueTop + valueHeight * 0.72F, columnWidth - 5,
                                            Height - valueTop - valueHeight * 0.72F - 3), left);
                            }
                        }
                        else
                        {
                            float rowHeight = (Height - titleHeight - 8) / 3F;
                            for (int index = 0; index < 3; index++)
                            {
                                float rowTop = titleHeight + 2 + index * rowHeight;
                                e.Graphics.DrawString(_auxiliaryValues[index], auxiliaryValueFont, valueBrush,
                                    new RectangleF(auxiliaryLeft, rowTop, auxiliaryWidth,
                                        showAuxiliaryLabels ? rowHeight * 0.58F : rowHeight * 0.90F), left);
                                if (showAuxiliaryLabels)
                                    e.Graphics.DrawString(_auxiliaryLabels[index], auxiliaryLabelFont, detailBrush,
                                        new RectangleF(auxiliaryLeft, rowTop + rowHeight * 0.48F, auxiliaryWidth, rowHeight * 0.42F), left);
                            }
                        }
                    }
                }
                else
                {
                    float stackedTop = arc.Bottom + 3;
                    float stackedHeight = Height - stackedTop - 3;
                    if (Width >= 52 && stackedHeight >= 42)
                    {
                        float rowHeight = stackedHeight / 3F;
                        float stackedValueSize = Math.Max(6F, Math.Min(9F,
                            Math.Min(Width / 10.5F, rowHeight / 1.65F)));
                        using (Font stackedValueFont = new Font("Segoe UI", stackedValueSize,
                            FontStyle.Bold, GraphicsUnit.Point))
                        {
                            for (int index = 0; index < 3; index++)
                            {
                                float rowTop = stackedTop + index * rowHeight;
                                e.Graphics.DrawString(_auxiliaryValues[index], stackedValueFont, valueBrush,
                                    new RectangleF(2, rowTop, Math.Max(1, Width - 4), rowHeight), centered);
                            }
                        }
                    }
                }
            }
        }
    }

    internal sealed class SensorHistoryControl : Control
    {
        private const int MaximumSamples = 300;
        private readonly string _title;
        private readonly Color _loadColor;
        private readonly List<float> _temperatures = new List<float>();
        private readonly List<float> _loads = new List<float>();

        public SensorHistoryControl(string title, Color loadColor)
        {
            _title = title ?? String.Empty;
            _loadColor = loadColor;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public void AddSample(double temperature, double load)
        {
            if (_temperatures.Count >= MaximumSamples)
            {
                _temperatures.RemoveAt(0);
                _loads.RemoveAt(0);
            }

            _temperatures.Add(temperature > 0
                ? (float)Math.Max(0, Math.Min(120, temperature))
                : Single.NaN);
            _loads.Add((float)Math.Max(0, Math.Min(100, load)));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(49, 55, 65)))
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            bool fullHeader = Width >= 300 && Height >= 115;
            int headerHeight = fullHeader ? 42 : 24;
            RectangleF plotArea = new RectangleF(10, headerHeight,
                Math.Max(1, Width - 44), Math.Max(1, Height - headerHeight - 8));
            Color temperatureColor = Color.FromArgb(255, 183, 77);
            Color loadColor = _loadColor;

            float minimum;
            float average;
            float maximum;
            float minimumLoad;
            float averageLoad;
            float maximumLoad;
            bool hasTemperature = TryGetStatistics(out minimum, out average, out maximum,
                out minimumLoad, out averageLoad, out maximumLoad);
            string compactHistoryValues = hasTemperature
                ? minimum.ToString("0", CultureInfo.InvariantCulture) + "° · " +
                  average.ToString("0", CultureInfo.InvariantCulture) + "° · " +
                  maximum.ToString("0", CultureInfo.InvariantCulture) + "°"
                : "—° · —° · —°";

            using (Font titleFont = new Font("Segoe UI", 7.4F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font statsFont = new Font("Segoe UI", 6.7F, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush statsBrush = new SolidBrush(Color.FromArgb(175, 183, 194)))
            using (StringFormat near = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            using (StringFormat far = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                e.Graphics.DrawString(fullHeader
                        ? "ИСТОРИЯ " + _title
                        : _title + " · " + compactHistoryValues,
                    titleFont, titleBrush, new RectangleF(10, 2, Math.Max(1, Width - 20), 18), near);

                if (fullHeader && hasTemperature)
                {
                    int secondColumn = Math.Max(104, Width / 2);
                    e.Graphics.DrawString("МИН · СРЕД · МАКС", statsFont, statsBrush,
                        new RectangleF(110, 2, Math.Max(1, Width - 120), 18), far);
                    e.Graphics.DrawString("ТЕМП. " + minimum.ToString("0", CultureInfo.InvariantCulture) + " · " +
                            average.ToString("0", CultureInfo.InvariantCulture) + " · " +
                            maximum.ToString("0", CultureInfo.InvariantCulture) + "°",
                        statsFont, statsBrush, new RectangleF(10, 19, Math.Max(1, secondColumn - 14), 20), near);
                    e.Graphics.DrawString("НАГР. " + minimumLoad.ToString("0", CultureInfo.InvariantCulture) + " · " +
                            averageLoad.ToString("0", CultureInfo.InvariantCulture) + " · " +
                            maximumLoad.ToString("0", CultureInfo.InvariantCulture) + "%",
                        statsFont, statsBrush, new RectangleF(secondColumn, 19,
                            Math.Max(1, Width - secondColumn - 10), 20), near);
                }
            }

            float chartGap = fullHeader ? 16F : 10F;
            float chartHeight = Math.Max(1, (plotArea.Height - chartGap) / 2F);
            RectangleF temperatureGraph = new RectangleF(plotArea.Left, plotArea.Top,
                plotArea.Width, chartHeight);
            RectangleF loadGraph = new RectangleF(plotArea.Left,
                plotArea.Top + chartHeight + chartGap, plotArea.Width, chartHeight);

            float temperatureScaleMinimum = 20;
            float temperatureScaleMaximum = 100;
            if (hasTemperature)
                CalculateTemperatureScale(minimum, maximum,
                    out temperatureScaleMinimum, out temperatureScaleMaximum);
            DrawScale(e.Graphics, temperatureGraph, temperatureScaleMinimum,
                temperatureScaleMaximum, "°");
            DrawScale(e.Graphics, loadGraph, 0, 100, "%");

            if (_loads.Count <= 1)
            {
                using (Font emptyFont = new Font("Segoe UI", 7F, FontStyle.Regular, GraphicsUnit.Point))
                using (Brush emptyBrush = new SolidBrush(Color.FromArgb(95, 105, 118)))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    e.Graphics.DrawString("НАКАПЛИВАЕМ ИСТОРИЮ…", emptyFont, emptyBrush, plotArea, centered);
            }
            else
            {
                DrawSeries(e.Graphics, temperatureGraph, _temperatures, temperatureColor,
                    temperatureScaleMinimum, temperatureScaleMaximum);
                DrawSeries(e.Graphics, loadGraph, _loads, loadColor, 0, 100);
            }

            DrawGraphLabel(e.Graphics, temperatureGraph,
                fullHeader ? "ТЕМПЕРАТУРА, °C" : "ТЕМП., °C", temperatureColor);
            DrawGraphLabel(e.Graphics, loadGraph,
                fullHeader ? "НАГРУЗКА, %" : "НАГР., %", loadColor);
        }

        private static void DrawGraphLabel(Graphics graphics, RectangleF bounds,
            string text, Color color)
        {
            using (Font font = new Font("Segoe UI", 6.8F, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush textBrush = new SolidBrush(color))
            using (Brush backdrop = new SolidBrush(Color.FromArgb(225, 18, 22, 27)))
            {
                SizeF measured = graphics.MeasureString(text, font);
                RectangleF labelBounds = new RectangleF(bounds.Left + 3, bounds.Top + 3,
                    Math.Min(bounds.Width - 6, measured.Width + 15), measured.Height + 3);
                if (labelBounds.Width <= 8 || labelBounds.Height <= 3)
                    return;
                graphics.FillRectangle(backdrop, labelBounds);
                graphics.FillEllipse(textBrush, labelBounds.Left + 3,
                    labelBounds.Top + labelBounds.Height / 2F - 2.5F, 5, 5);
                graphics.DrawString(text, font, textBrush,
                    labelBounds.Left + 11, labelBounds.Top + 1);
            }
        }

        private bool TryGetStatistics(out float minimum, out float average,
            out float maximum, out float minimumLoad, out float averageLoad,
            out float maximumLoad)
        {
            minimum = Single.MaxValue;
            maximum = Single.MinValue;
            float sum = 0;
            int count = 0;
            foreach (float value in _temperatures)
            {
                if (Single.IsNaN(value))
                    continue;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                sum += value;
                count++;
            }

            minimumLoad = _loads.Count > 0 ? _loads.Min() : 0;
            averageLoad = _loads.Count > 0 ? (float)_loads.Average() : 0;
            maximumLoad = _loads.Count > 0 ? _loads.Max() : 0;
            if (count == 0)
            {
                average = 0;
                return false;
            }
            average = sum / count;
            return true;
        }

        private static void CalculateTemperatureScale(float minimum, float maximum,
            out float scaleMinimum, out float scaleMaximum)
        {
            scaleMinimum = Math.Max(0, (float)Math.Floor((minimum - 5) / 5F) * 5F);
            scaleMaximum = Math.Min(120, (float)Math.Ceiling((maximum + 5) / 5F) * 5F);
            if (scaleMaximum - scaleMinimum < 10)
            {
                scaleMinimum = Math.Max(0, scaleMinimum - 5);
                scaleMaximum = Math.Min(120, scaleMaximum + 5);
            }
        }

        private static void DrawScale(Graphics graphics, RectangleF bounds,
            float minimum, float maximum, string suffix)
        {
            using (Pen grid = new Pen(Color.FromArgb(37, 43, 52), 1F))
            using (Font scaleFont = new Font("Segoe UI", 6.3F, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush scaleBrush = new SolidBrush(Color.FromArgb(100, 110, 124)))
            {
                for (int row = 0; row <= 2; row++)
                {
                    float y = bounds.Top + bounds.Height * row / 2F;
                    float value = maximum - (maximum - minimum) * row / 2F;
                    graphics.DrawLine(grid, bounds.Left, y, bounds.Right, y);
                    graphics.DrawString(value.ToString("0", CultureInfo.InvariantCulture) + suffix,
                        scaleFont, scaleBrush, bounds.Right + 4, y - 7);
                }
            }
        }

        private static void DrawSeries(Graphics graphics, RectangleF bounds,
            IList<float> values, Color color, float scaleMinimum, float scaleMaximum)
        {
            if (values == null || values.Count < 2)
                return;
            float step = bounds.Width / Math.Max(1, values.Count - 1);
            using (Pen pen = new Pen(color, 1.8F))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                for (int index = 1; index < values.Count; index++)
                {
                    float previous = values[index - 1];
                    float current = values[index];
                    if (Single.IsNaN(previous) || Single.IsNaN(current))
                        continue;
                    previous = Math.Max(scaleMinimum, Math.Min(scaleMaximum, previous));
                    current = Math.Max(scaleMinimum, Math.Min(scaleMaximum, current));
                    float range = Math.Max(1, scaleMaximum - scaleMinimum);
                    PointF from = new PointF(bounds.Left + (index - 1) * step,
                        bounds.Bottom - bounds.Height * (previous - scaleMinimum) / range);
                    PointF to = new PointF(bounds.Left + index * step,
                        bounds.Bottom - bounds.Height * (current - scaleMinimum) / range);
                    graphics.DrawLine(pen, from, to);
                }
            }
        }
    }

    internal sealed class ResourceSummaryControl : Control
    {
        private string _title;
        private readonly bool _networkMode;
        private double _progress;
        private string _primary = "—";
        private string _secondary = "ОЖИДАНИЕ";
        private string _download = "—";
        private string _upload = "—";
        private Color _accent = Color.FromArgb(73, 190, 198);

        public ResourceSummaryControl(string title, bool networkMode)
        {
            _title = title;
            _networkMode = networkMode;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public void SetUsage(double progress, string primary, string secondary, Color accent)
        {
            _progress = Math.Max(0, Math.Min(1, progress));
            _primary = primary ?? "—";
            _secondary = secondary ?? String.Empty;
            _accent = accent;
            Invalidate();
        }

        public void SetTitle(string title)
        {
            string next = title ?? String.Empty;
            if (_title == next)
                return;
            _title = next;
            Invalidate();
        }

        public void SetNetwork(string download, string upload)
        {
            _download = download ?? "—";
            _upload = upload ?? "—";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(49, 55, 65)))
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            if (!_networkMode && Height < 72)
            {
                DrawCompactUsage(e.Graphics);
                return;
            }

            float scale = Math.Max(0.68F,
                Math.Min(1.25F, Math.Min(Height / 110F, Width / 170F)));
            using (Font titleFont = new Font("Segoe UI", 7.5F * scale, FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", 10.5F * scale, FontStyle.Bold, GraphicsUnit.Point))
            using (Font detailFont = new Font("Segoe UI", 7F * scale, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush valueBrush = new SolidBrush(Color.White))
            using (Brush detailBrush = new SolidBrush(Color.FromArgb(145, 155, 168)))
            {
                e.Graphics.DrawString(_title, titleFont, titleBrush, 10, 7);
                if (_networkMode)
                {
                    float rowTop = Math.Max(28, Height * 0.34F);
                    float iconSize = Math.Max(13F, 17F * scale);
                    DrawTransferRow(e.Graphics, _download, false, rowTop, iconSize, valueFont, valueBrush);
                    DrawTransferRow(e.Graphics, _upload, true,
                        rowTop + Math.Max(23, Height * 0.25F), iconSize, valueFont, valueBrush);
                    return;
                }

                float barTop = Math.Max(34, Height * 0.42F);
                RectangleF bar = new RectangleF(12, barTop, Math.Max(10, Width - 24), Math.Max(10, Height * 0.16F));
                using (Brush barBackground = new SolidBrush(Color.FromArgb(32, 37, 45)))
                    e.Graphics.FillRectangle(barBackground, bar);
                RectangleF filled = new RectangleF(bar.X, bar.Y, (float)(bar.Width * _progress), bar.Height);
                using (Brush fill = new SolidBrush(Color.FromArgb(90, _accent)))
                    e.Graphics.FillRectangle(fill, filled);
                using (Pen outline = new Pen(_accent))
                    e.Graphics.DrawRectangle(outline, bar.X, bar.Y, bar.Width, bar.Height);

                float textTop = bar.Bottom + 4;
                e.Graphics.DrawString(_primary, valueFont, valueBrush, 12, textTop);
                e.Graphics.DrawString(_secondary, detailFont, detailBrush, 12, textTop + Math.Max(19, Height * 0.18F));
            }
        }

        private void DrawCompactUsage(Graphics graphics)
        {
            int barHeight = Math.Max(3, Math.Min(5, Height / 10));
            Rectangle bar = new Rectangle(10, Math.Max(1, Height - barHeight - 5),
                Math.Max(1, Width - 20), barHeight);
            float textScale = Math.Max(0.62F, Math.Min(1F, Width / 155F));
            using (Brush barBackground = new SolidBrush(Color.FromArgb(32, 37, 45)))
            using (Brush fill = new SolidBrush(Color.FromArgb(150, _accent)))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush valueBrush = new SolidBrush(Color.White))
            using (Brush detailBrush = new SolidBrush(Color.FromArgb(145, 155, 168)))
            using (Font titleFont = new Font("Segoe UI", Math.Max(5.2F, 7F * textScale), FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", Math.Max(6F, 9.5F * textScale), FontStyle.Bold, GraphicsUnit.Point))
            using (Font detailFont = new Font("Segoe UI", Math.Max(5.1F, 7F * textScale), FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat ellipsis = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = StringAlignment.Center
            })
            {
                graphics.FillRectangle(barBackground, bar);
                graphics.FillRectangle(fill, bar.X, bar.Y,
                    (int)Math.Round(bar.Width * _progress), bar.Height);

                if (Width >= 280)
                {
                    graphics.DrawString(_title, titleFont, titleBrush,
                        new RectangleF(10, 4, 72, Math.Max(16, Height - barHeight - 10)), ellipsis);
                    graphics.DrawString(_primary, valueFont, valueBrush,
                        new RectangleF(86, 3, Math.Max(80, Width * 0.38F), Math.Max(18, Height - barHeight - 9)), ellipsis);
                    graphics.DrawString(_secondary, detailFont, detailBrush,
                        new RectangleF(Width * 0.58F, 4, Math.Max(1, Width * 0.42F - 10),
                            Math.Max(16, Height - barHeight - 10)), ellipsis);
                }
                else
                {
                    bool showSecondary = Height >= 49 && !String.IsNullOrWhiteSpace(_secondary);
                    graphics.DrawString(_title, titleFont, titleBrush,
                        new RectangleF(10, 2, Math.Max(1, Width - 20), 15), ellipsis);
                    graphics.DrawString(_primary, valueFont, valueBrush,
                        new RectangleF(10, 16, Math.Max(1, Width - 20), showSecondary ? 20 : Math.Max(17, Height - barHeight - 21)), ellipsis);
                    if (showSecondary)
                        graphics.DrawString(_secondary, detailFont, detailBrush,
                            new RectangleF(10, 35, Math.Max(1, Width - 20),
                                Math.Max(12, Height - barHeight - 40)), ellipsis);
                }
            }
        }

        private static void DrawTransferRow(Graphics graphics, string value, bool upload, float top,
            float iconSize, Font font, Brush textBrush)
        {
            RectangleF circle = new RectangleF(12, top, iconSize, iconSize);
            Color iconColor = upload ? Color.FromArgb(73, 190, 132) : Color.FromArgb(66, 155, 215);
            using (Brush circleBrush = new SolidBrush(iconColor))
            using (Pen arrowPen = new Pen(Color.White, Math.Max(1.3F, iconSize / 10F)))
            {
                arrowPen.StartCap = LineCap.Round;
                arrowPen.EndCap = LineCap.Round;
                graphics.FillEllipse(circleBrush, circle);
                float centerX = circle.Left + circle.Width / 2F;
                float shaftTop = circle.Top + circle.Height * 0.25F;
                float shaftBottom = circle.Bottom - circle.Height * 0.25F;
                if (upload)
                {
                    graphics.DrawLine(arrowPen, centerX, shaftBottom, centerX, shaftTop);
                    graphics.DrawLine(arrowPen, centerX, shaftTop, centerX - circle.Width * 0.20F, shaftTop + circle.Height * 0.20F);
                    graphics.DrawLine(arrowPen, centerX, shaftTop, centerX + circle.Width * 0.20F, shaftTop + circle.Height * 0.20F);
                }
                else
                {
                    graphics.DrawLine(arrowPen, centerX, shaftTop, centerX, shaftBottom);
                    graphics.DrawLine(arrowPen, centerX, shaftBottom, centerX - circle.Width * 0.20F, shaftBottom - circle.Height * 0.20F);
                    graphics.DrawLine(arrowPen, centerX, shaftBottom, centerX + circle.Width * 0.20F, shaftBottom - circle.Height * 0.20F);
                }
            }
            graphics.DrawString(value ?? "—", font, textBrush,
                12 + iconSize + 7, top - Math.Max(0, (font.Height - iconSize) / 2F));
        }
    }

    internal sealed class FanSummaryControl : Control
    {
        private string[] _names = new string[0];
        private double[] _rpm = new double[0];
        private double[] _control = new double[0];

        public FanSummaryControl()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public bool HasFans
        {
            get { return _names.Length > 0; }
        }

        public bool SetFans(string[] names, double[] rpm, double[] control)
        {
            names = names ?? new string[0];
            rpm = rpm ?? new double[0];
            control = control ?? new double[0];
            int count = Math.Min(names.Length, Math.Min(rpm.Length, control.Length));
            bool availabilityChanged = HasFans != (count > 0);
            _names = names.Take(count).ToArray();
            _rpm = rpm.Take(count).ToArray();
            _control = control.Take(count).ToArray();
            Invalidate();
            return availabilityChanged;
        }

        public int GetPreferredHeight(int width)
        {
            int columns = ColumnCount(width, Math.Max(1, _names.Length));
            int rows = Math.Max(1, (int)Math.Ceiling(_names.Length / (double)columns));
            return 24 + rows * 34 + 5;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(49, 55, 65)))
                e.Graphics.DrawRectangle(border, 0, 0,
                    Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            if (_names.Length == 0)
                return;

            int columns = ColumnCount(Width, _names.Length);
            int rows = Math.Max(1, (int)Math.Ceiling(_names.Length / (double)columns));
            float columnWidth = Math.Max(1, (Width - 20F) / columns);
            float rowHeight = Math.Max(26, (Height - 24F) / rows);
            using (Font titleFont = new Font("Segoe UI", 7.3F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font nameFont = new Font("Segoe UI", 6.8F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", 9.2F, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush nameBrush = new SolidBrush(Color.FromArgb(120, 185, 194)))
            using (Brush valueBrush = new SolidBrush(Color.White))
            using (Pen divider = new Pen(Color.FromArgb(38, 44, 53)))
            using (StringFormat ellipsis = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                e.Graphics.DrawString("ВЕНТИЛЯТОРЫ", titleFont, titleBrush, 10, 5);
                for (int index = 0; index < _names.Length; index++)
                {
                    int column = index % columns;
                    int row = index / columns;
                    float left = 10 + column * columnWidth;
                    float top = 23 + row * rowHeight;
                    float itemWidth = columnWidth - 10;
                    if (column > 0)
                        e.Graphics.DrawLine(divider, left - 6, top + 1,
                            left - 6, Math.Min(Height - 5, top + rowHeight - 4));
                    e.Graphics.DrawString(_names[index], nameFont, nameBrush,
                        new RectangleF(left, top, itemWidth, 13), ellipsis);
                    string value = FormatFanValue(_rpm[index], _control[index]);
                    e.Graphics.DrawString(value, valueFont, valueBrush,
                        new RectangleF(left, top + 12, itemWidth, rowHeight - 12), ellipsis);
                }
            }
        }

        private static int ColumnCount(int width, int itemCount)
        {
            int maximum = width >= 660 ? 4 : width >= 430 ? 3 : width >= 250 ? 2 : 1;
            return Math.Max(1, Math.Min(itemCount, maximum));
        }

        private static string FormatFanValue(double rpm, double control)
        {
            string speed = rpm >= 0
                ? Math.Round(rpm).ToString("0", CultureInfo.InvariantCulture) + " RPM"
                : "— RPM";
            if (control >= 0)
                speed += "  ·  " + Math.Round(control).ToString("0", CultureInfo.InvariantCulture) + "%";
            return speed;
        }
    }

    internal sealed class CompactMetricColumn : Control
    {
        private string[] _values = new string[0];
        private string[] _captions = new string[0];
        private Color _accent = Color.FromArgb(150, 158, 169);
        private int _visibleMetricCount;

        public CompactMetricColumn()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public int MetricCount
        {
            get { return Math.Min(_values.Length, _captions.Length); }
        }

        public int VisibleMetricCount
        {
            get { return _visibleMetricCount; }
            set
            {
                int next = Math.Max(0, Math.Min(MetricCount, value));
                if (_visibleMetricCount == next)
                    return;
                _visibleMetricCount = next;
                Invalidate();
            }
        }

        public void SetMetrics(string[] values, string[] captions, Color accent)
        {
            _values = values ?? new string[0];
            _captions = captions ?? new string[0];
            _accent = accent;
            if (_visibleMetricCount <= 0 || _visibleMetricCount > MetricCount)
                _visibleMetricCount = MetricCount;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int count = Math.Min(MetricCount, _visibleMetricCount);
            if (count <= 0 || Width <= 2 || Height <= 2)
                return;

            float rowHeight = Height / (float)count;
            using (Brush valueBrush = new SolidBrush(_accent))
            using (Brush captionBrush = new SolidBrush(Color.FromArgb(116, 126, 140)))
            using (Pen separator = new Pen(Color.FromArgb(35, 42, 51)))
            {
                for (int index = 0; index < count; index++)
                {
                    float rowTop = index * rowHeight;
                    float blockHeight = Math.Min(62F, rowHeight - 2F);
                    float blockTop = rowTop + Math.Max(0, (rowHeight - blockHeight) / 2F);
                    float desiredValueSize = Math.Max(8.5F,
                        Math.Min(22F, 9F + (rowHeight - 34F) * 0.19F));
                    float valueSize = FitSingleLineFont(_values[index], desiredValueSize, 6F, Math.Max(1, Width));
                    float captionSize = Math.Max(5.2F,
                        Math.Min(8.5F, 5.8F + (rowHeight - 34F) * 0.055F));
                    using (Font valueFont = new Font("Segoe UI", valueSize, FontStyle.Bold, GraphicsUnit.Point))
                    using (Font captionFont = new Font("Segoe UI", captionSize, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        RectangleF valueBounds = new RectangleF(0, blockTop, Width, Math.Max(12F, blockHeight * 0.60F));
                        RectangleF captionBounds = new RectangleF(0, blockTop + blockHeight * 0.55F, Width,
                            Math.Max(8F, blockHeight * 0.43F));
                        e.Graphics.DrawString(_values[index], valueFont, valueBrush, valueBounds);
                        e.Graphics.DrawString(_captions[index], captionFont, captionBrush, captionBounds);
                    }
                    if (index < count - 1)
                        e.Graphics.DrawLine(separator, 0, rowTop + rowHeight - 1, Width, rowTop + rowHeight - 1);
                }
            }
        }

        private static float FitSingleLineFont(string text, float maximum, float minimum, int width)
        {
            float size = maximum;
            while (size > minimum)
            {
                using (Font font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point))
                {
                    Size measured = TextRenderer.MeasureText(text ?? String.Empty, font,
                        new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    if (measured.Width <= width)
                        break;
                }
                size -= 0.5F;
            }
            return Math.Max(minimum, size);
        }
    }

    internal sealed class SlimOpacitySlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _hovered;

        public SlimOpacitySlider()
        {
            Cursor = Cursors.Hand;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public event EventHandler ValueChanged;

        public int Minimum
        {
            get { return _minimum; }
            set
            {
                _minimum = Math.Min(value, _maximum - 1);
                Value = _value;
            }
        }

        public int Maximum
        {
            get { return _maximum; }
            set
            {
                _maximum = Math.Max(value, _minimum + 1);
                Value = _value;
            }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int next = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value == next)
                    return;
                _value = next;
                Invalidate();
                EventHandler handler = ValueChanged;
                if (handler != null)
                    handler(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            Capture = true;
            SetValueFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && (e.Button & MouseButtons.Left) != 0)
                SetValueFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Value += e.Delta > 0 ? 5 : -5;
            base.OnMouseWheel(e);
        }

        private void SetValueFromMouse(int x)
        {
            int left = 7;
            int width = Math.Max(1, ClientSize.Width - 14);
            double ratio = Math.Max(0.0, Math.Min(1.0, (x - left) / (double)width));
            Value = _minimum + (int)Math.Round((_maximum - _minimum) * ratio);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int left = 7;
            int right = Math.Max(left + 1, ClientSize.Width - 7);
            int centerY = ClientSize.Height / 2;
            double ratio = (_value - _minimum) / (double)Math.Max(1, _maximum - _minimum);
            int thumbX = left + (int)Math.Round((right - left) * ratio);
            Color inactive = Color.FromArgb(59, 68, 80);
            Color active = _hovered || Capture
                ? Color.FromArgb(83, 202, 209)
                : Color.FromArgb(73, 172, 181);
            using (Pen inactivePen = new Pen(inactive, 3F))
            using (Pen activePen = new Pen(active, 3F))
            using (Brush thumbBrush = new SolidBrush(active))
            {
                inactivePen.StartCap = inactivePen.EndCap = LineCap.Round;
                activePen.StartCap = activePen.EndCap = LineCap.Round;
                e.Graphics.DrawLine(inactivePen, left, centerY, right, centerY);
                e.Graphics.DrawLine(activePen, left, centerY, thumbX, centerY);
                int radius = _hovered || Capture ? 5 : 4;
                e.Graphics.FillEllipse(thumbBrush, thumbX - radius, centerY - radius,
                    radius * 2, radius * 2);
            }
        }
    }

    internal sealed class ExpandableStrip : Button
    {
        private bool _expanded;
        private bool _hovered;

        public ExpandableStrip()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public bool Expanded
        {
            get { return _expanded; }
            set
            {
                if (_expanded == value)
                    return;
                _expanded = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int pillWidth = Math.Max(56, Math.Min(180, Width / 3));
            int pillHeight = _hovered ? 5 : 4;
            int left = (Width - pillWidth) / 2;
            int top = (Height - pillHeight) / 2;
            Color color = !Enabled
                ? Color.FromArgb(48, 56, 66)
                : _expanded
                    ? Color.FromArgb(73, 190, 198)
                    : _hovered ? Color.FromArgb(108, 183, 194) : Color.FromArgb(70, 82, 96);
            using (Brush brush = new SolidBrush(color))
            {
                e.Graphics.FillRectangle(brush, left + pillHeight / 2, top,
                    Math.Max(1, pillWidth - pillHeight), pillHeight);
                e.Graphics.FillEllipse(brush, left, top, pillHeight, pillHeight);
                e.Graphics.FillEllipse(brush, left + pillWidth - pillHeight, top, pillHeight, pillHeight);
            }
        }
    }

    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    internal sealed class MetricReadout : Panel
    {
        private readonly Label _value;
        private readonly Label _caption;

        public MetricReadout(string caption)
        {
            BackColor = Color.Transparent;
            _value = new Label();
            _value.Text = "—";
            _value.Location = new Point(0, 0);
            _value.Size = new Size(84, 27);
            _value.Font = new Font("Segoe UI", 12.5F, FontStyle.Bold, GraphicsUnit.Point);
            _value.ForeColor = Color.FromArgb(150, 158, 169);
            _value.BackColor = Color.Transparent;
            _value.TextAlign = ContentAlignment.MiddleLeft;

            Label captionLabel = _caption = new Label();
            captionLabel.Text = caption;
            captionLabel.Location = new Point(0, 27);
            captionLabel.Size = new Size(84, 17);
            captionLabel.Font = new Font("Segoe UI", 6.5F, FontStyle.Bold, GraphicsUnit.Point);
            captionLabel.ForeColor = Color.FromArgb(112, 122, 136);
            captionLabel.BackColor = Color.Transparent;
            captionLabel.TextAlign = ContentAlignment.MiddleLeft;

            Controls.Add(_value);
            Controls.Add(captionLabel);
        }

        public void SetValue(string text, Color color)
        {
            _value.Text = text;
            _value.ForeColor = color;
        }

        public void SetCaption(string text)
        {
            _caption.Text = text ?? String.Empty;
        }

        public void SetScale(float scale)
        {
            SetControlFont(_value, Math.Max(11F, Math.Min(24F, 12.5F * scale)), FontStyle.Bold);
            SetControlFont(_caption, Math.Max(6F, Math.Min(8.5F, 6.5F * scale)), FontStyle.Bold);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            int valueHeight = Math.Max(27, Math.Min(Math.Max(27, Height - 15), (int)(Height * 0.64F)));
            _value.Size = new Size(Math.Max(1, Width), valueHeight);
            _caption.Location = new Point(0, valueHeight);
            _caption.Size = new Size(Math.Max(1, Width), Math.Max(1, Height - valueHeight));
        }

        private static void SetControlFont(Control control, float size, FontStyle style)
        {
            if (Math.Abs(control.Font.Size - size) < 0.15F && control.Font.Style == style)
                return;
            Font oldFont = control.Font;
            control.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            oldFont.Dispose();
        }
    }

    internal sealed class ResizeGripControl : Control
    {
        private readonly bool _leftOriented;
        private readonly bool _topOriented;

        public ResizeGripControl(bool leftOriented, bool topOriented)
        {
            _leftOriented = leftOriented;
            _topOriented = topOriented;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Cursor = topOriented || !leftOriented ? Cursors.SizeNWSE : Cursors.SizeNESW;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(130, 140, 153)))
            {
                if (_topOriented)
                {
                    e.Graphics.DrawLine(pen, 3, 0, 0, 3);
                    e.Graphics.DrawLine(pen, 7, 0, 0, 7);
                    e.Graphics.DrawLine(pen, 11, 0, 0, 11);
                }
                else if (_leftOriented)
                {
                    e.Graphics.DrawLine(pen, 3, Height - 1, 0, Height - 4);
                    e.Graphics.DrawLine(pen, 7, Height - 1, 0, Height - 8);
                    e.Graphics.DrawLine(pen, 11, Height - 1, 0, Height - 12);
                }
                else
                {
                    e.Graphics.DrawLine(pen, Width - 4, Height - 1, Width - 1, Height - 4);
                    e.Graphics.DrawLine(pen, Width - 8, Height - 1, Width - 1, Height - 8);
                    e.Graphics.DrawLine(pen, Width - 12, Height - 1, Width - 1, Height - 12);
                }
            }
        }
    }

    internal sealed class MonitorCard : Panel
    {
        private bool _backgroundless;
        private bool _borderVisible = true;

        public MonitorCard()
        {
            BackColor = Color.FromArgb(29, 33, 40);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public bool BorderVisible
        {
            get { return _borderVisible; }
            set
            {
                if (_borderVisible == value)
                    return;
                _borderVisible = value;
                Invalidate();
            }
        }

        public void SetBackgroundless(bool enabled, Color backgroundKey)
        {
            _backgroundless = enabled;
            BackColor = enabled ? backgroundKey : Color.FromArgb(29, 33, 40);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_backgroundless || !_borderVisible)
                return;
            using (Pen border = new Pen(Color.FromArgb(49, 55, 65)))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }
}
