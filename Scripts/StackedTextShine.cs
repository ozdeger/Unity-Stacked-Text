using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Optional companion component for <see cref="StackedText"/>. Sweeps a soft highlight band across
/// the main text layer, and across any stack whose <see cref="StackedText.StackConfig.Shine"/> is
/// above 0. <see cref="StackedText"/> writes each vertex's position along the shine direction and
/// its shine amount into UV3; this component animates the band on per-text copies of the
/// rendering materials.
///
/// The copies are pushed straight to the text's <see cref="CanvasRenderer"/>s (the main text and
/// any fallback-font sub-meshes) and never assigned to <see cref="TMP_Text.fontSharedMaterial"/>,
/// so the text's serialized material reference is never touched and Mask / RectMask2D stencil
/// materials keep working.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(StackedText))]
public class StackedTextShine : MonoBehaviour
{
    #region Settings

    [Serializable]
    public class ShineSettings
    {
        [Tooltip("Width of the band as a fraction of the text block.")]
        [Range(0.01f, 1f)] public float Width = 0.25f;
        [Tooltip("Direction the band travels, in degrees: 0 = left to right, 90 = bottom to top. " +
                 "The band itself is perpendicular to this direction.")]
        [Range(-180f, 180f)] public float Rotation = 45f;
        [Tooltip("Edge softness of the band.")]
        [Range(0.01f, 1f)] public float Softness = 0.569f;
        [Tooltip("Strength of the highlight.")]
        [Range(0f, 1f)] public float Brightness = 0.268f;
        [Tooltip("0 = plain white highlight, 1 = highlight tinted by the face color for a metallic look.")]
        [Range(0f, 1f)] public float Gloss = 0.7f;
        [Tooltip("Multiplies the highlight color.")]
        public Color Color = Color.white;
        [Tooltip("Seconds one sweep across the text takes.")]
        [Min(0.01f)] public float Duration = 2f;
        [Tooltip("Seconds to wait between two sweeps.")]
        [Min(0f)] public float LoopDelay = 3f;

        public ShineSettings Clone()
        {
            return (ShineSettings)MemberwiseClone();
        }
    }

    #endregion

    #region Fields

    private static readonly int ShineLocationId = Shader.PropertyToID("_ShineLocation");
    private static readonly int ShineWidthId = Shader.PropertyToID("_ShineWidth");
    private static readonly int ShineSoftnessId = Shader.PropertyToID("_ShineSoftness");
    private static readonly int ShineBrightnessId = Shader.PropertyToID("_ShineBrightness");
    private static readonly int ShineGlossId = Shader.PropertyToID("_ShineGloss");
    private static readonly int ShineColorId = Shader.PropertyToID("_ShineColor");

    public ShineSettings Settings = new();
    [Tooltip("Start sweeping as soon as the component is enabled. Turn off to start it from code with Play().")]
    public bool PlayOnEnable = true;
    [Tooltip("Repeat the sweep every Duration + Loop Delay seconds. Turn off for a single sweep per Play().")]
    public bool Loop = true;
    [Tooltip("Keep shining while Time.timeScale is 0, for example in paused menus.")]
    public bool UseUnscaledTime = true;
#if UNITY_EDITOR
    [Tooltip("Animate the shine in Edit Mode. When off, the band rests in the middle of the text so its look can still be tuned.")]
    public bool PreviewInEditMode = true;
#endif

    private struct MaterialSlot
    {
        public CanvasRenderer Renderer;
        public Material Source;
        public Material Copy;
    }

    private readonly List<MaterialSlot> _slots = new();
    private readonly List<CanvasRenderer> _renderers = new();
    private TMP_Text _text;
    // Set by StackedTextStyleApplier while a style drives the shine; Settings stays as the
    // component's own (unstyled) setup.
    private ShineSettings _styleSettings;
    private bool _isPlaying;
    private double _elapsed;
    private double _lastTimestamp = -1;

    #endregion

    #region Public API

    /// <summary>The settings currently rendered: the applied style's, otherwise <see cref="Settings"/>.</summary>
    public ShineSettings ActiveSettings => _styleSettings ?? Settings;

