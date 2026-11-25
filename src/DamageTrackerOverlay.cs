using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;

namespace DamageTracker;

/// <summary>
/// Flat, table-style damage overlay. Supports dragging, resizing, Ctrl+Wheel scaling,
/// persistent UI layout settings, and resilient avatar/row rendering.
/// </summary>
public sealed partial class DamageTrackerOverlay : CanvasLayer
{
    private const float SideTabWidth = 86f;
    private const float SideTabHeight = 32f;
    private const float ViewportMargin = 12f;
    private const float MinPanelHeight = 64f;
    private const float MinUiScale = 0.7f;
    private const float MaxUiScale = 2.5f;
    private const float DefaultExpandedWidth = 470f;
    private const float DefaultCompactWidth = 270f;
    private const float Unset = -99999f;
    private const string SettingsResPath = "user://damage_tracker_ui.json";

    // ── Localization ───────────────────────────────────────────

    private static Dictionary<string, string>? _locStrings;
    private static string _lang = "eng";

    private static Dictionary<string, string> LocStrings => _locStrings ??= LoadLocStrings();

    private static Dictionary<string, string> LoadLocStrings()
    {
        string lang = _lang = ResolveGameLanguage();

        // Use res:// path - requires .pck file to be loaded
        string resPath = $"res://assets/localization/{lang}/damage_tracker.json";
        if (!Godot.FileAccess.FileExists(resPath))
        {
            if (lang != "eng")
                resPath = "res://assets/localization/eng/damage_tracker.json";
            else
            {
                GD.Print("[DamageTracker] LoadLocStrings: localization file not found!");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        using Godot.FileAccess? file = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        string json = file?.GetAsText() ?? "{}";
        GD.Print($"[DamageTracker] LoadLocStrings: loaded json from {resPath}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    private static string Hint(string zh, string en) => _lang == "zhs" ? zh : en;

    // ── Palette ────────────────────────────────────────────────

    private static readonly Color White = new("FFFFFF");
    private static readonly Color Gray = new("A0A8B4");
    private static readonly Color DimGray = new("687480");
    private static readonly Color Green = new("4ADE80");
    private static readonly Color Red = new("F87171");
    private static readonly Color Yellow = new("FACC15");
    private static readonly Color Cyan = new("22D3EE");
    private static readonly Color BgDark = new("000000B0");
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
    }

    private readonly UiSettings _ui = new();

    private PanelContainer? _root;
    private MarginContainer? _contentPad;
    private ScrollContainer? _bodyScroll;
    private VBoxContainer? _bodyContent;
    private VBoxContainer? _rows;
    private Label? _emptyLabel;
    private Control? _columnHeadings;
    private Control? _separator;
    private Button? _toggleBtn;
    private Button? _hideBtn;
    private Button? _sideTab;
    private GripNode? _grip;

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

    // ── Scaled layout metrics ──────────────────────────────────

    private float R(float v) => Mathf.Round(v * _ui.Scale);
    private int FS(float v) => Mathf.Max(8, Mathf.RoundToInt(v * _ui.Scale));

    private float PadX => R(10f);
    private float PadY => R(8f);
    private float Gap => R(4f);
    private float HeaderH => R(24f);
    private float HeadingH => R(15f);
    private float RowH => R(_ui.Expanded ? 42f : 30f);
    private float RowGap => R(2f);
    private float RowPadX => R(6f);
    private float AvatarSize => R(_ui.Expanded ? 32f : 24f);
    private float AvatarGap => R(_ui.Expanded ? 8f : 6f);
    private float NameMin => R(60f);
    private float ColPct => R(38f);
    private float ColTotal => R(62f);
    private float ColCombat => R(62f);
    private float ColLast => R(52f);
    private float ColMax => R(52f);
    private float GripSize => R(18f);

    private float StatsWidth => _ui.Expanded
        ? ColPct + ColTotal + ColCombat + ColLast + ColMax
        : ColTotal;

    private float MinPanelWidth =>
        PadX * 2f + RowPadX * 2f + AvatarSize + AvatarGap + NameMin + StatsWidth;

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
        PadY * 2f + HeaderH + Gap + (_ui.Expanded ? HeadingH + Gap + 1f + Gap : 0f);

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
                }
            }

            if (settings.Scale <= 0f)
                settings.Scale = 1f;
            settings.Scale = Mathf.Clamp(settings.Scale, MinUiScale, MaxUiScale);
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

        BuildUi();
        ApplyState(RunDamageTrackerService.BuildOverlayState());
    }

