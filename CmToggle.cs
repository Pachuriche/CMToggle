using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace CmToggle
{
    // Always-visible on-screen button that opens/closes ConfigurationManager via IMGUI
    // clicks (independent of the keyboard backend). All appearance options are exposed
    // as ConfigurationManager settings so they can be edited live in the CM window.
    [BepInPlugin(PluginGuid, "CM Button Toggle", "4.0.0")]
    [BepInDependency(CmGuid)]
    public class CmTogglePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.cmtoggle";
        public const string CmGuid = "com.bepis.bepinex.configurationmanager";

        public enum FontStyleOption { Normal, Bold, Italic, BoldAndItalic }

        private static ManualLogSource Log;

        // ---- Behaviour / text ----
        private ConfigEntry<string> _openText;
        private ConfigEntry<string> _closeText;

        // ---- Size / font ----
        private ConfigEntry<float> _buttonWidth;
        private ConfigEntry<float> _buttonHeight;
        private ConfigEntry<float> _fontSize;
        private ConfigEntry<string> _fontName;
        private ConfigEntry<FontStyleOption> _fontStyle;
        private ConfigEntry<float> _textYOffset;
        private ConfigEntry<float> _textXOffset;

        // ---- Colors ----
        private ConfigEntry<Color> _buttonColor;
        private ConfigEntry<Color> _textColor;

        // ---- Border ----
        private ConfigEntry<float> _borderThickness;
        private ConfigEntry<Color> _borderColor;
        private ConfigEntry<int> _cornerRadius;

        // ---- CM reflection ----
        private object _cm;
        private System.Reflection.PropertyInfo _displayingWindowProp;
        private System.Reflection.FieldInfo _overrideHotkeyField;
        private bool _initialized;

        // ---- Drag state ----
        private Rect _buttonRect = new Rect(20, 20, 130, 30);
        private bool _dragging;
        private Vector2 _dragOffset;

        // ---- Style caches ----
        private GUIStyle _style;
        private Texture2D _bgTex, _borderTex;
        private Color _lastBg, _lastBorderCol;
        private int _lastRadius = -1;
        private Texture2D _roundedBg, _roundedBorder;
        private Dictionary<string, Font> _fontLookup;
        private readonly Dictionary<string, Font> _dynamicFontCache = new Dictionary<string, Font>();

        private void Awake()
        {
            Log = Logger;

            // Enumerate available fonts (OS-installed + already-loaded) for the dropdown.
            BuildFontList();
            string defaultFont = "Default";

            _openText = Config.Bind("1 - Behaviour", "Open label", "Open Config",
                "Button text when the config window is closed.");
            _closeText = Config.Bind("1 - Behaviour", "Close label", "Close Config",
                "Button text when the config window is open.");

            _buttonWidth = Config.Bind("2 - Size & Font", "Width", 130f,
                new ConfigDescription("Button width (px).", new AcceptableValueRange<float>(40f, 500f)));
            _buttonHeight = Config.Bind("2 - Size & Font", "Height", 30f,
                new ConfigDescription("Button height (px).", new AcceptableValueRange<float>(20f, 200f)));
            _fontSize = Config.Bind("2 - Size & Font", "Font size", 14f,
                new ConfigDescription("Label font size.", new AcceptableValueRange<float>(8f, 48f)));
            _fontName = Config.Bind("2 - Size & Font", "Font", defaultFont,
                new ConfigDescription("Font family (dropdown). Includes OS-installed fonts and fonts loaded by the game.",
                    new AcceptableValueList<string>(_fontNames)));
            _fontStyle = Config.Bind("2 - Size & Font", "Font style", FontStyleOption.Normal,
                "Normal / Bold / Italic / Bold+Italic.");
            _textYOffset = Config.Bind("2 - Size & Font", "Text Y offset", 0f,
                new ConfigDescription("Nudge label up/down (px) to fix fonts that sit off-center.",
                    new AcceptableValueRange<float>(-20f, 20f)));
            _textXOffset = Config.Bind("2 - Size & Font", "Text X offset", 0f,
                new ConfigDescription("Nudge label left/right (px).",
                    new AcceptableValueRange<float>(-20f, 20f)));

            _buttonColor = Config.Bind("3 - Colors", "Button color", new Color(0.2f, 0.2f, 0.2f, 0.9f),
                "Button background color (alpha = transparency).");
            _textColor = Config.Bind("3 - Colors", "Text color", Color.white,
                "Label text color.");

            _borderThickness = Config.Bind("4 - Border", "Thickness", 2f,
                new ConfigDescription("Border thickness (px). 0 = no border.", new AcceptableValueRange<float>(0f, 12f)));
            _borderColor = Config.Bind("4 - Border", "Color", new Color(1f, 1f, 1f, 0.8f),
                "Border color (alpha = transparency).");
            _cornerRadius = Config.Bind("4 - Border", "Corner radius", 0,
                new ConfigDescription("Corner rounding (px). 0 = sharp corners.", new AcceptableValueRange<int>(0, 30)));

            _buttonRect.width = _buttonWidth.Value;
            _buttonRect.height = _buttonHeight.Value;

            Log.LogInfo("CMToggle (button v4) Awake. Fonts available: " + _fontNames.Length);
        }

        private string[] _fontNames = new string[0];

        private void BuildFontList()
        {
            var names = new List<string>();

            // 1. OS-installed fonts: these can be created on demand by name via Font.CreateDynamicFontFromOSFont.
            try
            {
                var osFonts = Font.GetOSInstalledFontNames();
                if (osFonts != null) names.AddRange(osFonts);
            }
            catch (Exception ex) { Log.LogWarning("OS font enumeration failed: " + ex.Message); }

            // 2. Fonts already loaded as assets in the game/engine.
            var loaded = Resources.FindObjectsOfTypeAll<Font>()
                .Where(f => f != null && !string.IsNullOrEmpty(f.name))
                .ToArray();

            _fontLookup = new Dictionary<string, Font>();
            foreach (var f in loaded)
            {
                if (!_fontLookup.ContainsKey(f.name)) _fontLookup[f.name] = f;
                if (!names.Contains(f.name)) names.Add(f.name);
            }

            // Always offer a Default option that uses the GUI skin font.
            names.Insert(0, "Default");

            _fontNames = names.Distinct().ToArray();
        }

        private void TryInit()
        {
            if (_initialized) return;
            if (!Chainloader.PluginInfos.TryGetValue(CmGuid, out var info) || info?.Instance == null)
                return;

            _cm = info.Instance;
            var t = _cm.GetType();
            _displayingWindowProp = t.GetProperty("DisplayingWindow");
            _overrideHotkeyField = t.GetField("OverrideHotkey");

            if (_displayingWindowProp == null || _overrideHotkeyField == null)
            {
                Log.LogError("Could not find DisplayingWindow/OverrideHotkey.");
                _initialized = true;
                return;
            }

            _overrideHotkeyField.SetValue(_cm, true);
            _initialized = true;
            Log.LogInfo("CMToggle hooked ConfigurationManager.");
        }

        private void ToggleWindow()
        {
            bool cur = (bool)_displayingWindowProp.GetValue(_cm, null);
            _displayingWindowProp.SetValue(_cm, !cur, null);
        }

        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // Generate a rounded-rectangle texture (white, alpha mask) for the given size/radius.
        private static Texture2D RoundedTex(int w, int h, int radius, Color color)
        {
            w = Mathf.Max(1, w); h = Mathf.Max(1, h);
            radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) / 2);
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool inside = true;
                    if (radius > 0)
                    {
                        // distance check only in the four corner boxes
                        int cx = -1, cy = -1;
                        if (x < radius && y < radius) { cx = radius; cy = radius; }
                        else if (x >= w - radius && y < radius) { cx = w - radius - 1; cy = radius; }
                        else if (x < radius && y >= h - radius) { cx = radius; cy = h - radius - 1; }
                        else if (x >= w - radius && y >= h - radius) { cx = w - radius - 1; cy = h - radius - 1; }

                        if (cx >= 0)
                        {
                            float dx = x - cx, dy = y - cy;
                            inside = (dx * dx + dy * dy) <= (radius * radius);
                        }
                    }
                    pixels[y * w + x] = inside ? color : new Color(0, 0, 0, 0);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        // Returns a Font for the given name: "Default" -> null (GUI skin font),
        // a loaded game font if available, otherwise a dynamic OS font created on demand.
        private Font ResolveFont(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "Default")
                return null;

            if (_fontLookup != null && _fontLookup.TryGetValue(name, out var loaded) && loaded != null)
                return loaded;

            if (_dynamicFontCache.TryGetValue(name, out var cached) && cached != null)
                return cached;

            try
            {
                var f = Font.CreateDynamicFontFromOSFont(name, Mathf.Max(8, Mathf.RoundToInt(_fontSize.Value)));
                _dynamicFontCache[name] = f;
                return f;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not create OS font '" + name + "': " + ex.Message);
                return null;
            }
        }

        private void EnsureStyle()
        {
            if (_style == null) _style = new GUIStyle(GUI.skin.button);

            int w = Mathf.RoundToInt(_buttonWidth.Value);
            int h = Mathf.RoundToInt(_buttonHeight.Value);
            int r = _cornerRadius.Value;

            bool rebuildRounded = (_roundedBg == null) || _lastRadius != r ||
                                   _lastBg != _buttonColor.Value || _lastBorderCol != _borderColor.Value ||
                                   _roundedBg.width != w || _roundedBg.height != h;

            if (r > 0 && rebuildRounded)
            {
                _roundedBg = RoundedTex(w, h, r, Color.white);     // white mask, tinted at draw time
                _roundedBorder = RoundedTex(w, h, r, Color.white);
                _lastRadius = r;
            }

            if (_bgTex == null || _lastBg != _buttonColor.Value)
            {
                _bgTex = SolidTex(_buttonColor.Value);
                _lastBg = _buttonColor.Value;
            }
            if (_borderTex == null || _lastBorderCol != _borderColor.Value)
            {
                _borderTex = SolidTex(_borderColor.Value);
                _lastBorderCol = _borderColor.Value;
            }

            // Make GUI.Button itself transparent; we draw the fill ourselves so rounding works.
            var clear = SolidTex(new Color(0, 0, 0, 0));
            _style.normal.background = clear;
            _style.hover.background = clear;
            _style.active.background = clear;

            _style.normal.textColor = _textColor.Value;
            _style.hover.textColor = _textColor.Value;
            _style.active.textColor = _textColor.Value;
            _style.fontSize = Mathf.RoundToInt(_fontSize.Value);
            _style.alignment = TextAnchor.MiddleCenter;
            _style.contentOffset = new Vector2(_textXOffset.Value, _textYOffset.Value);

            switch (_fontStyle.Value)
            {
                case FontStyleOption.Bold: _style.fontStyle = FontStyle.Bold; break;
                case FontStyleOption.Italic: _style.fontStyle = FontStyle.Italic; break;
                case FontStyleOption.BoldAndItalic: _style.fontStyle = FontStyle.BoldAndItalic; break;
                default: _style.fontStyle = FontStyle.Normal; break;
            }

            _style.font = ResolveFont(_fontName.Value);
        }

        private void DrawRect(Rect rect, Texture2D tex, Color tint)
        {
            var prev = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(rect, tex);
            GUI.color = prev;
        }

        private void OnGUI()
        {
            if (!_initialized)
            {
                TryInit();
                if (!_initialized) return;
            }
            EnsureStyle();

            _buttonRect.width = _buttonWidth.Value;
            _buttonRect.height = _buttonHeight.Value;

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1 && _buttonRect.Contains(e.mousePosition))
            {
                _dragging = true;
                _dragOffset = e.mousePosition - new Vector2(_buttonRect.x, _buttonRect.y);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _dragging)
            {
                _buttonRect.x = e.mousePosition.x - _dragOffset.x;
                _buttonRect.y = e.mousePosition.y - _dragOffset.y;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _dragging)
            {
                _dragging = false;
                e.Use();
            }

            float bt = _borderThickness.Value;
            int r = _cornerRadius.Value;

            if (r > 0 && _roundedBorder != null && _roundedBg != null)
            {
                // Border = larger rounded rect; fill = inset rounded rect.
                if (bt > 0)
                    DrawRect(_buttonRect, _roundedBorder, _borderColor.Value);
                Rect inner = new Rect(_buttonRect.x + bt, _buttonRect.y + bt,
                                      _buttonRect.width - bt * 2, _buttonRect.height - bt * 2);
                DrawRect(inner, _roundedBg, _buttonColor.Value);
            }
            else
            {
                // Sharp corners: simple rects.
                if (bt > 0)
                    DrawRect(_buttonRect, _borderTex, _borderColor.Value);
                Rect inner = new Rect(_buttonRect.x + bt, _buttonRect.y + bt,
                                      _buttonRect.width - bt * 2, _buttonRect.height - bt * 2);
                DrawRect(inner, _bgTex, _buttonColor.Value);
            }

            bool isOpen = (bool)_displayingWindowProp.GetValue(_cm, null);
            string label = isOpen ? _closeText.Value : _openText.Value;

            // Transparent button on top handles the click + draws the label.
            if (GUI.Button(_buttonRect, label, _style))
                ToggleWindow();
        }
    }
}