    /// <summary>True while a sweep is running or looping.</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>Starts a sweep from the beginning (and keeps looping if <see cref="Loop"/> is on).</summary>
    public void Play()
    {
        _isPlaying = true;
        _elapsed = 0;
    }

    /// <summary>Hides the band until <see cref="Play"/> is called again.</summary>
    public void Stop()
    {
        _isPlaying = false;
    }

    /// <summary>
    /// Renders <paramref name="settings"/> instead of <see cref="Settings"/> until
    /// <see cref="ClearStyleSettings"/> is called. Used by <see cref="StackedTextStyleApplier"/>;
    /// nothing is serialized.
    /// </summary>
    public void SetStyleSettings(ShineSettings settings)
    {
        _styleSettings = settings;
    }

    public void ClearStyleSettings()
    {
        _styleSettings = null;
    }

    /// <summary>Unit vector the band travels along, in the text's local space.</summary>
    public Vector2 GetDirection()
    {
        float radians = ActiveSettings.Rotation * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
    }

    /// <summary>
    /// Hash of the parameters baked into the mesh (only the direction; everything else is a
    /// material property). Lets <see cref="StackedText"/> rebuild when it changes, including
    /// changes made by Animation clips or a style.
    /// </summary>
    public int GetParametersHash()
    {
        return ActiveSettings.Rotation.GetHashCode();
    }

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        TryGetComponent(out _text);
        Canvas.willRenderCanvases += OnWillRenderCanvases;
        _lastTimestamp = -1;
        if (PlayOnEnable)
            Play();
        else
            Stop();
        NotifyStackedText();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update += EditorUpdate;
#endif
    }

    private void OnDisable()
    {
        Canvas.willRenderCanvases -= OnWillRenderCanvases;
        ReleaseMaterials();
        NotifyStackedText();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorUpdate;
#endif
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        NotifyStackedText();
    }
#endif

    private void NotifyStackedText()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectShine();
            stackedText.MarkDirty();
        }
    }

    #endregion

    #region Animation

    // Runs after the canvas update registry has rebuilt graphics this frame, so any material TMP
    // just pushed to a renderer is swapped for our copy before the canvas renders.
    private void OnWillRenderCanvases()
    {
        if (_text == null)
            return;

        AdvanceTime();
        float location = EvaluateLocation(ActiveSettings, out bool isVisible);
        UpdateMaterials(location, isVisible);
    }

    private void AdvanceTime()
    {
        double now = GetTimestamp();
        if (_lastTimestamp >= 0 && now > _lastTimestamp)
            _elapsed += now - _lastTimestamp;
        _lastTimestamp = now;
    }

    private double GetTimestamp()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            return UnityEditor.EditorApplication.timeSinceStartup;
#endif
        return UseUnscaledTime ? Time.unscaledTimeAsDouble : Time.timeAsDouble;
    }

    private float EvaluateLocation(ShineSettings settings, out bool isVisible)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying && !PreviewInEditMode)
        {
            isVisible = true;
            return 0.5f;
        }
