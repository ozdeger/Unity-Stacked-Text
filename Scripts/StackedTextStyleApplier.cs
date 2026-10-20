using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Optional companion component that applies a <see cref="StackedTextStyle"/> asset to the text on
/// this GameObject. The inspector lists every style asset in the project by name; at runtime call
/// <see cref="SetStyle"/>.
///
/// What a style drives, per section it overrides:
/// <list type="bullet">
/// <item>Stacks: <see cref="StackedText"/> renders the style's stacks instead of its own
/// <see cref="StackedText.Stacks"/> list, which is never modified. Editing the style asset updates
/// every text using it.</item>
/// <item>Face: the TMP face color and vertex gradient are written to the text. The values the text
/// had before are kept and restored when the style is removed or the component is disabled.</item>
/// <item>Shine: the sibling <see cref="StackedTextShine"/> is enabled or disabled and renders the
/// style's shine settings. In Play Mode the module is added automatically when a style needs it.</item>
/// </list>
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class StackedTextStyleApplier : MonoBehaviour
{
    #region Fields

    [Tooltip("Style asset to apply. None keeps the text's own look.")]
    [SerializeField, AssetDropdown] private StackedTextStyle Style;

    [Tooltip("Auto-collected from this GameObject.")]
    [SerializeField] private TMP_Text Text;
    [Tooltip("Auto-collected from this GameObject.")]
    [SerializeField] private StackedText StackedText;
    [Tooltip("Auto-collected from this GameObject. Added automatically in Play Mode when a style turns the shine on.")]
    [SerializeField] private StackedTextShine Shine;

    // The text's own look, captured the first time a style overrides it and restored when no
    // style drives it any more. Serialized so a scene or prefab saved with a style applied still
    // remembers the original values.
    [SerializeField, HideInInspector] private bool _hasFaceSnapshot;
    [SerializeField, HideInInspector] private Color _unstyledColor;
    [SerializeField, HideInInspector] private bool _unstyledEnableVertexGradient;
    [SerializeField, HideInInspector] private VertexGradient _unstyledGradient;
    [SerializeField, HideInInspector] private TMP_ColorGradient _unstyledGradientPreset;
    [SerializeField, HideInInspector] private bool _hasShineSnapshot;
    [SerializeField, HideInInspector] private bool _unstyledShineEnabled;

    private StackedTextStyle _appliedStyle;

    #endregion

    #region Public API

    /// <summary>The assigned style asset, or null when the text uses its own look.</summary>
    public StackedTextStyle CurrentStyle => Style;

    /// <summary>
    /// Applies <paramref name="style"/>; pass null to return to the text's own look.
    /// </summary>
    public void SetStyle(StackedTextStyle style)
    {
        Style = style;
        Apply();
    }

    /// <summary>Removes the style and restores the text's own look.</summary>
    public void ClearStyle()
    {
        SetStyle(null);
    }

    /// <summary>
    /// Re-applies the current style, for example after code changed TMP colors that the style
    /// drives. Called automatically on enable, on inspector changes and when the style asset changes.
    /// </summary>
    public void Apply()
    {
        CollectReferences();

        var style = isActiveAndEnabled ? Style : null;
        bool styleChanged = style != _appliedStyle;
        _appliedStyle = style;

        ApplyFace(style);
        ApplyShine(style, styleChanged);

        if (StackedText != null)
        {
            StackedText.TryAutoCollectStyleApplier();
            StackedText.MarkDirty();
        }
    }

    /// <summary>
    /// Used by <see cref="StackedText"/>: the stack list to render in place of its own, if the
    /// current style overrides stacks.
    /// </summary>
    public bool TryGetStyleStacks(out List<StackedText.StackConfig> stacks)
    {
        if (isActiveAndEnabled && Style != null && Style.OverrideStacks && Style.Stacks != null)
        {
            stacks = Style.Stacks;
            return true;
        }

        stacks = null;
        return false;
    }

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        StackedTextStyle.Changed += OnStyleChanged;
        Apply();
    }

    private void OnDisable()
    {
        StackedTextStyle.Changed -= OnStyleChanged;
        // isActiveAndEnabled is already false here, so this restores the text's own look.
        Apply();
    }

    private void Reset()
    {
        CollectReferences();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Writing to TMP from OnValidate triggers "SendMessage cannot be called during OnValidate"
        // warnings, so apply once the inspector change has settled.
        UnityEditor.EditorApplication.delayCall -= ApplyDelayed;
        UnityEditor.EditorApplication.delayCall += ApplyDelayed;
    }

    private void ApplyDelayed()
    {
        if (this != null && isActiveAndEnabled)
            Apply();
    }
#endif

    private void OnStyleChanged(StackedTextStyle style)
    {
        if (style != Style)
            return;
#if UNITY_EDITOR
        // Style edits arrive from the asset's OnValidate.
        UnityEditor.EditorApplication.delayCall -= ApplyDelayed;
        UnityEditor.EditorApplication.delayCall += ApplyDelayed;
#else
        Apply();
#endif
    }

    private void CollectReferences()
    {
        if (Text == null)
            TryGetComponent(out Text);
        if (StackedText == null)
            TryGetComponent(out StackedText);
        if (Shine == null)
            TryGetComponent(out Shine);
    }

    #endregion

    #region Apply

    private void ApplyFace(StackedTextStyle style)
    {
        if (Text == null)
            return;

        if (style != null && style.OverrideFace)
        {
            if (!_hasFaceSnapshot)
            {
                _unstyledColor = Text.color;
                _unstyledEnableVertexGradient = Text.enableVertexGradient;
                _unstyledGradient = Text.colorGradient;
                _unstyledGradientPreset = Text.colorGradientPreset;
                _hasFaceSnapshot = true;
            }

            // A gradient preset takes priority over colorGradient in TMP, so clear it while the
            // style drives the face.
            Text.colorGradientPreset = null;
            Text.color = style.FaceColor;
            Text.enableVertexGradient = style.EnableVertexGradient;
            Text.colorGradient = style.FaceGradient;
        }
        else if (_hasFaceSnapshot)
        {
            Text.color = _unstyledColor;
            Text.enableVertexGradient = _unstyledEnableVertexGradient;
            Text.colorGradient = _unstyledGradient;
            Text.colorGradientPreset = _unstyledGradientPreset;
            _hasFaceSnapshot = false;
            _unstyledGradientPreset = null;
        }
    }

    private void ApplyShine(StackedTextStyle style, bool styleChanged)
    {
        if (style != null && style.OverrideShine)
        {
            if (Shine == null && style.ShineEnabled && Application.isPlaying && StackedText != null)
            {
                Shine = gameObject.AddComponent<StackedTextShine>();
                Shine.enabled = false;
            }
            if (Shine == null)
                return;

            if (!_hasShineSnapshot)
            {
                _unstyledShineEnabled = Shine.enabled;
                _hasShineSnapshot = true;
            }

            Shine.SetStyleSettings(style.Shine);
            if (Shine.enabled != style.ShineEnabled)
                Shine.enabled = style.ShineEnabled;
            else if (styleChanged && style.ShineEnabled && Shine.PlayOnEnable)
                Shine.Play();
        }
        else if (_hasShineSnapshot)
        {
            if (Shine != null)
            {
                Shine.ClearStyleSettings();
                if (Shine.enabled != _unstyledShineEnabled)
                    Shine.enabled = _unstyledShineEnabled;
            }
            _hasShineSnapshot = false;
        }
    }

    #endregion
}
