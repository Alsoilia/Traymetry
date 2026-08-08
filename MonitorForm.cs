using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// What the top bar is allowed to do.  Automatic treats it as chrome - it
    /// belongs to the hand on the mouse, not to the readings - while the other
    /// two are standing decisions the window is not allowed to overrule.
    /// </summary>
    internal enum HeaderVisibilityMode
    {
        Automatic,
        AlwaysVisible,
        AlwaysHidden
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
        public TextReadout Caption;
        public TextReadout Value;
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

    internal sealed class CompactCardKindTag
    {
        public CompactCardKindTag(CompactCardKind kind)
        {
            Kind = kind;
        }

        public CompactCardKind Kind { get; private set; }
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

    /// <summary>
    /// Marks one of the two graph source submenus.  The caption is rebuilt with
    /// the current source appended, the way the card slots are, so the drop-down
    /// answers "what is on the left graph" without being opened.
    /// </summary>
    internal sealed class GraphSlotMenuTag
    {
        public GraphSlotMenuTag(bool left, string titleKey)
        {
            Left = left;
            TitleKey = titleKey;
        }

        public bool Left { get; private set; }
        public string TitleKey { get; private set; }
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
        // The corner grips are the only diagonal resize targets, so they are
        // deliberately larger than the 7 px straight-edge band around them.
        private const int GripSize = 20;
        private const int ResizeEdge = 7;
        // What the shape gives up along the bottom edge once the pointer
        // leaves.  Deliberately smaller than the reserve below: the cards keep
        // the same dark margin under them that they have at their sides, and
        // the widget does not end flush against the last row of readings.
        private const int ChromeBandHeight = 14;
        // The band along the bottom edge that only ever holds the expand strip.
        // The cards stop here.
        private const int ChromeBandReserve = 20;
        private const string AppRegistryPath = @"Software\Traymetry";
        private const string StartupValueName = "Traymetry";
        private static string OpacityTooltip
        {
            get { return Loc.T("tip.opacity"); }
        }
        private static readonly Color NormalBackground = Color.FromArgb(20, 23, 28);
        private static readonly Color BackgroundKey = Color.FromArgb(1, 2, 3);
        // Identity colours stay clear of the amber/red warning pair and are all
        // light enough to read once the window background is removed.  They are
        // only defaults: every card accent can be overridden from the menu.
        private static readonly Color CpuAccent = Color.FromArgb(87, 217, 139);
        private static readonly Color GpuAccent = Color.FromArgb(139, 124, 255);
        private static readonly Color MemoryAccent = Color.FromArgb(92, 170, 255);
        private static readonly Color NetworkAccent = Color.FromArgb(213, 219, 227);
        private static readonly Color StorageAccent = Color.FromArgb(182, 133, 255);
        private static readonly Color FansAccent = Color.FromArgb(73, 190, 198);
        private static readonly Color FpsAccent = Color.FromArgb(242, 107, 212);

        private readonly TextReadout _compactCpu;
        private readonly TextReadout _compactGpu;
        private readonly TextReadout _compactNetwork;
        private readonly TextReadout _compactMemory;
        private readonly CompactMetricColumn _compactCpuColumn;
        private readonly CompactMetricColumn _compactGpuColumn;
        private readonly CompactMetricColumn _compactNetworkColumn;
        private readonly CompactMetricColumn _compactMemoryColumn;
        private readonly CompactCardSlotView[] _compactSlots;
        private readonly TextReadout _title;
        private readonly TextReadout _cpuName;
        private readonly TextReadout _gpuName;
        private readonly TextReadout _gpuMemory;
        private readonly TextReadout _opacityLabel;
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
        private readonly Button _languageButton;
        private readonly Button _cycleButton;
        private readonly Button _pinButton;
        private readonly Button _expandButton;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _headerMenu;
        private readonly ToolStripMenuItem _pinItem;
        private readonly ToolStripMenuItem _topMostItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _backgroundItem;
        private readonly ToolStripMenuItem _streamHiddenItem;
        // Armed by pressing an entry that is meant to be clicked repeatedly, and
        // read by the Closing handlers of the menus it sits in.  See
        // KeepOpenOnHeldClick.
        private bool _keepMenuOpenAfterClick;
        private bool _catcherSyncDeferred;
        // Entries inside an otherwise sticky menu that still close it, because
        // they open a window of their own or hand the widget over to something
        // else.  Filled while the menu is built, read when it is made sticky.
        private readonly HashSet<ToolStripItem> _menuClosesOnClick = new HashSet<ToolStripItem>();
        private readonly List<ToolStripMenuItem> _opacityItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> _headerModeItems = new List<ToolStripMenuItem>();
        private readonly List<MonitorCard> _cards = new List<MonitorCard>();
        private readonly List<Button> _headerButtons = new List<Button>();
        private readonly ToolTip _tips = new ToolTip();
        private readonly ResizeGripControl _topLeftResizeGrip;
        private readonly ResizeGripControl _leftResizeGrip;
        private readonly ResizeGripControl _resizeGrip;
        private readonly BackgroundHitForm _backgroundHitForm;

        private volatile bool _stopping;
        // Read by the sensor thread.  PresentMon only runs while a card that can
        // show frame rate is configured, and the service stops it a few seconds
        // after the last demanding poll.
        private volatile bool _frameTelemetryDemand;
        private bool _expanded;
        private bool _superExpanded;
        private bool _backgroundless;
        private int _opacityPercent = 90;
        private bool _streamHidden;
        private bool _opacityPopupVisible;
        private DateTime _opacityPopupOpenedAt = DateTime.MinValue;
        private bool _loadingSettings;
        private bool _switchingView;
        private bool _automaticTransition;
        private bool _pinned;
        private bool _interactiveResize;
        private bool _applyingSizeLimits;
        private bool _layoutInProgress;
        private bool _compactLocationKnown;
        private HeaderVisibilityMode _headerMode = HeaderVisibilityMode.Automatic;
        private bool _restoredAutomaticHeaderHidden;
        // True while the band along the bottom edge is cut out of the window
        // shape because the hover chrome is away.  The window keeps its size:
        // only what is drawn changes, so no reading ever moves.
        private bool _chromeCollapsed;
        // The same arrangement for the bar along the top, given up in the
        // automatic mode once the pointer leaves.
        private bool _headerHoverHidden;
        // The shape the click catcher is wearing, so it is only ever re-shaped
        // when the widget's own shape has actually moved.
        private Rectangle _backgroundHitCrop;
        private List<Rectangle> _backgroundHitShape;
        private int _atomicLayoutDepth;
        private Point _compactLocation;
        private int _compactPageIndex;
        private CompactCardKind[] _compactSlotKinds = CreateSystemCompactPreset();
        private Dictionary<CompactCardKind, Color> _cardAccents =
            new Dictionary<CompactCardKind, Color>();
        // Snapshot of the accents the user picked, kept apart from the live set
        // so "reset every colour" stays undoable in one click.
        private Dictionary<CompactCardKind, Color> _customCardAccents =
            new Dictionary<CompactCardKind, Color>();
        private CompactCardKind[] _customCompactPreset;
        private ToolStripMenuItem _customPresetItem;
        private ToolStripMenuItem _customPaletteItem;
        private ToolStripMenuItem _pinHotkeyItem;
        private ToolStripMenuItem _hideHotkeyItem;
        private ToolStripMenuItem _helpHotkeyItem;
        private ToolStripMenuItem _dismissHotkeyItem;
        private ToolStripMenuItem _resetHotkeysItem;
        private ToolStripMenuItem _cycleCardsItem;
        private ToolStripMenuItem _compactCardsRoot;
        private readonly List<KeyValuePair<ToolStripItem, string>> _localizedItems =
            new List<KeyValuePair<ToolStripItem, string>>();
        private readonly List<ToolStripMenuItem> _languageItems =
            new List<ToolStripMenuItem>();
        private string _preferredLanguage = Loc.PreferredDefault();
        private int _headerButtonsWidth = 88;
        private bool _compactCycleAvailable = true;
        // Which sensor each of the two history graphs draws.  Two panels, any
        // card kind in either of them.
        private CompactCardKind[] _customGraphPreset;
        private ToolStripMenuItem _customGraphPresetItem;
        private ToolStripMenuItem _graphsRoot;
        private ToolStripMenuItem _cardColorRoot;
        private ToolStripMenuItem _resetAllColorsItem;
        private readonly Dictionary<CompactCardKind, ToolStripMenuItem> _colorResetItems =
            new Dictionary<CompactCardKind, ToolStripMenuItem>();
        private CompactCardKind _leftGraphSource = CompactCardKind.Cpu;
        private CompactCardKind _rightGraphSource = CompactCardKind.Gpu;
        private int _currentCompactVisibleCards = 1;
        private int _currentCompactCardCount = 4;
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
        private int _storageMenuClosedTick = Int32.MinValue / 2;
        // Set by the layout pass: a column that can no longer hold all its cards
        // spends the header row on them before it drops one.  Derived from the
        // current geometry on every pass, so it never gets stuck.
        private bool _headerHiddenByColumnPressure;
        private Thread _worker;
        private bool _dragClickPending;
        private int _lastDragClickTick;
        private Point _lastDragClickPosition;
        private bool _windowMovedDuringDragClick;
        private bool _pendingSnapshotRender;
        private Rectangle[] _dragWorkingAreas;
        private readonly System.Windows.Forms.Timer _pointerWatch = new System.Windows.Forms.Timer();
        private bool _outsideButtonDown;
        private bool _widgetClickedLast;
        private DateTime _lastUiTick = DateTime.UtcNow;
        private DateTime _lastHeartbeat = DateTime.UtcNow;
        private long _sensorReadCount;
        private long _lastSensorReadMs;
        private long _maxSensorReadMs;
        private double _stripFade = 1;
        private bool _pointerInside = true;
        private bool _chromeShown = true;
        private bool _topLeftGripAllowed;
        private readonly int _currentProcessId = Process.GetCurrentProcess().Id;

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

            TextReadout title = _title = MakeLabel("TRAYMETRY", new Point(12, 4), new Size(180, 22), 8F, FontStyle.Bold, Color.FromArgb(125, 135, 148));
            Controls.Add(title);
            _tips.SetToolTip(_title, Loc.T("tip.title"));

            _opacityButton = MakeHeaderButton("%", 312);
            _opacityButton.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
            _opacityButton.AccessibleName = Loc.T("access.opacity");
            _backgroundButton = MakeHeaderButton("◐", 336);
            _backgroundButton.AccessibleName = Loc.T("access.backgroundToggle");
            _languageButton = MakeHeaderButton(Loc.Code.ToUpperInvariant(), 354);
            // A two-letter badge needs the whole button: the default inner
            // margin of a Button eats enough of a 23 px slot to leave "R".
            _languageButton.Font = new Font("Segoe UI", 8F, FontStyle.Bold, GraphicsUnit.Point);
            _languageButton.Padding = new Padding(0);
            _languageButton.Margin = new Padding(0);
            _languageButton.TextAlign = ContentAlignment.MiddleCenter;
            _languageButton.AutoEllipsis = false;
            _languageButton.AccessibleName = Loc.T("access.language");
            _cycleButton = MakeHeaderButton("↻", 360);
            _cycleButton.AccessibleName = Loc.T("access.cycle");
            _pinButton = MakeHeaderButton("\uE718", 384);
            _pinButton.Font = new Font("Segoe MDL2 Assets", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _pinButton.AccessibleName = Loc.T("access.pin");
            _expandButton = MakeHeaderButton("▾", 408);
            _expandButton.Location = new Point(404, 1);
            _expandButton.Size = new Size(24, 25);
            _expandButton.Font = new Font("Segoe UI Symbol", 13F, FontStyle.Bold, GraphicsUnit.Point);
            _expandButton.AccessibleName = Loc.T("tip.expand");
            _headerButtons.Add(_opacityButton);
            _headerButtons.Add(_backgroundButton);
            _headerButtons.Add(_languageButton);
            _headerButtons.Add(_cycleButton);
            _headerButtons.Add(_pinButton);
            _headerButtons.Add(_expandButton);
            Controls.Add(_opacityButton);
            Controls.Add(_backgroundButton);
            Controls.Add(_languageButton);
            Controls.Add(_cycleButton);
            Controls.Add(_pinButton);
            Controls.Add(_expandButton);

            _tips.InitialDelay = 650;
            _tips.ReshowDelay = 150;
            _tips.AutoPopDelay = 5000;
            _tips.ShowAlways = true;
            _tips.SetToolTip(_opacityButton, OpacityTooltip);
            _tips.SetToolTip(_backgroundButton, Loc.T("access.background"));
            _tips.SetToolTip(_languageButton, Loc.T("tip.language"));
            _tips.SetToolTip(_cycleButton, Loc.T("tip.cycle.default"));
            _tips.SetToolTip(_pinButton, Loc.T("tip.pin.off", HotkeyDisplay.Pin));
            _tips.SetToolTip(_expandButton, Loc.T("tip.expand"));
            _opacityButton.Click += delegate { ToggleOpacityPopup(); };
            _backgroundButton.Click += delegate { ApplyBackgroundMode(!_backgroundless, true); };
            _languageButton.Click += delegate { ToggleLanguage(); };
            _cycleButton.Click += delegate { CycleCompactCards(); };
            _pinButton.Click += delegate { ApplyPinnedMode(!_pinned, true); };
            _expandButton.Click += delegate { CloseOpacityPopup(); Hide(); };
            AddHeaderHover(_opacityButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_backgroundButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_languageButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_cycleButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_pinButton, Color.FromArgb(43, 48, 57));
            AddHeaderHover(_expandButton, Color.FromArgb(43, 48, 57));

            MonitorCard cpuCompactCard = _cpuCompactCard = new MonitorCard();
            _cards.Add(cpuCompactCard);
            cpuCompactCard.Location = new Point(10, 29);
            cpuCompactCard.Size = new Size(125, 58);
            TextReadout cpuCompactCaption = MakeLabel("CPU", new Point(9, 4), new Size(105, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
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
            TextReadout gpuCompactCaption = MakeLabel("GPU", new Point(9, 4), new Size(105, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
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
            TextReadout networkCompactCaption = MakeLabel(Loc.T("caption.network"), new Point(9, 4), new Size(125, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
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
            TextReadout memoryCompactCaption = MakeLabel(Loc.T("caption.memory"), new Point(9, 4), new Size(125, 17), 7.5F, FontStyle.Bold, Color.FromArgb(130, 140, 153));
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
                new[] { Loc.T("history.tempShort"), Loc.T("caption.load"), Loc.T("caption.clock"), Loc.T("caption.power") }, Color.FromArgb(150, 158, 169));
            _compactGpuColumn.SetMetrics(new[] { "—°C", "—%", "—", "—", "—" },
                new[] { Loc.T("history.tempShort"), Loc.T("caption.load"), Loc.T("caption.clock"), Loc.T("caption.power"), "VRAM" }, Color.FromArgb(150, 158, 169));
            _compactNetworkColumn.SetMetrics(new[] { "—", "—" },
                new[] { Loc.T("caption.download"), Loc.T("caption.upload") }, Color.FromArgb(150, 158, 169));
            _compactMemoryColumn.SetMetrics(new[] { "—%", "— / —", "—" },
                new[] { Loc.T("caption.used"), Loc.T("caption.usedLong"), Loc.T("caption.clock") }, Color.FromArgb(92, 170, 255));

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
            cpuCard.Controls.Add(MakeLabel("CPU", new Point(10, 7), new Size(42, 17), 7.5F, FontStyle.Bold, CpuAccent));
            _cpuName = MakeLabel(Loc.T("state.waitingShort"), new Point(10, 23), new Size(180, 22), 8.5F, FontStyle.Regular, Color.FromArgb(195, 202, 211));
            _cpuName.AutoEllipsis = true;
            cpuCard.Controls.Add(_cpuName);
            _cpuTemperature = AddMetric(cpuCard, Loc.T("caption.temperature"), 10, 49);
            _cpuUsage = AddMetric(cpuCard, Loc.T("caption.load"), 106, 49);
            _cpuClock = AddMetric(cpuCard, Loc.T("caption.clock"), 10, 102);
            _cpuPower = AddMetric(cpuCard, Loc.T("caption.power"), 106, 102);

            MonitorCard gpuCard = _gpuCard = new MonitorCard();
            _cards.Add(gpuCard);
            gpuCard.Location = new Point(220, 0);
            gpuCard.Size = new Size(200, 172);
            gpuCard.Controls.Add(MakeLabel("GPU", new Point(10, 7), new Size(42, 17), 7.5F, FontStyle.Bold, GpuAccent));
            _gpuName = MakeLabel(Loc.T("state.waitingShort"), new Point(10, 23), new Size(180, 22), 8.5F, FontStyle.Regular, Color.FromArgb(195, 202, 211));
            _gpuName.AutoEllipsis = true;
            gpuCard.Controls.Add(_gpuName);
            _gpuTemperature = AddMetric(gpuCard, Loc.T("caption.temperature"), 10, 49);
            _gpuUsage = AddMetric(gpuCard, Loc.T("caption.load"), 106, 49);
            _gpuClock = AddMetric(gpuCard, Loc.T("caption.clock"), 10, 102);
            _gpuPower = AddMetric(gpuCard, Loc.T("caption.power"), 106, 102);
            _gpuMemory = MakeLabel("VRAM  — / —", new Point(10, 149), new Size(180, 17), 7.8F, FontStyle.Regular, Color.FromArgb(145, 155, 168));
            gpuCard.Controls.Add(_gpuMemory);

            OpacityPopupForm opacityCard = _opacityCard = new OpacityPopupForm();
            opacityCard.BackColor = Color.FromArgb(29, 33, 40);
            opacityCard.ClientSize = new Size(250, 32);
            opacityCard.Visible = false;
            _opacityLabel = MakeLabel(Loc.T("caption.opacitySample"), new Point(8, 5), new Size(130, 22), 7.5F, FontStyle.Bold, Color.FromArgb(145, 155, 168));
            opacityCard.Controls.Add(_opacityLabel);
            _backgroundCheckBox = new CheckBox();
            _backgroundCheckBox.Text = Loc.T("caption.noBackground");
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
                    // A middle click on a widget that was not the foreground
                    // window opens this card and activates the widget in the
                    // same gesture, and that activation is a deactivation for
                    // the card.  Closing on it made the first middle click do
                    // nothing at all and the second one work.
                    bool justOpened = (DateTime.UtcNow - _opacityPopupOpenedAt)
                        .TotalMilliseconds < 400;
                    if (_opacityPopupVisible && !_opacityCard.ContainsFocus &&
                        !pointerIsOverToggle && !justOpened)
                        CloseOpacityPopup();
                }));
            };
            opacityCard.MouseDown += ToggleOpacityWithMiddleMouse;
            AssignMiddleOpacityToggle(opacityCard.Controls);

            _superToggleButton = new ExpandableStrip();
            // Matches the form background so that a fully retracted pill leaves
            // no visible band along the bottom edge.
            _superToggleButton.BackColor = NormalBackground;
            _superToggleButton.ForeColor = Color.FromArgb(145, 155, 168);
            _superToggleButton.AccessibleName = Loc.T("access.superStats");
            _superToggleButton.Visible = false;
            _superToggleButton.Click += delegate { ToggleSuperExpanded(); };

            _superArea = new BufferedPanel();
            _superArea.BackColor = BackColor;
            _superArea.Visible = false;
            _cpuGauge = new RingGauge("CPU");
            _gpuGauge = new RingGauge("GPU");
            _cpuHistory = new SensorHistoryControl(CompactCardKind.Cpu,
                GetCompactCardDisplayName(CompactCardKind.Cpu), CpuAccent);
            _gpuHistory = new SensorHistoryControl(CompactCardKind.Gpu,
                GetCompactCardDisplayName(CompactCardKind.Gpu), GpuAccent);
            _memorySummary = new ResourceSummaryControl(Loc.T("caption.memory"), false);
            _storageSummary = new ResourceSummaryControl(Loc.T("caption.storage"), false);
            _fanSummary = new FanSummaryControl();
            _storageSummary.Cursor = Cursors.Hand;
            _storageMenu = new ContextMenuStrip();
            _storageMenu.ShowImageMargin = false;
            _storageMenu.BackColor = Color.FromArgb(29, 33, 40);
            _storageMenu.ForeColor = Color.FromArgb(225, 230, 236);
            KeepOutOfTaskbar(_storageMenu);
            // Closing the drop-down swallows the click that dismissed it, so the
            // press that follows must not be read as "open again" - that is what
            // made every second click on the card look like a dead one.
            _storageMenu.Closed += delegate { _storageMenuClosedTick = Environment.TickCount; };
            // MouseDown, not MouseClick: a click is only reported when the press
            // and the release land on the same control without the pointer
            // wandering, which on a small card is a coin toss.
            _storageSummary.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                    TryOpenStorageMenu(Cursor.Position);
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
            _topLeftResizeGrip.Size = new Size(GripSize, GripSize);
            _topLeftResizeGrip.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            _topLeftResizeGrip.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_pinned)
                    BeginResize(13);
            };
            Controls.Add(_topLeftResizeGrip);

            _leftResizeGrip = new ResizeGripControl(true, false);
            _leftResizeGrip.Size = new Size(GripSize, GripSize);
            _leftResizeGrip.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _leftResizeGrip.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !_pinned)
                    BeginResize(16);
            };
            Controls.Add(_leftResizeGrip);

            _resizeGrip = new ResizeGripControl(false, false);
            _resizeGrip.Size = new Size(GripSize, GripSize);
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
            _headerMenu = CreateHeaderVisibilityMenu();
            _pinItem = LocalizedItem("access.pin");
            _pinItem.CheckOnClick = true;
            _pinItem.Click += delegate { ApplyPinnedMode(_pinItem.Checked, true); };
            _topMostItem = LocalizedItem("menu.topMost");
            _topMostItem.CheckOnClick = true;
            _topMostItem.Click += delegate
            {
                TopMost = _topMostItem.Checked;
                SyncBackgroundHitForm();
                // Declaring the window topmost puts it over the very menu this
                // was clicked in, and the menu is still standing now that it
                // survives a click.  The menu is put back above it.
                RaiseOpenMenus();
                SaveSettings();
            };
            _startupItem = LocalizedItem("menu.startup");
            _startupItem.CheckOnClick = true;
            _startupItem.Click += delegate { SetStartup(_startupItem.Checked); };
            ToolStripMenuItem compactCardsMenu = CreateCompactCardsMenu();
            ToolStripMenuItem graphsMenu = CreateGraphsMenu(AvailableCompactCardKinds());
            ToolStripMenuItem colorMenu = CreateCardColorMenu(AvailableCompactCardKinds());

            ToolStripMenuItem opacityMenu = LocalizedItem("menu.opacity");
            foreach (int percent in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 })
            {
                int selectedPercent = percent;
                ToolStripMenuItem item = new ToolStripMenuItem(percent.ToString(CultureInfo.InvariantCulture) + "%");
                item.Click += delegate { SetOpacityPercent(selectedPercent, true); };
                _opacityItems.Add(item);
                opacityMenu.DropDownItems.Add(item);
            }
            _backgroundItem = LocalizedItem("menu.background");
            _backgroundItem.CheckOnClick = true;
            _backgroundItem.Click += delegate { ApplyBackgroundMode(_backgroundItem.Checked, true); };
            _streamHiddenItem = LocalizedItem("menu.streamHidden");
            _streamHiddenItem.CheckOnClick = true;
            _streamHiddenItem.ToolTipText =
                Loc.T("tip.streamHidden");
            _streamHiddenItem.Click += delegate { ApplyStreamHidden(_streamHiddenItem.Checked, true); };

            ToolStripMenuItem updateItem = LocalizedItem("menu.checkUpdates");
            updateItem.Click += delegate { UpdateManager.CheckForUpdatesAsync(this, true); };
            ToolStripMenuItem supportItem = CreateSupportMenu();
            ToolStripMenuItem repairServiceItem = LocalizedItem("menu.repairSensors");
            repairServiceItem.Click += delegate
            {
                bool repaired = MachineBootstrap.RequestRepair();
                MessageBox.Show(repaired
                        ? Loc.T("service.ready")
                        : Loc.T("service.setupFailed"),
                    "Traymetry",
                    MessageBoxButtons.OK,
                    repaired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            ToolStripMenuItem removeServiceItem = LocalizedItem("menu.removeService");
            removeServiceItem.Click += delegate
            {
                DialogResult answer = MessageBox.Show(
                    Loc.T("service.remove.body") +
                    Loc.T("common.continueQuestion"),
                    Loc.T("service.remove.title"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                    return;
                bool removed = MachineBootstrap.RequestUninstall();
                MessageBox.Show(removed
                        ? Loc.T("service.removed")
                        : Loc.T("service.removeFailed"),
                    "Traymetry",
                    MessageBoxButtons.OK,
                    removed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            ToolStripMenuItem helpItem = LocalizedItem("menu.help");
            helpItem.Click += delegate { ShowHelpWindow(); };
            ToolStripMenuItem exitItem = LocalizedItem("menu.exit");
            exitItem.Click += delegate { Close(); };

            // Ordered by how often a hand actually reaches for the entry, not by
            // how the code grew.  First the switches that change what is on
            // screen right now, then what the widget shows, then the standing
            // window behaviour, and last the things touched once and forgotten.
            menu.Items.Add(_headerMenu);
            menu.Items.Add(opacityMenu);
            menu.Items.Add(new ToolStripSeparator());
            // Cards, graphs and colours are the three things the widget is made
            // of, so one right click reaches any of them.  Colours used to hang
            // off the cards menu, where a graph accent was two levels deep under
            // a heading that did not mention it.
            menu.Items.Add(compactCardsMenu);
            menu.Items.Add(graphsMenu);
            menu.Items.Add(colorMenu);
            menu.Items.Add(new ToolStripSeparator());
            // Everything the user ticks lives in one column.  "No background" is
            // a tick like the rest of them, and it sat on its own above only
            // because it changes the look rather than the behaviour.
            menu.Items.Add(_pinItem);
            menu.Items.Add(_topMostItem);
            menu.Items.Add(_backgroundItem);
            menu.Items.Add(_streamHiddenItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateLanguageMenu());
            menu.Items.Add(CreateHotkeysMenu());
            menu.Items.Add(helpItem);
            // "Snap to the top right" and "Hide to the notification area" left the
            // menu: the ▾ header button already hides the widget, and the corner
            // shortcut was one entry of clutter for a window that is dragged
            // anywhere anyway.
            // Two neighbouring entries that both said "sensors" read as a choice
            // the user has to make before knowing what either does.  One entry
            // now names the subject, and the drop-down names the actions.
            ToolStripMenuItem sensorsMenu = LocalizedItem("menu.sensors");
            sensorsMenu.DropDownItems.Add(repairServiceItem);
            sensorsMenu.DropDownItems.Add(new ToolStripSeparator());
            sensorsMenu.DropDownItems.Add(removeServiceItem);

            ToolStripMenuItem reportItem = LocalizedItem("menu.report");
            reportItem.Click += delegate { CollectProblemReport(); };

            menu.Items.Add(updateItem);
            menu.Items.Add(supportItem);
            menu.Items.Add(sensorsMenu);
            menu.Items.Add(reportItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            // Everything that shapes the widget is picked in runs, so those
            // menus stay up: cards, graphs, colours, opacity, the header, and
            // the column of ticks.  What leads elsewhere - expanding, the
            // language, help, updates, the sensor actions, exit - closes the
            // menu, because after it there is nothing left to pick.
            HoldOpenOnClick(compactCardsMenu.DropDown);
            HoldOpenOnClick(graphsMenu.DropDown);
            HoldOpenOnClick(colorMenu.DropDown);
            HoldOpenOnClick(opacityMenu.DropDown);
            HoldOpenOnClick(_headerMenu.DropDown);
            HoldOpenOnClick(_pinItem);
            HoldOpenOnClick(_topMostItem);
            HoldOpenOnClick(_backgroundItem);
            HoldOpenOnClick(_streamHiddenItem);
            HoldOpenOnClick(_startupItem);
            // An item that holds its own drop-down open has to hold the chain it
            // hangs off open too, or the sub-menu is left standing on nothing.
            KeepOpenOnHeldClick(menu);
            // Every drop-down in the tree, sticky or not: being behind the
            // widget is not about what the entry does, it is about the menu
            // being a menu over a topmost window.
            RaiseWhenOpened(menu);
            // The framework knows exactly why it is closing a drop-down and
            // never says so out loud.  A menu that goes when a switch inside it
            // is used is one of five different faults depending on the reason,
            // and guessing between them has cost more than writing it down.
            menu.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                // A menu goes when the program's active window changes, and for
                // this program that is not evidence the user has finished with
                // it.  The widget gives up the foreground on purpose - the
                // catcher takes the click without activating, and pinned the
                // click goes through to whatever is underneath - so activation
                // moves away constantly while the menu is plainly still being
                // used.  Worse, the entries themselves cause it: showing or
                // hiding the catcher, re-cutting its shape, re-stating the
                // band.  This was guarded by a depth count around those calls
                // until the log showed the notice arriving 367ms after the
                // entry was clicked, long after any count had unwound.
                //
                // Dismissal is not lost by ignoring it: CloseMenusOnOutsideClick
                // polls the physical buttons and closes the menu on a press
                // anywhere off it, which is what actually dismisses menus here
                // and works whether or not this program held the foreground.
                if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange)
                {
                    e.Cancel = true;
                    DiagLog.Write("menu kept through a focus change");
                    return;
                }
                DiagLog.Write("menu closing reason=" + e.CloseReason +
                    " cancel=" + (e.Cancel ? "1" : "0") +
                    " pinned=" + (_pinned ? "1" : "0"));
            };
            ContextMenuStrip = menu;
            AssignContextMenu(Controls, menu);
            _opacityCard.ContextMenuStrip = menu;
            AssignContextMenu(_opacityCard.Controls, menu);

            _mouseHookCallback = OnGlobalMouse;
            _backgroundHitForm = new BackgroundHitForm();
            _backgroundHitForm.CursorResolver = ResolveBackgroundHitCursor;
            _backgroundHitForm.MouseDown += BackgroundHitMouseDown;
            _backgroundHitForm.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                // Right click reaches the widget even while it is pinned: the
                // catcher is cut down to the readings for that case, so this is
                // a click on the numbers rather than on empty air.
                if (e.Button == MouseButtons.Right)
                    menu.Show(Cursor.Position);
            };

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Information;
            _tray.Text = "Traymetry";
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            // One click, not two.  The icon has exactly one thing to do with a
            // left click, and a widget that has to be asked twice to come back
            // reads as an icon that did not hear the first click.
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left)
                    return;
                // A widget that is on screen but behind everything else is a
                // widget the user is looking at the icon to get back, and
                // hiding it is the opposite of what the click meant.  The
                // second click then brought it back and the icon looked like it
                // needed two.  Windows is asked where the window really is
                // rather than this program being asked what it intended.
                if (Visible && TopMost && NativeUi.IsBuriedUnderNormalWindow(Handle))
                {
                    DiagLog.Write("tray raised a widget that had fallen out of the band");
                    ApplyWindowBand();
                    Activate();
                    return;
                }
                if (Visible)
                {
                    CloseOpacityPopup();
                    Hide();
                }
                else
                {
                    Show();
                    Activate();
                    // Not by comparison: the icon is the way back to a widget
                    // that cannot be seen, so where it lands has to be stated
                    // rather than assumed to be already right.
                    ApplyWindowBand();
                }
            };

            LoadSettings();
            ApplyWindowShape();
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
                    (_expanded || LayoutHeight >= CompactHeaderRevealHeight))
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

            _pointerWatch.Interval = 40;
            _pointerWatch.Tick += delegate
            {
                NoteUiTick();
                UpdateStripPresence();
                // Keeping the menu on top is not a one-off.  Anything that
                // re-declares the widget or the catcher topmost - a pin, a
                // background toggle, a layout pass, the catcher being shown
                // again - lands it back over an open menu, and there is no
                // event that covers all of them.  While a menu is standing,
                // the order is simply re-stated; when nothing is open this
                // costs one comparison.
                KeepWidgetBelowMenus();
                KeepWidgetInBand();
                CloseMenusOnOutsideClick();
            };
            _pointerWatch.Start();

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
            if (LayeredMode)
                StartComposition();
            StartupTrace.Write("form-constructor-exit");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int ToolWindow = 0x00000080;
                const int AppWindow = 0x00040000;
                CreateParams parameters = base.CreateParams;
                // No class drop shadow.  The system draws that shadow in a
                // window of its own around the shape of this one, and the shape
                // is not the card: the hover bands are given up by cutting them
                // out of the window region, so the shadow was laid along that
                // cut - a dark line across the middle of a widget that is meant
                // to be nothing but its readings.  A card that wants a shadow
                // can draw its own, inside the frame this program composes.
                parameters.ExStyle |= ToolWindow;
                parameters.ExStyle &= ~AppWindow;
                // The layered style has to be here rather than applied later:
                // a window switched to layered once it is on screen loses what
                // it had drawn and never gets it back until the next frame.
                if (LayeredMode)
                    parameters.ExStyle |= LayeredSurface.ExStyleLayered;
                return parameters;
            }
        }

        // -- layered composition --------------------------------------------

        /// <summary>
        /// Whether the window carries its own transparency per pixel instead of
        /// declaring one colour invisible.  Read before the window exists: the
        /// extended style cannot be added later without the window losing what
        /// it had on screen.
        /// </summary>
        internal static bool LayeredMode = true;

        private LayeredSurface _surface;
        private Bitmap _contentBuffer;
        private Bitmap _outlineBuffer;
        private System.Windows.Forms.Timer _composeTimer;
        private bool _composeDirty;
        private bool _composing;

        private void StartComposition()
        {
            _surface = new LayeredSurface(this);
            _surface.ConstantAlpha = OpacityToAlpha(_opacityPercent);
            _composeTimer = new System.Windows.Forms.Timer();
            _composeTimer.Interval = 25;
            _composeTimer.Tick += delegate { ComposeIfDirty(); };
            _composeTimer.Start();
            WatchForRepaint(this);
            // A resize has to reach the screen in the same beat it happens, or
            // the window trails the pointer by a frame all the way down the drag.
            Resize += delegate
            {
                _composeDirty = true;
                ComposeIfDirty();
            };
            _composeDirty = true;
        }

        /// <summary>
        /// The system does not repaint a layered window: nothing reaches the
        /// screen until a whole frame is handed over.  So every invalidation any
        /// control raises is collected here, and the timer turns a burst of them
        /// into a single frame.
        /// </summary>
        private void WatchForRepaint(Control control)
        {
            control.Invalidated += delegate { _composeDirty = true; };
            control.ControlAdded += delegate(object sender, ControlEventArgs added)
            {
                WatchForRepaint(added.Control);
                _composeDirty = true;
            };
            foreach (Control child in control.Controls)
                WatchForRepaint(child);
        }

        private void ComposeIfDirty()
        {
            if (!_composeDirty || _composing || !IsHandleCreated || !Visible)
                return;
            _composeDirty = false;
            _composing = true;
            try
            {
                _surface.Render(Size, PaintFrame);
            }
            finally
            {
                _composing = false;
            }
        }

        /// <summary>
        /// Builds the frame in two passes.  The widget is drawn once into a
        /// buffer of its own, and with no panel behind it that buffer is then
        /// laid down twice: a dark copy nudged a pixel in each direction, and
        /// the real one on top.
        ///
        /// The dark copy is the whole answer to white desktops.  Light text on a
        /// light wall is invisible whatever shade of grey it is given, and a
        /// palette that reads on both walls does not exist; an outline is what
        /// subtitles have always done, and it costs the widget nothing on a dark
        /// desktop because black on black is not there.
        /// </summary>
        private void PaintFrame(Graphics graphics)
        {
            Rectangle frame = new Rectangle(0, 0,
                Math.Max(1, Width), Math.Max(1, Height));
            _contentBuffer = EnsureFrameBuffer(_contentBuffer, frame.Size);

            using (Graphics content = Graphics.FromImage(_contentBuffer))
            {
                // The buffer is usually larger than the window, so everything
                // here is fenced to the part that is actually the widget.
                content.SetClip(frame);
                content.Clear(Color.Transparent);
                content.SmoothingMode = SmoothingMode.AntiAlias;
                content.TextRenderingHint = TextRenderingHint.AntiAlias;
                PaintComposite(content);
            }

            if (_backgroundless)
                DrawContrastOutline(graphics, _contentBuffer, frame);
            graphics.DrawImage(_contentBuffer, frame, frame, GraphicsUnit.Pixel);
        }

        /// <summary>
        /// A frame buffer that only ever grows, in steps.
        ///
        /// Sized exactly to the window, these are thrown away and made again on
        /// every frame of a resize drag - and there are three of them, each the
        /// size of the whole widget.  Expanded, that was tens of megabytes of
        /// brand new pages per frame; the log read as a solid run of half-second
        /// stalls for as long as the corner was held.  Kept and reused, the
        /// pages stay where they are and a resize costs what drawing costs.
        /// </summary>
        private static Bitmap EnsureFrameBuffer(Bitmap existing, Size size)
        {
            if (existing != null && existing.Width >= size.Width &&
                existing.Height >= size.Height)
                return existing;
            const int Step = 128;
            int width = ((size.Width + Step - 1) / Step) * Step;
            int height = ((size.Height + Step - 1) / Step) * Step;
            if (existing != null)
            {
                width = Math.Max(width, existing.Width);
                height = Math.Max(height, existing.Height);
                existing.Dispose();
            }
            return new Bitmap(Math.Max(Step, width), Math.Max(Step, height),
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        /// <summary>
        /// Lays a thin dark halo under the frame.
        ///
        /// The offsets are the four sides, not a diagonal pair.  A pair is a
        /// drop shadow: it sits on one side of every edge, so a one-pixel card
        /// border comes out as two lines of different colour and reads as a
        /// smear rather than as an outline.  Four symmetric copies surround the
        /// shape instead, which is what makes it look drawn rather than lit.
        ///
        /// They cost no more than the pair did, because the darkening happens
        /// once: the colour matrix - the expensive part - runs into a buffer of
        /// its own, and what gets laid down four times is a plain blit.
        /// </summary>
        private void DrawContrastOutline(Graphics graphics, Bitmap content, Rectangle frame)
        {
            _outlineBuffer = EnsureFrameBuffer(_outlineBuffer, frame.Size);

            using (Graphics silhouette = Graphics.FromImage(_outlineBuffer))
            using (System.Drawing.Imaging.ImageAttributes attributes =
                new System.Drawing.Imaging.ImageAttributes())
            {
                silhouette.SetClip(frame);
                silhouette.CompositingMode = CompositingMode.SourceCopy;
                // Every channel of colour thrown away, the coverage kept and
                // thinned: what is left is the shape of the widget in black.
                System.Drawing.Imaging.ColorMatrix matrix =
                    new System.Drawing.Imaging.ColorMatrix();
                matrix.Matrix00 = 0;
                matrix.Matrix11 = 0;
                matrix.Matrix22 = 0;
                // Four copies stack, so a modest share each still adds up to a
                // solid edge; a heavier one turns the whole widget into outline.
                matrix.Matrix33 = 0.28F;
                attributes.SetColorMatrix(matrix);
                silhouette.DrawImage(content, frame,
                    frame.X, frame.Y, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
            }

            for (int index = 0; index < OutlineOffsets.Length; index++)
                graphics.DrawImage(_outlineBuffer,
                    new Rectangle(OutlineOffsets[index].X, OutlineOffsets[index].Y,
                        frame.Width, frame.Height),
                    frame, GraphicsUnit.Pixel);
        }

        private static readonly Point[] OutlineOffsets =
        {
            new Point(-1, 0), new Point(1, 0), new Point(0, -1), new Point(0, 1)
        };

        /// <summary>
        /// Draws what the screen would have shown: the window background, then
        /// every visible control in back-to-front order.  The controls paint
        /// themselves - the same OnPaint they have always run - onto a surface
        /// that keeps the transparency they produce.
        /// </summary>
        private void PaintComposite(Graphics graphics)
        {
            if (!_backgroundless)
                using (Brush fill = new SolidBrush(BackColor))
                    graphics.FillRectangle(fill, 0, 0, Width, Height);
            PaintChildren(graphics, this);
        }

        private void PaintChildren(Graphics graphics, Control parent)
        {
            Control.ControlCollection children = parent.Controls;
            // Controls[0] is the front of the z-order, so the walk runs backwards
            // and the frontmost control is painted last.
            for (int index = children.Count - 1; index >= 0; index--)
            {
                Control child = children[index];
                if (!child.Visible || child.Width < 1 || child.Height < 1)
                    continue;
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(child.Left, child.Top);
                    graphics.IntersectClip(new Rectangle(0, 0, child.Width, child.Height));
                    // Only a solid background is painted for the control.  An
                    // alpha of anything less is the widget saying this control
                    // has no panel behind it, and the desktop keeps the pixel.
                    Color background = child.BackColor;
                    if (background.A == 255)
                        using (Brush fill = new SolidBrush(background))
                            graphics.FillRectangle(fill, 0, 0, child.Width, child.Height);
                    using (PaintEventArgs args = new PaintEventArgs(graphics,
                        new Rectangle(0, 0, child.Width, child.Height)))
                        InvokePaint(child, args);
                    PaintChildren(graphics, child);
                }
                finally
                {
                    graphics.Restore(state);
                }
            }
        }

        private const int HelpHotkeyId = 0x5471;
        private bool _hotkeysSuspended;
        private const int PinHotkeyId = 0x5472;
        private const int HideHotkeyId = 0x5473;
        private readonly GlobalHotkey _pinHotkey =
            new GlobalHotkey(PinHotkeyId, HotkeyBinding.DefaultPin);
        private readonly GlobalHotkey _hideHotkey =
            new GlobalHotkey(HideHotkeyId, HotkeyBinding.DefaultHide);
        // Registered only while the pointer is over the widget, so the key
        // belongs to whatever the user is actually working in the rest of the
        // time.  Its own binding is kept apart from the registration for that
        // reason: the wanted combination changes with the pointer, the user's
        // choice does not.
        private readonly GlobalHotkey _helpHotkey =
            new GlobalHotkey(HelpHotkeyId, HotkeyBinding.None);
        private HotkeyBinding _helpBinding = HotkeyBinding.DefaultHelp;
        // Window-level: no registration, so it costs nobody else their key.
        private HotkeyBinding _dismissBinding = HotkeyBinding.DefaultDismiss;

        private enum HotkeyTarget { Pin, Hide, Help, Dismiss }

        /// <summary>
        /// One system-wide hotkey: what the user asked for, what was last handed
        /// to the system, and which window holds it.  The two are kept apart
        /// because a combination another application owns can only be found out
        /// by asking for it, and asking again on every layout pass would be a
        /// registration attempt a hundred times a minute.
        /// </summary>
        private sealed class GlobalHotkey
        {
            public GlobalHotkey(int id, HotkeyBinding wanted)
            {
                Id = id;
                Wanted = wanted;
                Active = HotkeyBinding.None;
            }

            public readonly int Id;
            public HotkeyBinding Wanted;
            public HotkeyBinding Active;
            public IntPtr Window;
            public bool Registered;
        }

        protected override void WndProc(ref Message message)
        {
            const int HotKey = 0x0312;
            if (message.Msg == HotKey)
            {
                int hotkeyId = message.WParam.ToInt32();
                if (hotkeyId == HelpHotkeyId)
                {
                    ToggleHelpWindow();
                    return;
                }
                if (hotkeyId == PinHotkeyId)
                {
                    ApplyPinnedMode(!_pinned, true);
                    return;
                }
                if (hotkeyId == HideHotkeyId)
                {
                    ToggleTrayVisibility();
                    return;
                }
            }
            // A press on a widget that is not the active window has to do what
            // it looks like it does.  Answering the activation probe with
            // MA_ACTIVATE keeps the press itself alive, so the button under the
            // pointer gets it; anything that eats the press instead reads as a
            // dead button rather than as a window taking focus.
            const int MouseActivate = 0x0021;
            if (message.Msg == MouseActivate)
            {
                const int Activate = 1;
                StartupTrace.Write("widget mouse-activate");
                message.Result = (IntPtr)Activate;
                return;
            }
            if (message.Msg == 0x0201)
                StartupTrace.Write("widget lbuttondown");
            const int NonClientHitTest = 0x0084;
            const int WindowMoving = 0x0216;
            const int EnterSizeMove = 0x0231;
            const int ExitSizeMove = 0x0232;
            if (message.Msg == EnterSizeMove)
            {
                CloseOpacityPopup();
                _interactiveResize = true;
                // Screen.AllScreens re-enumerates the monitors on every call and
                // WM_MOVING arrives with every mouse move, so the desktop layout
                // is captured once for the whole drag instead.
                _dragWorkingAreas = Screen.AllScreens
                    .Select(delegate(Screen screen) { return screen.WorkingArea; })
                    .ToArray();
                ClearRoundedCorners();
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
                message.Result = (IntPtr)GetResizeHitTest(point, ResizeEdge);
                return;
            }
            if (message.Msg == WindowMoving && message.LParam != IntPtr.Zero)
            {
                _windowMovedDuringDragClick = true;
                NativeRect rectangle = (NativeRect)System.Runtime.InteropServices.Marshal.PtrToStructure(message.LParam, typeof(NativeRect));
                int width = rectangle.Right - rectangle.Left;
                int height = rectangle.Bottom - rectangle.Top;
                Rectangle proposed = new Rectangle(rectangle.Left, rectangle.Top, width, height);

                Rectangle[] workingAreas = GetWorkingAreas();
                if (!IsRectangleInsideDesktop(proposed, workingAreas))
                {
                    Rectangle area = GetWorkingAreaAt(workingAreas, Cursor.Position);
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
                _dragWorkingAreas = null;
                if (_pendingSnapshotRender && _lastSnapshot != null)
                {
                    _pendingSnapshotRender = false;
                    UpdateSnapshot(_lastSnapshot);
                }
                RunLayoutPass(true);
                // The hover chrome was frozen for the whole gesture, so its band
                // is reconciled with where the pointer actually ended up.
                UpdateStripPresence();
                SyncBackgroundHitForm();
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

            // The wheel is the quickest way to dim the widget, so it works over
            // the whole window and not only while the slider happens to be on
            // screen.  Without a background the pointer is over the catcher and
            // never over the widget itself, and the same scroll would otherwise
            // need a click first - the one gesture behaving differently in the
            // two modes reads as a fault, not as a rule.
            //
            // Pinned, the scroll belongs to whatever is underneath - that is
            // what pinning is for - until the widget is the thing the user last
            // clicked.  Then it is plainly the widget being aimed at, and a
            // click followed by a scroll is the whole gesture.
            if (message.Msg == 0x020A && (!_pinned || _widgetClickedLast) &&
                (belongsToWindow || BelongsToCatcher(message.HWnd)))
            {
                int wheel = unchecked((short)(((long)message.WParam >> 16) & 0xFFFF));
                if (wheel != 0)
                {
                    _opacitySlider.Value += wheel > 0 ? 5 : -5;
                    SaveSettings();
                }
                return true;
            }

            if (message.Msg == 0x020A)
                DiagLog.Write("wheel unclaimed hwnd=" + message.HWnd.ToInt64() +
                    " control=" + DescribeHandle(message.HWnd) +
                    " catcher=" + (_backgroundHitForm != null &&
                        _backgroundHitForm.IsHandleCreated
                        ? _backgroundHitForm.Handle.ToInt64().ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "none") +
                    " backgroundless=" + (_backgroundless ? "1" : "0") +
                    " pinned=" + (_pinned ? "1" : "0"));

            if (!belongsToWindow)
                return false;

            if (_pinned && IsMiddleMouseDownMessage(message.Msg))
            {
                ToggleOpacityPopup();
                return true;
            }

            // A left message that reaches this queue while pinned is one the hit
            // test let in - over the pin, which keeps its clicks.  Everything
            // else was routed to the window below and never arrives here at all.
            if (_pinned && IsLeftMouseMessage(message.Msg) &&
                !BelongsToControl(message.HWnd, _pinButton))
                return true;

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
                int hitTest = GetResizeHitTest(PointToClient(Cursor.Position), ResizeEdge);
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
                StripHitBounds().Contains(point);
        }

        /// <summary>
        /// The expand strip minus a gap at each end.  The strip runs the full
        /// width along the bottom, so its last pixels sit exactly where the
        /// corner grip is aimed at - and a press that missed the corner by
        /// three pixels used to unfold the whole window instead of resizing
        /// it.  A resize that did not start is a much smaller surprise, so the
        /// corners belong to the grip.
        /// </summary>
        private Rectangle StripHitBounds()
        {
            Rectangle bounds = _superToggleButton.Bounds;
            int gap = Math.Min(GripSize, Math.Max(0, bounds.Width / 4));
            bounds.X += gap;
            bounds.Width = Math.Max(0, bounds.Width - gap * 2);
            return bounds;
        }

        private static string DescribeHandle(IntPtr handle)
        {
            Control target = Control.FromHandle(handle);
            return target == null ? "(not ours)" : target.GetType().Name;
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

        /// <summary>
        /// The catcher stands in for the widget under the pointer, so anything
        /// aimed at the widget as a surface - the wheel above all - has to count
        /// it as the widget.  It is deliberately not part of
        /// <see cref="BelongsToWindow"/>: clicks on the catcher are already
        /// handled by its own mouse-down, and letting them through the filter as
        /// well would resize the window on a press meant for a button.
        /// </summary>
        private bool BelongsToCatcher(IntPtr handle)
        {
            return _backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                _backgroundHitForm.IsHandleCreated && handle == _backgroundHitForm.Handle;
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
            // The diagonal zone is a full GripSize square so that it matches the
            // painted marker exactly.  An edge*edge square was almost impossible
            // to hit, and without a background the colour-keyed pixels are
            // click-through, which left only the drawn lines as a target.
            int corner = Math.Max(edge, GripSize);
            bool cornerLeft = point.X <= corner;
            bool cornerRight = point.X >= ClientSize.Width - corner;
            bool cornerTop = point.Y <= corner;
            bool cornerBottom = point.Y >= ClientSize.Height - corner;

            if (cornerLeft && cornerTop) return 13;
            if (cornerRight && cornerTop) return 14;
            if (cornerLeft && cornerBottom) return 16;
            if (cornerRight && cornerBottom) return 17;
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

        /// <summary>
        /// Whether a pinned widget should be invisible to the mouse for this
        /// hit test, so the press lands on whatever the widget is covering.
        ///
        /// Two mechanisms came before this one and both were wrong.  The press
        /// was posted with PostMessage at first, straight at the window
        /// underneath: a program that reads its message queue takes that, but a
        /// game reads the device instead, through raw input or by asking the
        /// state directly, and for those the forwarded click never happened -
        /// pinned over a game, which is the only reason pinning exists, the
        /// click was eaten by an overlay that then quietly told nobody.  Then
        /// the widget went transparent to the mouse for the length of the press
        /// and replayed it into the system.  That routes correctly, but it is
        /// three faults waiting to happen: the transparency is a whole-word
        /// write of the extended style, which empties a layered window and
        /// carries the topmost bit off with it, and a replayed press that the
        /// program then hears again is a loop that fires synthetic clicks into
        /// whatever is under the pointer.
        ///
        /// Answering the hit test costs none of that.  The system asks which
        /// window is under this point and is told "not this one", so the press
        /// is routed to the window below as real input, by the same code that
        /// would have routed it if the widget were not there.  Nothing is
        /// injected, no style is written, and there is no state to get stuck in.
        ///
        /// This is the catcher's answer, and the catcher only.  A hit test is
        /// asked of the window that owns the pixel, and the widget's readings
        /// are controls - windows in their own right - so the form was never
        /// asked about the one place that mattered and a click on the numbers
        /// was not let through.  The widget takes itself out of the hit test
        /// whole instead, children and all: see SyncPinnedClickThrough.
        ///
        /// The pin keeps its own clicks: it is the way back out.
        /// </summary>
        /// <summary>
        /// Takes the right press over a pinned widget before anything else sees
        /// it.  While pinned nothing of this program is left in the mouse's way
        /// - that is the whole point - so there is no window left to receive
        /// that press either, and this is what puts one button back.
        ///
        /// The press is swallowed rather than passed on, so the program
        /// underneath does not get a context menu of its own at the same time,
        /// and its release is swallowed with it: a release with no press is a
        /// stuck button in anything that pairs them.
        /// </summary>
        private IntPtr OnGlobalMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            const int RightDown = 0x0204;
            const int RightUp = 0x0205;
            const int MiddleDown = 0x0207;
            const int MiddleUp = 0x0208;
            const int Wheel = 0x020A;
            if (code == 0 && _pinned && !_stopping)
            {
                int message = wParam.ToInt32();
                // Only while the opacity card is up.  That is the one moment
                // the wheel is plainly aimed at the widget rather than at what
                // the widget is covering, and it is the gesture the card is
                // opened for: middle click, then scroll.  Otherwise a pinned
                // widget goes on giving the wheel away, like every other
                // button.  The card itself keeps its own wheel - it is a window
                // in the ordinary way and does not need taking.
                if (message == Wheel && _opacityPopupVisible &&
                    Visible && IsHandleCreated)
                {
                    MouseHookInput scroll = (MouseHookInput)Marshal.PtrToStructure(
                        lParam, typeof(MouseHookInput));
                    Point at = new Point(scroll.X, scroll.Y);
                    if (OverWidget(at) && !PointOverOpacityCard(at))
                    {
                        int delta = unchecked((short)((scroll.Data >> 16) & 0xFFFF));
                        BeginInvoke(new Action(delegate { NudgeOpacity(delta); }));
                        return new IntPtr(1);
                    }
                }
                if (message == RightDown || message == RightUp ||
                    message == MiddleDown || message == MiddleUp)
                {
                    MouseHookInput input = (MouseHookInput)Marshal.PtrToStructure(
                        lParam, typeof(MouseHookInput));
                    Point screen = new Point(input.X, input.Y);
                    bool press = message == RightDown || message == MiddleDown;
                    bool middle = message == MiddleDown || message == MiddleUp;
                    if (press)
                    {
                        // The opacity card is a window of its own and is meant
                        // to be used while pinned - it is the only way left to
                        // set opacity - so a press on it is its own.
                        bool mine = Visible && IsHandleCreated && OverWidget(screen) &&
                            !PointOverPinButton(screen) && !PointOverOpacityCard(screen);
                        if (middle)
                            _swallowedMiddlePress = mine;
                        else
                            _swallowedRightPress = mine;
                        if (mine)
                        {
                            DiagLog.Write((middle ? "middle" : "right") +
                                " press taken from under a pinned widget at " +
                                screen.X.ToString(CultureInfo.InvariantCulture) + "," +
                                screen.Y.ToString(CultureInfo.InvariantCulture));
                            // Never from inside the hook: the system gives this
                            // callback a few hundred milliseconds before it
                            // stops calling it altogether, and showing a menu
                            // spends far more than that.
                            if (middle)
                                BeginInvoke(new Action(ToggleOpacityPopup));
                            else
                                BeginInvoke(new Action(delegate { ShowMenuAt(screen); }));
                            return new IntPtr(1);
                        }
                    }
                    else if (middle ? _swallowedMiddlePress : _swallowedRightPress)
                    {
                        if (middle)
                            _swallowedMiddlePress = false;
                        else
                            _swallowedRightPress = false;
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void NudgeOpacity(int delta)
        {
            if (delta == 0 || _stopping)
                return;
            _opacitySlider.Value += delta > 0 ? 5 : -5;
            SaveSettings();
        }

        private bool PointOverOpacityCard(Point screen)
        {
            return _opacityPopupVisible && _opacityCard != null &&
                !_opacityCard.IsDisposed && _opacityCard.Visible &&
                _opacityCard.Bounds.Contains(screen);
        }

        private void ShowMenuAt(Point screen)
        {
            if (ContextMenuStrip == null || _stopping)
                return;
            ContextMenuStrip.Show(screen);
        }

        /// <summary>
        /// The hook is only installed while it has something to do.  A global
        /// mouse hook is called for every movement of the pointer anywhere on
        /// the machine, and this program is a widget that mostly sits still.
        /// </summary>
        private void SyncPinnedMouseHook()
        {
            bool wanted = _pinned && Visible && !_stopping;
            if (wanted == (_mouseHook != IntPtr.Zero))
                return;
            if (wanted)
            {
                const int LowLevelMouseHook = 14;
                _mouseHook = SetMouseHook(LowLevelMouseHook, _mouseHookCallback,
                    GetHookModule(null), 0);
                DiagLog.Write("mouse hook installed=" +
                    (_mouseHook != IntPtr.Zero ? "1" : "0"));
                return;
            }
            UnhookMouseHook(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _swallowedRightPress = false;
            _swallowedMiddlePress = false;
            DiagLog.Write("mouse hook removed");
        }

        private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHookInput
        {
            public int X;
            public int Y;
            public int Data;
            public int Flags;
            public int Time;
            public IntPtr Extra;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static extern IntPtr SetMouseHook(int hookId, MouseHookProc callback,
            IntPtr module, uint thread);

        [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookMouseHook(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetHookModule(string name);

        // Held in a field for as long as the hook is installed: the delegate is
        // the only managed reference the system has, and a collected one is a
        // callback into freed memory.
        private readonly MouseHookProc _mouseHookCallback;
        private IntPtr _mouseHook;
        private bool _swallowedRightPress;
        private bool _swallowedMiddlePress;

        /// <summary>
        /// Hands the whole widget to whatever is underneath while it is pinned.
        /// The extended style is read by the system before any window procedure
        /// runs and covers the window's entire tree, which is the only thing
        /// that does: the readings are controls, and a control answers its own
        /// hit test.
        ///
        /// Written on the pin changing and never per click.  Writing the
        /// extended style of a layered window empties it, so the frame it costs
        /// is handed straight back.
        /// </summary>
        private void SyncPinnedClickThrough()
        {
            if (!IsHandleCreated)
                return;
            if (!NativeUi.SetClickThrough(Handle, _pinned))
                return;
            DiagLog.Write("widget click-through " + (_pinned ? "on" : "off") +
                " exstyle=" + NativeUi.DescribeExStyle(Handle));
            _composeDirty = true;
            ComposeIfDirty();
        }

        private bool PointOverPinButton(Point screen)
        {
            return _pinButton != null && !_pinButton.IsDisposed &&
                _pinButton.Visible && _pinButton.IsHandleCreated &&
                _pinButton.RectangleToScreen(_pinButton.ClientRectangle).Contains(screen);
        }

        private Rectangle[] GetWorkingAreas()
        {
            if (_dragWorkingAreas != null)
                return _dragWorkingAreas;
            return Screen.AllScreens
                .Select(delegate(Screen screen) { return screen.WorkingArea; })
                .ToArray();
        }

        private static Rectangle GetWorkingAreaAt(Rectangle[] workingAreas, Point point)
        {
            foreach (Rectangle area in workingAreas)
            {
                if (area.Contains(point))
                    return area;
            }
            return workingAreas.Length > 0 ? workingAreas[0] : Screen.PrimaryScreen.WorkingArea;
        }

        private static bool IsRectangleInsideDesktop(Rectangle rectangle, Rectangle[] workingAreas)
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
                bool covered = false;
                foreach (Rectangle area in workingAreas)
                {
                    if (area.Contains(corner))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        private TextReadout MakeLabel(string text, Point location, Size size, float fontSize, FontStyle style, Color color)
        {
            TextReadout label = new TextReadout();
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

        /// <summary>
        /// What a header button falls back to once the pointer leaves it.  With
        /// the panel gone and per-pixel alpha on, that is nothing at all: the
        /// colour-keyed path had to paint the key there instead.
        /// </summary>
        private Color HeaderButtonBackground
        {
            get
            {
                if (!_backgroundless)
                    return BackColor;
                return LayeredMode ? HeaderButtonGhost : BackgroundKey;
            }
        }

        /// <summary>
        /// Almost nothing, but not nothing.
        ///
        /// A layered window is hit-tested through its own alpha, and a pixel at
        /// zero is not part of the window at all - the click goes to whatever is
        /// behind it.  A button painted on nothing therefore only answered on
        /// the strokes of its own glyph; everywhere else the press fell through
        /// to the catcher underneath and became the start of a drag.  Which is
        /// the button "not working until you click the widget first".
        ///
        /// Four counts of alpha is invisible - a twentieth of the faintest step
        /// the panel itself uses - and it is the whole rectangle back.
        /// </summary>
        private static readonly Color HeaderButtonGhost = Color.FromArgb(4, 26, 30, 37);

        private void AddHeaderHover(Button button, Color hoverColor)
        {
            button.MouseEnter += delegate
            {
                if (button.Enabled)
                    button.BackColor = hoverColor;
            };
            button.MouseLeave += delegate { button.BackColor = HeaderButtonBackground; };
        }

        private int HeaderButtonWidth(Button button)
        {
            // Keep one compact spacing scheme at every width.  Switching
            // between a loose and a compact scheme at 140 px made the whole
            // group jump while the window crossed that breakpoint.  Only the
            // two buttons that carry more than a single glyph are wider.
            if (ReferenceEquals(button, _expandButton))
                return 20;
            if (ReferenceEquals(button, _languageButton))
                return 30;
            return 17;
        }

        /// <summary>
        /// Decides which header buttons exist right now and packs them against
        /// the right edge.  Visibility is settled here rather than by the
        /// callers, so the row cannot be flipped twice inside one layout pass.
        /// </summary>
        private void LayoutHeaderButtons()
        {
            bool headerHidden = IsHeaderHidden();
            // Left to right this is only where the buttons sit; which of them
            // survives a narrow window is decided further down.  The swap button
            // leads the row because it is the one control that changes what the
            // widget is showing rather than how it looks, so it reads as part of
            // the content, not as window chrome.
            Button[] order =
            {
                _cycleButton, _opacityButton, _backgroundButton,
                _languageButton, _pinButton, _expandButton
            };
            List<Button> placed = new List<Button>();
            int total = 0;
            foreach (Button button in order)
            {
                // A disabled flat button paints as an empty square: the icon
                // looks like it failed to draw while still holding its place in
                // the row.  Drop it out entirely and let the rest close the gap.
                // Pinned, every click goes through the widget except the one on
                // the pin itself, so the rest of the row is a row of controls
                // that look pressable and are not.  The pin stays because it is
                // the way back out with a mouse.
                if (headerHidden ||
                    (_pinned && !ReferenceEquals(button, _pinButton)) ||
                    (ReferenceEquals(button, _cycleButton) && !_compactCycleAvailable))
                    continue;
                placed.Add(button);
                total += HeaderButtonWidth(button);
            }

            // Narrow windows give buttons up in order of how often they are
            // actually used, not by where they sit in the row.  Swapping cards
            // and dropping the background are what the widget is for at its
            // narrowest; opacity is a slider that can be turned with the wheel
            // once it is open, and language is set once and then never again.
            // Everything dropped here is still in the right-click menu; the pin
            // and the collapse arrow are not worth hunting for and never go.
            Button[] dropOrder =
            {
                _opacityButton, _languageButton, _backgroundButton, _cycleButton
            };
            int available = Math.Max(0, ClientSize.Width - 4);
            int candidate = 0;
            while (candidate < dropOrder.Length && total > available)
            {
                if (placed.Remove(dropOrder[candidate]))
                    total -= HeaderButtonWidth(dropOrder[candidate]);
                candidate++;
            }

            foreach (Button button in order)
                button.Visible = placed.Contains(button);

            _headerButtonsWidth = total;
            int x = Math.Max(0, ClientSize.Width - total);
            foreach (Button button in placed)
            {
                int width = HeaderButtonWidth(button);
                button.Bounds = new Rectangle(x, 1, width, 25);
                x += width;
            }
        }

        /// <summary>
        /// The buttons are packed against the right edge first, so whatever is
        /// left over belongs to the name.  It appears only when the whole word
        /// fits in that gap: a name cut mid-letter reads as a rendering fault,
        /// and a fixed width threshold either clips it or hides it while there
        /// is still room, depending on how many buttons happen to be up.
        /// </summary>
        private void LayoutHeaderTitle(bool headerHidden)
        {
            const int titleLeft = 12;
            const int titleGap = 10;
            _title.Text = "TRAYMETRY";
            int room = ClientSize.Width - _headerButtonsWidth - titleLeft - titleGap;
            _title.Size = new Size(Math.Max(1, room), 22);
            _title.Visible = !headerHidden &&
                room >= TextRenderer.MeasureText(_title.Text, _title.Font).Width;
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

        /// <summary>
        /// The interface thread is meant to come back here every 40 ms.  When it
        /// does not, that gap is the stutter the user sees, and it is written
        /// down with the machine state at that moment - which is the only way to
        /// tell a widget that was busy from a widget whose pages were taken
        /// away while nobody was using it.
        /// </summary>
        private void NoteUiTick()
        {
            DateTime now = DateTime.UtcNow;
            double gap = (now - _lastUiTick).TotalMilliseconds;
            _lastUiTick = now;
            long cpu = DiagLog.ProcessorMilliseconds();
            long spent = _lastUiCpuMs < 0 || cpu < 0 ? -1 : cpu - _lastUiCpuMs;
            _lastUiCpuMs = cpu;
            const double StallMilliseconds = 250;
            if (gap >= StallMilliseconds)
                DiagLog.Write("ui stall " +
                    Math.Round(gap).ToString(CultureInfo.InvariantCulture) + "ms" +
                    // The whole question in one number.  Work done across the
                    // gap that is a fair share of the gap is a widget that was
                    // busy; a gap of a second that cost twenty milliseconds is a
                    // widget that was not being run, and the reason for that is
                    // in the machine figures rather than in this program.
                    " cpu=" + spent.ToString(CultureInfo.InvariantCulture) + "ms " +
                    DiagLog.DescribeProcess() + " " + DiagLog.DescribeMachine() + " " +
                    DescribeWidgetState());
            if ((now - _lastHeartbeat).TotalSeconds < 300)
                return;
            _lastHeartbeat = now;
            DiagLog.Write("heartbeat " + DiagLog.DescribeProcess() +
                " " + DiagLog.DescribeMachine() +
                " reads=" + _sensorReadCount +
                " read.last=" + _lastSensorReadMs + "ms" +
                " read.max=" + _maxSensorReadMs + "ms " +
                DescribeWidgetState());
        }

        private long _lastUiCpuMs = -1;

        private string DescribeWidgetState()
        {
            return "visible=" + (Visible ? "1" : "0") +
                " pinned=" + (_pinned ? "1" : "0") +
                " layered=" + (LayeredMode ? "1" : "0") +
                " expanded=" + (_expanded ? "1" : "0");
        }

        /// <summary>
        /// Writes the file a user attaches to a bug report and shows it to them
        /// in the folder, so "send the log" is one click and not a walk through
        /// AppData.
        /// </summary>
        private void CollectProblemReport()
        {
            try
            {
                DiagLog.Write("report requested " + DiagLog.DescribeProcess() + " " +
                    DescribeWidgetState());
                string path = DiagnosticReport.Write(
                    DiagnosticReport.ReadSettings(), DescribeSensors());
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch { }
                MessageBox.Show(this, Loc.T("report.done", path), "Traymetry",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                DiagLog.Write("report failed " + error.GetType().Name + ": " + error.Message);
                MessageBox.Show(this, Loc.T("report.failed", error.Message), "Traymetry",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string DescribeSensors()
        {
            SensorSnapshot snapshot = _lastSnapshot;
            StringBuilder text = new StringBuilder();
            text.AppendLine("reads=" + _sensorReadCount +
                " last=" + _lastSensorReadMs + "ms max=" + _maxSensorReadMs + "ms");
            if (snapshot == null)
                return text.AppendLine("(no snapshot yet)").ToString();
            text.AppendLine("cpu: " + snapshot.CpuName +
                " temp=" + Round(snapshot.Temperature) +
                " load=" + Round(snapshot.Usage) +
                " clock=" + Round(snapshot.ClockMhz) +
                " power=" + Round(snapshot.PowerWatts));
            text.AppendLine("gpu: " + snapshot.GpuName +
                " temp=" + Round(snapshot.GpuTemperature) +
                " load=" + Round(snapshot.GpuUsage) +
                " clock=" + Round(snapshot.GpuClockMhz) +
                " power=" + Round(snapshot.GpuPowerWatts) +
                " memory=" + Round(snapshot.GpuMemoryUsedGb) + "/" +
                Round(snapshot.GpuMemoryTotalGb));
            text.AppendLine("memory: " + Round(snapshot.MemoryUsedGb) + "/" +
                Round(snapshot.MemoryTotalGb) + " at " + Round(snapshot.MemoryClockMhz));
            text.AppendLine("storage: " + Round(snapshot.StorageUsedGb) + "/" +
                Round(snapshot.StorageTotalGb) +
                " drives=" + (snapshot.StorageDriveNames == null
                    ? 0 : snapshot.StorageDriveNames.Length));
            text.AppendLine("fans: " + (snapshot.FanNames == null ? 0 : snapshot.FanNames.Length));
            text.AppendLine("frame telemetry state: " + snapshot.FrameTelemetryState +
                " processes=" + (snapshot.FrameProcessIds == null
                    ? 0 : snapshot.FrameProcessIds.Length));
            return text.ToString();
        }

        private static string Round(double value)
        {
            return Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
        }

        private void NoteSensorRead(long milliseconds)
        {
            _sensorReadCount++;
            _lastSensorReadMs = milliseconds;
            if (milliseconds > _maxSensorReadMs)
                _maxSensorReadMs = milliseconds;
            const long SlowRead = 400;
            if (milliseconds >= SlowRead)
                DiagLog.Write("sensor read " +
                    milliseconds.ToString(CultureInfo.InvariantCulture) + "ms");
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
                            // Timed because a slow answer from the sensor
                            // service looks exactly like a slow widget from the
                            // outside, and the two are fixed in different files.
                            Stopwatch read = Stopwatch.StartNew();
                            SensorSnapshot snapshot = session.ReadSnapshot(_frameTelemetryDemand);
                            read.Stop();
                            NoteSensorRead(read.ElapsedMilliseconds);
                            PostSnapshot(snapshot);
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
            // A move or resize runs inside a modal loop that still dispatches the
            // marshalled sensor update.  Repainting every card there competes with
            // the drag for the UI thread and, with the transparency key on, forces
            // a full layered-window recomposite - the widget then stutters once
            // per sensor tick.  Render the pending snapshot at ExitSizeMove.
            if (_interactiveResize)
            {
                _pendingSnapshotRender = true;
                return;
            }

            Color cpuColor = CpuStatusColor(snapshot.Temperature);
            bool gpuDetected = !String.IsNullOrWhiteSpace(snapshot.GpuName);
            Color gpuColor = GpuStatusColor(snapshot.GpuTemperature, gpuDetected);
            double memoryPercent = snapshot.MemoryTotalGb > 0 ? snapshot.MemoryUsedGb / snapshot.MemoryTotalGb : 0;
            RenderCompactCards(snapshot, true);

            _cpuName.Text = String.IsNullOrWhiteSpace(snapshot.CpuName) ? "CPU" : snapshot.CpuName;
            _cpuTemperature.SetValue(FormatTemperature(snapshot.Temperature), cpuColor);
            _cpuUsage.SetValue(Math.Round(snapshot.Usage).ToString("0", CultureInfo.InvariantCulture) + "%", Color.White);
            _cpuClock.SetValue(FormatClockGhz(snapshot.ClockMhz), Color.White);
            _cpuPower.SetValue(FormatPower(snapshot.PowerWatts), Color.White);

            _gpuName.Text = String.IsNullOrWhiteSpace(snapshot.GpuName) ? Loc.T("gpu.notDetected") : snapshot.GpuName;
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
                FormatClockGhz(snapshot.ClockMhz), Loc.T("caption.clock"),
                FormatTemperature(snapshot.Temperature), Loc.T("caption.temperature"),
                FormatPower(snapshot.PowerWatts), Loc.T("caption.power"));
            _gpuGauge.SetData(snapshot.GpuUsage / 100.0,
                snapshot.GpuTemperature > 0 ? Math.Round(snapshot.GpuUsage).ToString("0", CultureInfo.InvariantCulture) + "%" : "—",
                "LOAD",
                gpuColor);
            _gpuGauge.SetAuxiliary(
                FormatClockMhz(snapshot.GpuClockMhz), Loc.T("caption.clock"),
                FormatTemperature(snapshot.GpuTemperature), Loc.T("caption.temperature"),
                snapshot.GpuMemoryTotalGb > 0
                    ? snapshot.GpuMemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + snapshot.GpuMemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                    : "—", Loc.T("caption.gpuMemory"));
            AddHistorySample(_cpuHistory, snapshot, _leftGraphSource);
            AddHistorySample(_gpuHistory, snapshot, _rightGraphSource);
            string memoryDetails = snapshot.MemoryClockMhz > 0
                ? snapshot.MemoryClockMhz.ToString("0", CultureInfo.InvariantCulture) + " MHz  ·  " +
                    (memoryPercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : Loc.T("caption.usagePadded") + (memoryPercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            _memorySummary.SetUsage(memoryPercent,
                snapshot.MemoryUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " + snapshot.MemoryTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB",
                memoryDetails,
                GetCardAccent(CompactCardKind.Memory));
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
                _storageSummary.SetTitle(Loc.T("caption.storage"));
                double aggregatePercent = snapshot.StorageTotalGb > 0 ? snapshot.StorageUsedGb / snapshot.StorageTotalGb : 0;
                _storageSummary.SetUsage(aggregatePercent,
                    snapshot.StorageUsedGb.ToString("0", CultureInfo.InvariantCulture) + " / " + snapshot.StorageTotalGb.ToString("0", CultureInfo.InvariantCulture) + " GB",
                    Loc.T("caption.usedPadded") + (aggregatePercent * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                    GetCardAccent(CompactCardKind.Storage));
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
            _storageSummary.SetTitle(Loc.T("caption.storagePadded") + names[selectedIndex] + "   ▾");
            _storageSummary.SetUsage(percent,
                used[selectedIndex].ToString("0", CultureInfo.InvariantCulture) + " / " + totals[selectedIndex].ToString("0", CultureInfo.InvariantCulture) + " GB",
                Loc.T("caption.usedPadded") + (percent * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                GetCardAccent(CompactCardKind.Storage));
            EnsureStorageMenu(names.Take(count).ToArray());
        }

        /// <summary>
        /// Drops the drive list out of the "▾" in the card title, wherever inside
        /// the card the press landed.  A list that appeared under the pointer
        /// looked unrelated to the glyph that advertises it.
        /// </summary>
        /// <summary>
        /// Opens the drive list when the press belongs to the storage panel,
        /// anywhere on it.  Both the panel itself and the click catcher come
        /// through here: without a background the panel is mostly holes - the
        /// window only owns the pixels it painted - so a press between the
        /// letters falls through to the catcher, where it used to start a drag.
        /// </summary>
        private bool TryOpenStorageMenu(Point screenPoint)
        {
            if (!_storageSummary.Visible || !_storageSummary.Enabled ||
                _storageMenu.Items.Count == 0)
                return false;
            if (!_storageSummary.RectangleToScreen(_storageSummary.ClientRectangle)
                .Contains(screenPoint))
                return false;
            // Closing the drop-down swallows the click that dismissed it, so the
            // press that follows must not be read as "open again" - that is what
            // made every second click on the card look like a dead one.
            int sinceClosed = unchecked(Environment.TickCount - _storageMenuClosedTick);
            if (sinceClosed >= 0 && sinceClosed < 250)
                return true;
            ShowStorageMenu();
            return true;
        }

        private void ShowStorageMenu()
        {
            Rectangle caret = _storageSummary.TitleCaretBounds;
            Point anchor = caret.IsEmpty
                ? new Point(10, Math.Max(0, Math.Min(24, _storageSummary.Height - 4)))
                : new Point(caret.Left, caret.Bottom + 2);
            // The drop-down opens to the right of the anchor, so it is pulled
            // back inside a card too narrow to hold it.
            int menuWidth = Math.Max(54, _storageMenu.PreferredSize.Width);
            anchor.X = Math.Max(0, Math.Min(anchor.X, _storageSummary.Width - menuWidth));
            _storageMenu.Show(_storageSummary, anchor, ToolStripDropDownDirection.BelowRight);
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
            _cpuName.Text = Loc.T("state.waitingSensors");
            _gpuName.Text = Loc.T("state.waitingSensors");
            foreach (MetricReadout metric in new[] { _cpuTemperature, _cpuUsage, _cpuClock, _cpuPower, _gpuTemperature, _gpuUsage, _gpuClock, _gpuPower })
                metric.SetValue("—", muted);
            _gpuMemory.Text = "VRAM   —";
            _cpuGauge.SetData(0, "—", Loc.T("state.waiting"), Color.FromArgb(100, 110, 124));
            _gpuGauge.SetData(0, "—", Loc.T("state.waiting"), Color.FromArgb(100, 110, 124));
            _memorySummary.SetUsage(0, "—", Loc.T("state.waiting"), Color.FromArgb(100, 110, 124));
            _storageSummary.SetUsage(0, "—", Loc.T("state.waiting"), Color.FromArgb(100, 110, 124));
            _tray.Text = Loc.T("tray.waiting");
        }

        // The CPU keeps its identity colour whenever nothing is wrong, including
        // when the temperature sensor is unavailable.  The previous version fell
        // back to muted grey there, which made the whole card fade out.  A custom
        // accent replaces the identity colour only; the warning colours still win.
        private Color CpuStatusColor(double temperature)
        {
            if (temperature >= 85)
                return Color.FromArgb(255, 93, 108);
            if (temperature >= 70)
                return Color.FromArgb(255, 184, 77);
            return GetCardAccent(CompactCardKind.Cpu);
        }

        private Color GpuStatusColor(double temperature, bool detected)
        {
            if (!detected)
                return Color.FromArgb(150, 158, 169);
            if (temperature >= 85)
                return Color.FromArgb(255, 93, 108);
            if (temperature >= 70)
                return Color.FromArgb(255, 184, 77);
            return GetCardAccent(CompactCardKind.Gpu);
        }

        internal static Color GetDefaultCardAccent(CompactCardKind kind)
        {
            switch (kind)
            {
                case CompactCardKind.Cpu: return CpuAccent;
                case CompactCardKind.Gpu: return GpuAccent;
                case CompactCardKind.Memory: return MemoryAccent;
                case CompactCardKind.Network: return NetworkAccent;
                case CompactCardKind.Storage: return StorageAccent;
                case CompactCardKind.Fans: return FansAccent;
                case CompactCardKind.Fps: return FpsAccent;
                default: return Color.FromArgb(150, 158, 169);
            }
        }

        private Color GetCardAccent(CompactCardKind kind)
        {
            Color custom;
            return _cardAccents.TryGetValue(kind, out custom)
                ? custom
                : GetDefaultCardAccent(kind);
        }

        private void SetCardAccent(CompactCardKind kind, Color? accent)
        {
            // The saved palette is a set the user chose to keep, not a running
            // copy of whatever is on screen: it is written by "save as my
            // palette" and by nothing else, so it survives experimenting.
            if (accent.HasValue)
                _cardAccents[kind] = accent.Value;
            else
                _cardAccents.Remove(kind);
            RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
            RefreshHistoryPanels();
            SaveSettings();
        }

        private static string SerializeCardAccents(Dictionary<CompactCardKind, Color> accents)
        {
            return String.Join(";", accents
                .OrderBy(delegate(KeyValuePair<CompactCardKind, Color> pair) { return (int)pair.Key; })
                .Select(delegate(KeyValuePair<CompactCardKind, Color> pair)
                {
                    return pair.Key + "=" + pair.Value.R.ToString("X2", CultureInfo.InvariantCulture) +
                        pair.Value.G.ToString("X2", CultureInfo.InvariantCulture) +
                        pair.Value.B.ToString("X2", CultureInfo.InvariantCulture);
                })
                .ToArray());
        }

        private static Dictionary<CompactCardKind, Color> ParseCardAccents(string value)
        {
            Dictionary<CompactCardKind, Color> accents = new Dictionary<CompactCardKind, Color>();
            if (String.IsNullOrWhiteSpace(value))
                return accents;
            foreach (string entry in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('=');
                if (parts.Length != 2 || parts[1].Trim().Length != 6)
                    continue;
                CompactCardKind kind;
                int packed;
                if (!TryParseCompactCardKind(parts[0].Trim(), out kind))
                    continue;
                if (!Int32.TryParse(parts[1].Trim(), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out packed))
                    continue;
                accents[kind] = Color.FromArgb((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
            }
            return accents;
        }

        private static bool TryParseCompactCardKind(string name, out CompactCardKind kind)
        {
            foreach (CompactCardKind candidate in Enum.GetValues(typeof(CompactCardKind)))
            {
                if (String.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    kind = candidate;
                    return true;
                }
            }
            kind = CompactCardKind.Cpu;
            return false;
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

        private static CompactCardKind[] CreateSystemGraphPreset()
        {
            return new[] { CompactCardKind.Cpu, CompactCardKind.Gpu };
        }

        private static CompactCardKind[] CreateGamingGraphPreset()
        {
            return new[] { CompactCardKind.Fps, CompactCardKind.Gpu };
        }

        /// <summary>
        /// Creates a menu item whose caption is retranslated in place whenever
        /// the language changes, so the menu never has to be rebuilt.
        /// </summary>
        private ToolStripMenuItem LocalizedItem(string key)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(Loc.T(key));
            _localizedItems.Add(new KeyValuePair<ToolStripItem, string>(item, key));
            return item;
        }

        private static CompactCardKind[] AvailableCompactCardKinds()
        {
            return new[]
            {
                CompactCardKind.Cpu,
                CompactCardKind.Gpu,
                CompactCardKind.Memory,
                CompactCardKind.Network,
                CompactCardKind.Storage,
                CompactCardKind.Fans,
                CompactCardKind.Fps
            };
        }

        private ToolStripMenuItem CreateCompactCardsMenu()
        {
            ToolStripMenuItem root = LocalizedItem("menu.cards");
            _compactCardsRoot = root;
            root.DropDownItems.Add(CreateCompactPresetMenuItem("preset.system", CreateSystemCompactPreset()));
            root.DropDownItems.Add(CreateCompactPresetMenuItem("preset.gaming", CreateGamingCompactPreset()));
            _customPresetItem = LocalizedItem("preset.custom");
            _customPresetItem.Click += delegate
            {
                if (_customCompactPreset != null)
                    ApplyCompactSlots(_customCompactPreset, true);
            };
            root.DropDownItems.Add(_customPresetItem);
            // Written only when asked for.  The set used to be captured on every
            // slot the user touched, which meant it was never the arrangement
            // they meant to keep - the first experiment after saving overwrote
            // it, and nothing on screen said when that happened.
            ToolStripMenuItem savePresetItem = LocalizedItem("menu.preset.save");
            savePresetItem.Click += delegate
            {
                _customCompactPreset = NormalizeCompactSlotKinds(_compactSlotKinds);
                SaveSettings();
                RefreshCompactCardsMenu(_compactCardsRoot);
            };
            root.DropDownItems.Add(savePresetItem);
            root.DropDownItems.Add(new ToolStripSeparator());

            CompactCardKind[] availableKinds = AvailableCompactCardKinds();
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

            // Paging sits under the four cards it pages through, and it is the
            // one entry that expects to be clicked more than once: the menu is
            // held open so the user can watch the cards come round instead of
            // reopening three levels of menu for every step.
            root.DropDownItems.Add(new ToolStripSeparator());
            _cycleCardsItem = LocalizedItem("menu.cycleCards");
            _cycleCardsItem.Click += delegate
            {
                CycleCompactCards();
                RefreshCompactCardsMenu(root);
            };
            root.DropDownItems.Add(_cycleCardsItem);

            root.DropDownOpening += delegate { RefreshCompactCardsMenu(root); };
            RefreshCompactCardsMenu(root);
            return root;
        }

        /// <summary>
        /// Puts the open menus back above the widget.  Every switch in that menu
        /// ends in the window re-asserting itself as topmost - pinning, "always
        /// on top", dropping the background - and each of those lifts the widget
        /// over the very menu the switch was clicked in.  With per-pixel alpha
        /// the result is not a window in front of a menu but readings bleeding
        /// through it, which reads as a rendering fault.
        ///
        /// Now that the menu survives a click, it is on screen to be climbed
        /// over; before, it had already closed by the time the widget moved.
        /// </summary>
        private void RaiseOpenMenus()
        {
            RaiseDropDown(ContextMenuStrip);
        }

        /// <summary>
        /// Puts the widget and its catcher into the band they belong in right
        /// now, whatever they are in.  Called without a state comparison where
        /// the answer has to be right rather than merely unchanged - coming back
        /// from the tray above all, where a widget left in the wrong band is a
        /// widget the user cannot see and clicks the icon twice for.
        /// </summary>
        private void ApplyWindowBand()
        {
            const uint NoSize = 0x0001;
            const uint NoMove = 0x0002;
            const uint NoActivate = 0x0010;
            bool wanted = TopMost;
            IntPtr band = wanted ? TopMostWindow : NotTopMostWindow;
            if (wanted != _lastAppliedBand)
            {
                _lastAppliedBand = wanted;
                DiagLog.Write("band " + (wanted ? "topmost" : "normal") +
                    " menuOpen=" + (IsMenuOpen() ? "1" : "0"));
            }
            if (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                _backgroundHitForm.IsHandleCreated && _backgroundHitForm.Visible)
                NativeUi.SetWindowPos(_backgroundHitForm.Handle, band, 0, 0, 0, 0,
                    NoSize | NoMove | NoActivate);
            if (IsHandleCreated)
                NativeUi.SetWindowPos(Handle, band, 0, 0, 0, 0,
                    NoSize | NoMove | NoActivate);

            // And check that it took.  Asking for the topmost band is a no-op on
            // a window the system already counts as being in it, and the style
            // word this used to be read from is stale in exactly that case, so
            // the check fired on every single menu close and forced a transition
            // nothing needed - which the user sees, because forcing one drops
            // the window out of the band on the way through.  The z-order is
            // asked instead, and only a window really buried under a normal one
            // is dug back out.
            if (wanted && IsHandleCreated &&
                NativeUi.IsBuriedUnderNormalWindow(Handle))
                ForceTopMostBand();
        }

        private bool _lastAppliedBand;

        private bool _bandFallReported;

        private DateTime _lastBandComplaint;

        private static readonly IntPtr NotTopMostWindow = new IntPtr(-2);

        private bool IsMenuOpen()
        {
            return ContextMenuStrip != null && ContextMenuStrip.Visible;
        }

        /// <summary>
        /// Closes a menu the user has clicked away from.  A drop-down normally
        /// goes when its owner loses activation, but a right click on the
        /// widget need not have made this program the foreground one - over the
        /// catcher it deliberately does not - and a click in another program
        /// never enters this queue, so neither the framework's filter nor the
        /// one here ever hears about it.  The menu then stood until the user
        /// came back and clicked the widget again.
        ///
        /// The button state is read from the system rather than from messages
        /// for that reason, on the tick that is already running.
        /// </summary>
        private void CloseMenusOnOutsideClick()
        {
            bool down = NativeUi.AnyMouseButtonDown();
            bool pressed = down && !_outsideButtonDown;
            _outsideButtonDown = down;

            Point cursor = Cursor.Position;
            // Which window was clicked last, for the wheel while pinned - see
            // PreFilterMessage.  Taken here because this is the one place that
            // hears a press wherever it landed, and on the edge because it is
            // about which press was the most recent one.
            if (pressed)
                _widgetClickedLast = OverWidget(cursor);
            // Dismissal, though, is asked of the button being down at all and
            // not of the edge.  A click lasts a tenth of a second and this runs
            // every fortieth, which is plenty - right up until the widget stalls
            // for a second, and then the whole press falls into the gap, no edge
            // is ever seen, and the menu stands there until something else
            // closes it.  Held down and outside is the same instruction.
            if (!down)
                return;
            if (IsMenuOpen())
            {
                if (!PointOverDropDown(ContextMenuStrip, cursor))
                {
                    DiagLog.Write("menu closed by outside click at " +
                        cursor.X.ToString(CultureInfo.InvariantCulture) + "," +
                        cursor.Y.ToString(CultureInfo.InvariantCulture));
                    ContextMenuStrip.Close(ToolStripDropDownCloseReason.AppClicked);
                }
                return;
            }
            if (_opacityPopupVisible && _opacityCard != null &&
                !_opacityCard.IsDisposed && !_opacityCard.Bounds.Contains(cursor) &&
                !OverWidget(cursor))
                CloseOpacityPopup();
        }

        private bool OverWidget(Point screen)
        {
            return (Visible && Bounds.Contains(screen)) ||
                (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                    _backgroundHitForm.Visible && _backgroundHitForm.Bounds.Contains(screen));
        }

        /// <summary>
        /// Whether the point is on the menu or on any sub-menu standing under
        /// it.  Only the root is a child of this form; a sub-menu is a window of
        /// its own and a press on one has to count as a press on the menu.
        /// </summary>
        private static bool PointOverDropDown(ToolStripDropDown dropDown, Point screen)
        {
            if (dropDown == null || !dropDown.Visible)
                return false;
            if (dropDown.Bounds.Contains(screen))
                return true;
            foreach (ToolStripItem raw in dropDown.Items)
            {
                ToolStripDropDownItem branch = raw as ToolStripDropDownItem;
                if (branch != null && branch.HasDropDownItems &&
                    PointOverDropDown(branch.DropDown, screen))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Puts off showing or hiding the catcher until the menu is gone.  One
        /// subscription at a time, dropped as soon as it fires, so a menu opened
        /// and closed twenty times does not end up with twenty handlers.
        /// </summary>
        private void DeferCatcherSync()
        {
            if (_catcherSyncDeferred || ContextMenuStrip == null)
                return;
            _catcherSyncDeferred = true;
            ContextMenuStrip.Closed += SyncCatcherAfterMenu;
        }

        private void SyncCatcherAfterMenu(object sender, ToolStripDropDownClosedEventArgs e)
        {
            ContextMenuStrip.Closed -= SyncCatcherAfterMenu;
            _catcherSyncDeferred = false;
            SyncBackgroundHitForm();
        }

        /// <summary>
        /// Lifts a drop-down the moment it appears.  The widget declares itself
        /// topmost and a menu does not, so a menu opened over the widget is
        /// behind it from the start: the readings show through it until the
        /// pointer moves onto the menu and the menu repositions itself, which is
        /// what made this look like a rendering fault that fixes itself.
        /// </summary>
        private void RaiseWhenOpened(ToolStripDropDown dropDown)
        {
            if (dropDown == null)
                return;
            // Removed before it is added, so this can be called again on a menu
            // whose entries were rebuilt without ending up raising twice.
            dropDown.Opened -= DropDownOpened;
            dropDown.Opened += DropDownOpened;
            KeepOutOfTaskbar(dropDown);
            HookBranches(dropDown);
        }

        /// <summary>
        /// Takes a menu window out of the taskbar.  The window is made here and
        /// not left until the menu is first shown on purpose: the shell decides
        /// whether a window earns a taskbar button as it appears, so a style set
        /// afterwards still leaves one showing for the first open of each menu.
        /// Re-applied on the handle as well, because the framework rebuilds it
        /// whenever a drop-down's own window flags change.
        /// </summary>
        private static void KeepOutOfTaskbar(ToolStripDropDown dropDown)
        {
            if (dropDown == null || dropDown.IsDisposed)
                return;
            dropDown.HandleCreated -= MarkAsToolWindow;
            dropDown.HandleCreated += MarkAsToolWindow;
            NativeUi.SetToolWindow(dropDown.Handle);
        }

        private static void MarkAsToolWindow(object sender, EventArgs e)
        {
            Control control = sender as Control;
            if (control != null && !control.IsDisposed && control.IsHandleCreated)
                NativeUi.SetToolWindow(control.Handle);
        }

        private void DropDownOpened(object sender, EventArgs e)
        {
            ToolStripDropDown target = sender as ToolStripDropDown;
            if (target == null)
                return;
            // Raised and sunk against the root, never against the branch that
            // just opened.  Sinking below the branch put the widget under that
            // one window and above every other - including the menu the branch
            // belongs to - so running the pointer down entries that open
            // sub-menus flashed the widget over the menu until the next tick
            // straightened it out.
            ToolStripDropDown root = RootDropDown(target);
            RaiseDropDown(root);
            // Walking the tree once at startup only reaches the branches that
            // existed then.  The card, graph and colour lists are rebuilt to
            // show what is current, and every branch built after that opened
            // without a handler of its own: unraised, it spent up to one tick
            // underneath the widget, which is the widget flashing over the menu
            // for an instant as the pointer runs down the entries.
            HookBranches(target);
            // Once more after the window has finished being placed: at Opened
            // it may still be moving into position, and a raise against a
            // window that is about to be repositioned does not survive the
            // reposition.
            if (IsHandleCreated)
                BeginInvoke(new Action(delegate { RaiseDropDown(root); }));
        }

        /// <summary>
        /// The menu a drop-down ultimately hangs off.  The widget has to sit
        /// below that one: the root is the lowest of the menu windows once they
        /// have been raised parents-first, so sitting below it is sitting below
        /// all of them.
        /// </summary>
        private static ToolStripDropDown RootDropDown(ToolStripDropDown dropDown)
        {
            ToolStripDropDown current = dropDown;
            for (int guard = 0; guard < 16; guard++)
            {
                ToolStripItem owner = current.OwnerItem;
                ToolStripDropDown parent = owner == null
                    ? null
                    : owner.Owner as ToolStripDropDown;
                if (parent == null)
                    return current;
                current = parent;
            }
            return current;
        }

        private void HookBranches(ToolStripDropDown dropDown)
        {
            foreach (ToolStripItem raw in dropDown.Items)
            {
                ToolStripDropDownItem branch = raw as ToolStripDropDownItem;
                if (branch == null || !branch.HasDropDownItems)
                    continue;
                // A sub-menu is a window of its own and takes none of the
                // colours of the menu it belongs to, which left every branch
                // opening pale against a dark parent.
                branch.DropDown.BackColor = dropDown.BackColor;
                branch.DropDown.ForeColor = dropDown.ForeColor;
                RaiseWhenOpened(branch.DropDown);
            }
        }

        private static void RaiseDropDown(ToolStripDropDown dropDown)
        {
            if (dropDown == null || !dropDown.Visible || !dropDown.IsHandleCreated)
                return;
            const uint NoSize = 0x0001;
            const uint NoMove = 0x0002;
            const uint NoActivate = 0x0010;
            NativeUi.SetWindowPos(dropDown.Handle, TopMostWindow, 0, 0, 0, 0,
                NoSize | NoMove | NoActivate);
            // Parents first: each call lands its window on top of the topmost
            // band, so a sub-menu raised after its parent stays over it.
            foreach (ToolStripItem raw in dropDown.Items)
            {
                ToolStripDropDownItem branch = raw as ToolStripDropDownItem;
                if (branch != null && branch.HasDropDownItems)
                    RaiseDropDown(branch.DropDown);
            }
        }

        private static readonly IntPtr TopMostWindow = new IntPtr(-1);

        /// <summary>
        /// Makes a whole drop-down sticky: its entries survive their own click,
        /// and so does every menu they hang off.  Settings are chosen in runs -
        /// a preset, then a slot, then a colour, then a tick - and a menu that
        /// closes after each one turns a minute of tuning into a minute of
        /// reopening three levels of menu.
        ///
        /// Entries that lead somewhere else are left out through
        /// <see cref="_menuClosesOnClick"/>: they end the visit to the menu
        /// rather than continue it, so the menu has no business standing there.
        /// </summary>
        private void HoldOpenOnClick(ToolStripDropDown dropDown)
        {
            if (dropDown == null)
                return;
            KeepOpenOnHeldClick(dropDown);
            foreach (ToolStripItem raw in dropDown.Items)
            {
                ToolStripDropDownItem branch = raw as ToolStripDropDownItem;
                if (branch != null && branch.HasDropDownItems)
                {
                    // A heading is not clicked, it is opened; what is clicked
                    // sits one level further in.
                    HoldOpenOnClick(branch.DropDown);
                    continue;
                }
                HoldOpenOnClick(raw);
            }
        }

        /// <summary>
        /// Arms one entry, on the press rather than on the click: a drop-down
        /// raises Closing while the button is still going up, so a flag set from
        /// a Click handler is read after the menu has already made up its mind.
        /// </summary>
        private void HoldOpenOnClick(ToolStripItem item)
        {
            if (item == null || item is ToolStripSeparator || _menuClosesOnClick.Contains(item))
                return;
            item.MouseDown += delegate { _keepMenuOpenAfterClick = true; };
            // Sliding off the entry before letting go is not a click, and must
            // not leave a reprieve lying around for whatever is clicked next.
            item.MouseLeave += delegate { _keepMenuOpenAfterClick = false; };
            // A menu that stayed open through a click is a menu showing the
            // setting as it was before that click: the ticks and the captions
            // are refreshed when a drop-down opens, and this one never closed.
            // Without this the entry reads as dead - the widget changed behind
            // the menu, and the list the user is looking at did not.
            item.Click += delegate
            {
                if (IsHandleCreated)
                    BeginInvoke(new Action(AfterHeldMenuClick));
                else
                    RefreshOpenMenuState();
            };
        }

        /// <summary>
        /// Runs once the setting the click asked for has been applied.  Anything
        /// that resizes or reshapes the widget puts it back at the front of the
        /// topmost band, and the menu it was chosen from is topmost too - so the
        /// menu ends up behind the readings, which reads as the menu going
        /// transparent.  Raising it here covers every sticky entry at once,
        /// whatever it changed.
        /// </summary>
        private void AfterHeldMenuClick()
        {
            RefreshOpenMenuState();
            RaiseOpenMenus();
        }

        /// <summary>
        /// Replays what the drop-downs do when they open, for the ones that are
        /// already open.  Cheap enough to run on every click of a sticky entry:
        /// it walks a few dozen menu items and assigns text nobody repaints
        /// unless it changed.
        /// </summary>
        private void RefreshOpenMenuState()
        {
            if (_compactCardsRoot != null)
                RefreshCompactCardsMenu(_compactCardsRoot);
            if (_graphsRoot != null)
                RefreshGraphsMenu(_graphsRoot);
            if (_cardColorRoot != null)
                RefreshCardColorMenu(_cardColorRoot);
            UpdateHeaderMenuState();
        }

        /// <summary>
        /// Keeps a menu on screen for an entry that was armed by its own press.
        /// Cancelling here holds the whole chain open: the context menu only
        /// closes behind a drop-down that closed first, so a drop-down that
        /// stays takes its parent with it.
        /// </summary>
        private void KeepOpenOnHeldClick(ToolStripDropDown dropDown)
        {
            dropDown.Closing += delegate(object sender, ToolStripDropDownClosingEventArgs e)
            {
                if (!_keepMenuOpenAfterClick ||
                    e.CloseReason != ToolStripDropDownCloseReason.ItemClicked)
                    return;
                e.Cancel = true;
                // Every menu of the chain raises Closing inside the same click,
                // so the reprieve is dropped through the message queue: after
                // all of them have seen it, before anything else is clicked.
                if (IsHandleCreated)
                    BeginInvoke(new Action(delegate { _keepMenuOpenAfterClick = false; }));
                else
                    _keepMenuOpenAfterClick = false;
            };
        }

        private ToolStripMenuItem CreateCardColorMenu(CompactCardKind[] availableKinds)
        {
            ToolStripMenuItem root = LocalizedItem("menu.valueColour");
            _cardColorRoot = root;
            // Same shape as the cards and the graphs menus: the whole set at the
            // top, the individual pieces under it.  The palette used to sit at
            // the bottom, so the one menu in three that was read from the other
            // end was this one.
            _customPaletteItem = LocalizedItem("menu.color.myPaletteEmpty");
            _customPaletteItem.Click += delegate { ApplyCustomPalette(); };
            root.DropDownItems.Add(_customPaletteItem);
            ToolStripMenuItem savePaletteItem = LocalizedItem("menu.color.savePalette");
            savePaletteItem.Click += delegate
            {
                _customCardAccents = new Dictionary<CompactCardKind, Color>(_cardAccents);
                SaveSettings();
                RefreshCardColorMenu(_cardColorRoot);
            };
            root.DropDownItems.Add(savePaletteItem);
            root.DropDownItems.Add(new ToolStripSeparator());

            foreach (CompactCardKind kindValue in availableKinds)
            {
                CompactCardKind selectedKind = kindValue;
                ToolStripMenuItem kindMenu = new ToolStripMenuItem(GetCompactCardDisplayName(selectedKind));
                kindMenu.Tag = new CompactCardKindTag(selectedKind);

                ToolStripMenuItem pick = LocalizedItem("menu.color.pick");
                pick.Click += delegate { PickCardAccent(selectedKind); };
                // The colour dialog is a window of its own, and a menu left
                // standing behind it is in the way of the thing it opened.
                _menuClosesOnClick.Add(pick);
                kindMenu.DropDownItems.Add(pick);

                ToolStripMenuItem reset = LocalizedItem("menu.color.reset");
                reset.Click += delegate { SetCardAccent(selectedKind, null); };
                kindMenu.DropDownItems.Add(reset);
                // Remembered rather than only enabled on opening: a menu that
                // stays up through a click would keep the state it opened with,
                // and an entry greyed out because it was useless a moment ago
                // is an entry that does nothing when it becomes useful.
                _colorResetItems[selectedKind] = reset;
                root.DropDownItems.Add(kindMenu);
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            // Last, and on its own: it is the one entry here that throws work
            // away, and it has no business sitting where the pointer lands.
            _resetAllColorsItem = LocalizedItem("menu.color.resetAll");
            _resetAllColorsItem.Click += delegate
            {
                _cardAccents.Clear();
                RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
                RefreshHistoryPanels();
                SaveSettings();
            };
            root.DropDownItems.Add(_resetAllColorsItem);
            root.DropDownOpening += delegate { RefreshCardColorMenu(root); };
            RefreshCardColorMenu(root);
            return root;
        }

        private void RefreshCardColorMenu(ToolStripMenuItem root)
        {
            if (_resetAllColorsItem != null)
                _resetAllColorsItem.Enabled = _cardAccents.Count > 0;
            foreach (KeyValuePair<CompactCardKind, ToolStripMenuItem> entry in _colorResetItems)
                entry.Value.Enabled = _cardAccents.ContainsKey(entry.Key);
            if (_customPaletteItem != null)
            {
                bool defined = _customCardAccents.Count > 0;
                _customPaletteItem.Enabled = defined;
                _customPaletteItem.Checked = defined && CardAccentsEqual(_cardAccents, _customCardAccents);
                _customPaletteItem.Text = defined
                    ? Loc.T("menu.color.myPaletteCount", _customCardAccents.Count)
                    : Loc.T("menu.color.myPaletteEmpty");
            }

            foreach (ToolStripItem rawItem in root.DropDownItems)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                CompactCardKindTag tag = item != null ? item.Tag as CompactCardKindTag : null;
                if (tag == null)
                    continue;
                item.Text = GetCompactCardDisplayName(tag.Kind);
                item.Checked = _cardAccents.ContainsKey(tag.Kind);
            }
        }

        private void ApplyCustomPalette()
        {
            if (_customCardAccents.Count == 0)
                return;
            _cardAccents = new Dictionary<CompactCardKind, Color>(_customCardAccents);
            RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
            RefreshHistoryPanels();
            SaveSettings();
        }

        private static bool CardAccentsEqual(Dictionary<CompactCardKind, Color> left,
            Dictionary<CompactCardKind, Color> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            foreach (KeyValuePair<CompactCardKind, Color> pair in left)
            {
                Color other;
                if (!right.TryGetValue(pair.Key, out other) || other.ToArgb() != pair.Value.ToArgb())
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The graphs get the same shape of menu as the cards — presets first,
        /// then one entry per slot — because they are configured the same way
        /// and there is no reason for the two to be learned separately.
        /// </summary>
        private ToolStripMenuItem CreateGraphsMenu(CompactCardKind[] availableKinds)
        {
            ToolStripMenuItem root = LocalizedItem("menu.graphs");
            _graphsRoot = root;
            root.DropDownItems.Add(CreateGraphPresetMenuItem("preset.system", CreateSystemGraphPreset()));
            root.DropDownItems.Add(CreateGraphPresetMenuItem("preset.gaming", CreateGamingGraphPreset()));
            _customGraphPresetItem = LocalizedItem("preset.custom");
            _customGraphPresetItem.Click += delegate
            {
                if (_customGraphPreset != null)
                    ApplyGraphSources(_customGraphPreset[0], _customGraphPreset[1], true);
            };
            root.DropDownItems.Add(_customGraphPresetItem);
            ToolStripMenuItem saveGraphsItem = LocalizedItem("menu.graphs.save");
            saveGraphsItem.Click += delegate
            {
                _customGraphPreset = new[] { _leftGraphSource, _rightGraphSource };
                SaveSettings();
                RefreshGraphsMenu(_graphsRoot);
            };
            root.DropDownItems.Add(saveGraphsItem);
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(CreateGraphSourceSubmenu("menu.graph.left", availableKinds, true));
            root.DropDownItems.Add(CreateGraphSourceSubmenu("menu.graph.right", availableKinds, false));
            root.DropDownOpening += delegate { RefreshGraphsMenu(root); };
            RefreshGraphsMenu(root);
            return root;
        }

        private ToolStripMenuItem CreateGraphPresetMenuItem(string key, CompactCardKind[] pair)
        {
            ToolStripMenuItem item = LocalizedItem(key);
            item.Tag = new CompactPresetMenuTag(pair);
            item.Click += delegate { ApplyGraphSources(pair[0], pair[1], true); };
            return item;
        }

        private void RefreshGraphsMenu(ToolStripMenuItem root)
        {
            if (_customGraphPresetItem != null)
            {
                bool defined = _customGraphPreset != null;
                _customGraphPresetItem.Enabled = defined;
                _customGraphPresetItem.Checked = defined && GraphPairEquals(_customGraphPreset);
                _customGraphPresetItem.Text = defined
                    ? Loc.T("preset.custom.prefix") + String.Join(", ", _customGraphPreset
                        .Select(GetCompactCardDisplayName).ToArray()) + ")"
                    : Loc.T("preset.custom.empty");
            }

            foreach (ToolStripItem rawItem in root.DropDownItems)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                if (item == null)
                    continue;
                CompactPresetMenuTag preset = item.Tag as CompactPresetMenuTag;
                if (preset != null)
                {
                    item.Checked = GraphPairEquals(preset.Kinds);
                    continue;
                }

                GraphSlotMenuTag slot = item.Tag as GraphSlotMenuTag;
                if (slot != null)
                {
                    item.Text = Loc.T(slot.TitleKey) + ": " +
                        GetCompactCardDisplayName(slot.Left ? _leftGraphSource : _rightGraphSource);
                    RefreshGraphSourceSubmenu(item, slot.Left);
                }
            }
        }

        private bool GraphPairEquals(CompactCardKind[] pair)
        {
            return pair != null && pair.Length == 2 &&
                pair[0] == _leftGraphSource && pair[1] == _rightGraphSource;
        }

        private void ApplyGraphSources(CompactCardKind left, CompactCardKind right, bool save)
        {
            _leftGraphSource = left;
            _rightGraphSource = right;
            RefreshHistoryPanels();
            if (save)
                SaveSettings();
        }

        private ToolStripMenuItem CreateGraphSourceSubmenu(string titleKey,
            CompactCardKind[] availableKinds, bool left)
        {
            // Not a LocalizedItem: the caption carries the current source after
            // the title, so replaying the bare key would wipe that half of it.
            ToolStripMenuItem menu = new ToolStripMenuItem(Loc.T(titleKey));
            menu.Tag = new GraphSlotMenuTag(left, titleKey);
            foreach (CompactCardKind kindValue in availableKinds)
            {
                CompactCardKind selectedKind = kindValue;
                ToolStripMenuItem choice = new ToolStripMenuItem(GetCompactCardDisplayName(selectedKind));
                choice.Tag = new CompactCardKindTag(selectedKind);
                choice.Click += delegate { ApplyGraphSource(left, selectedKind, true); };
                menu.DropDownItems.Add(choice);
            }
            menu.DropDownOpening += delegate { RefreshGraphSourceSubmenu(menu, left); };
            return menu;
        }

        /// <summary>
        /// The ticks in one source list.  Called when the list opens and again
        /// after every pick, because the list now stays open across picks: the
        /// heading above it already said "Storage" while the tick was still on
        /// the previous source, which reads as a click that half worked.
        /// </summary>
        private void RefreshGraphSourceSubmenu(ToolStripMenuItem menu, bool left)
        {
            CompactCardKind current = left ? _leftGraphSource : _rightGraphSource;
            foreach (ToolStripItem rawItem in menu.DropDownItems)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                CompactCardKindTag tag = item != null ? item.Tag as CompactCardKindTag : null;
                if (tag == null)
                    continue;
                item.Text = GetCompactCardDisplayName(tag.Kind);
                item.Checked = tag.Kind == current;
            }
        }

        private void ApplyGraphSource(bool left, CompactCardKind kind, bool save)
        {
            if (left)
                _leftGraphSource = kind;
            else
                _rightGraphSource = kind;
            RefreshHistoryPanels();
            if (save)
                SaveSettings();
        }

        /// <summary>
        /// Extracts the pair of values a history panel plots for one card kind.
        /// Lives here rather than in the control because picking the frame-rate
        /// source needs the process identity the form already tracks.
        /// </summary>
        private void AddHistorySample(SensorHistoryControl history,
            SensorSnapshot snapshot, CompactCardKind kind)
        {
            double primary = Double.NaN;
            double secondary = Double.NaN;
            switch (kind)
            {
                case CompactCardKind.Cpu:
                    primary = snapshot.Temperature > 0 ? snapshot.Temperature : Double.NaN;
                    secondary = snapshot.Usage;
                    break;
                case CompactCardKind.Gpu:
                    primary = snapshot.GpuTemperature > 0 ? snapshot.GpuTemperature : Double.NaN;
                    secondary = snapshot.GpuUsage;
                    break;
                case CompactCardKind.Memory:
                    if (snapshot.MemoryTotalGb > 0)
                    {
                        primary = snapshot.MemoryUsedGb / snapshot.MemoryTotalGb * 100;
                        secondary = snapshot.MemoryUsedGb;
                    }
                    break;
                case CompactCardKind.Storage:
                    if (snapshot.StorageTotalGb > 0)
                        primary = snapshot.StorageUsedGb / snapshot.StorageTotalGb * 100;
                    break;
                case CompactCardKind.Network:
                    primary = snapshot.NetworkDownloadKbps / 1024.0;
                    secondary = snapshot.NetworkUploadKbps / 1024.0;
                    break;
                case CompactCardKind.Fans:
                    primary = Peak(snapshot.FanRpm);
                    secondary = Peak(snapshot.FanControlPercent);
                    break;
                case CompactCardKind.Fps:
                    GetFrameHistorySample(snapshot, out primary, out secondary);
                    break;
            }
            history.AddSample(primary, secondary);
        }

        private static double Peak(double[] values)
        {
            if (values == null || values.Length == 0)
                return Double.NaN;
            double best = Double.NaN;
            foreach (double value in values)
            {
                if (value <= 0)
                    continue;
                if (Double.IsNaN(best) || value > best)
                    best = value;
            }
            return best;
        }

        private void GetFrameHistorySample(SensorSnapshot snapshot,
            out double fps, out double frameTimeMs)
        {
            fps = Double.NaN;
            frameTimeMs = Double.NaN;
            int[] ids = snapshot.FrameProcessIds;
            string[] names = snapshot.FrameProcessNames;
            if (ids == null || names == null)
                return;
            int foreground = GetForegroundProcessId();
            int best = -1;
            int bestRank = -1;
            double bestFps = 0;
            int count = Math.Min(ids.Length, names.Length);
            for (int index = 0; index < count; index++)
            {
                if (ids[index] == _currentProcessId)
                    continue;
                double candidate = FirstPositive(
                    Pick(snapshot.FrameDisplayedFps, index),
                    Pick(snapshot.FramePresentedFps, index),
                    Pick(snapshot.FrameApplicationFps, index));
                if (!IsUsableFps(candidate))
                    continue;
                int rank = ids[index] == foreground ? 3 : IsDesktopCompositor(names[index]) ? 2 : 1;
                if (rank < bestRank || (rank == bestRank && candidate <= bestFps))
                    continue;
                best = index;
                bestRank = rank;
                bestFps = candidate;
            }
            if (best < 0)
                return;
            fps = bestFps;
            double time = Pick(snapshot.FrameTimeMs, best);
            frameTimeMs = time > 0 ? time : Double.NaN;
        }

        private static double Pick(double[] values, int index)
        {
            return values != null && index < values.Length ? values[index] : 0;
        }

        private void RefreshHistoryPanels()
        {
            _cpuHistory.Configure(_leftGraphSource, GetCompactCardDisplayName(_leftGraphSource),
                GetCardAccent(_leftGraphSource));
            _gpuHistory.Configure(_rightGraphSource, GetCompactCardDisplayName(_rightGraphSource),
                GetCardAccent(_rightGraphSource));
        }

        private void PickCardAccent(CompactCardKind kind)
        {
            CompactCardKind picked = kind;
            bool hadAccent = _cardAccents.ContainsKey(kind);
            Color previous = hadAccent ? _cardAccents[kind] : Color.Empty;
            // The reading behind the dialog changes as the crosshair moves, so
            // the colour is judged on the digits it will actually be used on
            // rather than on a swatch the size of a postage stamp.
            Action<Color> preview = delegate(Color color) { PreviewCardAccent(picked, color); };
            using (LiveColorDialog dialog = new LiveColorDialog(preview))
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.SolidColorOnly = true;
                dialog.Color = GetCardAccent(kind);
                dialog.CustomColors = _cardAccents.Values
                    .Select(delegate(Color color)
                    {
                        return color.R | (color.G << 8) | (color.B << 16);
                    })
                    .Take(16)
                    .ToArray();
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    // Cancel has to undo the preview exactly, including putting
                    // back the absence of a colour.
                    PreviewCardAccent(kind, hadAccent ? previous : (Color?)null);
                    return;
                }
                SetCardAccent(kind, Color.FromArgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        }

        /// <summary>
        /// Paints a colour without committing to it: no personal palette, no
        /// settings written.  Cancelling the dialog has to leave nothing behind,
        /// and a preview that saved would leave the palette full of colours the
        /// user only passed the pointer over.
        /// </summary>
        private void PreviewCardAccent(CompactCardKind kind, Color? accent)
        {
            if (accent.HasValue)
                _cardAccents[kind] = accent.Value;
            else
                _cardAccents.Remove(kind);
            RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
            RefreshHistoryPanels();
        }

        private ToolStripMenuItem CreateCompactPresetMenuItem(string key, CompactCardKind[] kinds)
        {
            ToolStripMenuItem item = LocalizedItem(key);
            item.Tag = new CompactPresetMenuTag(kinds);
            item.Click += delegate { ApplyCompactSlots(kinds, true); };
            return item;
        }

        private void RefreshCompactCardsMenu(ToolStripMenuItem root)
        {
            if (_customPresetItem != null)
            {
                bool defined = _customCompactPreset != null;
                _customPresetItem.Enabled = defined;
                _customPresetItem.Checked = defined &&
                    CompactSlotKindsEqual(_compactSlotKinds, _customCompactPreset);
                _customPresetItem.Text = defined
                    ? Loc.T("preset.custom.prefix") + String.Join(", ", _customCompactPreset
                        .Select(GetCompactCardDisplayName).ToArray()) + ")"
                    : Loc.T("preset.custom.empty");
            }

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
                    item.Text = Loc.T("menu.slotPrefix") + (slotIndex + 1).ToString(CultureInfo.InvariantCulture) +
                        ": " + GetCompactCardDisplayName(_compactSlotKinds[slotIndex]);
                    foreach (ToolStripItem rawChoice in item.DropDownItems)
                    {
                        ToolStripMenuItem choice = rawChoice as ToolStripMenuItem;
                        CompactSlotMenuTag slot = choice != null ? choice.Tag as CompactSlotMenuTag : null;
                        if (slot == null)
                            continue;
                        // The caption as well as the tick.  These entries were
                        // named once, when the menu was built, and so were the
                        // only names in the whole window that a language switch
                        // walked straight past.
                        choice.Text = GetCompactCardDisplayName(slot.Kind);
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
            UpdateFrameTelemetryDemand();
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

        /// <summary>
        /// The tooltip names the current order either way, because that is the
        /// question being asked when the pointer stops on the button.  What
        /// differs is the promise: a narrow window turns a page, a wide one
        /// moves the whole order along by one.
        /// </summary>
        private void UpdateCompactCycleTooltip()
        {
            string order = String.Join(" → ", _compactSlotKinds
                .Select(GetCompactCardDisplayName)
                .ToArray());
            bool canCycle = !_pinned && _compactSlotKinds.Length > 1;
            string hint = GetCompactPageCount() > 1
                ? Loc.T("tip.cycle.prefix") + order
                : Loc.T("tip.cycle.all") + order;
            _tips.SetToolTip(_cycleButton, _pinned ? String.Empty : hint);
            if (_cycleCardsItem == null)
                return;
            _cycleCardsItem.Enabled = canCycle;
            _cycleCardsItem.ToolTipText = hint;
        }

        private static string GetCompactCardDisplayName(CompactCardKind kind)
        {
            switch (kind)
            {
                case CompactCardKind.Cpu: return "CPU";
                case CompactCardKind.Gpu: return "GPU";
                case CompactCardKind.Memory: return Loc.T("card.memory");
                case CompactCardKind.Network: return Loc.T("card.network");
                case CompactCardKind.Storage: return Loc.T("card.storage");
                case CompactCardKind.Fans: return Loc.T("card.fans");
                case CompactCardKind.Fps: return "FPS";
                default: return Loc.T("card.metric");
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

        private static string GetHeaderModeId(HeaderVisibilityMode mode)
        {
            switch (mode)
            {
                case HeaderVisibilityMode.AlwaysVisible: return "always";
                case HeaderVisibilityMode.AlwaysHidden: return "never";
                default: return "auto";
            }
        }

        private static HeaderVisibilityMode ParseHeaderMode(string value,
            HeaderVisibilityMode fallback)
        {
            switch ((value ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "auto": return HeaderVisibilityMode.Automatic;
                case "always": return HeaderVisibilityMode.AlwaysVisible;
                case "never": return HeaderVisibilityMode.AlwaysHidden;
                default: return fallback;
            }
        }

        private static string SerializeCompactSlotKinds(CompactCardKind[] kinds)
        {
            CompactCardKind[] normalized = NormalizeCompactSlotKinds(kinds);
            return String.Join(";", normalized.Select(GetCompactCardId).ToArray());
        }

        private static CompactCardKind ParseCompactCardKind(string value, CompactCardKind fallback)
        {
            CompactCardKind parsed;
            return TryParseCompactCardId(value, out parsed) ? parsed : fallback;
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

        /// <summary>
        /// The graph pair is written with its own pair of helpers rather than the
        /// slot ones: those normalise to the four card slots and would hand back
        /// a quartet where a pair went in.
        /// </summary>
        private static string SerializeGraphPair(CompactCardKind[] pair)
        {
            return pair == null || pair.Length != 2
                ? String.Empty
                : GetCompactCardId(pair[0]) + ";" + GetCompactCardId(pair[1]);
        }

        private static CompactCardKind[] ParseGraphPair(string value)
        {
            string[] parts = (value ?? String.Empty).Split(new[] { ';' }, StringSplitOptions.None);
            CompactCardKind left;
            CompactCardKind right;
            if (parts.Length != 2 ||
                !TryParseCompactCardId(parts[0], out left) ||
                !TryParseCompactCardId(parts[1], out right))
                return null;
            return new[] { left, right };
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
                    Color accent = available ? CpuStatusColor(snapshot.Temperature) : muted;
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
                        new[] { Loc.T("history.tempShort"), Loc.T("caption.load"), Loc.T("caption.clock"), Loc.T("caption.power") }, accent,
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
                        new[] { Loc.T("history.tempShort"), Loc.T("caption.load"), Loc.T("caption.clock"), Loc.T("caption.power"), "VRAM" }, accent,
                        CompactCardLayoutFlavor.Normal);
                }
                case CompactCardKind.Memory:
                {
                    Color accent = available ? GetCardAccent(kind) : muted;
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
                    return MakeCompactPresentation(Loc.T("caption.memory"), percentText, usage,
                        new[]
                        {
                            percentText,
                            usage,
                            available && snapshot.MemoryClockMhz > 0
                                ? snapshot.MemoryClockMhz.ToString("0", CultureInfo.InvariantCulture) + " MHz"
                                : "—"
                        },
                        new[] { Loc.T("caption.used"), Loc.T("caption.usedLong"), Loc.T("caption.clock") }, accent,
                        CompactCardLayoutFlavor.Normal);
                }
                case CompactCardKind.Network:
                {
                    Color accent = available ? GetCardAccent(kind) : muted;
                    string download = available ? FormatCompactRate(snapshot.NetworkDownloadKbps) : "—";
                    string upload = available ? FormatCompactRate(snapshot.NetworkUploadKbps) : "—";
                    return MakeCompactPresentation(Loc.T("caption.network"), "▼ " + download, "▲ " + upload,
                        new[]
                        {
                            available ? FormatRate(snapshot.NetworkDownloadKbps) : "—",
                            available ? FormatRate(snapshot.NetworkUploadKbps) : "—"
                        },
                        new[] { Loc.T("caption.download"), Loc.T("caption.upload") }, accent,
                        CompactCardLayoutFlavor.Rate);
                }
                case CompactCardKind.Storage:
                    return CreateStorageCompactPresentation(snapshot, available, muted);
                case CompactCardKind.Fans:
                    return CreateFansCompactPresentation(snapshot, available, muted);
                case CompactCardKind.Fps:
                    return CreateFrameCompactPresentation(snapshot, available, muted);
                default:
                    return MakeCompactPresentation(Loc.T("caption.metric"), "—", String.Empty,
                        new[] { "—" }, new[] { Loc.T("state.noData") }, muted,
                        CompactCardLayoutFlavor.Normal);
            }
        }

        private CompactCardPresentation CreateStorageCompactPresentation(SensorSnapshot snapshot,
            bool available, Color muted)
        {
            double used = 0;
            double total = 0;
            string drive = Loc.T("caption.allDrives");
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
            Color accent = available ? GetCardAccent(CompactCardKind.Storage) : muted;
            return MakeCompactPresentation(Loc.T("caption.storage"), percentText, usage,
                new[] { percentText, usage, available ? drive : "—" },
                new[] { Loc.T("caption.used"), Loc.T("caption.usedLong"), Loc.T("caption.drive") }, accent,
                CompactCardLayoutFlavor.Normal);
        }

        private CompactCardPresentation CreateFansCompactPresentation(SensorSnapshot snapshot,
            bool available, Color muted)
        {
            string[] names = available && snapshot != null ? snapshot.FanNames ?? new string[0] : new string[0];
            double[] rpm = available && snapshot != null ? snapshot.FanRpm ?? new double[0] : new double[0];
            double[] control = available && snapshot != null ? snapshot.FanControlPercent ?? new double[0] : new double[0];
            int count = Math.Min(names.Length, rpm.Length);
            if (count <= 0)
            {
                return MakeCompactPresentation(Loc.T("caption.fans"), "— RPM", String.Empty,
                    new[] { "— RPM" }, new[] { Loc.T("state.noData") }, muted,
                    CompactCardLayoutFlavor.Normal);
            }

            string[] values = new string[Math.Min(5, count)];
            string[] captions = new string[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                double percent = index < control.Length ? control[index] : -1;
                values[index] = FormatCompactFan(rpm[index], percent);
                captions[index] = String.IsNullOrWhiteSpace(names[index]) ? Loc.T("caption.fan") : names[index];
            }
            string secondary = control.Length > 0 && control[0] >= 0
                ? Math.Round(control[0]).ToString("0", CultureInfo.InvariantCulture) + "%"
                : String.Empty;
            // The number alone.  "1704 RPM" is twice the length of "75°C", and a
            // card sizes its type to its longest reading, so spelling the unit
            // out here is what printed the fans at half the size of the cards
            // beside them.  The unit is on the caption and in the full view.
            return MakeCompactPresentation(Loc.T("caption.fans"),
                Math.Round(Math.Max(0, rpm[0])).ToString("0", CultureInfo.InvariantCulture),
                secondary, values, captions, GetCardAccent(CompactCardKind.Fans),
                CompactCardLayoutFlavor.Normal);
        }

        /// <summary>
        /// Frame rate for whatever is presenting right now.  A foreground
        /// application wins; otherwise the desktop compositor is used, so the
        /// card also reports a frame rate while no game is running.
        /// </summary>
        private CompactCardPresentation CreateFrameCompactPresentation(SensorSnapshot snapshot,
            bool available, Color muted)
        {
            Color accent = available ? GetCardAccent(CompactCardKind.Fps) : muted;
            int[] ids = snapshot != null ? snapshot.FrameProcessIds : null;
            string[] names = snapshot != null ? snapshot.FrameProcessNames : null;
            double[] displayed = snapshot != null ? snapshot.FrameDisplayedFps : null;
            double[] presented = snapshot != null ? snapshot.FramePresentedFps : null;
            double[] application = snapshot != null ? snapshot.FrameApplicationFps : null;
            double[] times = snapshot != null ? snapshot.FrameTimeMs : null;
            double[] lows = snapshot != null ? snapshot.FrameOnePercentLowFps : null;
            int count = new[]
            {
                ids == null ? 0 : ids.Length,
                names == null ? 0 : names.Length,
                displayed == null ? 0 : displayed.Length,
                presented == null ? 0 : presented.Length,
                application == null ? 0 : application.Length,
                times == null ? 0 : times.Length,
                lows == null ? 0 : lows.Length
            }.Min();

            int selected = -1;
            int selectedRank = 0;
            double selectedFps = 0;
            int foreground = GetForegroundProcessId();
            for (int index = 0; index < count; index++)
            {
                if (ids[index] == _currentProcessId)
                    continue;
                double fps = FirstPositive(displayed[index], presented[index], application[index]);
                if (fps <= 0)
                    continue;
                int rank = ids[index] == foreground && foreground > 0
                    ? 3
                    : IsDesktopCompositor(names[index]) ? 2 : 1;
                if (selected >= 0 && (rank < selectedRank || (rank == selectedRank && fps <= selectedFps)))
                    continue;
                selected = index;
                selectedRank = rank;
                selectedFps = fps;
            }

            if (!available || selected < 0)
            {
                string hint = DescribeFrameTelemetryState(snapshot, available, count);
                return MakeCompactPresentation("FPS", "— FPS", String.Empty,
                    new[] { "— FPS", "— ms", "—" },
                    new[] { "FPS", Loc.T("caption.frameTime"), hint }, muted,
                    CompactCardLayoutFlavor.Rate);
            }

            // The desktop compositor only presents when something on screen
            // actually changes, so a still desktop legitimately reports a couple
            // of frames per second.  Say so instead of looking broken.
            bool desktopIdle = IsDesktopCompositor(names[selected]) && selectedFps < 10;
            string source = DescribeFrameSource(names[selected], ids[selected]) +
                (desktopIdle ? Loc.T("fps.idleSuffix") : String.Empty);
            string fpsText = Math.Round(selectedFps).ToString("0", CultureInfo.InvariantCulture) + " FPS";
            string frameTime = times[selected] > 0
                ? times[selected].ToString("0.0", CultureInfo.InvariantCulture) + " ms"
                : "— ms";
            string low = lows[selected] > 0
                ? Math.Round(lows[selected]).ToString("0", CultureInfo.InvariantCulture) + " FPS"
                : "—";
            // The second reading on a small card has to be a reading: the name of
            // the source is prose, and prose that long shrinks the digits next to
            // it until this card no longer matches the ones beside it.  It keeps
            // its place in the metric list, where a card opened up enough to show
            // every row has the width for it.
            return MakeCompactPresentation("FPS", fpsText, frameTime,
                new[] { fpsText, frameTime, low, source },
                new[] { "FPS", Loc.T("caption.frameTime"), "1% LOW", Loc.T("caption.source") }, accent,
                CompactCardLayoutFlavor.Rate);
        }

        private static string DescribeFrameTelemetryState(SensorSnapshot snapshot,
            bool available, int count)
        {
            if (!available || snapshot == null)
                return Loc.T("state.noData");
            switch ((FrameTelemetryRunnerState)snapshot.FrameTelemetryState)
            {
                case FrameTelemetryRunnerState.Faulted:
                    return Loc.T("state.unavailable");
                case FrameTelemetryRunnerState.Idle:
                case FrameTelemetryRunnerState.Stopping:
                    return Loc.T("state.off");
                case FrameTelemetryRunnerState.Starting:
                    return Loc.T("state.starting");
                default:
                    return count > 0 ? Loc.T("state.collecting") : Loc.T("state.noFrames");
            }
        }

        private static string DescribeFrameSource(string processName, int processId)
        {
            if (IsDesktopCompositor(processName))
                return Loc.T("fps.desktop");
            if (String.IsNullOrWhiteSpace(processName))
                return "PID " + processId.ToString(CultureInfo.InvariantCulture);
            return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName.Substring(0, processName.Length - 4)
                : processName;
        }

        private static bool IsDesktopCompositor(string processName)
        {
            return String.Equals(processName, "dwm.exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(processName, "dwm", StringComparison.OrdinalIgnoreCase);
        }

        private static double FirstPositive(double first, double second, double third)
        {
            if (IsUsableFps(first))
                return first;
            if (IsUsableFps(second))
                return second;
            return IsUsableFps(third) ? third : 0;
        }

        private static bool IsUsableFps(double value)
        {
            return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value) && value < 100000;
        }

        private static int GetForegroundProcessId()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
                return 0;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return (int)processId;
        }

        private void UpdateFrameTelemetryDemand()
        {
            CompactCardKind[] kinds = _compactSlotKinds;
            _frameTelemetryDemand = kinds != null && kinds.Any(delegate(CompactCardKind kind)
            {
                return kind == CompactCardKind.Fps;
            });
        }

        /// <summary>
        /// Just the number.  A compact card sizes its type to whatever the
        /// longest reading needs, so "1019 RPM · 42%" against the CPU card's
        /// "43°C" left the fans printed at half the size of everything around
        /// them - unreadable at a glance, which is the only way this card is
        /// ever read.  The unit is on the card's own caption and the control
        /// percentage is in the opened-up view.
        /// </summary>
        private static string FormatCompactFan(double rpm, double control)
        {
            return rpm >= 0
                ? Math.Round(rpm).ToString("0", CultureInfo.InvariantCulture)
                : "—";
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

        private static void SetCompactValue(TextReadout label, string primary, string secondary)
        {
            string[] values = { primary ?? "—", secondary ?? String.Empty };
            label.Tag = values;
            label.Text = values[1].Length > 0 ? values[0] + "   " + values[1] : values[0];
        }

        private void CycleCompactCards()
        {
            int pageCount = GetCompactPageCount();
            if (pageCount > 1)
            {
                PerformAtomicLayout(delegate
                {
                    _compactPageIndex = (_compactPageIndex + 1) % pageCount;
                    LayoutResponsive();
                });
                return;
            }

            // Every card already fits, so there is no page left to turn.  The
            // button still has work to do: rotating the slots moves the next
            // card to the front, which is also the order the layout falls back
            // on as soon as the window is too narrow to hold them all.
            if (_compactSlotKinds.Length < 2)
                return;
            CompactCardKind[] rotated = new CompactCardKind[_compactSlotKinds.Length];
            for (int index = 0; index < rotated.Length; index++)
                rotated[index] = _compactSlotKinds[(index + 1) % _compactSlotKinds.Length];
            ApplyCompactSlots(rotated, true);
        }

        /// <summary>
        /// Runs a batch of geometry changes as one repaint.  WM_SETREDRAW is a
        /// switch and not a counter, so a nested call must leave it alone: the
        /// inner block turning drawing back on is exactly the half-finished
        /// frame this exists to prevent.
        /// </summary>
        private void PerformAtomicLayout(Action action)
        {
            bool redrawWasDisabled = _atomicLayoutDepth == 0 && IsHandleCreated;
            if (redrawWasDisabled)
                NativeUi.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
            _atomicLayoutDepth++;
            SuspendLayout();
            try
            {
                action();
            }
            finally
            {
                ResumeLayout(false);
                _atomicLayoutDepth--;
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
                    // Synchronously, not merely invalidated.  WM_PAINT is the
                    // lowest-priority message there is, so while the pointer
                    // keeps producing WM_SIZING the repaint never gets its turn
                    // and the widget only catches up when the button comes up.
                    // Without a background there is no opaque bitmap for DWM to
                    // stretch in the meantime, so the stall is plainly visible.
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
                // A window region is re-applied only outside the move/resize
                // loop: SetWindowRgn there costs a full repaint of everything
                // behind the widget on every mouse message.
                if (updateCorners && !_interactiveResize)
                    ApplyWindowShape();
                // The catcher is shaped from the card bounds, which this pass is
                // what moves.  Paging or swapping a slot never resizes anything,
                // so the resize hook alone would leave the shape a layout behind.
                SyncBackgroundHitForm();
            }
            finally
            {
                _layoutInProgress = false;
            }
        }

        private void PrepareCompactPaging(int visibleCards, int cardCount)
        {
            _currentCompactCardCount = Math.Max(1, cardCount);
            _currentCompactVisibleCards = Math.Max(1,
                Math.Min(_currentCompactCardCount, visibleCards));
            // The page is an anchor the user picked, not a value derived from the
            // window size.  Resizing (or an automatic compact/expanded switch)
            // only clamps it, so the slot order the user configured never snaps
            // back to the first slots on its own.
            int pageCount = GetCompactPageCount();
            _compactPageIndex = Math.Max(0, Math.Min(_compactPageIndex, pageCount - 1));
            // The button never goes away just because everything happens to fit:
            // with no page to turn it rotates the order instead, which is what
            // the user reaches for when the wrong card is sitting in front.
            bool canCycle = !_pinned && _compactSlotKinds.Length > 1;
            if (_cycleButton.Enabled != canCycle)
            {
                _cycleButton.Enabled = canCycle;
                UpdateCompactCycleTooltip();
            }
            if (_compactCycleAvailable != canCycle)
            {
                _compactCycleAvailable = canCycle;
                LayoutHeaderButtons();
                LayoutHeaderTitle(IsHeaderHidden());
            }
        }

        private int GetCompactPageCount()
        {
            return Math.Max(1, _currentCompactCardCount - _currentCompactVisibleCards + 1);
        }

        private int[] GetCompactVisibleIndices()
        {
            // A contiguous, non-wrapping window over the slots.  Wrapping used to
            // rotate the order itself, so cycling far enough put CPU and GPU back
            // in front of the cards the user had deliberately placed there.
            int count = Math.Max(1, Math.Min(_currentCompactCardCount, _currentCompactVisibleCards));
            int start = Math.Max(0, Math.Min(_currentCompactCardCount - count, _compactPageIndex));
            int[] indices = new int[count];
            for (int index = 0; index < count; index++)
                indices[index] = start + index;
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

            // The window is made whole before its geometry is handed over: a bar
            // cropped off for the pointer must not be counted into the view the
            // user is switching to.
            RestoreHeaderHoverCrop();
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
            ApplyMinimumSize();
            Size target = expanded ? _expandedSize : _compactSize;
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            int minimumWidth = expanded ? 220 : MinimumCompactWidth;
            int minimumHeight = expanded ? 278 : HeaderlessCompactMinimumHeight;
            target.Width = Math.Max(minimumWidth, Math.Min(target.Width, area.Width));
            target.Height = Math.Max(minimumHeight, Math.Min(target.Height, area.Height));
            ClientSize = target;
            Location = targetLocation;
            _expandButton.Text = "▾";
            _tips.SetToolTip(_expandButton, Loc.T("tip.expand"));
            _switchingView = false;
            RunLayoutPass(false);
            ApplyWindowShape();
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

        // Double click is a switch between the two extremes, not a step through
        // the ladder of layouts.  A window that already shows its panels — medium
        // or nearly full screen — has nothing to gain from growing by a handful
        // of pixels and shrinking back, so it collapses to the compact strip
        // instead.  Only the strip, where the panels are genuinely hidden and the
        // readings do not fit, opens the full view; from there the next double
        // click lands back on the strip.
        private void ToggleViewByDoubleClick()
        {
            if (_superExpanded || _expanded)
            {
                CollapseToCompact(true);
                return;
            }

            SetSuperExpanded(true, true);
        }

        private void CollapseToCompact(bool save)
        {
            // The captured pre-super geometry belongs to the layout being left
            // behind.  Dropping it keeps the strip → full → strip cycle honest
            // instead of resurrecting a size two gestures old.
            _superReturnStateKnown = false;
            SetExpanded(false, save);
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
            ApplyMinimumSize();
            ClientSize = target;
            Location = targetLocation;
            _expandButton.Text = "▾";
            _tips.SetToolTip(_expandButton, Loc.T("tip.expand"));
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
            ApplyWindowShape();
            EnsureWindowVisible();
            if (!returnExpanded)
                _compactLocation = Location;
            if (save)
                SaveSettings();
        }

        private void SetSuperExpanded(bool enabled, bool save)
        {
            RestoreHeaderHoverCrop();
            if (enabled && !_superExpanded && !_superReturnStateKnown)
                CapturePreSuperState();
            if (enabled && !_expanded)
                SetExpanded(true, false);
            if (!_expanded)
                return;

            RememberCurrentSize();
            _switchingView = true;
            _superExpanded = enabled;
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
            ApplyWindowShape();
            EnsureWindowVisible();
            if (save)
                SaveSettings();
        }

        private bool IsCompactHeaderHidden()
        {
            // Pinning changes input behaviour only.  It must not reveal the
            // header over compact cards or otherwise alter the current layout.
            return !_expanded && LayoutHeight < CompactHeaderRevealHeight;
        }

        private bool IsHeaderHidden()
        {
            // The two standing modes answer on their own.  Nothing the window
            // does to itself - too short for a bar, a column out of room, a
            // pointer that walked away - is allowed to overrule them.
            if (_headerMode == HeaderVisibilityMode.AlwaysHidden)
                return true;
            if (_headerMode == HeaderVisibilityMode.AlwaysVisible)
                return false;
            // The band the pointer takes is deliberately not here.  That one is
            // cut out of the window shape, and the layout must not notice: were
            // the cards moved up a bar as well, they would move back down the
            // moment the pointer returned, which is the twitch this mode had.
            return _restoredAutomaticHeaderHidden ||
                _headerHiddenByColumnPressure || IsCompactHeaderHidden();
        }

        /// <summary>
        /// True while the bar is the hover chrome's to take: only in the
        /// automatic mode, only in the compact view, and only when the bar would
        /// otherwise be on screen.  Every term is independent of whether the
        /// band is currently cropped, or asking the question would change the
        /// answer and the bar would flicker at every tick.
        /// </summary>
        private bool HeaderBandBelongsToChrome()
        {
            return _headerMode == HeaderVisibilityMode.Automatic && !_expanded &&
                !_restoredAutomaticHeaderHidden && !_headerHiddenByColumnPressure &&
                !IsCompactHeaderHidden();
        }

        private ToolStripMenuItem CreateHeaderVisibilityMenu()
        {
            ToolStripMenuItem root = LocalizedItem("menu.header");
            root.DropDownItems.Add(
                CreateHeaderModeItem("menu.header.auto", HeaderVisibilityMode.Automatic));
            root.DropDownItems.Add(
                CreateHeaderModeItem("menu.header.always", HeaderVisibilityMode.AlwaysVisible));
            root.DropDownItems.Add(
                CreateHeaderModeItem("menu.header.never", HeaderVisibilityMode.AlwaysHidden));
            root.DropDownOpening += delegate { UpdateHeaderMenuState(); };
            return root;
        }

        private ToolStripMenuItem CreateHeaderModeItem(string key, HeaderVisibilityMode mode)
        {
            ToolStripMenuItem item = LocalizedItem(key);
            item.Tag = mode;
            item.Click += delegate { SetHeaderMode(mode, true); };
            _headerModeItems.Add(item);
            return item;
        }

        private void UpdateHeaderMenuState()
        {
            foreach (ToolStripMenuItem item in _headerModeItems)
                item.Checked = item.Tag is HeaderVisibilityMode &&
                    (HeaderVisibilityMode)item.Tag == _headerMode;
        }

        private void SetHeaderMode(HeaderVisibilityMode mode, bool save)
        {
            CloseOpacityPopup();
            // A window cropped because the pointer is elsewhere is made whole
            // first, so every height below is the one the user actually sized.
            RestoreHeaderHoverCrop();
            bool automaticallyHidden = IsCompactHeaderHidden();
            bool wasHidden = IsHeaderHidden();
            _headerMode = mode;
            _restoredAutomaticHeaderHidden = false;
            // Always-visible raises the tracking minimum by one bar, which is
            // what keeps that mode honest: the window simply cannot be dragged
            // down to a height that has no room for both the bar and a reading.
            ApplyMinimumSize();
            bool nowHidden = IsHeaderHidden();

            // In compact mode the choice also consumes or returns the physical
            // header height.  Larger views keep their exact bounds: only their
            // top row goes.
            bool resizeCompactWindow = wasHidden != nowHidden && !_expanded && !_pinned &&
                (LayoutHeight <= CompactHeight || automaticallyHidden);
            if (resizeCompactWindow)
            {
                int layoutHeight = LayoutHeight;
                int targetHeight;
                if (nowHidden)
                {
                    targetHeight = Math.Min(CompactHeaderRevealHeight - 1,
                        Math.Max(HeaderlessCompactMinimumHeight,
                            layoutHeight - CompactHeaderDelta));
                }
                else
                {
                    targetHeight = layoutHeight < CompactHeaderRevealHeight
                        ? Math.Max(CompactHeaderRevealHeight,
                            layoutHeight + CompactHeaderDelta)
                        : layoutHeight;
                }

                DiagLog.Write("header resized the compact window from " +
                    layoutHeight + " to " + targetHeight +
                    " hidden=" + (nowHidden ? "1" : "0") +
                    " automatic=" + (automaticallyHidden ? "1" : "0"));
                _switchingView = true;
                ClientSize = new Size(ClientSize.Width, targetHeight);
                _compactSize = LayoutClientSize;
                _switchingView = false;
            }
            UpdateHeaderMenuState();
            RunLayoutPass(false);
            ApplyWindowShape();
            EnsureWindowVisible();
            if (save)
                SaveSettings();
        }

        /// <summary>
        /// One entry per installed language, written the way its own speakers
        /// write it.  A list built from the table needs nothing here when a
        /// language is added, and a name in its own language needs no
        /// translating when the menu itself switches.
        /// </summary>
        private ToolStripMenuItem CreateLanguageMenu()
        {
            ToolStripMenuItem root = LocalizedItem("menu.language");
            foreach (LanguagePack pack in Loc.Languages)
            {
                string code = pack.Code;
                ToolStripMenuItem item = new ToolStripMenuItem(pack.NativeName);
                item.Tag = code;
                item.Click += delegate { ApplyLanguage(code, true); };
                _languageItems.Add(item);
                root.DropDownItems.Add(item);
            }
            root.DropDownOpening += delegate { UpdateLanguageMenuState(); };
            UpdateLanguageMenuState();
            return root;
        }

        private void UpdateLanguageMenuState()
        {
            foreach (ToolStripMenuItem item in _languageItems)
                item.Checked = String.Equals(item.Tag as string, Loc.Code,
                    StringComparison.OrdinalIgnoreCase);
        }

        private void ShowHelpWindow()
        {
            CloseOpacityPopup();
            HelpWindow.ShowSingleton(this, new Action(ToggleLanguage));
        }

        /// <summary>
        /// What the F1 key does.  The same key both ways: a sheet you opened by
        /// accident, or read and finished with, closes with the key that opened
        /// it rather than by hunting for its frame.
        /// </summary>
        private void ToggleHelpWindow()
        {
            CloseOpacityPopup();
            if (HelpWindow.CloseIfOpen())
                return;
            HelpWindow.ShowSingleton(this, new Action(ToggleLanguage));
        }

        /// <summary>
        /// F1 while the pointer is on the widget, and only then.
        ///
        /// ProcessCmdKey alone needs the widget to hold the keyboard focus, and
        /// a widget that hides from the taskbar and sits over other windows
        /// almost never does - which is the key working on the fifth try, when
        /// the previous click happened to have left focus behind.  A hotkey is
        /// registered instead, and taken straight back when the pointer leaves,
        /// so F1 belongs to whatever the user is actually working in for all the
        /// time they are not pointing at the widget.
        /// </summary>
        private void SyncHelpHotkey()
        {
            bool wanted = _pointerInside && !_stopping && Visible && IsHandleCreated &&
                WindowState != FormWindowState.Minimized;
            _helpHotkey.Wanted = wanted ? _helpBinding : HotkeyBinding.None;
            SyncHotkey(_helpHotkey);
        }

        /// <summary>
        /// The two hotkeys the widget holds for as long as it lives, rather than
        /// only while the pointer is over it like F1.  Pinning makes the window
        /// click-through: once it is on, no gesture with a mouse can reach the
        /// widget to take it off again, because every click goes to whatever is
        /// underneath.  A key is the only way back, so it cannot be a key that
        /// needs the widget to be in hand first - and by the same argument the
        /// key that brings the window back from the tray cannot need the window.
        /// </summary>
        private void SyncHotkeys()
        {
            SyncHotkey(_pinHotkey);
            SyncHotkey(_hideHotkey);
        }

        /// <summary>
        /// Where to leave money: one entry that opens one page, with the choice
        /// of how made there rather than in a submenu.  The menu cannot explain
        /// which of two payment services takes the user's card, and a choice
        /// offered before the explanation is a choice made by guessing.
        ///
        /// An address that is not set yet leaves the entry in place but greyed:
        /// an entry that opens a page which does not exist asks for trust and
        /// spends it in the same click, while one that is visibly not ready yet
        /// says exactly what it is.
        /// </summary>
        private ToolStripMenuItem CreateSupportMenu()
        {
            ToolStripMenuItem item = LocalizedItem("menu.support");
            string target = ReleaseConfiguration.SupportUrl;
            item.Enabled = IsHttpsLink(target);
            item.Click += delegate
            {
                if (!IsHttpsLink(target))
                    return;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    MessageBox.Show(this,
                        Loc.T("support.openFailed"),
                        "Traymetry",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            };
            return item;
        }

        /// <summary>
        /// A shell execute runs whatever it is handed - a path, a document, an
        /// executable - so the only thing a donation entry is allowed to hand it
        /// is an ordinary https address.  A mistyped constant then greys the
        /// entry out instead of launching something.
        /// </summary>
        private static bool IsHttpsLink(string value)
        {
            if (String.IsNullOrEmpty(value))
                return false;
            Uri address;
            return Uri.TryCreate(value, UriKind.Absolute, out address) &&
                address.Scheme == Uri.UriSchemeHttps &&
                !String.IsNullOrEmpty(address.Host);
        }

        /// <summary>
        /// Both combinations, named by what they do and showing what they are
        /// set to.  A hotkey nobody can see is a hotkey nobody knows about, and
        /// one that turned out to be owned by another application says so here
        /// rather than looking set and doing nothing.
        /// </summary>
        private ToolStripMenuItem CreateHotkeysMenu()
        {
            ToolStripMenuItem root = LocalizedItem("menu.hotkeys");
            _pinHotkeyItem = CreateHotkeyItem(HotkeyTarget.Pin);
            _hideHotkeyItem = CreateHotkeyItem(HotkeyTarget.Hide);
            _helpHotkeyItem = CreateHotkeyItem(HotkeyTarget.Help);
            _dismissHotkeyItem = CreateHotkeyItem(HotkeyTarget.Dismiss);
            root.DropDownItems.Add(_pinHotkeyItem);
            root.DropDownItems.Add(_hideHotkeyItem);
            // The two below only fire while the widget itself has focus, which
            // is why they are allowed to be bare keys and why they sit apart.
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(_helpHotkeyItem);
            root.DropDownItems.Add(_dismissHotkeyItem);
            root.DropDownItems.Add(new ToolStripSeparator());
            _resetHotkeysItem = LocalizedItem("hotkey.reset");
            _resetHotkeysItem.Click += delegate { ResetHotkeys(); };
            root.DropDownItems.Add(_resetHotkeysItem);
            root.DropDownOpening += delegate { RefreshHotkeyMenu(); };
            RefreshHotkeyMenu();
            return root;
        }

        private ToolStripMenuItem CreateHotkeyItem(HotkeyTarget target)
        {
            HotkeyTarget selected = target;
            ToolStripMenuItem item = new ToolStripMenuItem();
            item.Click += delegate { EditHotkey(selected); };
            return item;
        }

        /// <summary>
        /// Puts all four back to what the widget ships with.  A hotkey that was
        /// bound to something unreachable cannot be rebound with the keyboard,
        /// so there has to be a way out that needs only the mouse.
        /// </summary>
        private void ResetHotkeys()
        {
            _pinHotkey.Wanted = HotkeyBinding.DefaultPin;
            _hideHotkey.Wanted = HotkeyBinding.DefaultHide;
            _helpBinding = HotkeyBinding.DefaultHelp;
            _dismissBinding = HotkeyBinding.DefaultDismiss;
            SaveSettings();
            SyncHotkeys();
            SyncHelpHotkey();
            RefreshHotkeyMenu();
            HelpWindow.RefreshIfOpen();
        }

        private void RefreshHotkeyMenu()
        {
            if (_resetHotkeysItem != null)
                _resetHotkeysItem.Enabled =
                    !HotkeyBinding.Same(_pinHotkey.Wanted, HotkeyBinding.DefaultPin) ||
                    !HotkeyBinding.Same(_hideHotkey.Wanted, HotkeyBinding.DefaultHide) ||
                    !HotkeyBinding.Same(_helpBinding, HotkeyBinding.DefaultHelp) ||
                    !HotkeyBinding.Same(_dismissBinding, HotkeyBinding.DefaultDismiss);
            ShowHotkeyOn(_pinHotkeyItem, HotkeyTarget.Pin);
            ShowHotkeyOn(_hideHotkeyItem, HotkeyTarget.Hide);
            ShowHotkeyOn(_helpHotkeyItem, HotkeyTarget.Help);
            ShowHotkeyOn(_dismissHotkeyItem, HotkeyTarget.Dismiss);
            // The cheat sheet prints the same combinations, and it is a window
            // of its own with no way to reach back in here.
            HotkeyDisplay.Pin = Describe(GetHotkeyBinding(HotkeyTarget.Pin));
            HotkeyDisplay.Hide = Describe(GetHotkeyBinding(HotkeyTarget.Hide));
            HotkeyDisplay.Help = Describe(_helpBinding);
            HotkeyDisplay.Dismiss = Describe(_dismissBinding);
        }

        private static string Describe(HotkeyBinding binding)
        {
            return binding.IsEmpty ? Loc.T("hotkey.none") : binding.Format();
        }

        private void ShowHotkeyOn(ToolStripMenuItem item, HotkeyTarget target)
        {
            if (item == null)
                return;
            HotkeyBinding binding = GetHotkeyBinding(target);
            string combination = Describe(binding);
            GlobalHotkey registration = GetHotkeyRegistration(target);
            // A combination another application owns looks set and does nothing,
            // so the menu says which one that is rather than leaving the user to
            // wonder why the key stopped working.
            if (registration != null && !binding.IsEmpty &&
                registration.Window != IntPtr.Zero &&
                HotkeyBinding.Same(registration.Active, binding) && !registration.Registered)
                combination += " — " + Loc.T("hotkey.taken");
            item.Text = Loc.T(GetHotkeyActionKey(target)) + ": " + combination;
        }

        private static string GetHotkeyActionKey(HotkeyTarget target)
        {
            switch (target)
            {
                case HotkeyTarget.Pin: return "menu.hotkey.pin";
                case HotkeyTarget.Hide: return "menu.hotkey.hide";
                case HotkeyTarget.Help: return "menu.hotkey.help";
                default: return "menu.hotkey.dismiss";
            }
        }

        private static bool IsGlobalHotkey(HotkeyTarget target)
        {
            return target != HotkeyTarget.Dismiss;
        }

        private GlobalHotkey GetHotkeyRegistration(HotkeyTarget target)
        {
            switch (target)
            {
                case HotkeyTarget.Pin: return _pinHotkey;
                case HotkeyTarget.Hide: return _hideHotkey;
                case HotkeyTarget.Help: return _helpHotkey;
                default: return null;
            }
        }

        private HotkeyBinding GetHotkeyBinding(HotkeyTarget target)
        {
            switch (target)
            {
                case HotkeyTarget.Pin: return _pinHotkey.Wanted;
                case HotkeyTarget.Hide: return _hideHotkey.Wanted;
                case HotkeyTarget.Help: return _helpBinding;
                default: return _dismissBinding;
            }
        }

        private void SetHotkeyBinding(HotkeyTarget target, HotkeyBinding binding)
        {
            switch (target)
            {
                case HotkeyTarget.Pin: _pinHotkey.Wanted = binding; break;
                case HotkeyTarget.Hide: _hideHotkey.Wanted = binding; break;
                case HotkeyTarget.Help: _helpBinding = binding; break;
                default: _dismissBinding = binding; break;
            }
        }

        /// <summary>
        /// Hands the widget's own keys back before asking for a new one and
        /// takes them again afterwards.  Without that, a key the widget holds is
        /// delivered to the widget while the user is pressing it at the capture
        /// window - a system-wide hotkey outranks the focused window - and the
        /// window would sit there showing nothing.
        /// </summary>
        private void EditHotkey(HotkeyTarget target)
        {
            bool global = IsGlobalHotkey(target);
            _hotkeysSuspended = true;
            ReleaseHotkeys();
            try
            {
                using (HotkeyCaptureForm dialog = new HotkeyCaptureForm(
                    Loc.T(GetHotkeyActionKey(target)),
                    Loc.T(global ? "hotkey.scope.global" : "hotkey.scope.window"),
                    GetHotkeyBinding(target),
                    global))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        SetHotkeyBinding(target, dialog.Binding);
                        SaveSettings();
                    }
                }
            }
            finally
            {
                _hotkeysSuspended = false;
            }
            SyncHotkeys();
            SyncHelpHotkey();
            RefreshHotkeyMenu();
            HelpWindow.RefreshIfOpen();
        }

        private void SyncHotkey(GlobalHotkey hotkey)
        {
            // Suspended while a combination is being chosen.  This runs off the
            // pointer timer twenty-five times a second, so without the flag the
            // keys handed back for the capture window are taken again before the
            // user has finished reaching for them - and a key the widget owns is
            // delivered to the widget, never to the window asking for it.
            if (_stopping || !IsHandleCreated || _hotkeysSuspended)
                return;
            // Asked for once per combination.  Remembered even when the
            // registration failed, so one another application already owns is
            // tried once and not twenty-five times a second; losing it costs
            // nothing the menu entries do not still offer.
            if (hotkey.Window == Handle && HotkeyBinding.Same(hotkey.Active, hotkey.Wanted))
                return;
            if (hotkey.Registered)
                NativeUi.UnregisterHotKey(hotkey.Window, hotkey.Id);
            hotkey.Registered = false;
            hotkey.Window = Handle;
            hotkey.Active = hotkey.Wanted;
            if (hotkey.Wanted.IsEmpty)
            {
                StartupTrace.Write("hotkey " + hotkey.Id.ToString("X", CultureInfo.InvariantCulture) +
                    " cleared");
                return;
            }
            const uint NoRepeat = 0x4000;
            hotkey.Registered = NativeUi.RegisterHotKey(Handle, hotkey.Id,
                hotkey.Wanted.Modifiers | NoRepeat, (uint)hotkey.Wanted.Key);
            StartupTrace.Write("hotkey " + hotkey.Wanted.Format() + " " +
                (hotkey.Registered ? "registered" : "unavailable"));
            // A key that another application owns is one of the two or three
            // things people report, and the only way to tell from the outside is
            // that nothing happens.
            if (!hotkey.Registered)
                DiagLog.Write("hotkey " + hotkey.Wanted.Format() + " unavailable");
        }

        /// <summary>
        /// Hands both combinations back to the system.  Used while the user is
        /// choosing a new one: the capture window finds out whether a
        /// combination is free by asking for it, and the widget holding its own
        /// keys would answer "taken" for the very keys it is being asked to
        /// give up.
        /// </summary>
        private void ReleaseHotkeys()
        {
            ReleaseHotkey(_pinHotkey);
            ReleaseHotkey(_hideHotkey);
            ReleaseHotkey(_helpHotkey);
        }

        private static void ReleaseHotkey(GlobalHotkey hotkey)
        {
            if (hotkey.Registered)
                NativeUi.UnregisterHotKey(hotkey.Window, hotkey.Id);
            hotkey.Registered = false;
            hotkey.Window = IntPtr.Zero;
            hotkey.Active = HotkeyBinding.None;
        }

        /// <summary>
        /// What a click on the tray icon does, reachable from the keyboard too.
        /// </summary>
        private void ToggleTrayVisibility()
        {
            if (Visible)
            {
                CloseOpacityPopup();
                Hide();
                return;
            }
            Show();
            Activate();
        }

        /// <summary>
        /// The widget has no menu bar to hang shortcuts off, so the two keys it
        /// answers to are handled here.  They only fire while the window itself
        /// has focus, which the cheat sheet says out loud.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            HotkeyBinding pressed = HotkeyBinding.FromKeyData(keyData);
            if (!pressed.IsEmpty)
            {
                // The help key is also registered system-wide while the pointer
                // is over the widget; this is the same key reaching the window
                // the ordinary way when it is not.
                if (HotkeyBinding.Same(pressed, _helpBinding))
                {
                    ToggleHelpWindow();
                    return true;
                }
                if (HotkeyBinding.Same(pressed, _dismissBinding))
                {
                    CloseOpacityPopup();
                    Hide();
                    return true;
                }
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        /// <summary>
        /// The header button swaps between English and the language the user
        /// actually reads - Russian until they pick another one in the menu,
        /// theirs from then on.  Stepping through every installed language
        /// instead would turn the one control that has to be predictable into
        /// a lottery as soon as there are five of them, and English is the one
        /// second language a monitor like this is worth having on a button.
        /// </summary>
        private void ToggleLanguage()
        {
            if (String.Equals(Loc.Code, EnglishCode, StringComparison.OrdinalIgnoreCase))
                ApplyLanguage(_preferredLanguage, true);
            else
                ApplyLanguage(EnglishCode, true);
        }

        private void ApplyLanguage(string language, bool save)
        {
            if (String.Equals(Loc.Code, language, StringComparison.OrdinalIgnoreCase))
                return;
            Loc.Code = language;
            // Anything that is not English is what the button will come back
            // to, whether it was chosen here or in the menu.
            if (!String.Equals(Loc.Code, EnglishCode, StringComparison.OrdinalIgnoreCase))
                _preferredLanguage = Loc.Code;
            RetranslateUi();
            if (save)
                SaveSettings();
        }

        private const string EnglishCode = "en";

        /// <summary>
        /// The header button carries the language it is currently showing, not
        /// the one a click would switch to: a widget that reads "EN" while the
        /// menu is Russian would be read as broken rather than as an offer.
        /// </summary>
        private void UpdateLanguageButton()
        {
            _languageButton.Text = Loc.Code.ToUpperInvariant();
            _languageButton.AccessibleName = Loc.T("access.language");
            _tips.SetToolTip(_languageButton, _pinned ? String.Empty : Loc.T("tip.language"));
        }

        /// <summary>
        /// Reapplies every caption the constructor set once.  Menu items carry
        /// their key with them, and everything that is redrawn from a snapshot is
        /// simply rendered again, so no control has to be recreated.
        /// </summary>
        private void RetranslateUi()
        {
            foreach (KeyValuePair<ToolStripItem, string> pair in _localizedItems)
                pair.Key.Text = Loc.T(pair.Value);
            _streamHiddenItem.ToolTipText = Loc.T("tip.streamHidden");
            UpdateLanguageMenuState();
            // The two hotkey entries are captions built from a name and a
            // combination, so no stored key can retranslate them.
            RefreshHotkeyMenu();

            _tips.SetToolTip(_title, Loc.T("tip.title"));
            _opacityButton.AccessibleName = Loc.T("access.opacity");
            _backgroundButton.AccessibleName = Loc.T("access.backgroundToggle");
            _cycleButton.AccessibleName = Loc.T("access.cycle");
            _pinButton.AccessibleName = Loc.T("access.pin");
            _expandButton.AccessibleName = Loc.T("tip.expand");
            _superToggleButton.AccessibleName = Loc.T("access.superStats");
            // Re-runs the whole tooltip block for the current pinned state.
            ApplyPinnedMode(_pinned, false);

            ApplyStaticCaptions();
            if (_compactCardsRoot != null)
                RefreshCompactCardsMenu(_compactCardsRoot);
            if (_graphsRoot != null)
                RefreshGraphsMenu(_graphsRoot);
            if (_cardColorRoot != null)
                RefreshCardColorMenu(_cardColorRoot);
            RefreshHistoryPanels();
            RenderCompactCards(_lastSnapshot, _lastSnapshot != null);
            if (_lastSnapshot != null)
                UpdateSnapshot(_lastSnapshot);
            HelpWindow.RefreshIfOpen();
            RunLayoutPass(false);
        }

        /// <summary>
        /// Captions that are assigned once at construction rather than on every
        /// snapshot.  Kept in one place so a language switch can replay them.
        /// </summary>
        private void ApplyStaticCaptions()
        {
            _cpuTemperature.SetCaption(Loc.T("caption.temperature"));
            _cpuUsage.SetCaption(Loc.T("caption.load"));
            _cpuClock.SetCaption(Loc.T("caption.clock"));
            _cpuPower.SetCaption(Loc.T("caption.power"));
            _gpuTemperature.SetCaption(Loc.T("caption.temperature"));
            _gpuUsage.SetCaption(Loc.T("caption.load"));
            _gpuClock.SetCaption(Loc.T("caption.clock"));
            _gpuPower.SetCaption(Loc.T("caption.power"));
            _memorySummary.SetTitle(Loc.T("caption.memory"));
            _backgroundCheckBox.Text = Loc.T("caption.noBackground");
            UpdateOpacityLabel();
        }

        private void UpdateOpacityLabel()
        {
            _opacityLabel.Text = _opacityCard.Width >= 220
                ? Loc.T("caption.opacityPadded") + _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void RememberCurrentSize()
        {
            if (_loadingSettings || _switchingView || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                return;
            if (_superExpanded)
                _superExpandedSize = LayoutClientSize;
            else if (_expanded)
                _expandedSize = LayoutClientSize;
            else
            {
                // The size about to be saved over the restored one.  A widget
                // that comes back a little larger than it was left has had this
                // run with something other than what was restored, and the log
                // has to say so before the old value is gone.
                if (_compactSize != LayoutClientSize)
                    DiagLog.Write("compact size changed from " +
                        _compactSize.Width + "x" + _compactSize.Height + " to " +
                        LayoutClientSize.Width + "x" + LayoutClientSize.Height);
                _compactSize = LayoutClientSize;
            }
        }

        private void ToggleOpacityPopup()
        {
            if (_opacityPopupVisible)
            {
                CloseOpacityPopup();
                return;
            }

            _opacityPopupVisible = true;
            _opacityPopupOpenedAt = DateTime.UtcNow;
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
            // Hiding the window that holds the foreground leaves this program
            // with no active window at all.  The next click on the widget is
            // then spent getting one back instead of doing what it was aimed
            // at, which is a right click that opens no menu until it is
            // repeated.  Only when the card itself is in front: if it is being
            // dismissed because another program was clicked, that program keeps
            // what it just took.
            bool cardHadForeground = _opacityCard != null && !_opacityCard.IsDisposed &&
                _opacityCard.IsHandleCreated &&
                GetForegroundWindow() == _opacityCard.Handle;
            _opacityPopupVisible = false;
            _opacityCard.Hide();
            _opacityButton.ForeColor = Color.FromArgb(165, 173, 184);
            if (!_pinned)
                _tips.SetToolTip(_opacityButton, OpacityTooltip);
            if (cardHadForeground && IsHandleCreated && Visible)
                SetForegroundWindow(Handle);
        }

        private static byte OpacityToAlpha(int percent)
        {
            return (byte)Math.Max(1, Math.Min(255, (int)Math.Round(percent * 2.55)));
        }

        private void SetOpacityPercent(int percent, bool save)
        {
            percent = Math.Max(10, Math.Min(100, percent));
            _opacityPercent = percent;
            if (LayeredMode)
            {
                // Form.Opacity is off limits here - see LayeredSurface.  The
                // surface may not exist yet when the settings are replayed, so
                // the value is kept and StartComposition picks it up.
                if (_surface != null)
                {
                    _surface.ConstantAlpha = OpacityToAlpha(percent);
                    _composeDirty = true;
                    ComposeIfDirty();
                }
            }
            else
            {
                Opacity = percent / 100.0;
            }
            _opacityLabel.Text = _opacityCard.Width >= 220
                ? Loc.T("caption.opacityPadded") + percent.ToString(CultureInfo.InvariantCulture) + "%"
                : percent.ToString(CultureInfo.InvariantCulture) + "%";
            if (_opacitySlider.Value != percent)
            {
                // Put back what it was rather than clear it.  Moving the slider
                // from code raises the same event a hand does, and this flag is
                // how that is told apart - but restoring settings sets it too,
                // and clearing it here ended the whole restore halfway through.
                // Everything replayed after opacity then counted as the user
                // resizing the window, so the size that had just been read was
                // saved over with whatever the default layout came out at:
                // a widget that comes back a little larger every time, with the
                // top bar showing after it was left hidden.
                bool wasLoading = _loadingSettings;
                _loadingSettings = true;
                _opacitySlider.Value = percent;
                _loadingSettings = wasLoading;
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

            // With per-pixel alpha there is no key to declare: the composite
            // simply leaves those pixels alone and the desktop keeps them.
            Color emptyBackground = LayeredMode ? Color.Transparent : BackgroundKey;
            if (enabled)
            {
                if (!LayeredMode)
                {
                    BackColor = BackgroundKey;
                    TransparencyKey = BackgroundKey;
                }
            }
            else
            {
                if (!LayeredMode)
                    TransparencyKey = Color.Empty;
                BackColor = NormalBackground;
            }

            // Switching the transparency key recreates the native window, which
            // resets its display affinity back to the default.
            ApplyDisplayAffinity();
            _detailsArea.BackColor = enabled ? emptyBackground : BackColor;
            _superArea.BackColor = enabled ? emptyBackground : BackColor;
            foreach (MonitorCard card in _cards)
                card.SetBackgroundless(enabled, emptyBackground);
            _cpuHistory.Backgroundless = enabled;
            _gpuHistory.Backgroundless = enabled;
            _memorySummary.Backgroundless = enabled;
            _storageSummary.Backgroundless = enabled;
            if (_compactSlots != null)
                foreach (CompactCardSlotView slot in _compactSlots)
                    slot.Column.Backgroundless = enabled;
            _opacityCard.BackColor = Color.FromArgb(29, 33, 40);
            foreach (Button button in _headerButtons)
                button.BackColor = HeaderButtonBackground;
            _backgroundButton.ForeColor = enabled
                ? Color.FromArgb(73, 190, 198)
                : Color.FromArgb(165, 173, 184);
            _tips.SetToolTip(_backgroundButton, _pinned
                ? String.Empty
                : enabled ? Loc.T("tip.background.restore") : Loc.T("tip.background.remove"));
            // Same reasoning as the header row: a button has to be reachable
            // over its whole face, not only where its glyph happens to be.
            _superToggleButton.BackColor = enabled
                ? (LayeredMode ? HeaderButtonGhost : emptyBackground)
                : NormalBackground;
            _opacitySlider.BackColor = Color.FromArgb(29, 33, 40);
            Invalidate(true);
            SyncBackgroundHitForm();
            RaiseOpenMenus();

            if (save)
                SaveSettings();
        }

        /// <summary>
        /// WDA_EXCLUDEFROMCAPTURE keeps the window on the physical monitor while
        /// removing it from desktop duplication, window capture and screenshots,
        /// so an overlay does not end up in a stream or a recording.
        /// </summary>
        private void ApplyStreamHidden(bool enabled, bool save)
        {
            _streamHidden = enabled;
            _streamHiddenItem.Checked = enabled;
            ApplyDisplayAffinity();
            RaiseOpenMenus();
            if (save)
                SaveSettings();
        }

        private void ApplyDisplayAffinity()
        {
            const uint none = 0x00000000;
            const uint excludeFromCapture = 0x00000011;
            uint affinity = _streamHidden ? excludeFromCapture : none;
            try
            {
                if (IsHandleCreated)
                    SetWindowDisplayAffinity(Handle, affinity);
                if (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                    _backgroundHitForm.IsHandleCreated)
                    SetWindowDisplayAffinity(_backgroundHitForm.Handle, affinity);
            }
            catch (EntryPointNotFoundException) { }
            KeepMenusAbove();
        }

        private void ApplyPinnedMode(bool enabled, bool save)
        {
            _pinned = enabled;
            _pinItem.Checked = enabled;
            _topMostItem.Enabled = true;
            Button[] lockedHeaderButtons =
            {
                _opacityButton, _backgroundButton, _cycleButton, _expandButton
            };
            foreach (Button button in lockedHeaderButtons)
            {
                button.Enabled = !enabled;
                button.BackColor = HeaderButtonBackground;
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
                ? Loc.T("tip.pin.on", HotkeyDisplay.Pin)
                : Loc.T("tip.pin.off", HotkeyDisplay.Pin));

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
                _tips.SetToolTip(_backgroundButton, _backgroundless ? Loc.T("tip.background.restore") : Loc.T("tip.background.remove"));
                _tips.SetToolTip(_expandButton, Loc.T("tip.expand"));
            }
            // Both own their tooltip on their own, including the pinned case, so
            // they run after the branch above rather than be overwritten by it.
            UpdateCompactCycleTooltip();
            UpdateLanguageButton();
            _superToggleButton.Invalidate();
            NoteMenuOrder("pin: controls");
            SyncPinnedClickThrough();
            SyncPinnedMouseHook();
            SyncBackgroundHitForm();
            NoteMenuOrder("pin: after catcher");
            if (!_loadingSettings)
                RunLayoutPass(false);
            NoteMenuOrder("pin: after layout");
            RaiseOpenMenus();
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
            bool verticalWindow = LayoutHeight > ClientSize.Width;
            int requiredWidth = verticalWindow ? 210 : 260;
            bool shouldBeExpanded = ClientSize.Width >= requiredWidth && LayoutHeight >= 300;
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
            ApplyMinimumSize();
            _expandButton.Text = "▾";
            _tips.SetToolTip(_expandButton, Loc.T("tip.expand"));
            _superToggleButton.Expanded = _superExpanded;
            if (_superExpanded)
                _superExpandedSize = LayoutClientSize;
            else if (_expanded)
                _expandedSize = LayoutClientSize;
            else
                _compactSize = LayoutClientSize;
            _automaticTransition = false;
        }

        /// <summary>
        /// How many cards a column of this height can show.  Two concise cards
        /// remain readable at 54 px each; using the full 58 px card height as the
        /// paging threshold left a narrow band where one oversized CPU card
        /// occupied the whole window even though CPU and GPU already fitted
        /// comfortably.
        /// </summary>
        private static int ColumnCardCapacity(int availableHeight, int cardCount)
        {
            const int preferredCardHeight = 54;
            const int gap = 8;
            return Math.Max(1, Math.Min(cardCount,
                (Math.Max(28, availableHeight) + gap) / (preferredCardHeight + gap)));
        }

        private void LayoutResponsive()
        {
            if (ClientSize.Width < 1 || ClientSize.Height < 1)
                return;

            ApplyDynamicSizeLimits();

            // The band along the bottom edge belongs to the expand strip alone.
            // It is never handed to the cards: the window is cropped instead, so
            // the values keep the exact geometry the user sized them to.
            const int bottomReserve = ChromeBandReserve;
            const int cardsTop = 29;
            int compactBottom = _expanded
                ? CompactHeight - 9
                : LayoutContentBottom - bottomReserve;
            int compactSideMargin = ClientSize.Width < 120 ? 6 : 10;
            int availableWidth = Math.Max(1, ClientSize.Width - compactSideMargin * 2);
            int gap = 8;
            MonitorCard[] allCompactCards = _compactSlots.Select(delegate(CompactCardSlotView slot)
            {
                return slot.Card;
            }).ToArray();
            int compactCardCount = allCompactCards.Length;
            bool verticalCompactLayout = !_expanded && LayoutHeight > ClientSize.Width;

            // A column dragged shorter gives up the header before it gives up a
            // card.  The bar is the one row on screen that is not a reading, so
            // it goes first and the last card stays for another band's worth of
            // dragging.  Measured against the whole window with the bar still on
            // it - not against the cropped one - so the answer never depends on
            // itself or on where the pointer is.
            _headerHiddenByColumnPressure = verticalCompactLayout &&
                ColumnCardCapacity(LayoutHeight - bottomReserve - cardsTop, compactCardCount) <
                    compactCardCount;

            bool headerHidden = IsHeaderHidden();
            // The tick follows the user's own standing choice and nothing else.
            // A widget shrunk to a strip, a column out of room or a pointer that
            // walked away all hide the bar too, but those are states the window
            // is in, not a setting, and moving a tick behind the user's back is
            // how a setting stops being trustworthy.
            UpdateHeaderMenuState();
            LayoutHeaderButtons();
            LayoutHeaderTitle(headerHidden);
            _topLeftResizeGrip.Location = Point.Empty;
            _leftResizeGrip.Location = new Point(0, ClientSize.Height - _leftResizeGrip.Height);
            _resizeGrip.Location = new Point(ClientSize.Width - _resizeGrip.Width, ClientSize.Height - _resizeGrip.Height);

            int compactTop = headerHidden ? cardsTop - CompactHeaderDelta : cardsTop;
            int compactAvailableHeight = Math.Max(28, compactBottom - compactTop);

            if (verticalCompactLayout)
            {
                int visibleCards = ColumnCardCapacity(compactAvailableHeight, compactCardCount);
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
            int detailsBottom = LayoutContentBottom - bottomReserve;
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

            // It lives in the band the chrome gives up, so the window shape
            // already takes it away with the rest of the band.  Toggling
            // Visible on top of that would only add a repaint - and one the
            // pointer would have to wait a whole sensor tick to get back.
            _superToggleButton.Visible = true;
            _superToggleButton.Expanded = _superExpanded;
            // Kept inside the band the shape gives up, top edge included, so
            // the strip disappears whole with the band instead of leaving a
            // few pixels of itself along the cut.
            _superToggleButton.Bounds = new Rectangle(compactSideMargin,
                Math.Max(0, LayoutContentBottom - ChromeBandHeight), availableWidth, 10);
            EnsureOnTop(_superToggleButton);

            bool absoluteMinimalCompact = !_expanded &&
                ClientSize.Width <= 120 && LayoutHeight <= CompactHeight;
            Rectangle topLeftGripBounds = new Rectangle(Point.Empty, _topLeftResizeGrip.Size);
            // Header buttons are right-anchored. At 137 px the leftmost one
            // first enters the resize marker, even before WinForms has completed
            // the native anchor pass for the current resize frame.
            bool topLeftGripOverlapsButton = ClientSize.Width <= 117 + GripSize ||
                _headerButtons.Any(delegate(Button button)
                {
                    return button.Visible && button.Bounds.IntersectsWith(topLeftGripBounds);
                });
            _topLeftGripAllowed = !headerHidden && !absoluteMinimalCompact &&
                !topLeftGripOverlapsButton;
            UpdateHoverChrome();
        }

        /// <summary>
        /// The resize markers follow the pointer the same way the bottom strip
        /// does, so an unattended widget shows nothing but its values.  They stay
        /// up while a resize is running, because the pointer leaves the window as
        /// soon as the corner is dragged outwards.
        /// </summary>
        private void UpdateHoverChrome()
        {
            bool visible = !_pinned && ShouldShowHoverChrome();
            SetGripVisible(_topLeftResizeGrip, visible && _topLeftGripAllowed);
            SetGripVisible(_leftResizeGrip, visible);
            SetGripVisible(_resizeGrip, visible);
        }

        private bool ShouldShowHoverChrome()
        {
            // A dropped-down menu takes the pointer outside the window bounds.
            // Cropping the widget away underneath it would be pure noise.
            return _pointerInside || _interactiveResize || _opacityPopupVisible ||
                (ContextMenuStrip != null && ContextMenuStrip.Visible) ||
                (_storageMenu != null && _storageMenu.Visible);
        }

        private Size LayoutClientSize
        {
            get { return ClientSize; }
        }

        /// <summary>
        /// The height every layout decision is made against.  The hover bands
        /// are given up by reshaping the window and never by resizing it, so
        /// this is simply the window: the layout is the same whether or not the
        /// pointer is on the widget, which is what keeps a decision from
        /// changing - and the cards from moving - as the pointer crosses.
        /// </summary>
        private int LayoutHeight
        {
            get { return ClientSize.Height; }
        }

        /// <summary>
        /// Where the content ends, in client coordinates.
        /// </summary>
        private int LayoutContentBottom
        {
            get { return LayoutHeight; }
        }

        /// <summary>
        /// Where the window is.  Kept as its own name because it is what gets
        /// saved and remembered, and the two used to differ while a band was
        /// cropped off the top.
        /// </summary>
        private Point LayoutLocation
        {
            get { return Location; }
        }

        /// <summary>
        /// Takes the header band off the window while the pointer is elsewhere,
        /// exactly the way the hover chrome gives up the band along the bottom.
        /// </summary>
        private void ApplyHeaderHoverCrop(bool crop)
        {
            ApplyHoverBands(_chromeCollapsed, crop);
        }

        private void RestoreHeaderHoverCrop()
        {
            ApplyHeaderHoverCrop(false);
        }

        /// <summary>
        /// Moves both hover bands at once, and moves nothing else: the bands
        /// are given up by reshaping the window, so the widget keeps its origin,
        /// its size, its layout and every pixel it had already drawn.  A pointer
        /// crossing the widget costs one region change and no repaint at all,
        /// which is what the twitching readings were - the window used to be
        /// resized under them twenty-five times a second.
        /// </summary>
        private void ApplyHoverBands(bool collapseChrome, bool cropHeader)
        {
            if (_chromeCollapsed == collapseChrome && _headerHoverHidden == cropHeader)
                return;
            // The shape must not change under the move/resize loop: it is read
            // once when the gesture starts, and replacing it mid-drag made a
            // dragged widget trail the pointer.  ExitSizeMove settles it.
            if (_interactiveResize)
                return;
            bool hiding = (collapseChrome && !_chromeCollapsed) ||
                (cropHeader && !_headerHoverHidden);
            if (hiding && (_switchingView || _loadingSettings))
                return;

            _chromeCollapsed = collapseChrome;
            _headerHoverHidden = cropHeader;
            ApplyWindowShape();
            SyncBackgroundHitForm();
        }

        private void ApplyMinimumSize()
        {
            Size desired = DesiredMinimumSize();
            if (MinimumSize != desired)
                MinimumSize = desired;
        }

        private Size DesiredMinimumSize()
        {
            // The hover bands cost the window no height any more, so the floor
            // no longer has to be loosened while one of them is given up.
            return new Size(MinimumCompactWidth,
                HeaderlessCompactMinimumHeight +
                // A bar that is never allowed to go needs somewhere to be: the
                // floor keeps room for it and for one row of readings under it,
                // instead of letting the window be dragged down onto the bar.
                (_headerMode == HeaderVisibilityMode.AlwaysVisible ? CompactHeaderDelta : 0));
        }

        private static void SetGripVisible(ResizeGripControl grip, bool visible)
        {
            if (grip.Visible != visible)
                grip.Visible = visible;
            if (visible)
                EnsureOnTop(grip);
        }

        // BringToFront reorders the child collection and repaints even when the
        // control already is in front.  Running that on every layout pass - one
        // per sensor tick - made the grips and the bottom strip visibly flicker.
        private static void EnsureOnTop(Control control)
        {
            Control parent = control.Parent;
            if (parent != null && parent.Controls.GetChildIndex(control, false) != 0)
                control.BringToFront();
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

        private void LayoutCompactValue(MonitorCard card, TextReadout value, CompactMetricColumn column, bool compactRate)
        {
            // Never infer semantic roles from z-order.  BringToFront() and the
            // vertical metric-column mode legitimately reorder child controls;
            // using Controls[0] then made a later pass treat the value itself
            // as the caption and produced the clipped fragments seen after a
            // few resize cycles.
            TextReadout caption = card.Controls.OfType<TextReadout>()
                .FirstOrDefault(delegate(TextReadout label) { return !ReferenceEquals(label, value); });
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
            // Both thresholds keep a margin against the decision the last pass
            // made.  A bare comparison flips at one exact width, and four cards
            // sharing a row cross it a pixel apart while the window is dragged:
            // the second reading disappears and comes back, or drops to its own
            // line and back, several times per drag.
            const int BothReadingsWidth = 112;
            const int WrapWidth = 132;
            const int Margin = 12;
            bool showOnlyPrimary = card.Width <
                (value.PrimaryOnly ? BothReadingsWidth + Margin : BothReadingsWidth);
            value.PrimaryOnly = showOnlyPrimary;
            int valueTop = card.Height < 38 ? 1 : card.Height < 48 ? 18 : 21;
            int valueHeight = Math.Max(1, card.Height - valueTop - 2);
            // A tall compact card has enough vertical room to become a real
            // glanceable display.  Keep both readings, stack them and let the
            // type grow with the card instead of leaving a small label in a
            // large empty rectangle.
            bool spaciousStack = !showOnlyPrimary && secondary.Length > 0 &&
                card.Height >= 86 && card.Height > card.Width * 0.72F;
            string singleLineText = showOnlyPrimary || secondary.Length == 0
                ? primary
                : primary + "   " + secondary;
            // Stacking was decided on width alone, so a card that was narrow but
            // also short got two lines into the room for one and a half: the
            // second one was cut across the middle by the card edge and read as
            // an artefact lying over the digits.  Without the vertical room the
            // readings stay on one line, where the type simply scales to fit.
            bool wrapValues = !showOnlyPrimary && secondary.Length > 0 &&
                valueHeight >= 38 &&
                (card.Width < (value.ValuesWrapped ? WrapWidth + Margin : WrapWidth) ||
                 spaciousStack);
            value.ValuesWrapped = wrapValues;
            value.Text = wrapValues ? primary + Environment.NewLine + secondary : singleLineText;
            float fontSize = Math.Max(10.5F, Math.Min(21F, 11F + (card.Height - 40) * 0.22F));
            if (spaciousStack)
            {
                float widthScale = Math.Max(16F, (card.Width - 16) / (compactRate ? 4.4F : 3.7F));
                float heightScale = Math.Max(16F, (card.Height - valueTop - 4) / 3.2F);
                fontSize = Math.Min(34F, Math.Min(widthScale, heightScale));
            }
            if (card.Height < 38)
                fontSize = Math.Min(fontSize, 12F);
            // No standing reduction for the rate cards any more.  It was there
            // because their readings are longer, but taking three points off up
            // front is what made a row of cards print at three different sizes;
            // the fit below already gives back exactly as much as each one
            // needs, and the row is levelled afterwards.
            if (compactRate)
                fontSize = Math.Max(8.5F, fontSize);
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
            value.Size = new Size(valueWidth, valueHeight);
            // Centred, never hung from the top edge.  Two cards whose type ended
            // up at different sizes shared a top edge and so sat on different
            // baselines, which reads as one of them being nudged upwards.
            value.TextAlign = ContentAlignment.MiddleLeft;
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

        /// <summary>
        /// The caption a card can actually print at this width.  One that does
        /// not fit used to wrap, which turned "ВЕНТИЛЯТОРЫ" into "ВЕНТИЛЯТОР"
        /// over a lone "Ы" and took the row the reading needed.
        ///
        /// So it is cut to a stem and a full stop instead.  Three or four
        /// letters, because that is the length the other captions already have -
        /// CPU, GPU, FPS - and a column of captions that are all about the same
        /// length is one the eye can skip over on the way to the numbers.
        /// </summary>
        private static string ShortenCaption(string text, Font font, int width)
        {
            if (String.IsNullOrEmpty(text) || width < 1)
                return text ?? String.Empty;
            if (TextReadout.MeasureText(text, font) <= width)
                return text;
            for (int length = Math.Min(text.Length - 1, 4); length >= 3; length--)
            {
                string stem = text.Substring(0, length).TrimEnd(' ', '.', '-', '·') + ".";
                if (TextReadout.MeasureText(stem, font) <= width)
                    return stem;
            }
            // Narrower than four letters of 7.5pt type is not a caption any
            // more; the reading gets the room and the card keeps its accent
            // colour as the only thing left saying what it is.
            return String.Empty;
        }

        private static void SetCompactCaptionLayout(TextReadout caption, bool visible,
            Rectangle bounds, ContentAlignment alignment)
        {
            if (caption == null)
                return;
            string originalText = caption.Tag as string ?? caption.Text;
            string nextText = visible
                ? ShortenCaption(originalText, caption.Font, bounds.Width)
                : String.Empty;
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
            LevelCompactValueFonts();
        }

        /// <summary>
        /// One type size for the whole row.
        ///
        /// Each card fits its own type to its own longest reading, which is
        /// right on its own and wrong in a row: four cards side by side ended up
        /// at four sizes, and a row you have to focus on card by card is not a
        /// row you can take in with one look.  So the smallest size any card
        /// needed becomes everybody's.  Only shrinking happens here, so nothing
        /// that already fitted can start to overflow.
        /// </summary>
        private void LevelCompactValueFonts()
        {
            float smallest = Single.MaxValue;
            foreach (CompactCardSlotView slot in _compactSlots)
            {
                // The same test LayoutCompactValue used to switch to the metric
                // column, asked of the card rather than of Visible - which is
                // false for every control while the window itself is hidden.
                if (slot.Card.Height >= 100 || slot.Value.Text.Length == 0)
                    continue;
                smallest = Math.Min(smallest, slot.Value.Font.SizeInPoints);
            }
            if (smallest == Single.MaxValue)
                return;

            foreach (CompactCardSlotView slot in _compactSlots)
            {
                if (slot.Card.Height >= 100 || slot.Value.Text.Length == 0)
                    continue;
                if (slot.Value.Font.SizeInPoints - smallest < 0.01F)
                    continue;
                SetLabelFont(slot.Value, smallest, slot.Value.Font.Style);
            }
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
                ? Loc.T("caption.opacityPadded") + _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%"
                : _opacitySlider.Value.ToString(CultureInfo.InvariantCulture) + "%";
            _opacityLabel.Location = new Point(8, 5);
            _opacityLabel.Size = new Size(labelWidth, 22);
            _opacitySlider.Location = new Point(labelWidth + 9, 5);
            _opacitySlider.Size = new Size(Math.Max(1, _opacityCard.Width - labelWidth - 17), 22);
        }

        private static void SetLabelFont(TextReadout label, float size, FontStyle style)
        {
            if (Math.Abs(label.Font.Size - size) < 0.15F && label.Font.Style == style)
                return;
            Font oldFont = label.Font;
            label.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            // Only a font this label owns.  Control.Font hands back the parent's
            // font until one is set here, and disposing that one takes the font
            // out from under every sibling that had not been given its own -
            // after which every measurement and every draw with it fails.
            Control parent = label.Parent;
            if (parent == null || !ReferenceEquals(oldFont, parent.Font))
                oldFont.Dispose();
        }

        private static void FitLabelFont(TextReadout label, float maximumSize, float minimumSize, FontStyle style)
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
            // The storage panel answers a press anywhere on itself, so a press
            // that reached the panel underneath it - the empty space between
            // the title and the bar belongs to the parent, not to the panel -
            // must open the drive list rather than pick the window up.
            if (TryOpenStorageMenu(Cursor.Position))
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
            ToggleViewByDoubleClick();
            return true;
        }

        private void BackgroundHitMouseDown(object sender, MouseEventArgs e)
        {
            // Where the press happened, not where the pointer is by the time it
            // is handled.  Input arrives late when the widget has been sitting
            // in the background, and Cursor.Position by then can be a hundred
            // pixels away - a press that then lands on whatever the pointer
            // wandered onto, or on nothing at all.
            Point screen = _backgroundHitForm.PointToScreen(e.Location);
            StartupTrace.Write("catcher mousedown " + e.Button + " at " + screen +
                " (pointer now " + Cursor.Position + ")");
            // A press on an open menu is the menu's, whatever the z-order says
            // at that instant.  Without this, a catcher that has climbed over
            // the menu - see KeepMenusAbove - swallows a click aimed at an
            // entry and the entry simply does not respond.
            if (IsMenuOpen() && PointOverDropDown(ContextMenuStrip, screen))
            {
                DiagLog.Write("catcher swallowed a press over the menu at " +
                    screen.X.ToString(CultureInfo.InvariantCulture) + "," +
                    screen.Y.ToString(CultureInfo.InvariantCulture));
                RaiseOpenMenus();
                return;
            }
            if (e.Button == MouseButtons.Middle)
            {
                ToggleOpacityPopup();
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;
            if (_pinned)
            {
                // One exception to "let the click through": the pin itself.
                // Without it, a widget pinned while backgroundless can only be
                // released from the tray or with the hotkey - the button is on
                // screen, says what it does, and does nothing.
                Button pinButton = HeaderButtonAtScreen(screen);
                if (pinButton != null && ReferenceEquals(pinButton, _pinButton))
                {
                    IRelayClick pinRelay = pinButton as IRelayClick;
                    if (pinRelay != null)
                        pinRelay.RelayClick();
                    else
                        pinButton.PerformClick();
                    return;
                }
                // Anything else should not have reached the catcher at all: its
                // hit test answers "not this window" for a left press while
                // pinned, so the press goes to whatever is underneath before
                // this is ever called.  Swallowed rather than acted on, in case
                // one slips through - the whole point of pinning is that a left
                // click is not the widget's.
                return;
            }

            Button pressed = HeaderButtonAtScreen(screen);
            if (pressed != null)
            {
                StartupTrace.Write("catcher forwards to " + pressed.AccessibleName);
                // Not PerformClick: header buttons are deliberately not
                // selectable - they must never pull the focus out of whatever
                // the user is working in - and PerformClick begins with a
                // CanSelect test, so on these buttons it has always done
                // exactly nothing.  That is the whole of "the first click does
                // not work": a press that landed on the catcher was relayed
                // into a method that silently dropped it.
                IRelayClick relay = pressed as IRelayClick;
                if (relay != null)
                    relay.RelayClick();
                else
                    pressed.PerformClick();
                return;
            }
            if (TryOpenStorageMenu(screen))
                return;

            Point point = PointToClient(screen);
            int hitTest = GetResizeHitTest(point, ResizeEdge);
            if (hitTest != 1)
                BeginResize(hitTest);
            else
                DragWindow(this, e);
        }

        /// <summary>
        /// The header button under a point on screen, if any.  Asked in screen
        /// coordinates on purpose: the catcher hands clicks back from a window
        /// of its own, and a button that later moves into a panel would silently
        /// stop matching a rectangle read out of its parent's coordinate space.
        /// </summary>
        private Button HeaderButtonAtScreen(Point screen)
        {
            foreach (Button button in _headerButtons)
            {
                if (button == null || !button.Visible || !button.Enabled ||
                    !button.IsHandleCreated)
                    continue;
                if (button.RectangleToScreen(button.ClientRectangle).Contains(screen))
                    return button;
            }
            if (_superToggleButton != null && _superToggleButton.Visible &&
                _superToggleButton.Enabled && _superToggleButton.IsHandleCreated &&
                RectangleToScreen(StripHitBounds()).Contains(screen))
                return _superToggleButton;
            return null;
        }

        // Child controls swallow MouseLeave for their parent, so the pointer is
        // polled rather than tracked.  A tick costs one GetCursorPos and repaints
        // nothing unless the strip is actually mid-animation.
        private void UpdateStripPresence()
        {
            _pointerInside = !_stopping && Visible &&
                WindowState != FormWindowState.Minimized &&
                Bounds.Contains(Cursor.Position);
            SyncHelpHotkey();
            SyncHotkeys();
            bool chrome = ShouldShowHoverChrome();
            // The top bar answers to the user's own setting and to nothing else:
            // automatic still reveals it under the pointer while the widget is
            // pinned, always-hidden keeps it hidden, always-shown keeps it
            // shown.  The strip along the bottom is different - it expands the
            // window, and pinned there is no click that can reach it - so it
            // stays down while the widget is click-through.
            bool strip = chrome && !_pinned;
            // A tick that changes nothing must cost nothing.  This runs
            // twenty-five times a second, and forcing a repaint here - even a
            // batched one - is a widget that flickers while the pointer is on
            // the other side of the screen.  Both calls below already do
            // nothing when the state they own has not moved.
            if (strip != _chromeShown)
            {
                _chromeShown = strip;
                UpdateHoverChrome();
            }
            // Reconciled on every tick and not only on the edge: a gesture that
            // froze the shape leaves the two out of step until something asks
            // again.
            ApplyHoverBands(!strip, !chrome && HeaderBandBelongsToChrome());

            double target = strip ? 1 : 0;
            if (Math.Abs(_stripFade - target) < 0.001)
                return;
            _stripFade = strip
                ? Math.Min(1, _stripFade + 0.25)
                : Math.Max(0, _stripFade - 0.1);
            _superToggleButton.Fade = _stripFade;
        }

        private Cursor ResolveBackgroundHitCursor(Point screenPoint)
        {
            if (_pinned)
                return Cursors.Default;
            Point point = PointToClient(screenPoint);
            if (IsPointOverResizeExcludedControl(point))
                return Cursors.Default;
            switch (GetResizeHitTest(point, ResizeEdge))
            {
                case 13:
                case 17:
                    return Cursors.SizeNWSE;
                case 14:
                case 16:
                    return Cursors.SizeNESW;
                case 10:
                case 11:
                    return Cursors.SizeWE;
                case 12:
                case 15:
                    return Cursors.SizeNS;
                default:
                    return Cursors.Default;
            }
        }

        private void SyncBackgroundHitForm()
        {
            if (_backgroundHitForm == null || _backgroundHitForm.IsDisposed)
                return;

            // The catcher is never hidden for the duration of a drag any more.
            // A drag over a transparent area starts on the catcher, and hiding
            // the window the gesture started on pulls the foreground out from
            // under the move loop: the widget then only caught up with the
            // pointer once the button was released.
            // Pinned counts as well as backgroundless: a pinned widget is out of
            // the hit test whole, so without the catcher there is nothing left
            // to right-click and no way to reach the pin.
            bool shouldShow = (_backgroundless || _pinned) && Visible &&
                WindowState != FormWindowState.Minimized;
            // Showing or hiding a window shifts the foreground, and an open menu
            // closes on that - AppFocusChange, which is not a click and cannot
            // be cancelled without swallowing every real focus change with it.
            // The catcher has nothing to catch while a menu is up anyway: the
            // menu holds the input until it closes, and it is synced then.
            if (shouldShow != _backgroundHitForm.Visible && IsMenuOpen())
            {
                DeferCatcherSync();
                return;
            }
            if (!shouldShow)
            {
                if (_backgroundHitForm.Visible)
                    _backgroundHitForm.Hide();
                return;
            }

            // Not while a menu is standing: this setter lands the catcher at the
            // top of the topmost band, which is over the menu.  The band is
            // restored once the menu closes.
            if (!IsMenuOpen())
                _backgroundHitForm.TopMost = TopMost;
            if (!_backgroundHitForm.Visible)
            {
                _backgroundHitForm.Bounds = Bounds;
                _backgroundHitForm.Show();
                ApplyDisplayAffinity();
            }
            NativeUi.SetWindowPos(_backgroundHitForm.Handle, Handle,
                Left, Top, Width, Height, 0x0010);
            ApplyBackgroundHitRegion();
            KeepMenusAbove();
        }

        /// <summary>
        /// Puts an open menu back on top the instant something has climbed over
        /// it, rather than on the next tick.
        ///
        /// Setting TopMost - on the widget or on the catcher - lands that window
        /// at the top of the topmost band, above a menu that is already on
        /// screen.  Switches in the menu do exactly that: pinning, "always on
        /// top", dropping the background.  Forty milliseconds later the tick
        /// undoes it, which is why it reads as the widget flashing over the
        /// menu.  In that gap the catcher is also the window the hit test finds,
        /// so a click aimed at an entry lands on the catcher and the entry never
        /// hears it - the same fault seen from the other side.
        /// </summary>
        private void KeepMenusAbove()
        {
            KeepWidgetBelowMenus();
        }

        /// <summary>
        /// Writes down the moment the widget is in front of a menu that is on
        /// screen, and which piece of work had just run.  The flash lasts less
        /// than a frame, so it cannot be caught by looking; naming the caller
        /// that leaves the window in that state is the only way to find out
        /// which one is doing it.
        /// </summary>
        private void NoteMenuOrder(string where)
        {
            if (WidgetCoversMenu())
                DiagLog.Write("menu covered after " + where);
        }

        private bool WidgetCoversMenu()
        {
            if (!IsMenuOpen() || !ContextMenuStrip.IsHandleCreated || !IsHandleCreated)
                return false;
            IntPtr menu = ContextMenuStrip.Handle;
            return NativeUi.IsInFrontOf(Handle, menu) ||
                (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                    _backgroundHitForm.IsHandleCreated && _backgroundHitForm.Visible &&
                    NativeUi.IsInFrontOf(_backgroundHitForm.Handle, menu));
        }

        /// <summary>
        /// Puts the menu back in front when the widget has climbed over it, and
        /// does nothing at all the rest of the time.
        ///
        /// This used to re-declare the menu and every open sub-menu topmost on
        /// every tick, whether or not anything had moved.  Twenty-five times a
        /// second that is the menu being re-stacked while the pointer runs down
        /// it, which is the text shivering and the sub-menus twitching as they
        /// open.  Checking first costs one walk of the z-order and re-stacks
        /// nothing on the ticks where the order is already right, which is
        /// almost all of them.
        ///
        /// The widget was held out of the topmost band for the life of the menu
        /// instead for a while, which does settle the order - but a menu here
        /// stays open across the switches used in it, and for all that time the
        /// widget was behind every ordinary window on the screen.  Clicking
        /// anything while the menu stood therefore buried the widget, which is a
        /// far worse fault than the one it fixed.
        /// </summary>
        private void KeepWidgetBelowMenus()
        {
            if (!WidgetCoversMenu())
                return;
            DiagLog.Write("widget had climbed over an open menu");
            RaiseOpenMenus();
        }

        /// <summary>
        /// Puts the widget back on top when it has fallen out of the band
        /// without being asked to.  Placing a window relative to another one can
        /// move it between bands, and there are enough of those calls here -
        /// the catcher, the menus, the pass-through - that the safe assumption
        /// is that one of them will get it wrong.  On screen the mistake is a
        /// widget that has quietly gone behind everything and does not come
        /// back until it is hidden and shown again.
        /// </summary>
        /// <summary>
        /// Records the window going and coming back.  A widget that has fallen
        /// out of the topmost band and one that has actually been hidden look
        /// identical to the user - both are simply gone - and they are opposite
        /// faults, so the log has to be able to tell them apart.
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // Settings are read before there is a window to write the style on,
            // so a widget that starts pinned gets it here.
            if (Visible)
                SyncPinnedClickThrough();
            SyncPinnedMouseHook();
            DiagLog.Write("widget " + (Visible ? "shown" : "hidden") +
                " pinned=" + (_pinned ? "1" : "0") +
                " menuOpen=" + (IsMenuOpen() ? "1" : "0"));
        }

        private void KeepWidgetInBand()
        {
            // Asked of the z-order and of nothing else.  The style bit is not a
            // second opinion worth having: it reads stale-clear on a window that
            // is topmost, and this program now writes it back set to stop that,
            // so it reads stale-set on a window that is not.  It lies in both
            // directions and cannot even be used as a cheap way to skip the
            // walk.  The walk is short in the case that matters - it stops at
            // the first visible window in front - and this runs on the tick that
            // was already running.
            if (!IsHandleCreated || !Visible || !TopMost ||
                !NativeUi.IsBuriedUnderNormalWindow(Handle))
            {
                _bandFallReported = false;
                return;
            }
            string before = NativeUi.DescribeExStyle(Handle);
            ForceTopMostBand();
            bool recovered = !NativeUi.IsBuriedUnderNormalWindow(Handle);
            // The first of a run, and then at most once a second for as long as
            // the window refuses to come back.  A fault that repeats every forty
            // milliseconds otherwise fills the log with the one line and buries
            // what came before it, which is the part that says why - but a
            // recovery that never takes has to keep saying so, or the log reads
            // as a single hiccup that was put right.
            DateTime now = DateTime.UtcNow;
            if (!_bandFallReported ||
                (!recovered && (now - _lastBandComplaint).TotalMilliseconds >= 1000))
            {
                _bandFallReported = true;
                _lastBandComplaint = now;
                DiagLog.Write("widget had fallen out of the topmost band" +
                    " pinned=" + (_pinned ? "1" : "0") +
                    " backgroundless=" + (_backgroundless ? "1" : "0") +
                    " exstyle=" + before +
                    " after=" + NativeUi.DescribeExStyle(Handle) +
                    " recovered=" + (recovered ? "1" : "0"));
            }
        }

        /// <summary>
        /// Puts the widget back in the topmost band the hard way, by taking it
        /// out of the band first.
        ///
        /// SetWindowPos(HWND_TOPMOST) on a window the system already counts as
        /// topmost does nothing at all, and the system's own bookkeeping and the
        /// extended style word can disagree - a raw style write is a whole-word
        /// write that does not go through the band accounting.  The polite call
        /// is then a no-op on a widget that really is behind everything, and it
        /// stays there for the rest of the session.  Passing through
        /// HWND_NOTOPMOST forces the transition to actually be made.
        /// </summary>
        private void ForceTopMostBand()
        {
            const uint NoSize = 0x0001;
            const uint NoMove = 0x0002;
            const uint NoActivate = 0x0010;
            uint flags = NoSize | NoMove | NoActivate;
            if (_backgroundHitForm != null && !_backgroundHitForm.IsDisposed &&
                _backgroundHitForm.IsHandleCreated && _backgroundHitForm.Visible)
            {
                NativeUi.SetWindowPos(_backgroundHitForm.Handle, NotTopMostWindow,
                    0, 0, 0, 0, flags);
                NativeUi.SetWindowPos(_backgroundHitForm.Handle, TopMostWindow,
                    0, 0, 0, 0, flags);
            }
            if (IsHandleCreated)
            {
                NativeUi.SetWindowPos(Handle, NotTopMostWindow, 0, 0, 0, 0, flags);
                NativeUi.SetWindowPos(Handle, TopMostWindow, 0, 0, 0, 0, flags);
            }
            // Landing at the top of the band puts the widget over a menu that is
            // standing, which is the one thing this must not leave behind.
            if (IsMenuOpen())
                RaiseOpenMenus();
        }

        /// <summary>
        /// Shapes the catcher.  Unpinned it covers the whole widget, because the
        /// widget is what the pointer is aiming at.  Pinned it is cut down to the
        /// panels that carry readings: pinning exists so a click goes to the game
        /// underneath, and a full-window catcher would quietly take that back,
        /// while a catcher shaped like the cards still leaves the numbers
        /// right-clickable.
        /// </summary>
        private void ApplyBackgroundHitRegion()
        {
            if (_backgroundHitForm == null || _backgroundHitForm.IsDisposed ||
                !_backgroundHitForm.IsHandleCreated)
                return;

            Region previous = _backgroundHitForm.Region;
            if (!_pinned)
            {
                // The catcher stands in for the widget, so it is cut the same
                // way.  Left whole it would keep taking clicks in the bands the
                // widget itself has given up - a strip of empty desktop above
                // and below the readings that quietly is not empty.
                Rectangle crop = HoverCropRectangle();
                // Re-applied only when it actually moves: setting a window
                // region repaints everything behind the window, and this runs
                // on every layout pass.
                if (_backgroundHitCrop == crop && previous != null)
                    return;
                _backgroundHitCrop = crop;
                _backgroundHitShape = null;
                _backgroundHitForm.Region = new Region(crop);
            }
            else
            {
                _backgroundHitCrop = Rectangle.Empty;
                Point origin = _backgroundHitForm.Location;
                List<Rectangle> boxes = new List<Rectangle>();
                // The pin and nothing else.  A catcher shaped like the readings
                // was how the right click was kept while pinned, and it is also
                // exactly why a left click on the numbers went nowhere: a
                // window that is there for one button is there for all of them.
                // Answering the hit test with HTTRANSPARENT does not undo that
                // - the search it restarts only walks windows of the same
                // thread, so it can hand a click to another window of this
                // program and never to the program underneath, which is the one
                // place it had to go.  The right press is taken by the mouse
                // hook now, and the pin stays a window because it is the only
                // thing here that is still meant to be clicked.
                if (_pinButton != null && !_pinButton.IsDisposed &&
                    _pinButton.Visible && _pinButton.IsHandleCreated)
                {
                    Rectangle pin = _pinButton.RectangleToScreen(_pinButton.ClientRectangle);
                    pin.Offset(-origin.X, -origin.Y);
                    boxes.Add(pin);
                }
                // The unpinned branch above compares before it assigns because
                // setting a window region repaints everything behind the
                // window; this one did not, so a pinned widget re-cut itself
                // and made the desktop under it repaint on every layout pass
                // for a shape that had not moved.
                if (previous != null && SameBoxes(_backgroundHitShape, boxes))
                    return;
                _backgroundHitShape = boxes;
                Region shape = new Region();
                shape.MakeEmpty();
                foreach (Rectangle box in boxes)
                    shape.Union(box);
                _backgroundHitForm.Region = shape;
            }
            if (previous != null)
                previous.Dispose();
        }

        private static bool SameBoxes(List<Rectangle> left, List<Rectangle> right)
        {
            if (left == null || right == null || left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
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

        /// <summary>
        /// Gives the window its shape: rounded corners, minus whatever hover
        /// bands are currently given up.  The bands are cut out of the shape
        /// and not out of the geometry, which is the whole reason the readings
        /// hold still - the window keeps its origin, its size and its child
        /// positions when the pointer arrives or leaves, so nothing is laid out
        /// again and nothing is repainted.  Only the desktop behind the bands
        /// comes back.  Cropping by geometry moved the origin instead, and the
        /// move and the repaint that followed it were two frames: the digits
        /// visibly dropped a bar's worth and snapped back.
        /// </summary>
        private void ApplyWindowShape()
        {
            if (!IsHandleCreated)
                return;
            Rectangle visible = HoverCropRectangle();
            IntPtr regionHandle = NativeUi.CreateRoundRectRgn(0, visible.Top,
                Width + 1, visible.Bottom + 1, 16, 16);
            Region oldRegion = Region;
            Region = Region.FromHrgn(regionHandle);
            NativeUi.DeleteObject(regionHandle);
            if (oldRegion != null)
                oldRegion.Dispose();
        }

        /// <summary>
        /// The part of the window that is on screen: everything, minus whatever
        /// hover band is currently given up.  The expand strip is laid out to
        /// sit entirely inside the bottom band, so the shape takes it away with
        /// the band and the control never has to be hidden - hiding it would be
        /// one more repaint, in the one place the pointer is looking.
        /// </summary>
        private Rectangle HoverCropRectangle()
        {
            int top = _headerHoverHidden ? CompactHeaderDelta : 0;
            int bottom = Height - (_chromeCollapsed ? ChromeBandHeight : 0);
            if (bottom - top < 1)
                return new Rectangle(0, 0, Width, Height);
            return Rectangle.FromLTRB(0, top, Width, bottom);
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

        /// <summary>
        /// Reads one stored number.  The key lives under the current user, so
        /// anything can end up in it - a hand edit, a tidy-up script, a build
        /// that wrote the value as text - and the conversion used to throw from
        /// inside the constructor.  That is a widget which crashes on every
        /// start and cannot be fixed without regedit, so a value that will not
        /// convert now costs its own setting and nothing else.
        /// </summary>
        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            try
            {
                object value = key.GetValue(name);
                return value == null
                    ? fallback
                    : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (FormatException) { return fallback; }
            catch (InvalidCastException) { return fallback; }
            catch (OverflowException) { return fallback; }
        }

        private static bool ReadFlag(RegistryKey key, string name, bool fallback)
        {
            return ReadInt(key, name, fallback ? 1 : 0) != 0;
        }

        /// <summary>
        /// A stored window edge, held between the smallest size that still
        /// works and the whole desktop.  Only the floor used to be enforced,
        /// and a stored width of a few million would have been believed - which
        /// is a window nobody can reach and a frame buffer to match.
        /// </summary>
        private static int StoredWidth(RegistryKey key, string name, int fallback, int minimum)
        {
            return Math.Max(minimum, Math.Min(
                Math.Max(minimum, SystemInformation.VirtualScreen.Width),
                ReadInt(key, name, fallback)));
        }

        private static int StoredHeight(RegistryKey key, string name, int fallback, int minimum)
        {
            return Math.Max(minimum, Math.Min(
                Math.Max(minimum, SystemInformation.VirtualScreen.Height),
                ReadInt(key, name, fallback)));
        }

        private static string ReadText(RegistryKey key, string name, string fallback)
        {
            try
            {
                object value = key.GetValue(name);
                if (value == null)
                    return fallback;
                string text = value as string;
                return text ?? (Convert.ToString(value, CultureInfo.InvariantCulture)
                    ?? fallback);
            }
            catch (FormatException) { return fallback; }
            catch (InvalidCastException) { return fallback; }
        }

        private void LoadSettings()
        {
            _loadingSettings = true;
            bool positioned = false;
            int opacityPercent = 90;
            bool expanded = false;
            bool superExpanded = false;
            bool backgroundless = false;
            bool streamHidden = false;
            bool pinned = false;
            HeaderVisibilityMode headerMode = HeaderVisibilityMode.Automatic;
            bool headerAutomaticallyHidden = false;
            // Every value is read defensively below, but the key itself can be
            // denied and the parsers are handed whatever the key holds.  This
            // runs from the constructor, so anything that escapes here is a
            // program that will not start at all; starting on the defaults is
            // always the better answer.
            try
            {
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
                        TopMost = ReadFlag(key, "TopMost", true);
                        opacityPercent = ReadInt(key, "Opacity", 90);
                        expanded = ReadFlag(key, "Expanded", false);
                        superExpanded = ReadFlag(key, "SuperExpanded", false);
                        backgroundless = ReadFlag(key, "Backgroundless", false);
                        streamHidden = ReadFlag(key, "StreamHidden", false);
                        pinned = ReadFlag(key, "Pinned", false);
                        // The bar used to be one switch, "hidden by hand" or not.
                        // A saved switch migrates to the mode that means the same.
                        headerMode = ParseHeaderMode(ReadText(key, "HeaderModeV1", String.Empty),
                            ReadFlag(key, "HeaderManuallyHidden", false)
                                ? HeaderVisibilityMode.AlwaysHidden
                                : HeaderVisibilityMode.Automatic);
                        headerAutomaticallyHidden = ReadFlag(key, "HeaderAutomaticallyHidden", false);
                        _selectedStorageDrive = ReadText(key, "StorageDrive", String.Empty);
                        _compactSlotKinds = ParseCompactSlotKinds(ReadText(key, "CompactSlotsV1",
                            SerializeCompactSlotKinds(CreateSystemCompactPreset())));
                        _cardAccents = ParseCardAccents(ReadText(key, "CardAccentsV1", String.Empty));
                        // Falls back to the live set so a palette saved before this
                        // key existed is adopted instead of starting out empty.
                        _customCardAccents = ParseCardAccents(ReadText(key, "CustomAccentsV1",
                            SerializeCardAccents(_cardAccents)));
                        string customPreset = ReadText(key, "CustomSlotsV1", String.Empty);
                        _customCompactPreset = String.IsNullOrWhiteSpace(customPreset)
                            ? null
                            : ParseCompactSlotKinds(customPreset);
                        Loc.Code = Loc.Parse(ReadText(key, "Language", String.Empty), Loc.Code);
                        // What the header button comes back to from English.  A
                        // session that ended in English still has to know which
                        // language it was English instead of.
                        _preferredLanguage = Loc.Parse(
                            ReadText(key, "LanguagePreferred", String.Empty),
                            String.Equals(Loc.Code, EnglishCode, StringComparison.OrdinalIgnoreCase)
                                ? _preferredLanguage
                                : Loc.Code);
                        _leftGraphSource = ParseCompactCardKind(
                            ReadText(key, "GraphLeftV1", String.Empty), CompactCardKind.Cpu);
                        _rightGraphSource = ParseCompactCardKind(
                            ReadText(key, "GraphRightV1", String.Empty), CompactCardKind.Gpu);
                        _customGraphPreset = ParseGraphPair(
                            ReadText(key, "CustomGraphsV1", String.Empty));
                        _pinHotkey.Wanted = HotkeyBinding.Parse(
                            ReadText(key, "HotkeyPinV1", String.Empty), HotkeyBinding.DefaultPin);
                        _hideHotkey.Wanted = HotkeyBinding.Parse(
                            ReadText(key, "HotkeyHideV1", String.Empty), HotkeyBinding.DefaultHide);
                        _helpBinding = HotkeyBinding.Parse(
                            ReadText(key, "HotkeyHelpV1", String.Empty), HotkeyBinding.DefaultHelp);
                        _dismissBinding = HotkeyBinding.Parse(
                            ReadText(key, "HotkeyDismissV1", String.Empty), HotkeyBinding.DefaultDismiss);
                        _compactSize = new Size(
                            StoredWidth(key, "CompactWidth", WindowWidth, MinimumCompactWidth),
                            StoredHeight(key, "CompactHeight", CompactHeight,
                                HeaderlessCompactMinimumHeight));
                        int storedExpandedHeight = StoredHeight(key, "ExpandedHeight",
                            ExpandedHeight, 278);
                        // Migrate the former defaults that reserved a separate
                        // opacity row to the clipped, continuous card layout.
                        if (storedExpandedHeight == 444 || storedExpandedHeight == 396)
                            storedExpandedHeight = ExpandedHeight;
                        _expandedSize = new Size(
                            StoredWidth(key, "ExpandedWidth", WindowWidth, 220),
                            Math.Max(278, storedExpandedHeight));
                        _superExpandedSize = new Size(
                            StoredWidth(key, "SuperWidth", SuperExpandedWidth, SuperExpandedWidth),
                            StoredHeight(key, "SuperHeight", SuperExpandedHeight, SuperExpandedHeight));
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
                        bool storedReturnKnown = ReadFlag(key, "SuperReturnKnown", false);
                        if (storedReturnKnown)
                        {
                            Point returnPoint = new Point(
                                ReadInt(key, "SuperReturnX", Location.X),
                                ReadInt(key, "SuperReturnY", Location.Y));
                            Size returnSize = new Size(
                                StoredWidth(key, "SuperReturnWidth", _compactSize.Width,
                                    MinimumCompactWidth),
                                StoredHeight(key, "SuperReturnHeight", _compactSize.Height,
                                    HeaderlessCompactMinimumHeight));
                            Rectangle returnBounds = new Rectangle(returnPoint, returnSize);
                            if (Screen.AllScreens.Any(delegate(Screen screen)
                                {
                                    return screen.WorkingArea.IntersectsWith(returnBounds);
                                }))
                            {
                                _superReturnStateKnown = true;
                                _superReturnExpanded = ReadFlag(key, "SuperReturnExpanded", false);
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
            }
            catch (Exception error)
            {
                DiagLog.Write("settings unreadable " + error.GetType().Name + ": " +
                    error.Message);
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
            _headerMode = headerMode;
            _restoredAutomaticHeaderHidden = headerAutomaticallyHidden &&
                headerMode == HeaderVisibilityMode.Automatic;
            UpdateHeaderMenuState();
            SetExpanded(expanded, false);
            if (expanded && superExpanded)
                SetSuperExpanded(true, false);
            ApplyBackgroundMode(backgroundless, false);
            ApplyStreamHidden(streamHidden, false);
            ApplyPinnedMode(pinned, false);
            _loadingSettings = false;
            UpdateFrameTelemetryDemand();
            UpdateCompactCycleTooltip();
            UpdateLanguageMenuState();
            RetranslateUi();
            RenderCompactCards(null, false);
            DiagLog.Write("settings restored" +
                " compact=" + _compactSize.Width + "x" + _compactSize.Height +
                " applied=" + LayoutClientSize.Width + "x" + LayoutClientSize.Height +
                " at=" + Location.X + "," + Location.Y +
                " expanded=" + (expanded ? "1" : "0") +
                " pinned=" + (pinned ? "1" : "0") +
                " headerMode=" + GetHeaderModeId(_headerMode) +
                " headerHidden=" + (_restoredAutomaticHeaderHidden ? "1" : "0"));
        }

        private void SaveSettings()
        {
            if (_loadingSettings)
                return;
            RememberCurrentSize();
            if (!_expanded)
            {
                _compactLocation = LayoutLocation;
                _compactLocationKnown = true;
            }
            try
            {
                WriteSettings();
            }
            catch (Exception error)
            {
                // Every gesture that changes something saves, including the
                // wheel, and that runs inside the message filter: a key that
                // cannot be written must cost the setting, not the session.
                DiagLog.Write("settings unwritable " + error.GetType().Name + ": " +
                    error.Message);
            }
        }

        private void WriteSettings()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppRegistryPath))
            {
                if (key == null)
                    return;
                key.SetValue("X", LayoutLocation.X, RegistryValueKind.DWord);
                key.SetValue("Y", LayoutLocation.Y, RegistryValueKind.DWord);
                key.SetValue("TopMost", TopMost ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Opacity", _opacityPercent, RegistryValueKind.DWord);
                key.SetValue("Expanded", _expanded ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("SuperExpanded", _superExpanded ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Backgroundless", _backgroundless ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("StreamHidden", _streamHidden ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("Pinned", _pinned ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("HeaderModeV1", GetHeaderModeId(_headerMode), RegistryValueKind.String);
                // Values from builds that had one switch for the bar, and then
                // one for a top-edge dock that no longer exists.  The mode above
                // says everything they said; left behind they would only be
                // state nobody reads.
                key.DeleteValue("HeaderManuallyHidden", false);
                key.DeleteValue("HeaderHiddenByTopEdge", false);
                // The column rule and the pointer are deliberately left out: both
                // are recomputed from the restored geometry on the first layout
                // pass anyway, and a latch that outlives its reason is how a
                // header ends up never coming back.
                key.SetValue("HeaderAutomaticallyHidden",
                    _headerMode == HeaderVisibilityMode.Automatic &&
                    (_restoredAutomaticHeaderHidden || IsCompactHeaderHidden()) ? 1 : 0,
                    RegistryValueKind.DWord);
                key.SetValue("StorageDrive", _selectedStorageDrive ?? String.Empty, RegistryValueKind.String);
                key.SetValue("CompactSlotsV1", SerializeCompactSlotKinds(_compactSlotKinds), RegistryValueKind.String);
                key.SetValue("CardAccentsV1", SerializeCardAccents(_cardAccents), RegistryValueKind.String);
                key.SetValue("CustomAccentsV1", SerializeCardAccents(_customCardAccents), RegistryValueKind.String);
                key.SetValue("CustomSlotsV1", _customCompactPreset == null
                    ? String.Empty
                    : SerializeCompactSlotKinds(_customCompactPreset), RegistryValueKind.String);
                key.SetValue("Language", Loc.Code, RegistryValueKind.String);
                key.SetValue("LanguagePreferred", _preferredLanguage, RegistryValueKind.String);
                key.SetValue("HotkeyPinV1", _pinHotkey.Wanted.Serialize(), RegistryValueKind.String);
                key.SetValue("HotkeyHideV1", _hideHotkey.Wanted.Serialize(), RegistryValueKind.String);
                key.SetValue("HotkeyHelpV1", _helpBinding.Serialize(), RegistryValueKind.String);
                key.SetValue("HotkeyDismissV1", _dismissBinding.Serialize(), RegistryValueKind.String);
                key.SetValue("GraphLeftV1", GetCompactCardId(_leftGraphSource), RegistryValueKind.String);
                key.SetValue("GraphRightV1", GetCompactCardId(_rightGraphSource), RegistryValueKind.String);
                key.SetValue("CustomGraphsV1", SerializeGraphPair(_customGraphPreset), RegistryValueKind.String);
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
            SyncPinnedMouseHook();
            _pointerWatch.Stop();
            _pointerWatch.Dispose();
            ReleaseHotkeys();
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

    /// <summary>
    /// A header glyph that paints itself rather than letting the button renderer
    /// do it.  The renderer goes through GDI, which writes no transparency, so a
    /// rendered button is a hole on a per-pixel-alpha window; this draws the same
    /// flat square and the same centred glyph with GDI+ instead.
    /// </summary>
    /// <summary>
    /// A button that can be pressed by something other than the pointer that
    /// is on top of it.  The catcher window sits over the widget without a
    /// background and has to hand presses back to the button underneath, and
    /// <see cref="Button.PerformClick"/> refuses to do it: it tests CanSelect
    /// first, and these buttons are not selectable on purpose.
    /// </summary>
    internal interface IRelayClick
    {
        void RelayClick();
    }

    internal sealed class HeaderButton : Button, IRelayClick
    {
        public HeaderButton()
        {
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = BackColor;
            if (background.A > 0)
                using (Brush fill = new SolidBrush(background))
                    e.Graphics.FillRectangle(fill, ClientRectangle);

            string text = Text;
            if (text.Length == 0)
                return;
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            Color colour = Enabled ? ForeColor : Color.FromArgb(96, ForeColor);
            using (StringFormat format = new StringFormat())
            using (Brush brush = new SolidBrush(colour))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                format.Trimming = StringTrimming.None;
                e.Graphics.DrawString(text, Font, brush, ClientRectangle, format);
            }
        }

        public void RelayClick()
        {
            OnClick(EventArgs.Empty);
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

    /// <summary>
    /// A line of text that paints itself with GDI+ instead of GDI.
    ///
    /// A TextReadout draws through the system text renderer, which writes colour but
    /// no transparency.  On a window whose transparency is decided per pixel
    /// that is the difference between a reading and an empty rectangle, so every
    /// piece of text the widget shows goes through this: the same handful of
    /// properties the labels were given, and one DrawString behind them.
    ///
    /// The rendering hint is grey coverage rather than ClearType for the same
    /// reason - ClearType spends all four channels on colour and leaves nothing
    /// to say how much of the pixel the glyph actually covers.
    /// </summary>
    internal sealed class TextReadout : Control
    {
        private ContentAlignment _textAlign = ContentAlignment.MiddleLeft;
        private bool _autoEllipsis;

        public TextReadout()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Color.Transparent;
        }

        /// <summary>
        /// What the last layout decided for this readout, so the next one can
        /// ask for a few pixels more before deciding the opposite.  A threshold
        /// crossed at exactly one width flips back and forth while the window is
        /// dragged and while a row of cards divides a couple of pixels between
        /// them - the reading jumps to a second line and back, which looks like
        /// the widget twitching rather than like a layout.
        /// </summary>
        public bool ValuesWrapped { get; set; }

        public bool PrimaryOnly { get; set; }

        public ContentAlignment TextAlign
        {
            get { return _textAlign; }
            set
            {
                if (_textAlign == value)
                    return;
                _textAlign = value;
                Invalidate();
            }
        }

        public bool AutoEllipsis
        {
            get { return _autoEllipsis; }
            set
            {
                if (_autoEllipsis == value)
                    return;
                _autoEllipsis = value;
                Invalidate();
            }
        }

        /// <summary>
        /// How wide a run comes out on this control, measured the way it is
        /// drawn.  TextRenderer measures GDI runs, and a GDI measurement with
        /// NoPadding is a few pixels narrower than what DrawString then puts on
        /// screen - enough that a caption declared to fit still wrapped.
        /// </summary>
        internal static int MeasureText(string text, Font font)
        {
            if (String.IsNullOrEmpty(text) || font == null)
                return 0;
            // One measuring surface for the life of the process.  A screen DC
            // per call put a device context on the critical path of every
            // layout pass, and layout runs on every frame of a resize.
            if (_measureContext == null)
            {
                _measureContext = Graphics.FromImage(new Bitmap(1, 1,
                    System.Drawing.Imaging.PixelFormat.Format32bppPArgb));
                _measureContext.TextRenderingHint = TextRenderingHint.AntiAlias;
            }
            using (StringFormat format = new StringFormat())
            {
                // An origin, not a layout box the size of the float range.  GDI+
                // turns the bounds into a rectangle of its own and reports the
                // overflow as "out of memory", which is the one thing it is not.
                format.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
                return (int)Math.Ceiling(
                    _measureContext.MeasureString(text, font, PointF.Empty, format).Width);
            }
        }

        private static Graphics _measureContext;

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Invalidate();
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            string text = Text;
            if (text.Length == 0 || Width < 1 || Height < 1)
                return;
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (StringFormat format = BuildFormat())
            using (Brush brush = new SolidBrush(ForeColor))
                e.Graphics.DrawString(text, Font, brush, ClientRectangle, format);
        }

        /// <summary>
        /// The default metrics, not the typographic ones.  Both GDI and GDI+
        /// reserve about a sixth of an em on each side of a run by default, and
        /// the labels this replaces were positioned against that padding; the
        /// typographic format drops it and slides every reading a few pixels
        /// left, more the larger the type.
        /// </summary>
        private StringFormat BuildFormat()
        {
            StringFormat format = new StringFormat();
            format.FormatFlags |= StringFormatFlags.NoClip;
            switch (_textAlign)
            {
                case ContentAlignment.TopCenter:
                case ContentAlignment.MiddleCenter:
                case ContentAlignment.BottomCenter:
                    format.Alignment = StringAlignment.Center;
                    break;
                case ContentAlignment.TopRight:
                case ContentAlignment.MiddleRight:
                case ContentAlignment.BottomRight:
                    format.Alignment = StringAlignment.Far;
                    break;
                default:
                    format.Alignment = StringAlignment.Near;
                    break;
            }
            switch (_textAlign)
            {
                case ContentAlignment.TopLeft:
                case ContentAlignment.TopCenter:
                case ContentAlignment.TopRight:
                    format.LineAlignment = StringAlignment.Near;
                    break;
                case ContentAlignment.BottomLeft:
                case ContentAlignment.BottomCenter:
                case ContentAlignment.BottomRight:
                    format.LineAlignment = StringAlignment.Far;
                    break;
                default:
                    format.LineAlignment = StringAlignment.Center;
                    break;
            }
            if (_autoEllipsis)
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags |= StringFormatFlags.NoWrap;
            }
            return format;
        }
    }

    /// <summary>
    /// What the two configurable combinations currently are, for the windows
    /// that print them and cannot reach the widget to ask.  Written by the
    /// widget whenever a binding changes.
    /// </summary>
    internal static class HotkeyDisplay
    {
        public static string Pin = HotkeyBinding.DefaultPin.Format();
        public static string Hide = HotkeyBinding.DefaultHide.Format();
        public static string Help = HotkeyBinding.DefaultHelp.Format();
        public static string Dismiss = HotkeyBinding.DefaultDismiss.Format();
    }

    /// <summary>
    /// One system-wide key combination, as the user chose it.  Immutable, so a
    /// binding handed to the capture window cannot be changed underneath the
    /// hotkey that is still registered with the old one.
    /// </summary>
    internal sealed class HotkeyBinding
    {
        // MOD_* from winuser.h.  MOD_NOREPEAT is added at registration time
        // rather than stored: it is how the widget wants the key delivered, not
        // part of the combination the user picked.
        public const uint AltModifier = 0x0001;
        public const uint ControlModifier = 0x0002;
        public const uint ShiftModifier = 0x0004;
        public const uint WindowsModifier = 0x0008;

        public static readonly HotkeyBinding None = new HotkeyBinding(0, Keys.None);
        // The key left of 1: backtick on a US layout, ё on a Russian one, the
        // same virtual key on both, and one no application uses with Alt held.
        // Reachable with the left hand alone, which is the point - the right one
        // is on the mouse when this is needed.
        public static readonly HotkeyBinding DefaultPin =
            new HotkeyBinding(AltModifier, Keys.Oemtilde);
        public static readonly HotkeyBinding DefaultHide =
            new HotkeyBinding(AltModifier, Keys.H);
        // Window-level, so bare keys are free to be used: they are only taken
        // from the widget's own window and from nowhere else.
        public static readonly HotkeyBinding DefaultHelp = new HotkeyBinding(0, Keys.F1);
        public static readonly HotkeyBinding DefaultDismiss = new HotkeyBinding(0, Keys.Escape);

        private readonly uint _modifiers;
        private readonly Keys _key;

        public HotkeyBinding(uint modifiers, Keys key)
        {
            _modifiers = modifiers;
            _key = key;
        }

        public uint Modifiers { get { return _modifiers; } }

        public Keys Key { get { return _key; } }

        public bool IsEmpty { get { return _key == Keys.None; } }

        public bool HasModifier { get { return _modifiers != 0; } }

        /// <summary>
        /// Whether this key would be taken away from typing if it were claimed
        /// on its own.  A system-wide hotkey outranks every application, so a
        /// bare letter is not a shortcut, it is a letter that stops working
        /// everywhere.  Keys that type nothing are free to be used alone.
        /// </summary>
        public bool NeedsModifier
        {
            get
            {
                if (_key >= Keys.F1 && _key <= Keys.F24)
                    return false;
                switch (_key)
                {
                    case Keys.Pause:
                    case Keys.Scroll:
                    case Keys.Insert:
                    case Keys.MediaPlayPause:
                    case Keys.MediaStop:
                    case Keys.MediaNextTrack:
                    case Keys.MediaPreviousTrack:
                        return false;
                }
                return true;
            }
        }

        public static bool Same(HotkeyBinding left, HotkeyBinding right)
        {
            if (left == null || right == null)
                return left == right;
            return left._modifiers == right._modifiers && left._key == right._key;
        }

        /// <summary>
        /// Turns what a key press carried into a binding, or nothing at all when
        /// only modifiers are down - holding Alt while reaching for the second
        /// key is not a choice of Alt.
        /// </summary>
        public static HotkeyBinding FromKeyData(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey ||
                key == Keys.Menu || key == Keys.LWin || key == Keys.RWin ||
                key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.LMenu || key == Keys.RMenu)
                return None;
            uint modifiers = 0;
            if ((keyData & Keys.Alt) == Keys.Alt)
                modifiers |= AltModifier;
            if ((keyData & Keys.Control) == Keys.Control)
                modifiers |= ControlModifier;
            if ((keyData & Keys.Shift) == Keys.Shift)
                modifiers |= ShiftModifier;
            return new HotkeyBinding(modifiers, key);
        }

        public string Format()
        {
            if (IsEmpty)
                return String.Empty;
            StringBuilder text = new StringBuilder();
            if ((_modifiers & ControlModifier) != 0)
                text.Append("Ctrl+");
            if ((_modifiers & AltModifier) != 0)
                text.Append("Alt+");
            if ((_modifiers & ShiftModifier) != 0)
                text.Append("Shift+");
            if ((_modifiers & WindowsModifier) != 0)
                text.Append("Win+");
            text.Append(KeyName(_key));
            return text.ToString();
        }

        public string Serialize()
        {
            if (IsEmpty)
                return "none";
            return _modifiers.ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)_key).ToString(CultureInfo.InvariantCulture);
        }

        public static HotkeyBinding Parse(string text, HotkeyBinding fallback)
        {
            if (String.IsNullOrEmpty(text))
                return fallback;
            if (String.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                return None;
            string[] parts = text.Split(':');
            uint modifiers;
            int key;
            if (parts.Length != 2 ||
                !UInt32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out modifiers) ||
                !Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out key) ||
                !Enum.IsDefined(typeof(Keys), key))
                return fallback;
            return new HotkeyBinding(modifiers, (Keys)key);
        }

        /// <summary>
        /// The name of the key as it is printed on it, not as the enumeration
        /// spells it: "Oemtilde" and "D4" are not what anyone is looking at.
        /// </summary>
        private static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9)
                return ((char)('0' + (key - Keys.D0))).ToString(CultureInfo.InvariantCulture);
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
                return "Num " + (key - Keys.NumPad0).ToString(CultureInfo.InvariantCulture);
            switch (key)
            {
                case Keys.Oemtilde: return Loc.T("key.tilde");
                case Keys.Space: return Loc.T("key.space");
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemPipe: return "\\";
                case Keys.OemBackslash: return "\\";
                case Keys.Scroll: return "Scroll Lock";
                case Keys.PageUp: return "Page Up";
                case Keys.PageDown: return "Page Down";
                case Keys.Back: return "Backspace";
                case Keys.Return: return "Enter";
            }
            return key.ToString();
        }
    }

    /// <summary>
    /// Asks for a combination by listening for one.  A hotkey is described by
    /// pressing it, not by picking modifiers out of a list of tick boxes, and
    /// the window says on the spot whether what was pressed can actually be
    /// taken: a combination another application already owns is refused here
    /// rather than silently doing nothing later.
    /// </summary>
    internal sealed class HotkeyCaptureForm : Form
    {
        private const int ProbeId = 0x5480;
        private const int LowLevelKeyboardHook = 13;

        private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, KeyboardHookProc callback,
            IntPtr module, uint thread);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private readonly Label _combination = new Label();
        private readonly Label _warning = new Label();
        private readonly Button _save = new Button();
        private readonly Button _clear = new Button();
        private readonly Button _cancel = new Button();
        // Held in a field for as long as the hook is installed: the delegate is
        // the only managed reference the system has, and a collected one is a
        // callback into freed memory.
        private readonly KeyboardHookProc _hookCallback;
        private readonly bool _global;
        private IntPtr _hook;
        private HotkeyBinding _binding;

        public HotkeyCaptureForm(string action, string scope, HotkeyBinding current, bool global)
        {
            _binding = current ?? HotkeyBinding.None;
            _global = global;
            _hookCallback = OnKey;
            Text = Loc.T("hotkey.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BackColor = Color.FromArgb(24, 24, 28);
            ForeColor = Color.FromArgb(228, 232, 240);
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(432, 254);

            Label title = new Label();
            title.Text = action;
            title.ForeColor = Color.FromArgb(150, 160, 180);
            title.SetBounds(20, 18, 392, 20);

            Label prompt = new Label();
            prompt.Text = Loc.T("hotkey.prompt") + " — " + scope;
            prompt.SetBounds(20, 40, 392, 20);

            _combination.TextAlign = ContentAlignment.MiddleCenter;
            _combination.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            _combination.SetBounds(20, 66, 392, 40);

            _warning.ForeColor = Color.FromArgb(226, 132, 96);
            _warning.SetBounds(20, 110, 392, 36);

            Label hint = new Label();
            hint.Text = Loc.T("hotkey.hint");
            hint.ForeColor = Color.FromArgb(120, 128, 145);
            // Two lines of it, so the sentence that explains Esc and Backspace
            // is not cut off half way through.
            hint.SetBounds(20, 150, 392, 44);

            _save.Text = Loc.T("hotkey.apply");
            _save.SetBounds(252, 208, 76, 28);
            _save.Click += delegate { Accept(); };
            _clear.Text = Loc.T("hotkey.clear");
            _clear.SetBounds(20, 208, 86, 28);
            _clear.Click += delegate { Show(HotkeyBinding.None); };
            _cancel.Text = Loc.T("hotkey.cancel");
            _cancel.SetBounds(336, 208, 76, 28);
            _cancel.DialogResult = DialogResult.Cancel;
            foreach (Button button in new[] { _save, _clear, _cancel })
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(64, 68, 78);
                button.BackColor = Color.FromArgb(36, 38, 44);
                // Buttons that keep the focus swallow the very keys this window
                // exists to hear: a press of Space or Enter would be a click.
                button.TabStop = false;
            }

            Controls.Add(title);
            Controls.Add(prompt);
            Controls.Add(_combination);
            Controls.Add(_warning);
            Controls.Add(hint);
            Controls.Add(_save);
            Controls.Add(_clear);
            Controls.Add(_cancel);
            CancelButton = _cancel;
            Show(_binding);
        }

        public HotkeyBinding Binding { get { return _binding; } }

        /// <summary>
        /// A low-level hook rather than the window's own key handling, because
        /// the window's own key handling never sees half of what can be bound.
        /// A combination another application registered system-wide is taken by
        /// the system before any window is offered it, and Alt+Tab, Win+D and
        /// the rest never reach a message queue at all.  The hook runs ahead of
        /// all of that, and swallowing the press keeps it from doing whatever it
        /// normally does while the user is only naming it.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _hook = SetWindowsHookEx(LowLevelKeyboardHook, _hookCallback,
                GetModuleHandle(null), 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            base.OnFormClosed(e);
        }

        private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            const int KeyDown = 0x0100;
            const int SystemKeyDown = 0x0104;
            const int KeyUp = 0x0101;
            const int SystemKeyUp = 0x0105;
            if (code < 0)
                return CallNextHookEx(_hook, code, wParam, lParam);
            int notification = wParam.ToInt32();
            bool down = notification == KeyDown || notification == SystemKeyDown;
            if (!down && notification != KeyUp && notification != SystemKeyUp)
                return CallNextHookEx(_hook, code, wParam, lParam);
            // vkCode is the first field of KBDLLHOOKSTRUCT.
            Keys key = (Keys)Marshal.ReadInt32(lParam);
            if (IsModifier(key))
                return CallNextHookEx(_hook, code, wParam, lParam);
            if (down)
                Pressed(key);
            // The release is eaten as well: a key whose press never arrived and
            // whose release did leaves applications thinking it is still held.
            return (IntPtr)1;
        }

        private void Pressed(Keys key)
        {
            HotkeyBinding pressed = new HotkeyBinding(HeldModifiers(), key);
            // Esc, Enter and Backspace on their own drive this window - which is
            // why the hint says so.  With a modifier they are ordinary keys and
            // can be bound like any other.
            if (!pressed.HasModifier)
            {
                if (key == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }
                if (key == Keys.Back)
                {
                    Show(HotkeyBinding.None);
                    return;
                }
                if (key == Keys.Return)
                {
                    if (_save.Enabled)
                        Accept();
                    return;
                }
            }
            Show(pressed);
        }

        /// <summary>
        /// Asked of the keyboard rather than of the message queue: the press
        /// that is being blocked never reaches the queue, so the modifiers held
        /// with it are not in the state a window would see.
        /// </summary>
        private static uint HeldModifiers()
        {
            const int Shift = 0x10;
            const int Control = 0x11;
            const int Alt = 0x12;
            const int LeftWindows = 0x5B;
            const int RightWindows = 0x5C;
            uint modifiers = 0;
            if (IsDown(Alt))
                modifiers |= HotkeyBinding.AltModifier;
            if (IsDown(Control))
                modifiers |= HotkeyBinding.ControlModifier;
            if (IsDown(Shift))
                modifiers |= HotkeyBinding.ShiftModifier;
            if (IsDown(LeftWindows) || IsDown(RightWindows))
                modifiers |= HotkeyBinding.WindowsModifier;
            return modifiers;
        }

        private static bool IsDown(int key)
        {
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        private static bool IsModifier(Keys key)
        {
            switch (key)
            {
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey:
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey:
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu:
                case Keys.LWin:
                case Keys.RWin:
                    return true;
            }
            return false;
        }

        private void Show(HotkeyBinding binding)
        {
            _binding = binding;
            _combination.Text = binding.IsEmpty ? Loc.T("hotkey.none") : binding.Format();
            // Only a system-wide key can be taken away from typing everywhere.
            // One that works while the widget has focus is the widget's own
            // business, so Esc and F1 stay bindable as they are.
            bool bare = _global && !binding.IsEmpty &&
                binding.NeedsModifier && !binding.HasModifier;
            _warning.Text = bare ? Loc.T("hotkey.needModifier") : String.Empty;
            _save.Enabled = !bare;
        }

        /// <summary>
        /// The only honest test of whether a combination is free is to ask the
        /// system for it.  Taken straight back: the widget registers it for
        /// itself the moment this window closes.
        /// </summary>
        private void Accept()
        {
            if (_global && !_binding.IsEmpty)
            {
                const uint NoRepeat = 0x4000;
                bool free = NativeUi.RegisterHotKey(Handle, ProbeId,
                    _binding.Modifiers | NoRepeat, (uint)_binding.Key);
                if (!free)
                {
                    _warning.Text = Loc.T("hotkey.taken");
                    return;
                }
                NativeUi.UnregisterHotKey(Handle, ProbeId);
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>
    /// The standard colour picker, with the widget repainting behind it while
    /// the crosshair moves.  Windows sends no "colour changed" notification to
    /// subscribe to, so the dialog's own red, green and blue boxes are read on
    /// a timer: they follow the crosshair and the luminance bar as they are
    /// dragged, which is exactly the value that has to be shown.
    ///
    /// Reading the boxes rather than driving the dialog means nothing here can
    /// disagree with what the user sees in it.
    /// </summary>
    internal sealed class LiveColorDialog : ColorDialog
    {
        private const int InitDialog = 0x0110;
        // dlgs.h: the three edit boxes of the full colour dialog.
        private const int RedBox = 706;
        private const int GreenBox = 707;
        private const int BlueBox = 708;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDlgItemInt(IntPtr dialog, int item,
            out bool translated, bool signed);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        private readonly Action<Color> _preview;
        private readonly System.Windows.Forms.Timer _watch = new System.Windows.Forms.Timer();
        private IntPtr _dialog;
        private int _lastSeen = -1;

        public LiveColorDialog(Action<Color> preview)
        {
            _preview = preview;
            _watch.Interval = 60;
            _watch.Tick += delegate { Sample(); };
        }

        protected override IntPtr HookProc(IntPtr window, int message, IntPtr wparam, IntPtr lparam)
        {
            if (message == InitDialog)
            {
                _dialog = window;
                _watch.Start();
            }
            return base.HookProc(window, message, wparam, lparam);
        }

        private void Sample()
        {
            if (_dialog == IntPtr.Zero || !IsWindow(_dialog))
            {
                _watch.Stop();
                return;
            }
            int red, green, blue;
            if (!TryRead(RedBox, out red) || !TryRead(GreenBox, out green) ||
                !TryRead(BlueBox, out blue))
                return;
            int packed = (red << 16) | (green << 8) | blue;
            if (packed == _lastSeen)
                return;
            _lastSeen = packed;
            if (_preview != null)
                _preview(Color.FromArgb(red, green, blue));
        }

        private bool TryRead(int box, out int value)
        {
            bool translated;
            // A box being retyped is empty for a keystroke or two.  That is not
            // a colour, and previewing it would flash black under the hand.
            uint raw = GetDlgItemInt(_dialog, box, out translated, false);
            value = (int)Math.Min(255U, raw);
            return translated;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watch.Stop();
                _watch.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class BackgroundHitForm : Form
    {
        /// <summary>
        /// Resolves the cursor for a screen point.  Colour-keyed pixels of the
        /// monitor window are click-through, so this form - not the resize grips -
        /// is what the pointer actually reaches over a transparent corner, and it
        /// has to provide the sizing cursor itself.
        /// </summary>
        internal Func<Point, Cursor> CursorResolver;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Func<Point, Cursor> resolver = CursorResolver;
            if (resolver == null)
                return;
            Cursor resolved = resolver(Cursor.Position) ?? Cursors.Default;
            if (Cursor != resolved)
                Cursor = resolved;
        }

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

        /// <summary>
        /// The catcher answers the activation probe itself.  Left to the
        /// framework it becomes the foreground window on the press that it is
        /// only supposed to relay, and the widget behind it then spends the
        /// next click getting the focus back instead of pressing a button.
        /// </summary>
        protected override void WndProc(ref Message message)
        {
            const int MouseActivate = 0x0021;
            if (message.Msg == MouseActivate)
            {
                const int NoActivate = 3;
                StartupTrace.Write("catcher mouse-activate");
                message.Result = (IntPtr)NoActivate;
                return;
            }
            if (message.Msg == 0x0201)
                StartupTrace.Write("catcher lbuttondown");
            base.WndProc(ref message);
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
            Text = Loc.T("menu.opacity");
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
        private string _detail = Loc.T("state.waiting");
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

    /// <summary>
    /// Describes one plotted series: what it is called, how it is formatted and
    /// how its axis is built.  Two of these make up a history panel.
    /// </summary>
    internal sealed class HistorySeriesInfo
    {
        public bool Present = true;
        public string Label = String.Empty;
        public string ShortLabel = String.Empty;
        public string Suffix = String.Empty;
        public string Format = "0";
        public Color Color = Color.FromArgb(150, 158, 169);
        public bool FixedScale;
        public float Minimum;
        public float Maximum = 100;
        public float Step = 5;
        public float CeilingLimit = 1000000;
    }

    /// <summary>
    /// A two-series rolling history.  The panel is not bound to CPU or GPU: the
    /// kind it shows is chosen from the menu, and both its colours follow the
    /// accent configured for that card.
    /// </summary>
    internal sealed class SensorHistoryControl : Control
    {
        private const int MaximumSamples = 300;
        private static readonly Color TemperatureSeriesColor = Color.FromArgb(255, 183, 77);

        private CompactCardKind _kind;
        private string _title = String.Empty;
        private Color _accent = Color.FromArgb(150, 158, 169);
        private readonly List<float> _primary = new List<float>();
        private readonly List<float> _secondary = new List<float>();
        private bool _backgroundless;

        public bool Backgroundless
        {
            get { return _backgroundless; }
            set
            {
                if (_backgroundless == value)
                    return;
                _backgroundless = value;
                Invalidate();
            }
        }

        public SensorHistoryControl(CompactCardKind kind, string title, Color accent)
        {
            _kind = kind;
            _title = title ?? String.Empty;
            _accent = accent;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        public CompactCardKind Kind
        {
            get { return _kind; }
        }

        /// <summary>
        /// Points the panel at a different card, or refreshes its accent after a
        /// colour change.  Switching the source drops the collected history,
        /// because the samples belong to the previous sensor.
        /// </summary>
        public void Configure(CompactCardKind kind, string title, Color accent)
        {
            bool sourceChanged = _kind != kind;
            bool changed = sourceChanged || _title != title || _accent.ToArgb() != accent.ToArgb();
            _kind = kind;
            _title = title ?? String.Empty;
            _accent = accent;
            if (sourceChanged)
            {
                _primary.Clear();
                _secondary.Clear();
            }
            if (changed)
                Invalidate();
        }

        public void AddSample(double primary, double secondary)
        {
            if (_primary.Count >= MaximumSamples)
            {
                _primary.RemoveAt(0);
                _secondary.RemoveAt(0);
            }

            _primary.Add(ToSample(primary));
            _secondary.Add(ToSample(secondary));
            Invalidate();
        }

        private static float ToSample(double value)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value)
                ? Single.NaN
                : (float)value;
        }

        private HistorySeriesInfo GetPrimaryInfo()
        {
            HistorySeriesInfo info = new HistorySeriesInfo();
            switch (_kind)
            {
                case CompactCardKind.Cpu:
                case CompactCardKind.Gpu:
                    info.Label = Loc.T("history.tempAxis");
                    info.ShortLabel = Loc.T("history.tempShortAxis");
                    info.Suffix = "°";
                    info.Color = TemperatureSeriesColor;
                    info.Step = 5;
                    info.CeilingLimit = 120;
                    break;
                case CompactCardKind.Memory:
                case CompactCardKind.Storage:
                    info.Label = Loc.T("history.usedAxis");
                    info.ShortLabel = Loc.T("history.usedAxis");
                    info.Suffix = "%";
                    info.Color = _accent;
                    info.FixedScale = true;
                    info.Minimum = 0;
                    info.Maximum = 100;
                    break;
                case CompactCardKind.Network:
                    info.Label = Loc.T("history.speedAxis");
                    info.ShortLabel = Loc.T("history.speedShortAxis");
                    info.Format = "0.0";
                    info.Color = _accent;
                    info.Step = 1;
                    break;
                case CompactCardKind.Fans:
                    info.Label = Loc.T("history.rpmAxis");
                    info.ShortLabel = Loc.T("history.rpmShortAxis");
                    info.Color = _accent;
                    info.Step = 100;
                    info.CeilingLimit = 20000;
                    break;
                case CompactCardKind.Fps:
                    info.Label = Loc.T("history.fpsAxis");
                    info.ShortLabel = Loc.T("history.fpsShortAxis");
                    info.Color = _accent;
                    info.Step = 10;
                    info.CeilingLimit = 2000;
                    break;
                default:
                    info.Present = false;
                    break;
            }
            return info;
        }

        private HistorySeriesInfo GetSecondaryInfo()
        {
            HistorySeriesInfo info = new HistorySeriesInfo();
            switch (_kind)
            {
                case CompactCardKind.Cpu:
                case CompactCardKind.Gpu:
                    info.Label = Loc.T("history.loadAxis");
                    info.ShortLabel = Loc.T("history.loadShortAxis");
                    info.Suffix = "%";
                    info.Color = _accent;
                    info.FixedScale = true;
                    info.Minimum = 0;
                    info.Maximum = 100;
                    break;
                case CompactCardKind.Memory:
                    info.Label = Loc.T("caption.usedLong") + ", GB";
                    info.ShortLabel = "GB";
                    info.Format = "0.0";
                    info.Color = Dim(_accent);
                    info.Step = 1;
                    break;
                case CompactCardKind.Network:
                    info.Label = Loc.T("caption.upload") + ", MB/s";
                    info.ShortLabel = Loc.T("caption.upload");
                    info.Format = "0.0";
                    info.Color = Dim(_accent);
                    info.Step = 1;
                    break;
                case CompactCardKind.Fans:
                    info.Label = Loc.T("caption.load") + ", %";
                    info.ShortLabel = "%";
                    info.Suffix = "%";
                    info.Color = Dim(_accent);
                    info.FixedScale = true;
                    info.Minimum = 0;
                    info.Maximum = 100;
                    break;
                case CompactCardKind.Fps:
                    info.Label = Loc.T("caption.frameTime") + ", ms";
                    info.ShortLabel = "ms";
                    info.Format = "0.0";
                    info.Color = Dim(_accent);
                    info.Step = 2;
                    info.CeilingLimit = 500;
                    break;
                default:
                    info.Present = false;
                    break;
            }
            return info;
        }

        // A second series drawn in the accent itself would read as one thick
        // line; halving the distance to the background keeps it related but
        // clearly subordinate.
        private static Color Dim(Color color)
        {
            return Color.FromArgb(
                (int)Math.Round(color.R * 0.62 + 22),
                (int)Math.Round(color.G * 0.62 + 24),
                (int)Math.Round(color.B * 0.62 + 28));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(49, 55, 65)))
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            HistorySeriesInfo primaryInfo = GetPrimaryInfo();
            HistorySeriesInfo secondaryInfo = GetSecondaryInfo();
            bool stacked = primaryInfo.Present && secondaryInfo.Present;

            bool fullHeader = Width >= 300 && Height >= 115;
            int headerHeight = fullHeader ? 48 : 24;
            RectangleF plotArea = new RectangleF(10, headerHeight,
                Math.Max(1, Width - 44), Math.Max(1, Height - headerHeight - 8));

            HistoryStatistics primaryStats = Summarise(_primary);
            HistoryStatistics secondaryStats = Summarise(_secondary);

            DrawHeader(e.Graphics, fullHeader, primaryInfo, secondaryInfo,
                primaryStats, secondaryStats);

            float chartGap = fullHeader ? 16F : 10F;
            float chartHeight = stacked
                ? Math.Max(1, (plotArea.Height - chartGap) / 2F)
                : Math.Max(1, plotArea.Height);
            RectangleF primaryGraph = new RectangleF(plotArea.Left, plotArea.Top,
                plotArea.Width, chartHeight);
            RectangleF secondaryGraph = new RectangleF(plotArea.Left,
                plotArea.Top + chartHeight + chartGap, plotArea.Width, chartHeight);

            float primaryLow, primaryHigh;
            ResolveScale(primaryInfo, primaryStats, out primaryLow, out primaryHigh);
            float secondaryLow, secondaryHigh;
            ResolveScale(secondaryInfo, secondaryStats, out secondaryLow, out secondaryHigh);

            if (primaryInfo.Present)
                DrawScale(e.Graphics, primaryGraph, primaryLow, primaryHigh, primaryInfo.Suffix);
            if (stacked)
                DrawScale(e.Graphics, secondaryGraph, secondaryLow, secondaryHigh, secondaryInfo.Suffix);

            if (_primary.Count <= 1)
            {
                using (Font emptyFont = new Font("Segoe UI", 7F, FontStyle.Regular, GraphicsUnit.Point))
                using (Brush emptyBrush = new SolidBrush(Color.FromArgb(95, 105, 118)))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    e.Graphics.DrawString(Loc.T("history.collecting"), emptyFont, emptyBrush, plotArea, centered);
            }
            else
            {
                if (primaryInfo.Present)
                    DrawSeries(e.Graphics, primaryGraph, _primary, primaryInfo.Color,
                        primaryLow, primaryHigh);
                if (stacked)
                    DrawSeries(e.Graphics, secondaryGraph, _secondary, secondaryInfo.Color,
                        secondaryLow, secondaryHigh);
            }

            if (primaryInfo.Present)
                DrawGraphLabel(e.Graphics, primaryGraph,
                    fullHeader ? primaryInfo.Label : primaryInfo.ShortLabel, primaryInfo.Color,
                    _backgroundless);
            if (stacked)
                DrawGraphLabel(e.Graphics, secondaryGraph,
                    fullHeader ? secondaryInfo.Label : secondaryInfo.ShortLabel, secondaryInfo.Color,
                    _backgroundless);
        }

        /// <summary>
        /// One title row, then one full-width row per series.  The old layout
        /// squeezed both summaries into half a line each at 6.7 pt, which was the
        /// smallest and least readable text in the whole window.
        /// </summary>
        private void DrawHeader(Graphics graphics, bool fullHeader,
            HistorySeriesInfo primaryInfo, HistorySeriesInfo secondaryInfo,
            HistoryStatistics primaryStats, HistoryStatistics secondaryStats)
        {
            string compactSummary = primaryStats.Valid
                ? Format(primaryStats.Minimum, primaryInfo) + " · " +
                  Format(primaryStats.Average, primaryInfo) + " · " +
                  Format(primaryStats.Maximum, primaryInfo)
                : "— · — · —";

            using (Font titleFont = new Font("Segoe UI", 7.6F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font headingFont = new Font("Segoe UI", 6.6F, FontStyle.Regular, GraphicsUnit.Point))
            using (Font labelFont = new Font("Segoe UI", 6.9F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font valueFont = new Font("Segoe UI", 9.4F, FontStyle.Bold, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(130, 140, 153)))
            using (Brush headingBrush = new SolidBrush(Color.FromArgb(112, 122, 136)))
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
                graphics.DrawString(fullHeader
                        ? Loc.T("history.titlePrefix") + _title
                        : _title + " · " + compactSummary,
                    titleFont, titleBrush, new RectangleF(10, 2, Math.Max(1, Width - 20), 16), near);
                if (!fullHeader)
                    return;

                graphics.DrawString(Loc.T("history.minAvgMax"), headingFont, headingBrush,
                    new RectangleF(110, 2, Math.Max(1, Width - 120), 16), far);

                int rows = (primaryInfo.Present ? 1 : 0) + (secondaryInfo.Present ? 1 : 0);
                if (rows == 0)
                    return;
                // One row is centred in the same block two rows would occupy, so
                // a single-series panel does not leave a gap under the title.
                float blockTop = 19;
                float rowHeight = rows == 1 ? 26 : 13.5F;
                int index = 0;
                if (primaryInfo.Present)
                {
                    DrawStatisticsRow(graphics, labelFont, valueFont,
                        blockTop + index * rowHeight, rowHeight,
                        primaryInfo, primaryStats, near);
                    index++;
                }
                if (secondaryInfo.Present)
                    DrawStatisticsRow(graphics, labelFont, valueFont,
                        blockTop + index * rowHeight, rowHeight,
                        secondaryInfo, secondaryStats, near);
            }
        }

        private void DrawStatisticsRow(Graphics graphics, Font labelFont, Font valueFont,
            float top, float rowHeight, HistorySeriesInfo info, HistoryStatistics stats,
            StringFormat near)
        {
            const float labelWidth = 58;
            RectangleF labelBounds = new RectangleF(10, top, labelWidth, rowHeight);
            using (Brush labelBrush = new SolidBrush(info.Color))
                graphics.DrawString(info.ShortLabel, labelFont, labelBrush, labelBounds, near);

            string text = stats.Valid
                ? Format(stats.Minimum, info) + "  ·  " + Format(stats.Average, info) +
                  "  ·  " + Format(stats.Maximum, info)
                : "—  ·  —  ·  —";
            RectangleF valueBounds = new RectangleF(10 + labelWidth, top,
                Math.Max(1, Width - 20 - labelWidth), rowHeight);
            using (Brush valueBrush = new SolidBrush(Color.FromArgb(214, 221, 231)))
                graphics.DrawString(text, valueFont, valueBrush, valueBounds, near);
        }

        private static string Format(float value, HistorySeriesInfo info)
        {
            return value.ToString(info.Format, CultureInfo.InvariantCulture) + info.Suffix;
        }

        private static void DrawGraphLabel(Graphics graphics, RectangleF bounds,
            string text, Color color, bool backgroundless)
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
                // The chip exists to lift the name off the graph behind it.  With
                // the panel gone there is no graph behind it, only the desktop,
                // and the chip is just a dark box sitting on someone's wallpaper.
                if (!backgroundless)
                    graphics.FillRectangle(backdrop, labelBounds);
                graphics.FillEllipse(textBrush, labelBounds.Left + 3,
                    labelBounds.Top + labelBounds.Height / 2F - 2.5F, 5, 5);
                graphics.DrawString(text, font, textBrush,
                    labelBounds.Left + 11, labelBounds.Top + 1);
            }
        }

        private static HistoryStatistics Summarise(IList<float> values)
        {
            HistoryStatistics result = new HistoryStatistics();
            float minimum = Single.MaxValue;
            float maximum = Single.MinValue;
            float sum = 0;
            int count = 0;
            for (int index = 0; index < values.Count; index++)
            {
                float value = values[index];
                if (Single.IsNaN(value))
                    continue;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                sum += value;
                count++;
            }
            if (count == 0)
                return result;
            result.Valid = true;
            result.Minimum = minimum;
            result.Maximum = maximum;
            result.Average = sum / count;
            return result;
        }

        private static void ResolveScale(HistorySeriesInfo info, HistoryStatistics stats,
            out float scaleMinimum, out float scaleMaximum)
        {
            if (info.FixedScale || !stats.Valid)
            {
                scaleMinimum = info.Minimum;
                scaleMaximum = info.Maximum;
                return;
            }

            float step = Math.Max(0.1F, info.Step);
            // Autoscale to the samples, but only in whole steps: a scale that
            // slides with every reading makes a flat line look like noise.
            scaleMinimum = (float)Math.Floor((stats.Minimum - step) / step) * step;
            scaleMaximum = (float)Math.Ceiling((stats.Maximum + step) / step) * step;
            // Every quantity a card exposes is non-negative, so the axis never
            // needs to dip below zero just because the rounding stepped past it.
            scaleMinimum = Math.Max(0, scaleMinimum);
            scaleMaximum = Math.Min(info.CeilingLimit, scaleMaximum);
            if (scaleMaximum - scaleMinimum < step)
                scaleMaximum = scaleMinimum + step;
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
                    graphics.DrawString(value.ToString(Math.Abs(value) < 10 && value != Math.Floor(value) ? "0.0" : "0",
                            CultureInfo.InvariantCulture) + suffix,
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
                    float range = Math.Max(0.001F, scaleMaximum - scaleMinimum);
                    PointF from = new PointF(bounds.Left + (index - 1) * step,
                        bounds.Bottom - bounds.Height * (previous - scaleMinimum) / range);
                    PointF to = new PointF(bounds.Left + index * step,
                        bounds.Bottom - bounds.Height * (current - scaleMinimum) / range);
                    graphics.DrawLine(pen, from, to);
                }
            }
        }
    }

    internal struct HistoryStatistics
    {
        public bool Valid;
        public float Minimum;
        public float Average;
        public float Maximum;
    }

    internal sealed class ResourceSummaryControl : Control
    {
        private string _title;
        private readonly bool _networkMode;
        private double _progress;
        private string _primary = "—";
        private string _secondary = Loc.T("state.waiting");
        private string _download = "—";
        private string _upload = "—";
        private Color _accent = Color.FromArgb(73, 190, 198);
        private Rectangle _titleCaret = Rectangle.Empty;
        private bool _backgroundless;

        /// <summary>
        /// Without a panel the unfilled part of the bar has nothing to sit on,
        /// so the track thins out to a hint and the outline carries the shape.
        /// </summary>
        public bool Backgroundless
        {
            get { return _backgroundless; }
            set
            {
                if (_backgroundless == value)
                    return;
                _backgroundless = value;
                Invalidate();
            }
        }

        /// <summary>
        /// Where the "▾" of the title was last painted, in control coordinates.
        /// Empty until the control has drawn a title that carries one.
        /// </summary>
        public Rectangle TitleCaretBounds
        {
            get { return _titleCaret; }
        }

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
                RememberTitleCaret(e.Graphics, titleFont, 10, 7, Width - 20);
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
                using (Brush barBackground = new SolidBrush(_backgroundless
                    ? Color.FromArgb(70, 32, 37, 45)
                    : Color.FromArgb(32, 37, 45)))
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

        /// <summary>
        /// Records the box the trailing "▾" occupies so the drive list can be
        /// hung off it.  Measured with the font and origin the title is about to
        /// be drawn with, which is the only way the two stay in step across the
        /// three layouts this control paints.
        /// </summary>
        private void RememberTitleCaret(Graphics graphics, Font font, float x, float y, float limit)
        {
            int caretIndex = String.IsNullOrEmpty(_title) ? -1 : _title.LastIndexOf('▾');
            if (caretIndex <= 0)
            {
                _titleCaret = Rectangle.Empty;
                return;
            }

            SizeF full = graphics.MeasureString(_title, font);
            SizeF before = graphics.MeasureString(_title.Substring(0, caretIndex), font);
            float left = Math.Min(before.Width, Math.Max(0, limit - 12));
            _titleCaret = new Rectangle(
                (int)Math.Round(x + left), (int)Math.Round(y),
                Math.Max(8, (int)Math.Round(full.Width - before.Width)),
                Math.Max(8, (int)Math.Round(full.Height)));
        }

        private void DrawCompactUsage(Graphics graphics)
        {
            int barHeight = Math.Max(3, Math.Min(5, Height / 10));
            Rectangle bar = new Rectangle(10, Math.Max(1, Height - barHeight - 5),
                Math.Max(1, Width - 20), barHeight);
            float textScale = Math.Max(0.62F, Math.Min(1F, Width / 155F));
            using (Brush barBackground = new SolidBrush(_backgroundless
                    ? Color.FromArgb(70, 32, 37, 45)
                    : Color.FromArgb(32, 37, 45)))
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
                    RememberTitleCaret(graphics, titleFont, 10, 4, 72);
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
                    // One reading, printed as large as the panel will carry it.
                    // At this width the total after the slash and the line of
                    // detail under it are both below the size anything can be
                    // read at, and they were holding the room that the number
                    // which does get read could have had.
                    bool roomForDetail = Width >= 150;
                    string reading = roomForDetail ? _primary : DropTotal(_primary);
                    bool showSecondary = roomForDetail && Height >= 49 &&
                        !String.IsNullOrWhiteSpace(_secondary);
                    float readingHeight = showSecondary
                        ? 20
                        : Math.Max(17, Height - barHeight - 21);
                    RememberTitleCaret(graphics, titleFont, 10, 2, Width - 20);
                    graphics.DrawString(_title, titleFont, titleBrush,
                        new RectangleF(10, 2, Math.Max(1, Width - 20), 15), ellipsis);
                    using (Font readingFont = new Font("Segoe UI",
                        FitReadingSize(reading, Math.Max(1, Width - 20), readingHeight,
                            valueFont.SizeInPoints),
                        FontStyle.Bold, GraphicsUnit.Point))
                        graphics.DrawString(reading, readingFont, valueBrush,
                            new RectangleF(10, 16, Math.Max(1, Width - 20), readingHeight), ellipsis);
                    if (showSecondary)
                        graphics.DrawString(_secondary, detailFont, detailBrush,
                            new RectangleF(10, 35, Math.Max(1, Width - 20),
                                Math.Max(12, Height - barHeight - 40)), ellipsis);
                }
            }
        }

        /// <summary>
        /// The reading without its total.  "40.6 / 63.8 GB" is two numbers where
        /// a narrow panel has room for one, and the one worth watching is the
        /// first; the unit comes along so the number keeps its meaning.
        /// </summary>
        private static string DropTotal(string reading)
        {
            if (String.IsNullOrEmpty(reading))
                return reading ?? String.Empty;
            int slash = reading.IndexOf('/');
            if (slash <= 0)
                return reading;
            string head = reading.Substring(0, slash).Trim();
            string tail = reading.Substring(slash + 1).Trim();
            int space = tail.LastIndexOf(' ');
            string unit = space >= 0 ? tail.Substring(space + 1) : String.Empty;
            return unit.Length > 0 ? head + " " + unit : head;
        }

        /// <summary>
        /// The largest type this reading fits at, never smaller than the size
        /// the layout asked for.
        /// </summary>
        private float FitReadingSize(string text, float width, float height, float minimum)
        {
            // The answer is remembered between frames.  This runs inside OnPaint
            // of a control that repaints with every frame the compositor builds,
            // and the search below makes a font object per step; unremembered it
            // was a thousand font handles a second for a number that changes
            // once.
            string key = (text ?? String.Empty) + "|" + width.ToString("0") + "|" +
                height.ToString("0") + "|" + minimum.ToString("0.0");
            if (key == _fitKey)
                return _fitSize;

            float size = Math.Max(minimum, Math.Min(22F, height * 0.82F));
            while (size > minimum)
            {
                using (Font probe = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point))
                {
                    Size measured = TextRenderer.MeasureText(text ?? String.Empty, probe,
                        new Size(4096, 4096),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine);
                    if (measured.Width <= width && measured.Height <= height)
                        break;
                }
                size -= 0.5F;
            }
            _fitKey = key;
            _fitSize = size;
            return size;
        }

        private string _fitKey;
        private float _fitSize;

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
                e.Graphics.DrawString(Loc.T("caption.fans"), titleFont, titleBrush, 10, 5);
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

        /// <summary>
        /// Fills the grid evenly instead of filling every row to the brim.  Four
        /// fans in a three-wide box are 2x2 and not 3+1: the widest row a layout
        /// allows is not the row count it should use, and a lone item hanging
        /// under a full row reads as a rendering fault rather than as a grid.
        /// </summary>
        private static int ColumnCount(int width, int itemCount)
        {
            int maximum = width >= 660 ? 4 : width >= 430 ? 3 : width >= 250 ? 2 : 1;
            maximum = Math.Max(1, Math.Min(itemCount, maximum));
            int rows = Math.Max(1, (int)Math.Ceiling(itemCount / (double)maximum));
            return Math.Max(1, (int)Math.Ceiling(itemCount / (double)rows));
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
        private bool _backgroundless;

        /// <summary>
        /// Without a panel the row rules have nothing to divide.  They were a
        /// shade of the card they sat on, and with the card gone they are just
        /// dark bars lying across the desktop - the kind of furniture someone
        /// switching the background off is switching off.
        /// </summary>
        public bool Backgroundless
        {
            get { return _backgroundless; }
            set
            {
                if (_backgroundless == value)
                    return;
                _backgroundless = value;
                Invalidate();
            }
        }

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
            // The caption is the card's extra information, so it waits until the
            // card is opened up enough to carry every metric it has and still
            // have room.  In a cramped column the words ate the space the digits
            // needed, which left the readings smaller than the neighbouring
            // cards for nothing: the card already says what it is.
            bool showCaptions = count == MetricCount && rowHeight >= 44F;
            using (Brush valueBrush = new SolidBrush(_accent))
            using (Brush captionBrush = new SolidBrush(Color.FromArgb(116, 126, 140)))
            using (Pen separator = new Pen(Color.FromArgb(35, 42, 51)))
            using (StringFormat valueFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
                LineAlignment = showCaptions ? StringAlignment.Near : StringAlignment.Center
            })
            {
                for (int index = 0; index < count; index++)
                {
                    float rowTop = index * rowHeight;
                    float blockHeight = Math.Min(62F, rowHeight - 2F);
                    float blockTop = rowTop + Math.Max(0, (rowHeight - blockHeight) / 2F);
                    float desiredValueSize = showCaptions
                        ? Math.Max(8.5F, Math.Min(22F, 9F + (rowHeight - 34F) * 0.19F))
                        : Math.Max(9F, Math.Min(26F, 10.5F + (rowHeight - 30F) * 0.26F));
                    float valueSize = FitSingleLineFont(_values[index], desiredValueSize, 6F, Math.Max(1, Width));
                    float captionSize = Math.Max(5.2F,
                        Math.Min(8.5F, 5.8F + (rowHeight - 34F) * 0.055F));
                    using (Font valueFont = new Font("Segoe UI", valueSize, FontStyle.Bold, GraphicsUnit.Point))
                    using (Font captionFont = new Font("Segoe UI", captionSize, FontStyle.Bold, GraphicsUnit.Point))
                    {
                        RectangleF valueBounds = new RectangleF(0, blockTop, Width,
                            Math.Max(12F, showCaptions ? blockHeight * 0.60F : blockHeight));
                        e.Graphics.DrawString(_values[index], valueFont, valueBrush, valueBounds, valueFormat);
                        if (showCaptions)
                            e.Graphics.DrawString(_captions[index], captionFont, captionBrush,
                                new RectangleF(0, blockTop + blockHeight * 0.55F, Width,
                                    Math.Max(8F, blockHeight * 0.43F)));
                    }
                    if (index < count - 1 && !_backgroundless)
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

    internal sealed class ExpandableStrip : Button, IRelayClick
    {
        private bool _expanded;
        private bool _hovered;
        private double _fade = 1;

        public void RelayClick()
        {
            OnClick(EventArgs.Empty);
        }

        public ExpandableStrip()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
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

        // 1 while the pointer is over the widget, 0 once it has left.  The pill
        // retracts along its own length instead of blending towards BackColor:
        // with the transparency key on, half-faded pixels miss the key value and
        // would flash as a dark bar over the desktop.
        public double Fade
        {
            get { return _fade; }
            set
            {
                double clamped = value < 0 ? 0 : value > 1 ? 1 : value;
                if (Math.Abs(_fade - clamped) < 0.001)
                    return;
                _fade = clamped;
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
            if (_fade <= 0.004)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int fullWidth = Math.Max(56, Math.Min(180, Width / 3));
            int pillHeight = _hovered ? 5 : 4;
            int pillWidth = (int)Math.Round(fullWidth * _fade);
            if (pillWidth <= pillHeight)
                return;
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
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    internal sealed class MetricReadout : Panel
    {
        private readonly TextReadout _value;
        private readonly TextReadout _caption;

        public MetricReadout(string caption)
        {
            BackColor = Color.Transparent;
            _value = new TextReadout();
            _value.Text = "—";
            _value.Location = new Point(0, 0);
            _value.Size = new Size(84, 27);
            _value.Font = new Font("Segoe UI", 12.5F, FontStyle.Bold, GraphicsUnit.Point);
            _value.ForeColor = Color.FromArgb(150, 158, 169);
            _value.BackColor = Color.Transparent;
            _value.TextAlign = ContentAlignment.MiddleLeft;

            TextReadout captionLabel = _caption = new TextReadout();
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
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
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
