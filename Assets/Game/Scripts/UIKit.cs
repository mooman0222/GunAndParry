using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GunAndParry
{
    /// <summary>Small code-first uGUI toolkit so every screen shares one visual language.</summary>
    public static class UIKit
    {
        public static Font Font;
        public static readonly Color Panel = new(0.06f, 0.08f, 0.13f, 0.94f);
        public static readonly Color PanelLight = new(0.11f, 0.14f, 0.22f, 1f);
        public static readonly Color Accent = new(0.25f, 0.95f, 1f);
        public static readonly Color Text = new(0.9f, 0.95f, 1f);
        public static readonly Color Dim = new(0.55f, 0.62f, 0.75f);
        public static readonly Color Gold = new(1f, 0.82f, 0.3f);
        public static readonly Color Danger = new(1f, 0.3f, 0.4f);
        public static readonly Color Magenta = new(1f, 0.4f, 1f);

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static RectTransform Stretch(this RectTransform r, float margin = 0)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(margin, margin);
            r.offsetMax = new Vector2(-margin, -margin);
            return r;
        }

        /// <summary>Anchor+pivot at the same normalized point, then offset by pos.</summary>
        public static RectTransform Place(this RectTransform r, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            r.anchorMin = r.anchorMax = r.pivot = anchor;
            r.anchoredPosition = pos;
            r.sizeDelta = size;
            return r;
        }

        public static Image Image(Transform parent, string name, Color color, Sprite sprite = null, bool rounded = true)
        {
            var img = Rect(parent, name).gameObject.AddComponent<Image>();
            img.sprite = sprite != null ? sprite : rounded ? Art.Rounded : null;
            img.type = img.sprite == Art.Rounded ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Text Label(Transform parent, string text, int size, Color color, TextAnchor align = TextAnchor.MiddleCenter, FontStyle style = FontStyle.Normal, bool shadow = true)
        {
            var t = Rect(parent, "Label").gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            if (shadow)
            {
                var s = t.gameObject.AddComponent<Shadow>();
                s.effectColor = new Color(0, 0, 0, 0.7f);
                s.effectDistance = new Vector2(2, -2);
            }
            return t;
        }

        public static Button Button(Transform parent, string label, UnityAction onClick, Vector2 size, int fontSize = 32)
        {
            var img = Image(parent, label, Color.white);
            img.raycastTarget = true;
            img.rectTransform.sizeDelta = size;
            var b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            b.colors = new ColorBlock
            {
                normalColor = PanelLight,
                highlightedColor = new Color(0.18f, 0.45f, 0.6f),
                selectedColor = new Color(0.18f, 0.45f, 0.6f),
                pressedColor = new Color(0.3f, 0.85f, 1f),
                disabledColor = new Color(0.08f, 0.09f, 0.12f, 0.7f),
                colorMultiplier = 1f,
                fadeDuration = 0.06f,
            };
            if (onClick != null) b.onClick.AddListener(onClick);
            b.onClick.AddListener(() => Sfx.Play(SfxId.Click, 0.6f));
            img.gameObject.AddComponent<ButtonFx>();
            if (!string.IsNullOrEmpty(label))
            {
                var t = Label(img.transform, label, fontSize, Text, TextAnchor.MiddleCenter, FontStyle.Bold);
                t.rectTransform.Stretch(8);
            }
            return b;
        }

        public static Slider Slider(Transform parent, float value, UnityAction<float> onChange, Vector2 size)
        {
            var root = Rect(parent, "Slider");
            root.sizeDelta = size;
            var bg = Image(root, "Background", PanelLight);
            bg.rectTransform.Stretch();
            bg.rectTransform.offsetMin = new Vector2(0, size.y * 0.35f);
            bg.rectTransform.offsetMax = new Vector2(0, -size.y * 0.35f);
            var fillArea = Rect(root, "Fill Area").Stretch();
            fillArea.offsetMin = new Vector2(0, size.y * 0.35f);
            fillArea.offsetMax = new Vector2(0, -size.y * 0.35f);
            var fill = Image(fillArea, "Fill", Accent);
            fill.rectTransform.sizeDelta = Vector2.zero;
            var handleArea = Rect(root, "Handle Area").Stretch();
            handleArea.offsetMin = new Vector2(size.y * 0.5f, 0);
            handleArea.offsetMax = new Vector2(-size.y * 0.5f, 0);
            var handle = Image(handleArea, "Handle", Color.white, Art.CircleSprite);
            handle.rectTransform.sizeDelta = new Vector2(size.y, 0);
            handle.raycastTarget = true;

            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = 0;
            s.maxValue = 1;
            s.value = value;
            s.colors = new ColorBlock
            {
                normalColor = new Color(0.8f, 0.85f, 0.9f),
                highlightedColor = Accent,
                selectedColor = Accent,
                pressedColor = Color.white,
                disabledColor = Color.gray,
                colorMultiplier = 1,
                fadeDuration = 0.06f,
            };
            s.onValueChanged.AddListener(onChange);
            root.gameObject.AddComponent<ButtonFx>();
            return s;
        }

        /// <summary>Horizontal fill bar; returns the fill image (set fillAmount).</summary>
        public static Image Bar(Transform parent, string name, Color fillColor, Vector2 size, out RectTransform root)
        {
            root = Rect(parent, name);
            root.sizeDelta = size;
            var bg = Image(root, "Bg", new Color(0, 0, 0, 0.6f));
            bg.rectTransform.Stretch();
            var fill = Image(root, "Fill", fillColor, null, false);
            fill.rectTransform.Stretch(3);
            fill.type = UnityEngine.UI.Image.Type.Filled;
            fill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            fill.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), Vector2.one * 0.5f);
            return fill;
        }

        public static VerticalLayoutGroup Column(RectTransform r, float spacing, TextAnchor align = TextAnchor.UpperCenter)
        {
            var v = r.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.childAlignment = align;
            v.childControlWidth = v.childControlHeight = false;
            v.childForceExpandWidth = v.childForceExpandHeight = false;
            return v;
        }

        public static HorizontalLayoutGroup Row(RectTransform r, float spacing, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = align;
            h.childControlWidth = h.childControlHeight = false;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            return h;
        }

        /// <summary>Destroys all children. Deactivates first: Destroy is deferred to end of frame and layout groups would still count them.</summary>
        public static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var c = parent.GetChild(i).gameObject;
                c.SetActive(false);
                Object.Destroy(c);
            }
        }

        public static void Select(Selectable s)
        {
            if (s == null || EventSystem.current == null) return;
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(s.gameObject);
        }

        public static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60:00}:{s % 60:00}";
        }
    }

    /// <summary>Hover = select (mouse and pad share one highlight), plus sound and a small pop.</summary>
    public sealed class ButtonFx : MonoBehaviour, IPointerEnterHandler, ISelectHandler, IDeselectHandler
    {
        Selectable selectable;
        float target = 1f;

        void Awake() => selectable = GetComponent<Selectable>();

        public void OnPointerEnter(PointerEventData e)
        {
            if (selectable != null && selectable.interactable && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(gameObject);
        }

        public void OnSelect(BaseEventData e)
        {
            target = 1.05f;
            Sfx.Play(SfxId.Hover, 0.5f);
        }

        public void OnDeselect(BaseEventData e) => target = 1f;

        void OnDisable()
        {
            target = 1f;
            transform.localScale = Vector3.one;
        }

        void Update()
        {
            float s = Mathf.Lerp(transform.localScale.x, target, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
            transform.localScale = new Vector3(s, s, 1);
        }
    }
}
