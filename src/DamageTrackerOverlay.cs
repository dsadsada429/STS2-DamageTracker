using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;

namespace DamageTracker;

/// <summary>
/// Flat, table-style damage overlay. Supports dragging, resizing, Ctrl+Wheel scaling,
/// persistent UI layout settings, scope history navigation, sortable columns,
/// rich source/card tooltips, lock/click-through, auto-dim, opacity cycling,
/// F8/F9 hotkeys, battle report export, and resilient avatar/row rendering.
/// </summary>
public sealed partial class DamageTrackerOverlay : CanvasLayer
{
    private const float SideTabWidth = 86f;
    private const float SideTabHeight = 32f;
    private const float ViewportMargin = 12f;
    private const float MinPanelHeight = 64f;
    private const float MinUiScale = 0.7f;
    private const float MaxUiScale = 2.5f;
    private const float DefaultExpandedWidth = 520f;
    private const float DefaultCompactWidth = 270f;
    private const float DefaultBackgroundOpacity = 0.70f;
    private const float MinBackgroundOpacity = 0.20f;
    private const float MaxBackgroundOpacity = 1.00f;
    private const float AutoDimAlpha = 0.35f;
    private const float Unset = -99999f;
    private const string SettingsResPath = "user://damage_tracker_ui.json";
    private const string OriginalMouseFilterMeta = "_dt_orig_mf";

    // ── Built-in Localization Fallback ─────────────────────────

