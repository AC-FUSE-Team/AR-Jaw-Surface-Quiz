using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BMC.JawAR.Quiz.Material3
{
    public enum JawButtonStyle { Filled, Tonal, Outlined, Text, IconOnly }

    /// <summary>References into a styled button so callers can update its label/icon/enabled look
    /// later without re-walking the hierarchy.</summary>
    public struct JawButtonSkin
    {
        public RectTransform Root;
        public Image Background;
        public TMP_Text Label;
        public Image Icon;
        public JawButtonStyle Style;
    }

    public struct JawSwitchSkin
    {
        public RectTransform Track;
        public RectTransform Thumb;
        public Image TrackImage;
        public Image ThumbImage;
    }

    /// <summary>Result of skinning a controller-owned row: the kept-alive label plus its styled
    /// color/size, so the caller can cheaply reassert them each frame against the controller's own
    /// periodic relayout (see <see cref="JawMaterialWidgets.SkinLegacyRow"/>).</summary>
    public struct JawLegacyRowSkin
    {
        public Text Label;
        public Color LabelColor;
        public int LabelFontSize;
        public JawSwitchSkin Switch;
    }

    public struct JawSnackbarSkin
    {
        public RectTransform Root;
        public CanvasGroup Group;
        public Image IconImage;
        public TMP_Text Label;
    }

    /// <summary>
    /// Reusable Material-style widget builders shared by the quiz UI's HUD, drawer, snackbar, and
    /// tracking-status components. Every builder is themed from <see cref="JawMaterialTheme"/> and
    /// enforces the minimum touch target in one place. This is a Unity-native, Material-inspired
    /// implementation built from Unity UI + TextMeshPro — not the official Google Material
    /// Components/Compose library.
    /// </summary>
    public static class JawMaterialWidgets
    {
        public static RectTransform Card(Transform parent, string name, Color background, float radius)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            var image = go.AddComponent<Image>();
            image.sprite = JawMaterialSprites.RoundedRect(radius);
            image.type = Image.Type.Sliced;
            image.color = background;
            return rect;
        }

        public static TMP_Text Label(Transform parent, string text, int size, TMP_FontAsset font, Color color,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(text is { Length: > 0 } ? text + " Label" : "Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.font = font != null ? font : JawMaterialTheme.FontRegular;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.enableWordWrapping = true;
            // Overflow, not Truncate: with a tight box, Truncate can decide the very first line
            // doesn't fit and render zero characters instead of clipping it — a much worse failure
            // than text that slightly overflows its nominal bounds.
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            return tmp;
        }

        public static Image Icon(Transform parent, string iconName, Color tint, float size = 40f)
        {
            var go = new GameObject(iconName + " Icon", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = JawMaterialIcons.Get(iconName);
            image.color = tint;
            image.raycastTarget = false;
            image.preserveAspect = true;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(size, size);
            return image;
        }

        /// <summary>
        /// Restyles an existing (already-wired) Button into a Material button. Existing onClick
        /// listeners and behaviour are preserved untouched — only the visual children change.
        /// </summary>
        public static JawButtonSkin Restyle(Button button, JawButtonStyle style, string label, string iconName = null)
        {
            var rect = button.GetComponent<RectTransform>();
            for (var i = rect.childCount - 1; i >= 0; i--)
            {
                var child = rect.GetChild(i).gameObject;
                if (Application.isPlaying) UnityEngine.Object.Destroy(child);
                else UnityEngine.Object.DestroyImmediate(child);
            }

            var image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
            image.sprite = JawMaterialSprites.RoundedRect(JawMaterialTheme.RadiusMedium);
            image.type = Image.Type.Sliced;
            button.targetGraphic = image;

            ApplyStyleColors(image, style, out var labelColor, out var iconColor);

            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(rect, false);
            var contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            var layout = content.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = JawMaterialTheme.SpaceXs;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            Image iconImage = null;
            if (!string.IsNullOrEmpty(iconName))
                iconImage = Icon(content.transform, iconName, iconColor, 32f);

            TMP_Text text = null;
            if (style != JawButtonStyle.IconOnly)
                text = Label(content.transform, label, JawMaterialTheme.TypeButtonLabelSize,
                    JawMaterialTheme.FontMedium, labelColor, TextAlignmentOptions.Center);

            EnsureTouchTarget(button.gameObject);

            return new JawButtonSkin
            {
                Root = rect,
                Background = image,
                Label = text,
                Icon = iconImage,
                Style = style,
            };
        }

        /// <summary>Creates a brand-new locally-owned Material button (hamburger, close, etc.).</summary>
        public static (Button button, JawButtonSkin skin) NewButton(Transform parent, string name,
            JawButtonStyle style, string label, string iconName, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            if (onClick != null) button.onClick.AddListener(() => onClick());
            var skin = Restyle(button, style, label, iconName);
            return (button, skin);
        }

        public static (RectTransform root, Image background, TMP_Text label, Image icon) Chip(
            Transform parent, string text, string iconName, Color tint, Color onTint)
        {
            // Deliberately no ContentSizeFitter: chips are placed either inside an explicitly
            // anchor-stretched HUD region or a fixed-height drawer row, both set by the caller.
            // Combining a stretch anchor with ContentSizeFitter fights Unity's layout rebuild, so
            // sizing is left entirely to whatever rect the caller assigns; only the icon/label's
            // internal alignment and padding come from the layout group.
            var root = Card(parent, text + " Chip", tint, JawMaterialTheme.RadiusPill);
            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = JawMaterialTheme.SpaceXs;
            layout.padding = new RectOffset(14, 18, 6, 6);
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            Image icon = null;
            if (!string.IsNullOrEmpty(iconName)) icon = Icon(root, iconName, onTint, 26f);
            var label = Label(root, text, JawMaterialTheme.TypeProgressStatusSize, JawMaterialTheme.FontMedium,
                onTint, TextAlignmentOptions.Center);
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 32f;

            return (root, root.GetComponent<Image>(), label, icon);
        }

/// <summary>
        /// Adds Material switch track/thumb visuals anchored to the right edge of an existing
        /// (already-wired) row without touching its onClick behaviour or any existing children, so
        /// callers can drive the thumb position from the real boolean state each frame.
        /// </summary>
        public static JawSwitchSkin AttachSwitch(Transform parent)
        {
            var trackGo = new GameObject("Switch Track", typeof(RectTransform));
            trackGo.transform.SetParent(parent, false);
            var track = (RectTransform)trackGo.transform;
            var trackImage = trackGo.AddComponent<Image>();
            trackImage.sprite = JawMaterialSprites.Pill();
            trackImage.type = Image.Type.Sliced;
            trackImage.color = JawMaterialTheme.OutlineFaint;
            trackImage.raycastTarget = false;
            track.anchorMin = track.anchorMax = new Vector2(1f, 0.5f);
            track.pivot = new Vector2(1f, 0.5f);
            track.sizeDelta = new Vector2(64f, 34f);
            track.anchoredPosition = new Vector2(-18f, 0f);

            var thumbGo = new GameObject("Switch Thumb", typeof(RectTransform));
            thumbGo.transform.SetParent(track, false);
            var thumb = (RectTransform)thumbGo.transform;
            var thumbImage = thumbGo.AddComponent<Image>();
            thumbImage.sprite = JawMaterialSprites.Pill();
            thumbImage.type = Image.Type.Sliced;
            thumbImage.color = Color.white;
            thumbImage.raycastTarget = false;
            thumb.sizeDelta = new Vector2(26f, 26f);
            thumb.anchorMin = thumb.anchorMax = new Vector2(0f, 0.5f);
            thumb.pivot = new Vector2(0.5f, 0.5f);
            thumb.anchoredPosition = new Vector2(20f, 0f);

            return new JawSwitchSkin { Track = track, Thumb = thumb, TrackImage = trackImage, ThumbImage = thumbImage };
        }

        public static void SetSwitchState(JawSwitchSkin skin, bool on)
        {
            if (skin.Track == null) return;
            skin.TrackImage.color = on ? JawMaterialTheme.Primary : JawMaterialTheme.OutlineFaint;
            var targetX = on ? skin.Track.sizeDelta.x - 20f : 20f;
            skin.Thumb.anchoredPosition = new Vector2(targetX, 0f);
        }

        /// <summary>
        /// Restyles an existing controller-owned row (Start, Profile, Mute, Overlay, Virtual Jaw,
        /// Diagnostics) IN PLACE: the row's existing legacy <see cref="Text"/> label is kept as the
        /// same component instance (only its color/size/font/rect are adjusted) because the
        /// controller holds a live reference to it and keeps writing to it (student id, mute state,
        /// overlay state, etc.) — destroying and replacing it would break that binding. An icon and
        /// an optional switch are added as new sibling decorations rather than replacing anything.
        /// </summary>
        public static JawLegacyRowSkin SkinLegacyRow(Button button, JawButtonStyle style, string iconName,
            bool asSwitch, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var image = button.GetComponent<Image>() ?? button.gameObject.AddComponent<Image>();
            image.sprite = JawMaterialSprites.RoundedRect(JawMaterialTheme.RadiusMedium);
            image.type = Image.Type.Sliced;
            button.targetGraphic = image;
            ApplyStyleColors(image, style, out var labelColor, out var iconColor);

            var text = button.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.color = labelColor;
                text.fontSize = JawMaterialTheme.TypeButtonLabelSize;
                text.fontStyle = FontStyle.Bold;
                text.alignment = alignment;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                var trt = text.rectTransform;
                var centered = alignment == TextAnchor.MiddleCenter;
                var leftInset = string.IsNullOrEmpty(iconName) ? 0.06f : 0.20f;
                var rightInset = centered ? leftInset : asSwitch ? 0.24f : 0.06f;
                trt.anchorMin = new Vector2(leftInset, 0f);
                trt.anchorMax = new Vector2(1f - rightInset, 1f);
                trt.offsetMin = Vector2.zero;
                trt.offsetMax = Vector2.zero;
            }

            if (!string.IsNullOrEmpty(iconName))
            {
                var icon = Icon(button.transform, iconName, iconColor, 32f);
                var irt = icon.rectTransform;
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.pivot = new Vector2(0f, 0.5f);
                irt.anchoredPosition = new Vector2(18f, 0f);
            }

            EnsureTouchTarget(button.gameObject);
            return new JawLegacyRowSkin
            {
                Label = text,
                LabelColor = labelColor,
                LabelFontSize = JawMaterialTheme.TypeButtonLabelSize,
                Switch = asSwitch ? AttachSwitch(button.transform) : default,
            };
        }

        public static JawSnackbarSkin Snackbar(Transform parent)
        {
            var root = Card(parent, "Material Snackbar", JawMaterialTheme.SurfaceElevated, JawMaterialTheme.RadiusMedium);
            var group = root.gameObject.AddComponent<CanvasGroup>();
            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.spacing = JawMaterialTheme.SpaceSm;
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.childForceExpandWidth = false;

            var iconRoot = Card(root, "Snackbar Icon Bg", JawMaterialTheme.Success, JawMaterialTheme.RadiusPill);
            var iconLe = iconRoot.gameObject.AddComponent<LayoutElement>();
            iconLe.minWidth = 44f;
            iconLe.minHeight = 44f;
            var icon = Icon(iconRoot, JawMaterialIcons.CheckCircle, Color.white, 28f);
            icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = Vector2.zero;

            var label = Label(root, string.Empty, JawMaterialTheme.TypeSupportingSize, JawMaterialTheme.FontMedium,
                JawMaterialTheme.OnSurface, TextAlignmentOptions.Left);
            var labelLe = label.gameObject.AddComponent<LayoutElement>();
            labelLe.flexibleWidth = 1f;

            return new JawSnackbarSkin { Root = root, Group = group, IconImage = icon, Label = label };
        }

        public static void EnsureTouchTarget(GameObject go)
        {
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            layout.minHeight = Mathf.Max(layout.minHeight, JawMaterialTheme.MinTouchTarget);
            layout.preferredHeight = layout.minHeight;
        }

        private static void ApplyStyleColors(Image background, JawButtonStyle style, out Color labelColor, out Color iconColor)
        {
            switch (style)
            {
                case JawButtonStyle.Filled:
                    background.color = JawMaterialTheme.Primary;
                    labelColor = iconColor = JawMaterialTheme.OnPrimary;
                    break;
                case JawButtonStyle.Tonal:
                    background.color = new Color(JawMaterialTheme.Tertiary.r, JawMaterialTheme.Tertiary.g, JawMaterialTheme.Tertiary.b, 0.24f);
                    labelColor = iconColor = JawMaterialTheme.Tertiary;
                    break;
                case JawButtonStyle.Outlined:
                    background.color = new Color(1f, 1f, 1f, 0.04f);
                    labelColor = iconColor = JawMaterialTheme.OnSurface;
                    break;
                case JawButtonStyle.IconOnly:
                    background.color = JawMaterialTheme.SurfaceContainer;
                    labelColor = iconColor = JawMaterialTheme.OnSurface;
                    break;
                case JawButtonStyle.Text:
                default:
                    background.color = Color.clear;
                    labelColor = iconColor = JawMaterialTheme.OnSurfaceVariant;
                    break;
            }
        }
    }
}