    private void BuildUi()
    {
        _root = new PanelContainer
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = ResolveStartPosition(),
            CustomMinimumSize = new Vector2(PreferredWidth, 0f)
        };
        _root.GuiInput += OnGuiInput;
        _root.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = BgDark,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        });

        _contentPad = new MarginContainer();
        _contentPad.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(PadX));
        _contentPad.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(PadX));
        _contentPad.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(PadY));
        _contentPad.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(PadY));

        VBoxContainer col = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", Mathf.RoundToInt(Gap));

        // ── Header row: title + toggle + hide ──
        col.AddChild(BuildHeader());

        // ── Column headings ──
        _columnHeadings = BuildColumnHeadings();
        col.AddChild(_columnHeadings);

        // ── Thin separator ──
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

        _bodyScroll.AddChild(_bodyContent);
        col.AddChild(_bodyScroll);

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
            TooltipText = Hint(
                "拖动缩放（双击恢复默认）\nCtrl+滚轮 调字号 · 拖动面板移动",
                "Drag to resize (double-click resets)\nCtrl+Wheel scales - drag panel to move")
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

        BuildUi();
        if (_lastState != null)
            ApplyState(_lastState);
        else
            ApplyLayout();
    }

    // ── Header ─────────────────────────────────────────────────

    private Control BuildHeader()
    {
        HBoxContainer h = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", Mathf.RoundToInt(R(6f)));

        Label title = MakeLabel(L("TITLE"), FS(15f), White);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.ClipText = true;
        title.TooltipText = Hint(
            "拖动移动 · 右下角拖动缩放 · Ctrl+滚轮 调字号",
            "Drag to move - drag bottom-right to resize - Ctrl+Wheel to scale");

        _toggleBtn = MakeCaptionButton("\u25bc", OnToggle, Hint("展开/收起", "Expand / collapse"));
        _hideBtn = MakeCaptionButton("\u00bb", OnHideToSide, Hint("收起到屏幕边缘", "Hide to screen edge"));

        h.AddChild(title);
        h.AddChild(_toggleBtn);
        h.AddChild(_hideBtn);
        return h;
    }

    private Button MakeCaptionButton(string text, Action onPressed, string tooltip)
    {
        Button btn = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(R(24f), R(24f)),
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = tooltip
        };
        btn.AddThemeFontSizeOverride("font_size", FS(12f));
        btn.AddThemeStyleboxOverride("normal", RoundedBox("FFFFFF10"));
        btn.AddThemeStyleboxOverride("hover", RoundedBox("FFFFFF20"));
        btn.AddThemeStyleboxOverride("pressed", RoundedBox("FFFFFF30"));
        btn.AddThemeColorOverride("font_color", Gray);
        btn.Pressed += onPressed;
        return btn;
    }

    private static StyleBoxFlat RoundedBox(string bg, int radius = 3)
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(bg),
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

    // ── Column headings ────────────────────────────────────────

    private Control BuildColumnHeadings()
    {
        HBoxContainer h = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", 0);

        h.AddChild(Spacer(AvatarSize));
        h.AddChild(Spacer(AvatarGap));
        h.AddChild(HeadLabel(L("PLAYER"), true));
        if (_ui.Expanded)
        {
            h.AddChild(HeadLabel(L("PCT"), false, ColPct));
            h.AddChild(HeadLabel(L("TOTAL"), false, ColTotal));
            h.AddChild(HeadLabel(L("COMBAT"), false, ColCombat));
            h.AddChild(HeadLabel(L("LAST"), false, ColLast));
            h.AddChild(HeadLabel(L("MAX"), false, ColMax));
        }
        else
        {
            h.AddChild(HeadLabel(L("TOTAL"), false, ColTotal));
        }
        return h;
    }

    // ── Player rows ────────────────────────────────────────────

    private Control CreateRow(PlayerDamageSnapshot snap, float ratio)
    {
        bool active = snap.IsActive;
        Color theme = GetCharTheme(snap.CharacterName);

        PanelContainer card = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, RowH),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = RowTooltip(snap, ratio)
        };
        card.AddThemeStyleboxOverride("panel", CardBox(theme, active));

        MarginContainer mp = new();
        mp.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(Mathf.Max(2f, RowH * 0.1f)));
        mp.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(Mathf.Max(2f, RowH * 0.1f)));

        HBoxContainer row = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 0);

        row.AddChild(BuildAvatar(snap, Mathf.RoundToInt(AvatarSize)));
        row.AddChild(Spacer(AvatarGap));

        VBoxContainer nameCol = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(NameMin, 0f)
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

        int pct = (int)(Mathf.Clamp(ratio, 0f, 1f) * 100f);
        row.AddChild(StatCell($"{pct}%", new Color(theme, 0.9f), ColPct, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.TotalDamage), Yellow, ColTotal, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.CombatDamage), Cyan, ColCombat, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.LastDamage), Red, ColLast, FS(13f)));
        row.AddChild(StatCell(FormatCell(snap.MaxHitDamage), new Color("FF79C6"), ColMax, FS(13f)));

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

        VBoxContainer cardContent = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        cardContent.AddThemeConstantOverride("separation", 0);
        cardContent.AddChild(mp);
        cardContent.AddChild(barBg);

        card.AddChild(cardContent);
        FlashOnUpdate(card);
        return card;
    }

    private Control CreateCompactRow(PlayerDamageSnapshot snap, float ratio)
    {
        Color theme = GetCharTheme(snap.CharacterName);
        bool active = snap.IsActive;

        PanelContainer card = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, RowH),
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = RowTooltip(snap, ratio)
        };
        card.AddThemeStyleboxOverride("panel", CardBox(theme, active));

        MarginContainer mp = new();
        mp.AddThemeConstantOverride("margin_left", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_right", Mathf.RoundToInt(RowPadX));
        mp.AddThemeConstantOverride("margin_top", Mathf.RoundToInt(R(2f)));
        mp.AddThemeConstantOverride("margin_bottom", Mathf.RoundToInt(R(2f)));

        HBoxContainer row = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 0);

        row.AddChild(BuildAvatar(snap, Mathf.RoundToInt(R(24f))));
        row.AddChild(Spacer(AvatarGap));

        Label nameLabel = MakeLabel(snap.DisplayName, FS(12f), active ? theme.Lightened(0.3f) : White);
        nameLabel.ClipText = true;
        nameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.CustomMinimumSize = new Vector2(NameMin, 0f);
        row.AddChild(nameLabel);

        row.AddChild(StatCell(FormatCell(snap.TotalDamage), Yellow, ColTotal, FS(13f)));

        mp.AddChild(row);
        card.AddChild(mp);
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
        card.Modulate = new Color(1f, 1f, 1f, 0.3f);
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

    private static string RowTooltip(PlayerDamageSnapshot snap, float ratio)
    {
        string pct = $"{Mathf.RoundToInt(Mathf.Clamp(ratio, 0f, 1f) * 100f)}%";
        return $"{snap.DisplayName} \u00b7 {snap.CharacterName} \u00b7 {pct}\n"
             + $"{L("TOTAL")}: {RunDamageTrackerService.Format(snap.TotalDamage)}    {L("COMBAT")}: {RunDamageTrackerService.Format(snap.CombatDamage)}\n"
             + $"{L("LAST")}: {RunDamageTrackerService.Format(snap.LastDamage)}    {L("MAX")}: {RunDamageTrackerService.Format(snap.MaxHitDamage)}";
    }

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

        CenterContainer cc = new();
        cc.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        Label ini = MakeLabel(Initials(snap), Mathf.Max(8, Mathf.RoundToInt(size * 0.38f)), White);
        cc.AddChild(ini);
        frame.AddChild(cc);
        return frame;
    }

    // ── Toggle expand / collapse / side hide ───────────────────

    private void OnToggle()
    {
        _ui.Expanded = !_ui.Expanded;
        SaveSettings();
        RefreshChrome();

        if (_lastState != null)
            ApplyState(_lastState);
        else
            ApplyLayout();
    }

    private void OnHideToSide()
    {
        if (_root == null)
            return;

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
            ApplyState(_lastState);
        else
            ApplyLayout();
    }

    // ── State update ───────────────────────────────────────────

    private void OnChanged(OverlayState s)
    {
        if (!IsInsideTree())
            return;
        Callable.From(() => ApplyState(s)).CallDeferred();
    }

    private void ApplyState(OverlayState s)
    {
        if (_rows == null || _emptyLabel == null)
            return;

        try
        {
            bool resetScroll = !string.Equals(_lastAppliedRunToken, s.RunToken, StringComparison.Ordinal)
                || _lastAppliedCombatIndex != s.CombatIndex;
            _lastAppliedRunToken = s.RunToken;
            _lastAppliedCombatIndex = s.CombatIndex;
            _resetScrollPending |= resetScroll;

            _lastState = s;
            _playerCount = s.Players.Count;

            if (_columnHeadings != null) _columnHeadings.Visible = _ui.Expanded;
            if (_separator != null) _separator.Visible = _ui.Expanded;

            foreach (Node c in _rows.GetChildren())
            {
                _rows.RemoveChild(c);
                c.QueueFree();
            }

            _emptyLabel.Visible = s.Players.Count == 0;

            decimal teamTotal = 0m;
            for (int i = 0; i < s.Players.Count; i++)
                teamTotal += s.Players[i].TotalDamage;

            for (int i = 0; i < s.Players.Count; i++)
            {
                PlayerDamageSnapshot snap = s.Players[i];
                float ratio = teamTotal > 0m ? (float)(snap.TotalDamage / teamTotal) : 0f;
                try
                {
                    _rows.AddChild(_ui.Expanded ? CreateRow(snap, ratio) : CreateCompactRow(snap, ratio));
                }
                catch (Exception ex)
                {
                    GD.Print($"[DamageTracker] row failed for {snap.DisplayName}: {ex.GetType().Name} {ex.Message}");
                    _rows.AddChild(FallbackRow(snap));
                }
            }

            RefreshChrome();
            ApplyLayout();
        }
        catch (Exception ex)
        {
            GD.Print($"[DamageTracker] ApplyState failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private Control FallbackRow(PlayerDamageSnapshot snap)
    {
        Label label = MakeLabel($"{snap.DisplayName}   {FormatCell(snap.TotalDamage)}", FS(13f), White);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.CustomMinimumSize = new Vector2(0f, RowH);
        return label;
    }

    // ── Drag, resize & scale input ─────────────────────────────

    public override void _Input(InputEvent @event)
    {
        if (_root == null || _hiddenToSide)
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
        if (_root == null)
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
        if (_root == null)
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

    // ── Layout & Chrome ────────────────────────────────────────

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

        _bodyScroll.CustomMinimumSize = new Vector2(0f, bodyH);
        _root.CustomMinimumSize = new Vector2(width, panelH);
        _root.Size = new Vector2(width, panelH);
        _panelWidth = width;
        _panelHeight = panelH;

        if (_grip != null)
        {
            float gripSize = GripSize;
            _grip.Size = new Vector2(gripSize, gripSize);
            _grip.Position = _root.Position + new Vector2(width - gripSize - R(2f), panelH - gripSize - R(2f));
            _grip.Visible = !_hiddenToSide;
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
            _grip.Position = _root.Position + new Vector2(sz.X - GripSize - R(2f), sz.Y - GripSize - R(2f));

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

        _root.Visible = !_hiddenToSide;
        if (_contentPad != null)
            _contentPad.Visible = !_hiddenToSide;
        if (_grip != null)
            _grip.Visible = !_hiddenToSide;

        if (_sideTab != null)
        {
            _sideTab.Visible = _hiddenToSide;
            _sideTab.Text = _hiddenSide == HiddenDockSide.Left ? $"\u00bb {L("TITLE")}" : $"{L("TITLE")} \u00ab";
        }

        UpdateSideTabPosition();
        if (!_hiddenToSide)
            ApplyLayout();
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
            if (x < 8f || y < 8f)
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
}