    private static readonly Dictionary<string, string> BuiltInEng = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TITLE"] = "Damage Tracker",
        ["PLAYER"] = "Player",
        ["PCT"] = "%",
        ["TOTAL"] = "Total",
        ["COMBAT"] = "Combat",
        ["LAST"] = "Last",
        ["MAX"] = "Max",
        ["EMPTY"] = "Waiting for damage events…",
        ["TURN"] = "Turn",
        ["KILLS"] = "Kills",
        ["OVERKILL"] = "Overkill",
        ["SOURCE_BREAKDOWN"] = "Damage sources",
        ["SOURCE_DIRECT"] = "Direct",
        ["SOURCE_POISON"] = "Poison",
        ["SOURCE_DOOM"] = "Doom",
        ["SOURCE_PET_OR_ORB"] = "Pet / Orb",
        ["SOURCE_OTHER"] = "Other",
        ["TOP_CARDS"] = "Top 3 cards",
        ["RUN"] = "Run",
        ["HISTORY_COMBAT"] = "Combat #{0}",
        ["PREVIOUS_VIEW"] = "Previous view",
        ["NEXT_VIEW"] = "Next view",
        ["LOCK"] = "Lock overlay (F9)",
        ["UNLOCK"] = "Unlock overlay (F9)",
        ["OPACITY"] = "Cycle background opacity",
        ["AUTO_DIM_ON"] = "Enable dimming outside combat",
        ["AUTO_DIM_OFF"] = "Disable dimming outside combat",
        ["COPY_REPORT"] = "Copy battle report",
        ["COPIED"] = "Copied!",
        ["REPORT_TITLE"] = "Damage report · {0}",
        ["REPORT_TEAM"] = "Team damage · {0}",
        ["REPORT_PLAYER"] = "{0}. {1} · {2} damage ({3}%) · {4} kills · {5} overkill",
        ["REPORT_MVP"] = "Damage MVP · {0}",
        ["REPORT_FINISHER"] = "Finisher · {0}",
        ["DRAG_HINT"] = "Drag to move · drag bottom-right to resize · Ctrl+Wheel to scale · F8 show/hide · F9 lock",
        ["GRIP_HINT"] = "Drag to resize (double-click resets)\nCtrl+Wheel scales · drag panel to move",
        ["EXPAND_COLLAPSE"] = "Expand / collapse",
        ["HIDE_SIDE"] = "Hide to screen edge"
    };

    private static readonly Dictionary<string, string> BuiltInZhs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TITLE"] = "伤害统计",
        ["PLAYER"] = "玩家",
        ["PCT"] = "%",
        ["TOTAL"] = "总计",
        ["COMBAT"] = "战斗",
        ["LAST"] = "上次",
        ["MAX"] = "最大",
        ["EMPTY"] = "等待伤害事件…",
        ["TURN"] = "本轮",
        ["KILLS"] = "击杀",
        ["OVERKILL"] = "溢出伤害",
        ["SOURCE_BREAKDOWN"] = "伤害来源",
        ["SOURCE_DIRECT"] = "直接伤害",
        ["SOURCE_POISON"] = "中毒",
        ["SOURCE_DOOM"] = "灾厄",
        ["SOURCE_PET_OR_ORB"] = "召唤物 / 充能球",
        ["SOURCE_OTHER"] = "其他",
        ["TOP_CARDS"] = "伤害最高的三张牌",
        ["RUN"] = "本局",
        ["HISTORY_COMBAT"] = "第 {0} 场战斗",
        ["PREVIOUS_VIEW"] = "上一个视图",
        ["NEXT_VIEW"] = "下一个视图",
        ["LOCK"] = "锁定面板（F9）",
        ["UNLOCK"] = "解锁面板（F9）",
        ["OPACITY"] = "切换背景不透明度",
        ["AUTO_DIM_ON"] = "开启战斗外淡化",
        ["AUTO_DIM_OFF"] = "关闭战斗外淡化",
        ["COPY_REPORT"] = "复制战报",
        ["COPIED"] = "已复制！",
        ["REPORT_TITLE"] = "伤害战报 · {0}",
        ["REPORT_TEAM"] = "全队伤害 · {0}",
        ["REPORT_PLAYER"] = "{0}. {1} · 伤害 {2}（{3}%）· 击杀 {4} · 溢出伤害 {5}",
        ["REPORT_MVP"] = "输出担当 · {0}",
        ["REPORT_FINISHER"] = "补刀王 · {0}",
        ["DRAG_HINT"] = "拖动移动 · 右下角拖动缩放 · Ctrl+滚轮 调字号 · F8 显隐 · F9 锁定",
        ["GRIP_HINT"] = "拖动缩放（双击恢复默认）\nCtrl+滚轮 调字号 · 拖动面板移动",
        ["EXPAND_COLLAPSE"] = "展开/收起",
        ["HIDE_SIDE"] = "收起到屏幕边缘"
    };

    private static readonly DamageSource[] CanonicalSourceOrder =
    {
        DamageSource.Direct,
        DamageSource.Poison,
        DamageSource.Doom,
        DamageSource.PetOrOrb,
        DamageSource.Other
    };

    private static Dictionary<string, string>? _locStrings;
    private static string _lang = "eng";

    private static Dictionary<string, string> LocStrings => _locStrings ??= LoadLocStrings();

    private static Dictionary<string, string> LoadLocStrings()
    {
        string lang = _lang = ResolveGameLanguage();
        Dictionary<string, string> defaults = string.Equals(lang, "zhs", StringComparison.OrdinalIgnoreCase)
            ? BuiltInZhs
            : BuiltInEng;
        Dictionary<string, string> merged = new(defaults, StringComparer.OrdinalIgnoreCase);

        string resPath = $"res://assets/localization/{lang}/damage_tracker.json";
        if (!Godot.FileAccess.FileExists(resPath)
            && !string.Equals(lang, "zhs", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lang, "eng", StringComparison.OrdinalIgnoreCase))
        {
            resPath = "res://assets/localization/eng/damage_tracker.json";
        }

        if (Godot.FileAccess.FileExists(resPath))
        {
            try
            {
                using Godot.FileAccess? file = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
                string? json = file?.GetAsText();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed != null)
                    {
                        foreach (KeyValuePair<string, string> kv in parsed)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                                merged[kv.Key] = kv.Value;
                        }
                    }
                    GD.Print($"[DamageTracker] LoadLocStrings: merged json from {resPath}");
                }
            }
            catch (Exception ex)
            {
                GD.Print($"[DamageTracker] LoadLocStrings: failed reading {resPath} ({ex.Message}); using built-in defaults");
            }
        }
        else
        {
            GD.Print($"[DamageTracker] LoadLocStrings: using built-in {lang} defaults");
        }

        return merged;
    }

    private static string ResolveGameLanguage()
    {
        try
        {
            Type? locMgr = Type.GetType(
                "MegaCrit.Sts2.Core.Localization.LocManager, sts2", throwOnError: false);
            object? inst = locMgr?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            string? lang = inst?.GetType().GetProperty("Language")?.GetValue(inst) as string;
            if (!string.IsNullOrEmpty(lang))
                return lang;
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] ResolveGameLanguage: LocManager error {ex.Message}");
        }

        // Fallback: map OS locale to STS2 three-letter code
        string locale = OS.GetLocaleLanguage().ToLowerInvariant();
        if (locale.StartsWith("zh")) return "zhs";
        if (locale.StartsWith("ja")) return "jpn";
        if (locale.StartsWith("ko")) return "kor";
        if (locale.StartsWith("de")) return "deu";
        if (locale.StartsWith("fr")) return "fra";
        if (locale.StartsWith("it")) return "ita";
        if (locale.StartsWith("pt")) return "ptb";
        if (locale.StartsWith("ru")) return "rus";
        if (locale.StartsWith("pl")) return "pol";
        if (locale.StartsWith("th")) return "tha";
        if (locale.StartsWith("tr")) return "tur";
        if (locale.StartsWith("es")) return "esp";
        return "eng";
    }

    private static string L(string key) => LocStrings.TryGetValue(key, out string? v) ? v : key;

    private static string LF(string key, params object[] args)
    {
        string template = L(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch
        {
            return template;
        }
    }

    // ── Palette ────────────────────────────────────────────────

    private static readonly Color White = new("FFFFFF");
    private static readonly Color Gray = new("A0A8B4");
    private static readonly Color DimGray = new("687480");
    private static readonly Color Green = new("4ADE80");
    private static readonly Color Red = new("F87171");
    private static readonly Color Yellow = new("FACC15");
    private static readonly Color Cyan = new("22D3EE");
    private static readonly Color Pink = new("FF79C6");
    private static readonly Color Border = new("3A3A5C");
    private static readonly Color BorderActive = new("4ADE80");

    // Character theme colors based on their visual identity
    private static readonly Dictionary<string, Color> CharTheme = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ironclad"]    = new Color("E05050"),  // red
        ["silent"]      = new Color("5DB85D"),  // green
        ["defect"]      = new Color("4AA8D8"),  // blue
        ["necrobinder"] = new Color("B060D0"),  // purple
        ["regent"]      = new Color("D8A030"),  // gold/orange
    };

    private static readonly Dictionary<string, Texture2D?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static DamageTrackerOverlay? _instance;
    private static bool _pendingCreate;

    // ── UI State & Layout Persistence ──────────────────────────

    private sealed class UiSettings
    {
        public float Scale { get; set; } = 1f;
        public float Width { get; set; }
        public float CompactWidth { get; set; }
        public float Height { get; set; }
        public float X { get; set; } = Unset;
        public float Y { get; set; } = Unset;
        public bool Expanded { get; set; } = true;
        public bool Locked { get; set; }
        public bool AutoDim { get; set; }
        public float BackgroundOpacity { get; set; } = DefaultBackgroundOpacity;
    }

    private readonly UiSettings _ui = new();

    private PanelContainer? _root;
    private StyleBoxFlat? _rootStyle;
    private MarginContainer? _contentPad;
    private ScrollContainer? _bodyScroll;
    private MarginContainer? _bodyPad;
    private VBoxContainer? _bodyContent;
    private VBoxContainer? _rows;
    private Label? _emptyLabel;
    private Control? _columnHeadings;
    private Control? _separator;
    private Button? _copyBtn;
    private Button? _opacityBtn;
    private Button? _autoDimBtn;
    private Button? _lockBtn;
    private Button? _toggleBtn;
    private Button? _hideBtn;
    private Button? _prevViewBtn;
    private Button? _nextViewBtn;
    private Label? _scopeLabel;
    private Button? _sideTab;
    private GripNode? _grip;

    private readonly Dictionary<SortColumn, Button> _sortHeaderButtons = new();
    private readonly Dictionary<ulong, decimal> _knownMaxByPlayer = new();
    private readonly Dictionary<ulong, ulong> _flashUntilMsecByPlayer = new();
    private bool _hasSeededRunMax;

    private SortColumn _sortColumn = SortColumn.Total;
    private bool _sortDescending = true;
    private int? _selectedHistoryCombatIndex;

    private bool _masterVisible = true;
    private bool _isDragging;
    private Vector2 _dragOffset;
    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartSize;
    private bool _hiddenToSide;
    private HiddenDockSide _hiddenSide = HiddenDockSide.Right;
    private bool _resetScrollPending;
    private string? _lastAppliedRunToken;
    private int _lastAppliedCombatIndex = -1;
    private OverlayState? _lastState;
    private int _playerCount;
    private float _panelWidth;
    private float _panelHeight;
    private Vector2 _lastViewportSize;
    private int _copyFeedbackVersion;

    // ── Scaled layout metrics ──────────────────────────────────

    private float R(float v) => Mathf.Round(v * _ui.Scale);
    private int FS(float v) => Mathf.Max(8, Mathf.RoundToInt(v * _ui.Scale));

    private float PadX => R(10f);
    private float PadY => R(8f);
    private float Gap => R(4f);
    private float HeaderH => R(24f);
    private float ScopeBarH => R(24f);
    private float HeadingH => R(16f);
    private float FooterH => R(16f);
    private float RowH => R(_ui.Expanded ? 42f : 30f);
    private float RowGap => R(2f);
    private float RowPadX => R(6f);
    private float ExpandedAvatarSize => R(32f);
    private float ExpandedAvatarGap => R(8f);
    private float CompactAvatarSize => R(24f);
    private float CompactAvatarGap => R(6f);
    private float AvatarSize => _ui.Expanded ? ExpandedAvatarSize : CompactAvatarSize;
    private float AvatarGap => _ui.Expanded ? ExpandedAvatarGap : CompactAvatarGap;
    private float NameMin => R(60f);
    private float ColPct => R(40f);
    private float ColTotal => R(60f);
    private float ColCombat => R(60f);
    private float ColTurn => R(50f);
    private float ColLast => R(52f);
    private float ColMax => R(52f);
    private float ScrollbarGutter => R(16f);
    private float GripSize => R(14f);

    private float StatsWidth => _ui.Expanded
        ? ColPct + ColTotal + ColCombat + ColTurn + ColLast + ColMax
        : ColTotal;

    private float TableMinWidth =>
        PadX * 2f + RowPadX * 2f + AvatarSize + AvatarGap + NameMin + StatsWidth + ScrollbarGutter;

    private float HeaderMinWidth =>
        PadX * 2f + R(44f) + 6f * R(24f) + 6f * R(4f);

    private float MinPanelWidth => Mathf.Max(TableMinWidth, HeaderMinWidth);

    private float PreferredWidth
    {
        get
        {
            if (_ui.Expanded)
                return _ui.Width > 0f ? _ui.Width : Mathf.Max(DefaultExpandedWidth, MinPanelWidth);

            return _ui.CompactWidth > 0f ? _ui.CompactWidth : Mathf.Max(DefaultCompactWidth, MinPanelWidth);
        }
    }

    private float ChromeHeight =>
        PadY * 2f
        + HeaderH + Gap
        + ScopeBarH + Gap
        + (_ui.Expanded ? HeadingH + Gap + 1f + Gap : 0f)
        + Gap + FooterH;

    private float BodyContentHeight(int count) =>
        count > 0 ? count * RowH + (count - 1) * RowGap : R(18f);

    private static string SettingsPath() => ProjectSettings.GlobalizePath(SettingsResPath);

    private static UiSettings LoadSettings()
    {
        UiSettings settings = new();
        try
        {
            string path = SettingsPath();
            if (!File.Exists(path))
                return settings;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = line[..eq].Trim().ToLowerInvariant();
                if (!float.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    continue;

                switch (key)
                {
                    case "scale": settings.Scale = val; break;
                    case "width": settings.Width = val; break;
                    case "compact_width": settings.CompactWidth = val; break;
                    case "height": settings.Height = val; break;
                    case "x": settings.X = val; break;
                    case "y": settings.Y = val; break;
                    case "expanded": settings.Expanded = val != 0f; break;
                    case "locked": settings.Locked = val != 0f; break;
                    case "auto_dim": settings.AutoDim = val != 0f; break;
                    case "background_opacity": settings.BackgroundOpacity = val; break;
                }
            }

            if (settings.Scale <= 0f)
                settings.Scale = 1f;
            settings.Scale = Mathf.Clamp(settings.Scale, MinUiScale, MaxUiScale);
            settings.BackgroundOpacity = Mathf.Clamp(settings.BackgroundOpacity, MinBackgroundOpacity, MaxBackgroundOpacity);
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] settings load failed: {ex.Message}");
        }
        return settings;
    }

    private void SaveSettings()
    {
        try
        {
            string path = SettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new();
            sb.AppendLine("# Damage Tracker overlay layout (edit while the game is closed)");
            sb.AppendLine($"scale={_ui.Scale.ToString("0.###", inv)}");
            sb.AppendLine($"width={_ui.Width.ToString("0.#", inv)}");
            sb.AppendLine($"compact_width={_ui.CompactWidth.ToString("0.#", inv)}");
            sb.AppendLine($"height={_ui.Height.ToString("0.#", inv)}");
            sb.AppendLine($"x={_ui.X.ToString("0.#", inv)}");
            sb.AppendLine($"y={_ui.Y.ToString("0.#", inv)}");
            sb.AppendLine($"expanded={(_ui.Expanded ? "1" : "0")}");
            sb.AppendLine($"locked={(_ui.Locked ? "1" : "0")}");
            sb.AppendLine($"auto_dim={(_ui.AutoDim ? "1" : "0")}");
            sb.AppendLine($"background_opacity={_ui.BackgroundOpacity.ToString("0.##", inv)}");
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] settings save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Schedule overlay creation on next frame (safe to call from mod init before game loop is ready).
    /// </summary>
    public static void ScheduleCreate()
    {
        _pendingCreate = true;
    }

    public override void _EnterTree()
    {
        Layer = 100;
        Name = nameof(DamageTrackerOverlay);
        RunDamageTrackerService.Changed += OnChanged;
    }

    public override void _ExitTree()
    {
        RunDamageTrackerService.Changed -= OnChanged;
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    public override void _Ready()
    {
        UiSettings loaded = LoadSettings();
        _ui.Scale = loaded.Scale;
        _ui.Width = loaded.Width;
        _ui.CompactWidth = loaded.CompactWidth;
        _ui.Height = loaded.Height;
        _ui.X = loaded.X;
        _ui.Y = loaded.Y;
        _ui.Expanded = loaded.Expanded;
        _ui.Locked = loaded.Locked;
        _ui.AutoDim = loaded.AutoDim;
        _ui.BackgroundOpacity = loaded.BackgroundOpacity;

        BuildUi();
        ApplyState(RunDamageTrackerService.BuildOverlayState(), isLiveUpdate: false);
    }

    private void BuildUi()
    {
        _sortHeaderButtons.Clear();

        _root = new PanelContainer
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = ResolveStartPosition(),
            CustomMinimumSize = new Vector2(PreferredWidth, 0f)
        };
        _root.GuiInput += OnGuiInput;

        _rootStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, _ui.BackgroundOpacity),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        _root.AddThemeStyleboxOverride("panel", _rootStyle);

        _contentPad = new MarginContainer();
        _contentPad.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(PadX));
        _contentPad.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(PadX));
        _contentPad.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(PadY));
        _contentPad.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(PadY));

        VBoxContainer col = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", Mathf.RoundToInt(Gap));

        // ── Header row: title + utility/mode buttons ──
        col.AddChild(BuildHeader());

        // ── Scope switcher row: Run / Combat #N ──
        col.AddChild(BuildScopeBar());

        // ── Column headings (expanded mode) ──
        _columnHeadings = BuildColumnHeadings();
        col.AddChild(_columnHeadings);

        // ── Thin separator (expanded mode) ──
        _separator = HLine();
        col.AddChild(_separator);

        // ── Scrollable body ──
        _bodyScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };

        _bodyPad = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        _bodyPad.AddThemeConstantOverride("margin_left", 0);
        _bodyPad.AddThemeConstantOverride("margin_right", 0);
        _bodyPad.AddThemeConstantOverride("margin_top", 0);
        _bodyPad.AddThemeConstantOverride("margin_bottom", 0);

        _bodyContent = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bodyContent.AddThemeConstantOverride("separation", 0);

        // ── Player rows ──
        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", Mathf.RoundToInt(RowGap));
        _bodyContent.AddChild(_rows);

        // ── Empty hint ──
        _emptyLabel = MakeLabel(L("EMPTY"), FS(12f), DimGray);
        _emptyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _bodyContent.AddChild(_emptyLabel);

        _bodyPad.AddChild(_bodyContent);
        _bodyScroll.AddChild(_bodyPad);
        col.AddChild(_bodyScroll);

        // ── Footer spacer so the bottom-right grip never overlaps stat cells ──
        col.AddChild(new Control
        {
            CustomMinimumSize = new Vector2(0f, FooterH),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        });

        _contentPad.AddChild(col);
        _root.AddChild(_contentPad);
        AddChild(_root);

        _sideTab = BuildSideTab();
        AddChild(_sideTab);

        _grip = new GripNode
        {
            Name = "Grip",
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            TooltipText = L("GRIP_HINT")
        };
        _grip.GuiInput += OnGripInput;
        AddChild(_grip);

        RefreshChrome();
    }

    private Vector2 ResolveStartPosition()
    {
        Vector2 vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280f, 720f);
        float x = _ui.X == Unset ? ViewportMargin : _ui.X;
        float y = _ui.Y == Unset ? ViewportMargin : _ui.Y;
        return new Vector2(
            Mathf.Clamp(x, 0f, Mathf.Max(0f, vp.X - 200f)),
            Mathf.Clamp(y, 0f, Mathf.Max(0f, vp.Y - 100f)));
    }

    private void RebuildUi()
    {
        if (_root != null && IsInstanceValid(_root))
        {
            _root.QueueFree();
            _root = null;
        }
        if (_sideTab != null && IsInstanceValid(_sideTab))
        {
            _sideTab.QueueFree();
            _sideTab = null;
        }
        if (_grip != null && IsInstanceValid(_grip))
        {
            _grip.QueueFree();
            _grip = null;
        }
        _rootStyle = null;

        BuildUi();
        if (_lastState != null)
            RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
        else
            ApplyLayout();
    }

    // ── Header & Scope Switcher ────────────────────────────────

    private Control BuildHeader()
    {
        HBoxContainer h = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, HeaderH)
        };
        h.AddThemeConstantOverride("separation", Mathf.RoundToInt(R(4f)));

        Label title = MakeLabel(L("TITLE"), FS(15f), White);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.MouseFilter = Control.MouseFilterEnum.Pass;
        title.TooltipText = L("DRAG_HINT");

        _copyBtn = MakeCaptionButton("Cp", OnCopyReport, L("COPY_REPORT"), FS(10f));
        _opacityBtn = MakeCaptionButton("Op", OnCycleOpacity, FormatOpacityTooltip(), FS(10f));
        _autoDimBtn = MakeCaptionButton("Dm", OnToggleAutoDim, _ui.AutoDim ? L("AUTO_DIM_OFF") : L("AUTO_DIM_ON"), FS(10f));
        _lockBtn = MakeCaptionButton("Lk", ToggleLocked, _ui.Locked ? L("UNLOCK") : L("LOCK"), FS(10f));
        _toggleBtn = MakeCaptionButton("\u25bc", OnToggle, L("EXPAND_COLLAPSE"), FS(12f));
        _hideBtn = MakeCaptionButton("\u00bb", OnHideToSide, L("HIDE_SIDE"), FS(12f));

        h.AddChild(title);
        h.AddChild(_copyBtn);
        h.AddChild(_opacityBtn);
        h.AddChild(_autoDimBtn);
        h.AddChild(_lockBtn);
        h.AddChild(_toggleBtn);
        h.AddChild(_hideBtn);
        return h;
    }

    private Control BuildScopeBar()
    {
        HBoxContainer h = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, ScopeBarH)
        };
        h.AddThemeConstantOverride("separation", Mathf.RoundToInt(R(4f)));

        _prevViewBtn = MakeCaptionButton("\u2039", OnPreviousView, L("PREVIOUS_VIEW"), FS(12f));
        _nextViewBtn = MakeCaptionButton("\u203a", OnNextView, L("NEXT_VIEW"), FS(12f));

        _scopeLabel = MakeLabel(L("RUN"), FS(11f), Gray);
        _scopeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scopeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _scopeLabel.ClipText = true;
        _scopeLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _scopeLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        _scopeLabel.TooltipText = L("RUN");

        h.AddChild(_prevViewBtn);
        h.AddChild(_scopeLabel);
        h.AddChild(_nextViewBtn);
        return h;
    }

    private Button MakeCaptionButton(string text, Action onPressed, string tooltip, int fontSize)
    {
        Button btn = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(R(24f), R(24f)),
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = tooltip
        };
        btn.AddThemeFontSizeOverride("font_size", fontSize);
        btn.AddThemeStyleboxOverride("normal", RoundedBox("FFFFFF10"));
        btn.AddThemeStyleboxOverride("hover", RoundedBox("FFFFFF22"));
        btn.AddThemeStyleboxOverride("pressed", RoundedBox("FFFFFF32"));
        btn.AddThemeStyleboxOverride("disabled", RoundedBox("FFFFFF08"));
        btn.AddThemeColorOverride("font_color", Gray);
        btn.AddThemeColorOverride("font_hover_color", White);
        btn.AddThemeColorOverride("font_pressed_color", White);
        btn.AddThemeColorOverride("font_disabled_color", new Color(DimGray, 0.45f));
        btn.Pressed += onPressed;
        return btn;
    }

    private static void StyleButtonState(Button? btn, bool active, Color activeColor)
    {
        if (btn == null || !IsInstanceValid(btn))
            return;

        if (active)
        {
            btn.AddThemeStyleboxOverride("normal", RoundedBox(new Color(activeColor, 0.22f)));
            btn.AddThemeStyleboxOverride("hover", RoundedBox(new Color(activeColor, 0.32f)));
            btn.AddThemeStyleboxOverride("pressed", RoundedBox(new Color(activeColor, 0.42f)));
            btn.AddThemeColorOverride("font_color", activeColor);
        }
        else
        {
            btn.AddThemeStyleboxOverride("normal", RoundedBox("FFFFFF10"));
            btn.AddThemeStyleboxOverride("hover", RoundedBox("FFFFFF22"));
            btn.AddThemeStyleboxOverride("pressed", RoundedBox("FFFFFF32"));
            btn.AddThemeColorOverride("font_color", Gray);
        }
    }

    private static StyleBoxFlat RoundedBox(string bg, int radius = 3) => RoundedBox(new Color(bg), radius);

    private static StyleBoxFlat RoundedBox(Color bg, int radius = 3)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius
        };
    }

    private Button BuildSideTab()
    {
        Button tab = new()
        {
            Name = "SideTab",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            Text = L("TITLE"),
            CustomMinimumSize = new Vector2(SideTabWidth, SideTabHeight),
            Size = new Vector2(SideTabWidth, SideTabHeight)
        };
        tab.AddThemeFontSizeOverride("font_size", 12);
        tab.AddThemeColorOverride("font_color", White);
        tab.AddThemeStyleboxOverride("normal", TabBox("121821EE"));
        tab.AddThemeStyleboxOverride("hover", TabBox("1A2431EE"));
        tab.Pressed += OnRestoreFromSide;
        return tab;
    }

    private static StyleBoxFlat TabBox(string bg)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(bg),
            BorderColor = BorderActive,
            BorderWidthLeft = 2,
            BorderWidthTop = 1,
            BorderWidthRight = 2,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
    }

    // ── Column headings & sorting ──────────────────────────────

    private Control BuildColumnHeadings()
    {
        HBoxContainer h = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, HeadingH)
        };
        h.AddThemeConstantOverride("separation", 0);

        h.AddChild(Spacer(RowPadX));
        h.AddChild(Spacer(ExpandedAvatarSize));
        h.AddChild(Spacer(ExpandedAvatarGap));
        h.AddChild(HeadLabel(L("PLAYER"), true));

        h.AddChild(HeadSortButton(SortColumn.Pct, ColPct));
        h.AddChild(HeadSortButton(SortColumn.Total, ColTotal));
        h.AddChild(HeadSortButton(SortColumn.Combat, ColCombat));
        h.AddChild(HeadSortButton(SortColumn.Turn, ColTurn));
        h.AddChild(HeadSortButton(SortColumn.Last, ColLast));
        h.AddChild(HeadSortButton(SortColumn.Max, ColMax));

        h.AddChild(Spacer(RowPadX));
        h.AddChild(Spacer(ScrollbarGutter));

        RefreshSortHeaders();
        return h;
    }

    private Button HeadSortButton(SortColumn column, float width)
    {
        Button btn = new()
        {
            CustomMinimumSize = new Vector2(width, HeadingH),
            Alignment = HorizontalAlignment.Right,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        btn.AddThemeFontSizeOverride("font_size", FS(10f));
        btn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        btn.AddThemeStyleboxOverride("hover", RoundedBox("FFFFFF14", 2));
        btn.AddThemeStyleboxOverride("pressed", RoundedBox("FFFFFF24", 2));
        btn.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        btn.AddThemeColorOverride("font_hover_color", White);
        btn.AddThemeColorOverride("font_pressed_color", White);

        SortColumn captured = column;
        btn.Pressed += () => OnSortColumnPressed(captured);
        _sortHeaderButtons[column] = btn;
        return btn;
    }

    private void OnSortColumnPressed(SortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = true;
        }

        RefreshSortHeaders();
        if (_lastState != null)
            RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
    }

    private void RefreshSortHeaders()
    {
        foreach (KeyValuePair<SortColumn, Button> kv in _sortHeaderButtons)
        {
            Button btn = kv.Value;
            if (!IsInstanceValid(btn))
                continue;

            string label = SortColumnLabel(kv.Key);
            bool active = kv.Key == _sortColumn;
            btn.Text = active ? $"{label}{(_sortDescending ? "\u25bc" : "\u25b2")}" : label;
            btn.AddThemeColorOverride("font_color", active ? White : DimGray);
        }
    }

    private static string SortColumnLabel(SortColumn column) => column switch
    {
        SortColumn.Pct => L("PCT"),
        SortColumn.Total => L("TOTAL"),
        SortColumn.Combat => L("COMBAT"),
        SortColumn.Turn => L("TURN"),
        SortColumn.Last => L("LAST"),
        SortColumn.Max => L("MAX"),
        _ => ""
    };

    private List<PlayerDamageSnapshot> SortPlayers(IReadOnlyList<PlayerDamageSnapshot> players, bool isHistory)
    {
        List<PlayerDamageSnapshot> sorted = new(players.Count);
        for (int i = 0; i < players.Count; i++)
            sorted.Add(players[i]);

        sorted.Sort((a, b) =>
        {
            if (!_ui.Expanded && isHistory)
            {
                int compactCmp = b.CombatDamage.CompareTo(a.CombatDamage);
                return compactCmp != 0 ? compactCmp : a.PlayerKey.CompareTo(b.PlayerKey);
            }

            decimal aVal = GetSortValue(a, _sortColumn, isHistory);
            decimal bVal = GetSortValue(b, _sortColumn, isHistory);
            int cmp = _sortDescending ? bVal.CompareTo(aVal) : aVal.CompareTo(bVal);
            return cmp != 0 ? cmp : a.PlayerKey.CompareTo(b.PlayerKey);
        });
        return sorted;
    }

    private static decimal GetSortValue(PlayerDamageSnapshot snap, SortColumn col, bool isHistory) => col switch
    {
        SortColumn.Pct => isHistory ? snap.CombatDamage : snap.TotalDamage,
        SortColumn.Total => snap.TotalDamage,
        SortColumn.Combat => snap.CombatDamage,
        SortColumn.Turn => snap.TurnDamage,
        SortColumn.Last => snap.LastDamage,
        SortColumn.Max => snap.MaxHitDamage,
        _ => snap.TotalDamage
    };

    // ── Player rows ────────────────────────────────────────────

    private Control CreateRow(PlayerDamageSnapshot snap, float ratio, bool isHistory, bool isLiveUpdate, bool flashMaxHit)
    {
        bool active = !isHistory && snap.IsActive;
        Color theme = GetCharTheme(snap.CharacterName);
        StyleBoxFlat cardStyle = CardBox(theme, active);

        PanelContainer card = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, RowH),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = RowTooltip(snap, ratio, isHistory)
        };
        card.AddThemeStyleboxOverride("panel", cardStyle);

        MarginContainer mp = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        mp.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(Mathf.Max(2f, RowH * 0.1f)));
        mp.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(Mathf.Max(2f, RowH * 0.1f)));

        HBoxContainer row = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 0);

        row.AddChild(BuildAvatar(snap, Mathf.RoundToInt(AvatarSize)));
        row.AddChild(Spacer(AvatarGap));

        VBoxContainer nameCol = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(NameMin, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        nameCol.AddThemeConstantOverride("separation", 0);

        Label nameLabel = MakeLabel(snap.DisplayName, FS(13f), active ? theme.Lightened(0.3f) : White);
        nameLabel.ClipText = true;
        nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        Label charLabel = MakeLabel(snap.CharacterName, FS(10f), new Color(theme, 0.7f));
        charLabel.ClipText = true;
        charLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        nameCol.AddChild(nameLabel);
        nameCol.AddChild(charLabel);
        row.AddChild(nameCol);

        int pct = Mathf.RoundToInt(Mathf.Clamp(ratio, 0f, 1f) * 100f);
        row.AddChild(StatCell($"{pct}%", new Color(theme, 0.9f), ColPct, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.TotalDamage), Yellow, ColTotal, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.CombatDamage), Cyan, ColCombat, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.TurnDamage), Green, ColTurn, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.LastDamage), Red, ColLast, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.MaxHitDamage), Pink, ColMax, FS(13f)));

        mp.AddChild(row);

        // ── Damage share bar ──
        Control barBg = new()
        {
            CustomMinimumSize = new Vector2(0f, Mathf.Max(2f, R(3f))),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        ColorRect barTrack = new()
        {
            Color = new Color("FFFFFF0A"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        barTrack.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        ColorRect barFill = new()
        {
            Color = new Color(theme, 0.6f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = Mathf.Clamp(ratio, 0f, 1f),
            AnchorBottom = 1f,
            OffsetLeft = 0f,
            OffsetTop = 0f,
            OffsetRight = 0f,
            OffsetBottom = 0f
        };

        barBg.AddChild(barTrack);
        barBg.AddChild(barFill);

        VBoxContainer cardContent = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        cardContent.AddThemeConstantOverride("separation", 0);
        cardContent.AddChild(mp);
        cardContent.AddChild(barBg);

        card.AddChild(cardContent);

        if (flashMaxHit)
            FlashMaxHitBorder(card, cardStyle, theme, active);
        else if (isLiveUpdate)
            FlashOnUpdate(card);

        return card;
    }

    private Control CreateCompactRow(PlayerDamageSnapshot snap, float ratio, bool isHistory, bool isLiveUpdate, bool flashMaxHit)
    {
        Color theme = GetCharTheme(snap.CharacterName);
        bool active = !isHistory && snap.IsActive;
        StyleBoxFlat cardStyle = CardBox(theme, active);

        PanelContainer card = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, RowH),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = RowTooltip(snap, ratio, isHistory)
        };
        card.AddThemeStyleboxOverride("panel", cardStyle);

        MarginContainer mp = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        mp.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(R(2f)));
        mp.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(R(2f)));

        HBoxContainer row = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 0);

        row.AddChild(BuildAvatar(snap, Mathf.RoundToInt(AvatarSize)));
        row.AddChild(Spacer(AvatarGap));

        Label nameLabel = MakeLabel(snap.DisplayName, FS(12f), active ? theme.Lightened(0.3f) : White);
        nameLabel.ClipText = true;
        nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.CustomMinimumSize = new Vector2(NameMin, 0f);
        row.AddChild(nameLabel);

        decimal primaryDamage = isHistory ? snap.CombatDamage : snap.TotalDamage;
        Color primaryColor = isHistory ? Cyan : Yellow;
        row.AddChild(StatCell(FormatCell(primaryDamage), primaryColor, ColTotal, FS(13f)));

        mp.AddChild(row);
        card.AddChild(mp);

        if (flashMaxHit)
            FlashMaxHitBorder(card, cardStyle, theme, active);
        else if (isLiveUpdate)
            FlashOnUpdate(card);

        return card;
    }

    private static StyleBoxFlat CardBox(Color theme, bool active)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(theme, active ? 0.18f : 0.08f),
            BorderColor = active ? new Color(theme, 0.9f) : new Color(theme, 0.3f),
            BorderWidthLeft = 3,
            BorderWidthTop = 0,
            BorderWidthRight = 0,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        };
    }

    private static void FlashOnUpdate(Control card)
    {
        card.Modulate = new Color(1f, 1f, 1f, 0.35f);
        card.TreeEntered += () =>
        {
            if (IsInstanceValid(card))
            {
                card.CreateTween()?
                    .TweenProperty(card, "modulate", new Color(1f, 1f, 1f, 1f), 0.35f)
                    .SetTrans(Tween.TransitionType.Cubic)
                    .SetEase(Tween.EaseType.Out);
            }
        };
    }

    private static void FlashMaxHitBorder(Control card, StyleBoxFlat cardStyle, Color theme, bool active)
    {
        Color targetBorder = active ? new Color(theme, 0.9f) : new Color(theme, 0.3f);
        Color targetBg = new(theme, active ? 0.18f : 0.08f);

        cardStyle.BorderColor = Yellow;
        cardStyle.BorderWidthLeft = 3;
        cardStyle.BorderWidthTop = 2;
        cardStyle.BorderWidthRight = 2;
        cardStyle.BorderWidthBottom = 2;
        cardStyle.BgColor = new Color(Yellow, 0.24f);

        card.TreeEntered += () =>
        {
            if (!IsInstanceValid(card) || !IsInstanceValid(cardStyle))
                return;

            Tween? tween = card.CreateTween();
            if (tween == null)
                return;

            tween.SetParallel(true);
            tween.TweenProperty(cardStyle, "border_color", targetBorder, 0.6f)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            tween.TweenProperty(cardStyle, "bg_color", targetBg, 0.6f)
                .SetTrans(Tween.TransitionType.Cubic)
                .SetEase(Tween.EaseType.Out);
            tween.Finished += () =>
            {
                if (!IsInstanceValid(cardStyle))
                    return;
                cardStyle.BorderWidthTop = 0;
                cardStyle.BorderWidthRight = 0;
                cardStyle.BorderWidthBottom = 0;
                cardStyle.BorderColor = targetBorder;
                cardStyle.BgColor = targetBg;
            };
        };
    }

    private static string RowTooltip(PlayerDamageSnapshot snap, float ratio, bool isHistory)
    {
        StringBuilder sb = new();
        string pct = $"{Mathf.RoundToInt(Mathf.Clamp(ratio, 0f, 1f) * 100f)}%";
        sb.AppendLine($"{snap.DisplayName} \u00b7 {snap.CharacterName} \u00b7 {pct}");
        sb.AppendLine(
            $"{L("TOTAL")}: {RunDamageTrackerService.Format(snap.TotalDamage)}    "
            + $"{L("COMBAT")}: {RunDamageTrackerService.Format(snap.CombatDamage)}    "
            + $"{L("TURN")}: {RunDamageTrackerService.Format(snap.TurnDamage)}");
        sb.AppendLine(
            $"{L("LAST")}: {RunDamageTrackerService.Format(snap.LastDamage)}    "
            + $"{L("MAX")}: {RunDamageTrackerService.Format(snap.MaxHitDamage)}");

        int kills = isHistory ? snap.CombatKills : snap.Kills;
        decimal overkill = isHistory ? snap.CombatOverkillDamage : snap.OverkillDamage;
        sb.Append($"{L("KILLS")}: {kills}    {L("OVERKILL")}: {RunDamageTrackerService.Format(overkill)}");

        IReadOnlyDictionary<DamageSource, decimal>? scopeSources = isHistory ? snap.CombatSourceDamage : snap.RunSourceDamage;
        decimal scopeDenom = isHistory ? snap.CombatDamage : snap.TotalDamage;
        AppendSourceBreakdown(sb, scopeSources, scopeDenom);

        IReadOnlyDictionary<string, decimal>? scopeCards = isHistory ? snap.CombatCardDamage : snap.RunCardDamage;
        AppendTopCards(sb, scopeCards);

        // In Run view, when latest combat differs from cumulative run totals, add a concise combat breakdown
        if (!isHistory
            && snap.CombatDamage > 0m
            && (snap.CombatDamage != snap.TotalDamage
                || snap.CombatKills != snap.Kills
                || snap.CombatOverkillDamage != snap.OverkillDamage))
        {
            AppendLatestCombatSection(sb, snap);
        }

        return sb.ToString();
    }

    private static void AppendSourceBreakdown(
        StringBuilder sb,
        IReadOnlyDictionary<DamageSource, decimal>? sources,
        decimal denominator)
    {
        if (sources == null || sources.Count == 0)
            return;

        List<(DamageSource Source, decimal Damage)> nonZero = new();
        for (int i = 0; i < CanonicalSourceOrder.Length; i++)
        {
            DamageSource src = CanonicalSourceOrder[i];
            if (sources.TryGetValue(src, out decimal dmg) && dmg > 0m)
                nonZero.Add((src, dmg));
        }

        if (nonZero.Count == 0)
            return;

        sb.AppendLine();
        sb.Append($"{L("SOURCE_BREAKDOWN")}:");
        for (int i = 0; i < nonZero.Count; i++)
        {
            (DamageSource src, decimal dmg) = nonZero[i];
            int srcPct = denominator > 0m
                ? Mathf.RoundToInt(Mathf.Clamp((float)(dmg / denominator), 0f, 1f) * 100f)
                : 0;
            sb.AppendLine();
            sb.Append($"  {SourceLabel(src)}: {RunDamageTrackerService.Format(dmg)} ({srcPct}%)");
        }
    }

    private static void AppendTopCards(StringBuilder sb, IReadOnlyDictionary<string, decimal>? cards)
    {
        List<KeyValuePair<string, decimal>> top = GetTopCards(cards, 3);
        if (top.Count == 0)
            return;

        sb.AppendLine();
        sb.Append($"{L("TOP_CARDS")}:");
        for (int i = 0; i < top.Count; i++)
        {
            sb.AppendLine();
            sb.Append($"  {i + 1}. {top[i].Key}: {RunDamageTrackerService.Format(top[i].Value)}");
        }
    }

    private static void AppendLatestCombatSection(StringBuilder sb, PlayerDamageSnapshot snap)
    {
        List<string> sourceParts = new();
        if (snap.CombatSourceDamage != null)
        {
            for (int i = 0; i < CanonicalSourceOrder.Length; i++)
            {
                DamageSource src = CanonicalSourceOrder[i];
                if (snap.CombatSourceDamage.TryGetValue(src, out decimal dmg) && dmg > 0m)
                {
                    int pct = snap.CombatDamage > 0m
                        ? Mathf.RoundToInt(Mathf.Clamp((float)(dmg / snap.CombatDamage), 0f, 1f) * 100f)
                        : 0;
                    sourceParts.Add($"{SourceLabel(src)} {RunDamageTrackerService.Format(dmg)} ({pct}%)");
                }
            }
        }

        List<KeyValuePair<string, decimal>> topCombatCards = GetTopCards(snap.CombatCardDamage, 3);
        bool hasCombatKills = snap.CombatKills > 0 || snap.CombatOverkillDamage > 0m;
        if (!hasCombatKills && sourceParts.Count == 0 && topCombatCards.Count == 0)
            return;

        sb.AppendLine();
        sb.Append($"{L("COMBAT")}:");

        if (hasCombatKills)
        {
            sb.AppendLine();
            sb.Append($"  {L("KILLS")}: {snap.CombatKills}    {L("OVERKILL")}: {RunDamageTrackerService.Format(snap.CombatOverkillDamage)}");
        }

        if (sourceParts.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"  {L("SOURCE_BREAKDOWN")}: {string.Join(", ", sourceParts)}");
        }

        if (topCombatCards.Count > 0)
        {
            List<string> cardParts = new(topCombatCards.Count);
            for (int i = 0; i < topCombatCards.Count; i++)
                cardParts.Add($"{topCombatCards[i].Key} {RunDamageTrackerService.Format(topCombatCards[i].Value)}");
            sb.AppendLine();
            sb.Append($"  {L("TOP_CARDS")}: {string.Join(", ", cardParts)}");
        }
    }

    private static List<KeyValuePair<string, decimal>> GetTopCards(IReadOnlyDictionary<string, decimal>? cards, int maxCount)
    {
        List<KeyValuePair<string, decimal>> result = new();
        if (cards == null || cards.Count == 0)
            return result;

        foreach (KeyValuePair<string, decimal> kv in cards)
        {
            if (kv.Value > 0m && !string.IsNullOrWhiteSpace(kv.Key))
                result.Add(kv);
        }

        result.Sort((a, b) =>
        {
            int cmp = b.Value.CompareTo(a.Value);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Key, b.Key);
        });

        if (result.Count > maxCount)
            result.RemoveRange(maxCount, result.Count - maxCount);
        return result;
    }

    private static string SourceLabel(DamageSource source) => source switch
    {
        DamageSource.Direct => L("SOURCE_DIRECT"),
        DamageSource.Poison => L("SOURCE_POISON"),
        DamageSource.Doom => L("SOURCE_DOOM"),
        DamageSource.PetOrOrb => L("SOURCE_PET_OR_ORB"),
        _ => L("SOURCE_OTHER")
    };

    // ── Avatar ─────────────────────────────────────────────────

    private static bool IsResourceAlive(Texture2D? tex)
    {
        if (tex == null)
            return false;

        try
        {
            return IsInstanceValid(tex) && tex.GetWidth() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Control BuildAvatar(PlayerDamageSnapshot snap, int size)
    {
        Texture2D? tex = null;
        try
        {
            tex = LoadIcon(snap.CharacterName);
            if (!IsResourceAlive(tex))
                tex = IsResourceAlive(snap.PortraitTexture) ? snap.PortraitTexture : null;
        }
        catch
        {
            tex = null;
        }

        if (tex != null)
        {
            try
            {
                return new TextureRect
                {
                    Texture = tex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(size, size),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
            }
            catch (Exception ex)
            {
                GD.Print($"[DamageTracker] avatar texture unavailable ({ex.GetType().Name}); using initials");
            }
        }

        return InitialsAvatar(snap, size);
    }

    private static Control InitialsAvatar(PlayerDamageSnapshot snap, int size)
    {
        PanelContainer frame = new()
        {
            CustomMinimumSize = new Vector2(size, size),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        Color bgColor;
        try
        {
            bgColor = Color.FromHsv((snap.PlayerKey % 360) / 360f, 0.45f, 0.55f);
        }
        catch
        {
            bgColor = Gray;
        }

        frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = bgColor,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        });

        CenterContainer cc = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        cc.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        Label ini = MakeLabel(Initials(snap), Mathf.Max(8, Mathf.RoundToInt(size * 0.38f)), White);
        cc.AddChild(ini);
        frame.AddChild(cc);
        return frame;
    }

    // ── Header & Scope Actions ─────────────────────────────────

    private void OnToggle()
    {
        _ui.Expanded = !_ui.Expanded;
        SaveSettings();
        RefreshChrome();

        if (_lastState != null)
            RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
        else
            ApplyLayout();
    }

    private void OnHideToSide()
    {
        if (_root == null)
            return;

        _isDragging = false;
        _resizing = false;
        _hiddenSide = ResolveHiddenSide();
        _hiddenToSide = true;
        RefreshChrome();
    }

    private void OnRestoreFromSide()
    {
        _hiddenToSide = false;
        RefreshChrome();
        SnapRootIntoViewFromSide();
        if (_lastState != null)
            RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
        else
            ApplyLayout();
    }

    private void OnCycleOpacity()
    {
        float cur = _ui.BackgroundOpacity;
        if (Mathf.Abs(cur - 0.70f) < 0.03f)
            _ui.BackgroundOpacity = 0.45f;
        else if (Mathf.Abs(cur - 0.45f) < 0.03f)
            _ui.BackgroundOpacity = 0.88f;
        else
            _ui.BackgroundOpacity = 0.70f;

        SaveSettings();
        ApplyAppearance();
    }

    private string FormatOpacityTooltip() =>
        $"{L("OPACITY")} ({Mathf.RoundToInt(_ui.BackgroundOpacity * 100f)}%)";

    private void OnToggleAutoDim()
    {
        _ui.AutoDim = !_ui.AutoDim;
        SaveSettings();
        ApplyAppearance();
    }

    private void ToggleLocked()
    {
        _ui.Locked = !_ui.Locked;
        if (_ui.Locked)
        {
            _isDragging = false;
            _resizing = false;
        }
        SaveSettings();
        RefreshChrome();
    }

    private void ToggleMasterVisible()
    {
        _masterVisible = !_masterVisible;
        if (!_masterVisible)
        {
            _isDragging = false;
            _resizing = false;
        }
        RefreshChrome();
    }

    private void OnCopyReport()
    {
        if (_lastState == null)
            return;

        try
        {
            string report = BattleReportFormatter.Format(_lastState, _selectedHistoryCombatIndex, L);
            if (string.IsNullOrWhiteSpace(report))
                return;

            DisplayServer.ClipboardSet(report);
            ShowCopyFeedback();
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] copy report failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void ShowCopyFeedback()
    {
        if (_copyBtn == null || !IsInstanceValid(_copyBtn))
            return;

        int version = ++_copyFeedbackVersion;
        _copyBtn.Text = "\u2713";
        _copyBtn.TooltipText = L("COPIED");
        StyleButtonState(_copyBtn, true, Green);

        SceneTreeTimer? timer = GetTree()?.CreateTimer(1.2);
        if (timer != null)
        {
            timer.Timeout += () =>
            {
                if (!IsInstanceValid(this) || _copyFeedbackVersion != version)
                    return;
                RestoreCopyButton();
            };
        }
    }

    private void RestoreCopyButton()
    {
        if (_copyBtn == null || !IsInstanceValid(_copyBtn))
            return;

        _copyBtn.Text = "Cp";
        _copyBtn.TooltipText = L("COPY_REPORT");
        StyleButtonState(_copyBtn, false, Green);
    }

    private void OnPreviousView()
    {
        if (_lastState == null)
            return;

        IReadOnlyList<CombatDamageSnapshot> history = _lastState.CombatHistory;
        int pos = FindSelectedHistoryPosition(history);
        if (pos < 0)
            return;

        _selectedHistoryCombatIndex = pos == 0 ? null : history[pos - 1].CombatIndex;
        _resetScrollPending = true;
        RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
    }

    private void OnNextView()
    {
        if (_lastState == null)
            return;

        IReadOnlyList<CombatDamageSnapshot> history = _lastState.CombatHistory;
        if (history.Count == 0)
            return;

        int pos = FindSelectedHistoryPosition(history);
        if (pos >= history.Count - 1)
            return;

        _selectedHistoryCombatIndex = history[pos + 1].CombatIndex;
        _resetScrollPending = true;
        RenderCurrentView(isLiveUpdate: false, maxFlashPlayers: null);
    }

    private int FindSelectedHistoryPosition(IReadOnlyList<CombatDamageSnapshot> history)
    {
        if (!_selectedHistoryCombatIndex.HasValue)
            return -1;

        int target = _selectedHistoryCombatIndex.Value;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].CombatIndex == target)
                return i;
        }
        return -1;
    }

    // ── State update ───────────────────────────────────────────

    private void OnChanged(OverlayState s)
    {
        if (!IsInsideTree())
            return;
        Callable.From(() => ApplyState(s, isLiveUpdate: true)).CallDeferred();
    }

    private void ApplyState(OverlayState s, bool isLiveUpdate = true)
    {
        if (_rows == null || _emptyLabel == null)
            return;

        try
        {
            bool runChanged = !string.Equals(_lastAppliedRunToken, s.RunToken, StringComparison.Ordinal);
            bool resetScroll = runChanged || _lastAppliedCombatIndex != s.CombatIndex;

            if (runChanged)
            {
                _selectedHistoryCombatIndex = null;
                _knownMaxByPlayer.Clear();
                _flashUntilMsecByPlayer.Clear();
                _hasSeededRunMax = false;
            }

            _lastAppliedRunToken = s.RunToken;
            _lastAppliedCombatIndex = s.CombatIndex;
            _resetScrollPending |= resetScroll;
            _lastState = s;

            HashSet<ulong>? maxFlashPlayers = UpdateKnownMaxHits(s, isLiveUpdate && !runChanged);
            RenderCurrentView(isLiveUpdate, maxFlashPlayers);
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] ApplyState failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private HashSet<ulong>? UpdateKnownMaxHits(OverlayState s, bool canFlash)
    {
        if (!_hasSeededRunMax)
        {
            for (int i = 0; i < s.Players.Count; i++)
                _knownMaxByPlayer[s.Players[i].PlayerKey] = s.Players[i].MaxHitDamage;

            if (s.Players.Count > 0)
                _hasSeededRunMax = true;
            return null;
        }

        HashSet<ulong>? flashed = null;
        for (int i = 0; i < s.Players.Count; i++)
        {
            PlayerDamageSnapshot p = s.Players[i];
            bool hadPrev = _knownMaxByPlayer.TryGetValue(p.PlayerKey, out decimal prevMax);
            if (canFlash && p.MaxHitDamage > prevMax && (prevMax > 0m || p.LastDamage > 0m))
            {
                flashed ??= new HashSet<ulong>();
                flashed.Add(p.PlayerKey);
                _flashUntilMsecByPlayer[p.PlayerKey] = Time.GetTicksMsec() + 600UL;
            }
            if (!hadPrev || p.MaxHitDamage > prevMax)
                _knownMaxByPlayer[p.PlayerKey] = p.MaxHitDamage;
        }
        return flashed;
    }

    private void RenderCurrentView(bool isLiveUpdate, HashSet<ulong>? maxFlashPlayers)
    {
        if (_rows == null || _emptyLabel == null || _lastState == null)
            return;

        try
        {
            IReadOnlyList<CombatDamageSnapshot> history = _lastState.CombatHistory;
            int historyPos = FindSelectedHistoryPosition(history);
            if (_selectedHistoryCombatIndex.HasValue && historyPos < 0)
                _selectedHistoryCombatIndex = null;

            bool isHistory = historyPos >= 0;
            CombatDamageSnapshot? selectedCombat = isHistory ? history[historyPos] : null;
            IReadOnlyList<PlayerDamageSnapshot> sourcePlayers = isHistory
                ? selectedCombat!.Players
                : _lastState.Players;

            UpdateScopeControls(history, historyPos, selectedCombat);

            List<PlayerDamageSnapshot> orderedPlayers = SortPlayers(sourcePlayers, isHistory);
            _playerCount = orderedPlayers.Count;

            if (_columnHeadings != null) _columnHeadings.Visible = _ui.Expanded;
            if (_separator != null) _separator.Visible = _ui.Expanded;

            foreach (Node c in _rows.GetChildren())
            {
                _rows.RemoveChild(c);
                c.QueueFree();
            }

            _emptyLabel.Visible = orderedPlayers.Count == 0;

            decimal teamScopeTotal = 0m;
            for (int i = 0; i < orderedPlayers.Count; i++)
                teamScopeTotal += isHistory ? orderedPlayers[i].CombatDamage : orderedPlayers[i].TotalDamage;

            ulong nowMsec = Time.GetTicksMsec();
            for (int i = 0; i < orderedPlayers.Count; i++)
            {
                PlayerDamageSnapshot snap = orderedPlayers[i];
                decimal playerScopeDamage = isHistory ? snap.CombatDamage : snap.TotalDamage;
                float ratio = teamScopeTotal > 0m ? (float)(playerScopeDamage / teamScopeTotal) : 0f;
                bool flashGold = isLiveUpdate && !isHistory && (
                    (maxFlashPlayers != null && maxFlashPlayers.Contains(snap.PlayerKey))
                    || (_flashUntilMsecByPlayer.TryGetValue(snap.PlayerKey, out ulong untilMsec) && nowMsec < untilMsec));

                try
                {
                    _rows.AddChild(_ui.Expanded
                        ? CreateRow(snap, ratio, isHistory, isLiveUpdate && !isHistory, flashGold)
                        : CreateCompactRow(snap, ratio, isHistory, isLiveUpdate && !isHistory, flashGold));
                }
                catch (Exception ex)
                {
                    GD.Print($"[DamageTracker] row failed for {snap.DisplayName}: {ex.GetType().Name} {ex.Message}");
                    _rows.AddChild(FallbackRow(snap, isHistory));
                }
            }

            RefreshChrome();
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] RenderCurrentView failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void UpdateScopeControls(
        IReadOnlyList<CombatDamageSnapshot> history,
        int historyPos,
        CombatDamageSnapshot? selectedCombat)
    {
        if (_prevViewBtn != null && IsInstanceValid(_prevViewBtn))
            _prevViewBtn.Disabled = historyPos < 0;

        if (_nextViewBtn != null && IsInstanceValid(_nextViewBtn))
            _nextViewBtn.Disabled = history.Count == 0 || historyPos >= history.Count - 1;

        if (_scopeLabel != null && IsInstanceValid(_scopeLabel))
        {
            string label;
            if (selectedCombat == null)
            {
                label = L("RUN");
                _scopeLabel.AddThemeColorOverride("font_color", Gray);
            }
            else
            {
                string combatTitle = LF("HISTORY_COMBAT", selectedCombat.CombatIndex);
                label = !string.IsNullOrWhiteSpace(selectedCombat.EncounterName)
                    ? $"{combatTitle} \u00b7 {selectedCombat.EncounterName}"
                    : combatTitle;
                _scopeLabel.AddThemeColorOverride("font_color", Cyan);
            }
            _scopeLabel.Text = label;
            _scopeLabel.TooltipText = label;
        }
    }

    private Control FallbackRow(PlayerDamageSnapshot snap, bool isHistory)
    {
        decimal dmg = isHistory ? snap.CombatDamage : snap.TotalDamage;
        Label label = MakeLabel($"{snap.DisplayName}   {FormatCell(dmg)}", FS(13f), White);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.CustomMinimumSize = new Vector2(0f, RowH);
        return label;
    }

    // ── Drag, resize, scale & hotkeys ──────────────────────────

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key
            && key.Pressed
            && !key.Echo
            && !key.CtrlPressed
            && !key.AltPressed
            && !key.ShiftPressed
            && !key.MetaPressed)
        {
            if (key.Keycode == Key.F8)
            {
                ToggleMasterVisible();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.F9)
            {
                ToggleLocked();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (_root == null || !_masterVisible || _hiddenToSide || _ui.Locked)
            return;

        if (@event is InputEventMouseButton mb
            && mb.Pressed
            && mb.CtrlPressed
            && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown
            && new Rect2(_root.GlobalPosition, _root.Size).HasPoint(GetViewport().GetMousePosition()))
        {
            ChangeScale(mb.ButtonIndex == MouseButton.WheelUp ? 0.1f : -0.1f);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && !mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (_isDragging || _resizing)
                SaveSettings();
            _isDragging = false;
            _resizing = false;
        }
    }

    private void OnGuiInput(InputEvent @event)
    {
        if (_root == null || _ui.Locked || !_masterVisible)
            return;

        switch (@event)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                if (mb.Pressed)
                {
                    _isDragging = true;
                    _dragOffset = GetViewport().GetMousePosition() - _root.Position;
                    GetViewport().SetInputAsHandled();
                }
                else if (_isDragging)
                {
                    _isDragging = false;
                    _ui.X = _root.Position.X;
                    _ui.Y = _root.Position.Y;
                    SaveSettings();
                }
                break;

            case InputEventMouseMotion when _isDragging:
                ClampPos(GetViewport().GetMousePosition() - _dragOffset);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void OnGripInput(InputEvent @event)
    {
        if (_root == null || _ui.Locked || !_masterVisible)
            return;

        switch (@event)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                if (mb.Pressed)
                {
                    if (mb.DoubleClick)
                    {
                        ResetLayoutDefaults();
                        return;
                    }
                    _resizing = true;
                    _resizeStartMouse = GetViewport().GetMousePosition();
                    _resizeStartSize = _root.Size;
                    GetViewport().SetInputAsHandled();
                }
                else if (_resizing)
                {
                    _resizing = false;
                    SaveSettings();
                }
                break;

            case InputEventMouseMotion when _resizing:
                ResizeFromMouse(GetViewport().GetMousePosition());
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void ResizeFromMouse(Vector2 mouse)
    {
        if (_root == null)
            return;

        Vector2 delta = mouse - _resizeStartMouse;
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float maxW = Mathf.Max(MinPanelWidth, vp.X - ViewportMargin * 2f);
        float maxH = Mathf.Max(MinPanelHeight, vp.Y - ViewportMargin * 2f);
        float width = Mathf.Clamp(_resizeStartSize.X + delta.X, MinPanelWidth, maxW);
        float height = Mathf.Clamp(_resizeStartSize.Y + delta.Y, MinPanelHeight, maxH);

        if (_ui.Expanded)
            _ui.Width = width;
        else
            _ui.CompactWidth = width;
        _ui.Height = height;

        ApplyLayout();
    }

    private void ChangeScale(float delta)
    {
        float next = Mathf.Clamp(Mathf.Round((_ui.Scale + delta) * 100f) / 100f, MinUiScale, MaxUiScale);
        if (Mathf.IsEqualApprox(next, _ui.Scale))
            return;

        _ui.Scale = next;
        SaveSettings();
        Callable.From(RebuildUi).CallDeferred();
    }

    private void ResetLayoutDefaults()
    {
        _ui.Scale = 1f;
        _ui.Width = 0f;
        _ui.CompactWidth = 0f;
        _ui.Height = 0f;
        SaveSettings();
        Callable.From(RebuildUi).CallDeferred();
    }

    // ── Layout, Chrome, Lock & Appearance ──────────────────────

    private void ApplyLayout()
    {
        if (_root == null || _bodyScroll == null)
            return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float maxPanelH = Mathf.Max(MinPanelHeight, vp.Y - ViewportMargin * 2f);
        float minPanelW = MinPanelWidth;
        float maxPanelW = Mathf.Max(minPanelW, vp.X - ViewportMargin * 2f);
        float width = Mathf.Clamp(PreferredWidth, minPanelW, maxPanelW);

        float chromeH = ChromeHeight;
        float bodyContentH = BodyContentHeight(_playerCount);
        float naturalH = chromeH + bodyContentH;

        float panelH;
        if (_ui.Height > 0f)
        {
            float minUserH = chromeH + Mathf.Min(RowH, Mathf.Max(1f, bodyContentH));
            panelH = Mathf.Clamp(_ui.Height, minUserH, maxPanelH);
        }
        else
        {
            panelH = Mathf.Min(naturalH, maxPanelH);
        }

        float bodyH = Mathf.Max(RowH * 0.8f, panelH - chromeH);
        panelH = chromeH + bodyH;

        bool needsVScroll = bodyContentH > bodyH + 0.5f;
        if (_bodyPad != null && IsInstanceValid(_bodyPad))
            _bodyPad.AddThemeConstantOverride("margin_right", needsVScroll ? 0 : Mathf.RoundToInt(ScrollbarGutter));

        _bodyScroll.CustomMinimumSize = new Vector2(0f, bodyH);
        _root.CustomMinimumSize = new Vector2(width, panelH);
        _root.Size = new Vector2(width, panelH);
        _panelWidth = width;
        _panelHeight = panelH;

        if (_grip != null)
        {
            float gripSize = GripSize;
            _grip.Size = new Vector2(gripSize, gripSize);
            _grip.Position = _root.Position + new Vector2(width - gripSize - R(3f), panelH - gripSize - R(3f));
            _grip.Visible = _masterVisible && !_hiddenToSide && !_ui.Locked;
            _grip.QueueRedraw();
        }

        if (_resetScrollPending)
        {
            _bodyScroll.ScrollVertical = 0;
            _resetScrollPending = false;
        }

        ClampPos(_root.Position);
    }

    private void ClampPos(Vector2 p)
    {
        if (_root == null)
            return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        Vector2 sz = GetCurrentRootSize();
        _root.Position = new Vector2(
            Mathf.Clamp(p.X, 0f, Mathf.Max(0f, vp.X - sz.X)),
            Mathf.Clamp(p.Y, 0f, Mathf.Max(0f, vp.Y - sz.Y)));

        if (_grip != null)
            _grip.Position = _root.Position + new Vector2(sz.X - GripSize - R(3f), sz.Y - GripSize - R(3f));

        UpdateSideTabPosition();
    }

    private Vector2 GetCurrentRootSize()
    {
        float w = _panelWidth > 0f ? _panelWidth : PreferredWidth;
        float h = _panelHeight > 0f
            ? _panelHeight
            : (_root != null && _root.Size.Y > 0f ? _root.Size.Y : MinPanelHeight);
        return new Vector2(w, h);
    }

    private void RefreshChrome()
    {
        if (_root == null)
            return;

        if (_toggleBtn != null)
            _toggleBtn.Text = _ui.Expanded ? "\u25bc" : "\u25b6";

        if (_hideBtn != null)
            _hideBtn.Text = _hiddenSide == HiddenDockSide.Left ? "\u00ab" : "\u00bb";

        bool showRoot = _masterVisible && !_hiddenToSide;
        _root.Visible = showRoot;
        if (_contentPad != null)
            _contentPad.Visible = showRoot;
        if (_grip != null)
            _grip.Visible = showRoot && !_ui.Locked;

        if (_sideTab != null)
        {
            _sideTab.Visible = _masterVisible && _hiddenToSide;
            _sideTab.Text = _hiddenSide == HiddenDockSide.Left ? $"\u00bb {L("TITLE")}" : $"{L("TITLE")} \u00ab";
        }

        ApplyAppearance();
        ApplyLockState();
        UpdateSideTabPosition();

        if (showRoot)
            ApplyLayout();
    }

    private void ApplyAppearance()
    {
        if (_rootStyle != null && IsInstanceValid(_rootStyle))
            _rootStyle.BgColor = new Color(0f, 0f, 0f, _ui.BackgroundOpacity);

        if (_opacityBtn != null && IsInstanceValid(_opacityBtn))
            _opacityBtn.TooltipText = FormatOpacityTooltip();

        if (_autoDimBtn != null && IsInstanceValid(_autoDimBtn))
        {
            _autoDimBtn.TooltipText = _ui.AutoDim ? L("AUTO_DIM_OFF") : L("AUTO_DIM_ON");
            StyleButtonState(_autoDimBtn, _ui.AutoDim, Cyan);
        }

        if (_lockBtn != null && IsInstanceValid(_lockBtn))
        {
            _lockBtn.TooltipText = _ui.Locked ? L("UNLOCK") : L("LOCK");
            StyleButtonState(_lockBtn, _ui.Locked, Yellow);
        }

        bool inCombat = _lastState?.CombatActive ?? false;
        float alpha = (_ui.AutoDim && !inCombat) ? AutoDimAlpha : 1f;
        Color mod = new(1f, 1f, 1f, alpha);
        if (_root != null && IsInstanceValid(_root))
            _root.Modulate = mod;
        if (_grip != null && IsInstanceValid(_grip))
            _grip.Modulate = mod;
    }

    private void ApplyLockState()
    {
        if (_root != null && IsInstanceValid(_root))
            ApplyMouseFilterRecursive(_root, _ui.Locked);

        if (_sideTab != null && IsInstanceValid(_sideTab))
            ApplyMouseFilterRecursive(_sideTab, _ui.Locked);

        if (_grip != null && IsInstanceValid(_grip))
            _grip.Visible = _masterVisible && !_hiddenToSide && !_ui.Locked;
    }

    private static void ApplyMouseFilterRecursive(Node node, bool locked)
    {
        if (node is Control ctrl)
        {
            if (locked)
            {
                if (!ctrl.HasMeta(OriginalMouseFilterMeta))
                    ctrl.SetMeta(OriginalMouseFilterMeta, (int)ctrl.MouseFilter);
                ctrl.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
            else if (ctrl.HasMeta(OriginalMouseFilterMeta))
            {
                ctrl.MouseFilter = (Control.MouseFilterEnum)ctrl.GetMeta(OriginalMouseFilterMeta).AsInt32();
            }
        }

        int childCount = node.GetChildCount(includeInternal: true);
        for (int i = 0; i < childCount; i++)
            ApplyMouseFilterRecursive(node.GetChild(i, includeInternal: true), locked);
    }

    private HiddenDockSide ResolveHiddenSide()
    {
        if (_root == null)
            return HiddenDockSide.Right;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float centerX = _root.Position.X + GetCurrentRootSize().X * 0.5f;
        return centerX < vp.X * 0.5f ? HiddenDockSide.Left : HiddenDockSide.Right;
    }

    private void SnapRootIntoViewFromSide()
    {
        if (_root == null)
            return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float width = GetCurrentRootSize().X;
        float x = _hiddenSide == HiddenDockSide.Left
            ? ViewportMargin
            : Mathf.Max(ViewportMargin, vp.X - width - ViewportMargin);
        ClampPos(new Vector2(x, _root.Position.Y));
    }

    private void UpdateSideTabPosition()
    {
        if (_sideTab == null || _root == null)
            return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        float y = Mathf.Clamp(_root.Position.Y + 10f, 0f, Mathf.Max(0f, vp.Y - SideTabHeight));
        float x = _hiddenSide == HiddenDockSide.Left ? 0f : Mathf.Max(0f, vp.X - SideTabWidth);
        _sideTab.Position = new Vector2(x, y);
        _sideTab.Size = new Vector2(SideTabWidth, SideTabHeight);
    }

    // ── Icon loader ────────────────────────────────────────────

    private static Texture2D? LoadIcon(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string key = name.Trim().ToLowerInvariant().Replace(' ', '_');
        if (IconCache.TryGetValue(key, out Texture2D? cached))
        {
            if (IsResourceAlive(cached))
                return cached;
            IconCache.Remove(key);
        }

        try
        {
            string path = $"res://images/ui/top_panel/character_icon_{key}.png";
            if (!ResourceLoader.Exists(path))
            {
                IconCache[key] = null;
                return null;
            }

            Texture2D? loaded = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            if (!IsResourceAlive(loaded))
                return null;

            IconCache[key] = loaded;
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    // ── Tiny helpers ───────────────────────────────────────────

    private static Label MakeLabel(string text, int size, Color color)
    {
        Label l = new()
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center
        };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private Label HeadLabel(string text, bool expand, float width = 0f)
    {
        Label l = MakeLabel(text, FS(10f), DimGray);
        if (expand)
        {
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            l.ClipText = true;
            l.CustomMinimumSize = new Vector2(NameMin, 0f);
        }
        else
        {
            l.CustomMinimumSize = new Vector2(width, 0f);
            l.HorizontalAlignment = HorizontalAlignment.Right;
        }
        return l;
    }

    private static Label StatCell(string val, Color color, float width, int fontSize)
    {
        Label l = MakeLabel(val, fontSize, color);
        l.CustomMinimumSize = new Vector2(width, 0f);
        l.HorizontalAlignment = HorizontalAlignment.Right;
        l.ClipText = true;
        return l;
    }

    private static string FormatCell(decimal value)
    {
        decimal abs = Math.Abs(value);
        if (abs >= 1_000_000m)
            return $"{(value / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture)}M";
        if (abs >= 10_000m)
            return $"{(value / 1_000m).ToString("0.#", CultureInfo.InvariantCulture)}k";
        return RunDamageTrackerService.Format(value);
    }

    private Control Spacer(float w)
    {
        return new Control
        {
            CustomMinimumSize = new Vector2(w, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
    }

    private static Control HLine()
    {
        return new ColorRect
        {
            Color = Border,
            CustomMinimumSize = new Vector2(0f, 1f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
    }

    private static Color GetCharTheme(string? charName)
    {
        if (!string.IsNullOrWhiteSpace(charName))
        {
            string key = charName.Trim().ToLowerInvariant().Replace(' ', '_');
            if (CharTheme.TryGetValue(key, out Color c))
                return c;
        }
        return Gray;
    }

    private static string Initials(PlayerDamageSnapshot s)
    {
        string src = !string.IsNullOrWhiteSpace(s.CharacterName) ? s.CharacterName : s.DisplayName;
        string[] p = src.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return "?";
        if (p.Length == 1) return p[0].Length >= 2 ? p[0][..2].ToUpperInvariant() : p[0].ToUpperInvariant();
        return string.Concat(p[0][0], p[1][0]).ToUpperInvariant();
    }

    public override void _Process(double _)
    {
        if (_pendingCreate)
        {
            _pendingCreate = false;
            EnsureCreated();
        }

        if (_root == null || !IsInstanceValid(_root))
            return;

        Vector2 vp = GetViewport().GetVisibleRect().Size;
        if (!Mathf.IsEqualApprox(vp.X, _lastViewportSize.X) || !Mathf.IsEqualApprox(vp.Y, _lastViewportSize.Y))
        {
            _lastViewportSize = vp;
            if (!_masterVisible)
                return;

            if (_hiddenToSide)
            {
                UpdateSideTabPosition();
                ClampPos(_root.Position);
            }
            else
            {
                ApplyLayout();
            }
        }
    }

    public static void EnsureCreated()
    {
        if (IsInstanceValid(_instance))
            return;

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            _pendingCreate = true;
            return;
        }

        _instance = new DamageTrackerOverlay();
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
    }

    private sealed partial class GripNode : Control
    {
        public override void _Draw()
        {
            float x = Size.X;
            float y = Size.Y;
            if (x < 6f || y < 6f)
                return;

            Color color = new("7A8698");
            float pad = Mathf.Max(2f, x * 0.16f);
            for (int i = 0; i < 3; i++)
            {
                float offset = i * x * 0.26f;
                DrawLine(new Vector2(x - pad - offset, y - pad), new Vector2(x - pad, y - pad - offset), color, 1.4f);
            }
        }

        public override void _Notification(int what)
        {
            if (what == (int)NotificationResized)
                QueueRedraw();
        }
    }

    private enum HiddenDockSide
    {
        Left,
        Right
    }

    private enum SortColumn
    {
        Pct,
        Total,
        Combat,
        Turn,
        Last,
        Max
    }
}
