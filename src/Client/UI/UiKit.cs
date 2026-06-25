using NeoModLoader.General; // OT.InitializeCommonText
using UnityEngine;
using UnityEngine.UI;

namespace WorldboxMP.Client.UI
{
    /// <summary>
    /// Small code-only uGUI factory. WorldBox/NML windows are all built from code,
    /// so we do the same: panels, labels, buttons, and text inputs with the game font.
    /// </summary>
    public static class UiKit
    {
        public static readonly Color PanelBg = new Color(0.13f, 0.14f, 0.16f, 0.97f);
        public static readonly Color SectionBg = new Color(0.18f, 0.19f, 0.22f, 1f);
        public static readonly Color ButtonRed = new Color(0.78f, 0.25f, 0.22f, 1f);
        public static readonly Color ButtonGrey = new Color(0.32f, 0.34f, 0.38f, 1f);
        public static readonly Color InputBg = new Color(0.08f, 0.085f, 0.10f, 1f);
        // Subtle card behind each section to group fields against the native frame.
        public static readonly Color CardBg = new Color(0.10f, 0.11f, 0.13f, 0.62f);
        // WorldBox's signature header orange (#F3961F).
        public static readonly Color Orange = new Color(0.953f, 0.588f, 0.122f, 1f);

        public static GameObject MakeRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Image MakePanel(string name, Transform parent, Color color)
        {
            var go = MakeRect(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        /// <summary>Vertical-layout container.</summary>
        public static GameObject MakeColumn(string name, Transform parent, Color? bg = null,
            float spacing = 6f, RectOffset padding = null)
        {
            var go = MakeRect(name, parent);
            if (bg.HasValue) { var img = go.AddComponent<Image>(); img.color = bg.Value; }
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = padding ?? new RectOffset(10, 10, 10, 10);
            v.childControlWidth = true; v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return go;
        }

        public static GameObject MakeRow(string name, Transform parent, float spacing = 6f)
        {
            var go = MakeRect(name, parent);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = true; h.childForceExpandHeight = false;
            return go;
        }

        public static Text MakeText(string name, Transform parent, string text, int size = 13,
            TextAnchor align = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal, Color? color = null)
        {
            var go = MakeRect(name, parent);
            var t = go.AddComponent<Text>();
            OT.InitializeCommonText(t);          // applies the game font/material
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.fontStyle = style;
            t.color = color ?? Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            // Native WorldBox text has a dark outline.
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);
            return t;
        }

        public static void SetHeight(GameObject go, float h)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = h; le.preferredHeight = h;
        }

        public static Button MakeButton(string name, Transform parent, string label,
            System.Action onClick, Color? color = null, int fontSize = 13)
        {
            var tmpl = GetNativeButtonTemplate();
            if (tmpl != null)
            {
                try
                {
                    var go = Object.Instantiate(tmpl, parent);
                    go.name = name;
                    go.SetActive(true);
                    var btn = go.GetComponent<Button>();
                    var img = go.GetComponent<Image>();
                    if (btn != null && img != null)
                    {
                        img.color = color ?? ButtonRed;       // tint the native pixel frame
                        img.type = Image.Type.Sliced;         // keep the pixel border crisp when stretched wide
                        btn.targetGraphic = img;
                        btn.onClick.RemoveAllListeners();
                        if (onClick != null) btn.onClick.AddListener(() => onClick());
                        // Hide the native center icon; we use a text label instead.
                        var icon = FindDeep(go.transform, "Icon");
                        if (icon != null) icon.gameObject.SetActive(false);
                        // Reuse an existing label if the prefab has one, else add our own.
                        var txt = go.GetComponentInChildren<Text>(true);
                        if (txt == null)
                        {
                            txt = MakeText(name + "_label", go.transform, label, fontSize, TextAnchor.MiddleCenter, FontStyle.Bold);
                            var lrt = txt.rectTransform;
                            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
                        }
                        else { txt.gameObject.SetActive(true); txt.text = label; txt.fontSize = fontSize; txt.alignment = TextAnchor.MiddleCenter; txt.color = Color.white; }
                        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                        le.minHeight = 30f; le.preferredHeight = 30f; le.flexibleWidth = 1f;
                        return btn;
                    }
                    Object.Destroy(go);
                }
                catch (System.Exception e) { ModLog.Warn("[UI] native button clone failed: " + e.Message); }
            }
            return MakeButtonFallback(name, parent, label, onClick, color, fontSize);
        }