#endif
        isVisible = false;
        if (!_isPlaying)
            return 0f;

        double duration = Mathf.Max(settings.Duration, 0.01f);
        double cycle = duration + Mathf.Max(settings.LoopDelay, 0f);
        double time = _elapsed;
        if (Loop)
        {
            time %= cycle;
        }
        else if (time >= duration)
        {
            _isPlaying = false;
            return 0f;
        }

        // Inside the loop delay the band waits off the text.
        if (time >= duration)
            return 0f;

        isVisible = true;
        return (float)(time / duration);
    }

    #endregion

    #region Materials

    private void UpdateMaterials(float location, bool isVisible)
    {
        CollectRenderers();
        PruneSlots();

        var settings = ActiveSettings;
        for (int i = 0; i < _renderers.Count; i++)
        {
            var canvasRenderer = _renderers[i];
            if (canvasRenderer.materialCount == 0)
                continue;

            var current = canvasRenderer.GetMaterial(0);
            if (current == null)
                continue;

            int slotIndex = FindSlot(canvasRenderer);
            MaterialSlot slot = slotIndex >= 0 ? _slots[slotIndex] : new MaterialSlot { Renderer = canvasRenderer };

            if (current != slot.Copy)
            {
                // TMP pushed a new material (first frame, font/mask change, sub-mesh rebuild).
                // Sprite materials and shaders without the shine properties are left alone.
                if (!current.HasProperty(ShineLocationId))
                {
                    if (slotIndex >= 0)
                    {
                        DestroyMaterial(slot.Copy);
                        _slots.RemoveAt(slotIndex);
                    }
                    continue;
                }

                if (slot.Copy == null)
                {
                    slot.Copy = new Material(current)
                    {
                        name = current.name + " (Shine)",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                }
                else
                {
                    slot.Copy.shader = current.shader;
                    slot.Copy.CopyPropertiesFromMaterial(current);
                }

                slot.Source = current;
                canvasRenderer.SetMaterial(slot.Copy, 0);
            }
#if UNITY_EDITOR
            else if (!Application.isPlaying && slot.Source != null)
            {
                // Pick up edits made in the material inspector while previewing.
                slot.Copy.CopyPropertiesFromMaterial(slot.Source);
            }
#endif

            ApplyProperties(slot.Copy, settings, location, isVisible);

            if (slotIndex >= 0)
                _slots[slotIndex] = slot;
            else
                _slots.Add(slot);
        }
    }

    private static void ApplyProperties(Material material, ShineSettings settings, float location, bool isVisible)
    {
        material.SetFloat(ShineLocationId, location);
        material.SetFloat(ShineWidthId, settings.Width);
        material.SetFloat(ShineSoftnessId, settings.Softness);
        material.SetFloat(ShineBrightnessId, isVisible ? settings.Brightness : 0f);
        material.SetFloat(ShineGlossId, settings.Gloss);
        material.SetColor(ShineColorId, settings.Color);
    }

    private void CollectRenderers()
    {
        _renderers.Clear();
        _renderers.Add(_text.canvasRenderer);

        // Fallback-font glyphs (e.g. Arabic) render through TMP_SubMeshUI children.
        var textTransform = _text.transform;
        for (int i = 0; i < textTransform.childCount; i++)
        {
            if (textTransform.GetChild(i).TryGetComponent<TMP_SubMeshUI>(out var subMesh) && subMesh.isActiveAndEnabled)
                _renderers.Add(subMesh.canvasRenderer);
        }
    }

    private int FindSlot(CanvasRenderer canvasRenderer)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Renderer == canvasRenderer)
                return i;
        }
        return -1;
    }

    private void PruneSlots()
    {
        // Drop copies whose sub-mesh TMP destroyed or deactivated.
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var slot = _slots[i];
            if (slot.Renderer != null && _renderers.Contains(slot.Renderer))
                continue;

            RestoreSource(slot);
            DestroyMaterial(slot.Copy);
            _slots.RemoveAt(i);
        }
    }

    private void ReleaseMaterials()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            RestoreSource(_slots[i]);
            DestroyMaterial(_slots[i].Copy);
        }
        _slots.Clear();

        // Let TMP push its own materials again on the next canvas update.
        if (_text != null)
            _text.SetMaterialDirty();
    }

    private static void RestoreSource(MaterialSlot slot)
    {
        if (slot.Renderer == null || slot.Renderer.materialCount == 0)
            return;
        if (slot.Renderer.GetMaterial(0) == slot.Copy)
            slot.Renderer.SetMaterial(slot.Source, 0);
    }

    private static void DestroyMaterial(Material material)
    {
        if (material == null)
            return;
        if (Application.isPlaying)
            Destroy(material);
        else
            DestroyImmediate(material);
    }

    #endregion

    #region Editor Preview

#if UNITY_EDITOR
    private static double s_lastEditorRepaintTime;

    private void EditorUpdate()
    {
        if (Application.isPlaying || !PreviewInEditMode || !_isPlaying)
            return;

        // Shared across instances: one repaint request per tick is enough for all of them.
        double now = UnityEditor.EditorApplication.timeSinceStartup;
        if (now - s_lastEditorRepaintTime < 1.0 / 30.0)
            return;
        s_lastEditorRepaintTime = now;

        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
    }
#endif

    #endregion
}
