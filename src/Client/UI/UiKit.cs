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

        /// <summary>A single-line text input with placeholder. Optionally a password field.</summary>
        public static InputField MakeInput(string name, Transform parent, string placeholder,
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