        /// <summary>Hand-built flat button — used only if the native clone is unavailable.</summary>
        public static Button MakeButtonFallback(string name, Transform parent, string label,
            System.Action onClick, Color? color = null, int fontSize = 13)
        {
            var go = MakeRect(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color ?? ButtonRed;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            colors.colorMultiplier = 1f;
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var label2 = MakeText(name + "_label", go.transform, label, fontSize, TextAnchor.MiddleCenter, FontStyle.Bold);
            var lrt = label2.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            SetHeight(go, 30f);
            return btn;
        }

        // Cached clone of WorldBox's native input-field element. The game styles its input fields
        // entirely in prefabs (sprite/border/font/caret/padding are NOT set in code), so the only
        // way to look native is to clone the real "NameInputElement" out of a window prefab.
        private static GameObject _uiTemplateHolder;
        private static GameObject _nativeInputTemplate;
        private static GameObject _nativeButtonTemplate;
        private static bool _nativeInputTried, _nativeButtonTried;

        private static Transform FindDeep(Transform t, string n)
        {
            if (t.name == n) return t;
            foreach (Transform c in t) { var r = FindDeep(c, n); if (r != null) return r; }
            return null;
        }

        // Shared inactive holder so cloned template subtrees never run their Awake/Start.
        private static void EnsureTemplateHolder()
        {
            if (_uiTemplateHolder != null) return;
            _uiTemplateHolder = new GameObject("WBMP_UiTemplates");
            _uiTemplateHolder.SetActive(false);
            Object.DontDestroyOnLoad(_uiTemplateHolder);
        }

        // WorldBox's pixel button: Resources "ui/PrefabWorldBoxButton" (a Button with a 9-sliced
        // pixel frame Image + an "Icon" child). We clone the frame, hide the icon, add a text label.
        private static GameObject GetNativeButtonTemplate()
        {
            if (_nativeButtonTemplate != null) return _nativeButtonTemplate;
            if (_nativeButtonTried) return null;
            _nativeButtonTried = true;
            try
            {
                var prefab = Resources.Load<Button>("ui/PrefabWorldBoxButton");
                if (prefab == null) { ModLog.Warn("[UI] PrefabWorldBoxButton missing — using fallback button"); return null; }
                EnsureTemplateHolder();
                _nativeButtonTemplate = Object.Instantiate(prefab.gameObject, _uiTemplateHolder.transform);
                _nativeButtonTemplate.name = "WBMP_NativeButtonTemplate";
                ModLog.Info("[UI] native button template ready (ui/PrefabWorldBoxButton)");
                return _nativeButtonTemplate;
            }
            catch (System.Exception e) { ModLog.Warn("[UI] native button template failed: " + e.Message); return null; }
        }

        private static GameObject GetNativeInputTemplate()
        {
            if (_nativeInputTemplate != null) return _nativeInputTemplate;
            if (_nativeInputTried) return null;
            _nativeInputTried = true;
            try
            {
                var prefab = Resources.Load<GameObject>("windows/kingdom");
                if (prefab == null) { ModLog.Warn("[UI] windows/kingdom prefab missing — using fallback input"); return null; }
                // Instantiate under an INACTIVE holder so the window's Awake/Start never run.
                EnsureTemplateHolder();
                var temp = Object.Instantiate(prefab, _uiTemplateHolder.transform);
                var src = FindDeep(temp.transform, "NameInputElement");
                if (src == null || src.GetComponent<NameInput>() == null)
                {
                    ModLog.Warn("[UI] NameInputElement not found in kingdom window — using fallback input");
                    Object.Destroy(temp);
                    return null;
                }
                _nativeInputTemplate = Object.Instantiate(src.gameObject, _uiTemplateHolder.transform);
                _nativeInputTemplate.name = "WBMP_NativeInputTemplate";
                Object.Destroy(temp);
                ModLog.Info("[UI] native input template ready (cloned from kingdom window)");
                return _nativeInputTemplate;
            }
            catch (System.Exception e) { ModLog.Warn("[UI] native input template failed: " + e.Message); return null; }
        }

        /// <summary>A single-line text input with placeholder, styled with WorldBox's native field
        /// (cloned from the game's rename input) when available; falls back to a hand-built box.</summary>
        public static InputField MakeInput(string name, Transform parent, string placeholder,
            bool password = false, int maxLen = 64)
        {
            var tmpl = GetNativeInputTemplate();
            if (tmpl != null)
            {
                try
                {
                    var go = Object.Instantiate(tmpl, parent);
                    go.name = name;
                    go.SetActive(true);
                    var ni = go.GetComponent<NameInput>();
                    var input = ni != null ? ni.inputField : go.GetComponentInChildren<InputField>(true);
                    if (input != null)
                    {
                        if (ni != null) { ni.can_be_empty = true; ni.is_onomastics = false; }
                        input.onValueChanged.RemoveAllListeners();
                        input.onEndEdit.RemoveAllListeners();
                        input.text = "";
                        input.characterLimit = maxLen;
                        input.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
                        if (password) input.inputType = InputField.InputType.Password;
                        if (input.textComponent != null) input.textComponent.color = Color.white;
                        if (ni != null && ni.textField != null) ni.textField.color = Color.white;
                        EnsurePlaceholder(input, placeholder);
                        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                        le.minHeight = 32f; le.preferredHeight = 32f; le.flexibleWidth = 1f;
                        return input;
                    }
                    Object.Destroy(go);
                }
                catch (System.Exception e) { ModLog.Warn("[UI] native input clone failed: " + e.Message); }
            }
            return MakeInputFallback(name, parent, placeholder, password, maxLen);
        }

        // Native rename fields show the current name, not a placeholder — so make sure one exists.
        private static void EnsurePlaceholder(InputField input, string placeholder)
        {
            var ph = input.placeholder as Text;
            if (ph == null && input.textComponent != null)
            {
                var phGo = MakeRect(input.name + "_ph", input.textComponent.transform.parent);
                var prt = phGo.GetComponent<RectTransform>();
                var trt = input.textComponent.rectTransform;
                prt.anchorMin = trt.anchorMin; prt.anchorMax = trt.anchorMax;
                prt.offsetMin = trt.offsetMin; prt.offsetMax = trt.offsetMax;
                ph = phGo.AddComponent<Text>();
                OT.InitializeCommonText(ph);
                ph.fontSize = input.textComponent.fontSize;
                ph.alignment = input.textComponent.alignment;
                input.placeholder = ph;
            }
            if (ph != null)
            {
                ph.text = placeholder;
                ph.fontStyle = FontStyle.Italic;
                ph.color = new Color(1f, 1f, 1f, 0.4f);
                ph.supportRichText = false;
            }
        }

        /// <summary>Hand-built dark input box — used only if the native clone is unavailable.</summary>
        public static InputField MakeInputFallback(string name, Transform parent, string placeholder,
            bool password = false, int maxLen = 64)
        {
            var go = MakeRect(name, parent);
            var bg = go.AddComponent<Image>();
            bg.color = InputBg;
            SetHeight(go, 28f);

            var input = go.AddComponent<InputField>();
            input.characterLimit = maxLen;
            if (password) { input.contentType = InputField.ContentType.Password; input.inputType = InputField.InputType.Password; }

            // Text area must be a child so caret/placeholder clip correctly.
            var textGo = MakeRect(name + "_text", go.transform);
            var rt = textGo.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(8, 2); rt.offsetMax = new Vector2(-8, -2);
            var txt = textGo.AddComponent<Text>();
            OT.InitializeCommonText(txt);
            txt.fontSize = 13; txt.alignment = TextAnchor.MiddleLeft;
            txt.supportRichText = false;
            txt.color = Color.white;

            var phGo = MakeRect(name + "_ph", go.transform);
            var prt = phGo.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(8, 2); prt.offsetMax = new Vector2(-8, -2);
            var ph = phGo.AddComponent<Text>();
            OT.InitializeCommonText(ph);
            ph.fontSize = 13; ph.alignment = TextAnchor.MiddleLeft;
            ph.fontStyle = FontStyle.Italic;
            ph.color = new Color(1f, 1f, 1f, 0.4f);
            ph.text = placeholder;

            input.textComponent = txt;
            input.placeholder = ph;
            return input;
        }
    }
}
