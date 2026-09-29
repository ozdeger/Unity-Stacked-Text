using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

[ExecuteInEditMode]
public class StackedText : MonoBehaviour
{
    #region Fields

    private const string RequiredShaderName = "TextMeshPro/Distance Field Dilate";

    [SerializeField] private TMP_Text Text;
    [SerializeField] private bool ShowMainText = true;
    [Range(0, 1)] private float MainTextSoftness;
    [Range(-1, 1f)] private float MainTextDilate;

    [Tooltip("Active stacks rendered behind the main text layer, drawn from last to first. " +
             "Leave empty to disable stacking (the main layer still renders).")]
    [SerializeField] public List<StackConfig> Stacks = new();

    public int StackCount => Stacks?.Count ?? 0;

    public StackConfig GetStack(int index) => Stacks[index];

    [Header("Optional")]
    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, its animation curve is applied to the text vertices before stacking.")]
    [SerializeField] private StackedTextCurve Curve;

    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, its animation curve is used to scale each character around its baseline midpoint before stacking.")]
    [SerializeField] private StackedTextScale Scale;

    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, its animation curve is used to rotate each character around its baseline midpoint on the Y axis before stacking.")]
    [SerializeField] private StackedTextRotate Rotate;

    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, its 8 fixed stack slots are appended to Stacks each frame so their fields can be keyframed by Animation clips.")]
    [SerializeField] private StackedTextAnimatableStacks AnimatableStacks;

    // Our generated stacked mesh keyed by the TMP source mesh it replaces (slot 0 = primary,
    // others = fallback sub-meshes used by Arabic / RTL glyphs and any other character that is
    // not in the primary font atlas). Keyed by mesh instance instead of material slot index so
    // overwrite detection stays valid when TMP reorders slots or a renderer is cleared to null.
    private readonly Dictionary<Mesh, Mesh> _stackedMeshBySourceMesh = new();
    private readonly List<Mesh> _deadSourceMeshes = new();
    private readonly List<TMP_SubMeshUI> _subMeshUIs = new();
    // Per-material flag: true if the slot only renders TMP sprites (e.g. <sprite=3> icons).
    // Sprite slots are deliberately left untouched by the stacking pass so icons render as
    // single, un-shadowed quads alongside stacked text.
    private readonly List<bool> _isSpriteSlot = new();
    // Tracks whether we've already warned about a fallback (e.g. Arabic) sub-mesh whose material
    // doesn't use the Distance Field Dilate shader. Without that shader, the per-stack softness
    // and dilate values written into UV3 are ignored — offset + color stacking still works, but
    // softness/dilate variation per layer won't render on those glyphs. Warn once per session so
    // the log doesn't spam.
    private bool _hasWarnedAboutFallbackShader;
    private readonly List<Vector3> _curveOffsets = new();
    private readonly List<Vector3> _scaleOffsets = new();
    private readonly List<Vector3> _rotateOffsets = new();
    // Per-vertex character-local Z axis (post-rotation) used to push per-stack depth offsets
    // along the rotated direction instead of flat world Z. Populated per material slot when the
    // Rotate module is active; left empty when no rotation is in play. Stays a unit-magnitude
    // direction; the per-character scale is applied separately in the stack loop.
    private readonly List<Vector3> _localZAxes = new();
    // Per-vertex character scale (1 = unscaled). Populated when the Scale module is active and
    // multiplied into the entire stack vertOffset so per-stack X/Y offsets AND depth shrink/grow
    // with the character — at scale = 0 the stack collapses with its character.
    private readonly List<float> _charScales = new();
    private readonly List<Vector3> _sourceVerts = new();
    private readonly List<Color32> _sourceColors = new();
    private readonly List<Vector4> _sourceUVs = new();
    private readonly List<Vector2> _sourceUV2s = new();
    private readonly List<int> _sourceTris = new();
    private readonly List<Vector3> _outVerts = new();
    private readonly List<Color32> _outColors = new();
    private readonly List<Vector4> _outUVs = new();
    private readonly List<Vector2> _outUV2s = new();
    private readonly List<Vector2> _outUV3s = new();
    private readonly List<int> _outTris = new();
    private bool _lastShowMainText;
    private float _lastMainTextDilate;
    private float _lastMainTextSoftness;
    private int _lastStackCount;
    private readonly List<StackConfig> _lastStacks = new();
    private Vector3 _lastLossyScale;
    private bool _forceUpdateNextFrame;
    private StackedTextCurve _lastCurveRef;
    private bool _lastCurveActive;
    private int _lastCurveHash;
    private StackedTextScale _lastScaleRef;
    private bool _lastScaleActive;
    private int _lastScaleHash;
    private StackedTextRotate _lastRotateRef;
    private bool _lastRotateActive;
    private int _lastRotateHash;
    private StackedTextAnimatableStacks _lastAnimatableStacksRef;
    private bool _lastAnimatableStacksActive;
    // Working stack list: Stacks + (AnimatableStacks slots if module is active). Rebuilt each
    // frame so animation-driven changes to the module's fields propagate without extra hooks.
    private readonly List<StackConfig> _workingStacks = new();

    // ColorSwap state: parsed once per frame from Text.text into _charHasTag/_charTagColors, then
    // projected per-material into _vertexHasTagColor/_vertexTagColors so stack layers can swap
    // gradient colors for characters wrapped in <color=...> rich-text tags.
    private readonly List<Color32> _vertexTagColors = new();
    private readonly List<bool> _vertexHasTagColor = new();
    private readonly List<Color32> _charTagColors = new();
    private readonly List<bool> _charHasTag = new();
    private readonly Stack<Color32> _colorTagStack = new();

    #endregion

    #region Editor - Material Validation

#if UNITY_EDITOR
    private void OnValidate()
    {
        Text ??= GetComponent<TMP_Text>();
        TryAutoCollectCurve();
        TryAutoCollectScale();
        TryAutoCollectRotate();
        TryAutoCollectAnimatableStacks();
        UnityEditor.EditorApplication.delayCall += ValidateMaterial;
        EnsureShaderChannels();
        _forceUpdateNextFrame = true;
    }

    private void ValidateMaterial()
    {
        UnityEditor.EditorApplication.delayCall -= ValidateMaterial;
        
        if (Text == null || Text.font == null)
            return;

        if (HasCompatibleShader(Text.fontSharedMaterial))
            return;

        var compatibleMat = GetOrCreateCompatibleMaterial(Text.font);
        if (compatibleMat == null)
            return;

        Text.fontSharedMaterial = compatibleMat;
        Text.SetVerticesDirty();
    }

    private static bool HasCompatibleShader(Material material)
    {
        return material != null &&
            material.shader != null &&
            material.shader.name == RequiredShaderName;
    }

    private static Material GetOrCreateCompatibleMaterial(TMP_FontAsset font)
    {
        if (font.atlasTextures == null || font.atlasTextures.Length == 0)
            return null;

        var fontAtlas = font.atlasTextures[0];
        var fontPath = UnityEditor.AssetDatabase.GetAssetPath(font);
        var fontDir = System.IO.Path.GetDirectoryName(fontPath)?.Replace('\\', '/');

        if (string.IsNullOrEmpty(fontDir))
            return null;

        var guids = UnityEditor.AssetDatabase.FindAssets("t:Material", new[] { fontDir });
        foreach (var guid in guids)
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
            if (HasCompatibleShader(mat) && mat.GetTexture("_MainTex") == fontAtlas)
                return mat;
        }

        var shader = Shader.Find(RequiredShaderName);
        if (shader == null)
        {
            Debug.LogError($"[StackedText] Shader '{RequiredShaderName}' not found in project.");
            return null;
        }

        var newMat = new Material(font.material) { shader = shader };
        var matPath = $"{fontDir}/{font.name} - StackedText.mat";
        matPath = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(matPath);
        UnityEditor.AssetDatabase.CreateAsset(newMat, matPath);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log($"[StackedText] Created material at '{matPath}'.", newMat);
        return newMat;
    }
#endif

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        Canvas.willRenderCanvases += OnPreRenderCanvas;
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);

        TryAutoCollectCurve();
        TryAutoCollectScale();
        TryAutoCollectRotate();
        TryAutoCollectAnimatableStacks();
        EnsureShaderChannels();

        _forceUpdateNextFrame = true;
    }

    public void TryAutoCollectCurve()
    {
        if (Curve == null)
            TryGetComponent(out Curve);
    }

    public void TryAutoCollectScale()
    {
        if (Scale == null)
            TryGetComponent(out Scale);
    }

    public void TryAutoCollectRotate()
    {
        if (Rotate == null)
            TryGetComponent(out Rotate);
    }

    public void TryAutoCollectAnimatableStacks()
    {
        if (AnimatableStacks == null)
            TryGetComponent(out AnimatableStacks);
    }

    private void OnDisable()
    {
        Canvas.willRenderCanvases -= OnPreRenderCanvas;
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);
        
        if (Text != null)
            Text.ForceMeshUpdate();
    }

    private void EnsureShaderChannels()
    {
        if (Text == null || Text.canvas == null)
            return;
        if (!Text.canvas.additionalShaderChannels.HasFlag(AdditionalCanvasShaderChannels.TexCoord3))
        {
            Text.canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord3;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(Text.canvas);
#endif
        }
    }

    private void OnRectTransformDimensionsChange()
    {
        // Mark for update, but don't execute yet to avoid race condition with TMP
        _forceUpdateNextFrame = true;
    }

    private void OnTextChanged(Object changedText)
    {
        if (changedText != Text)
            return;
        _forceUpdateNextFrame = true;
    }

    #endregion

    #region Mesh Generation
    
    // Runs right before canvas renders to keep mesh in sync, fixing race conditions with TMP
    private void OnPreRenderCanvas()
    {
        if (Text == null || !enabled)
            return;

        bool scaleChanged = transform.lossyScale != _lastLossyScale;
        bool meshOverwritten = AnyCachedMeshOverwritten();

        // Rebuild before HasPropertiesChanged so animation-clip mutations to AnimatableStacks
        // (which never fire OnValidate) surface via the existing _lastStacks vs _workingStacks diff.
        RebuildWorkingStacks();

        if (_forceUpdateNextFrame || scaleChanged || meshOverwritten || HasPropertiesChanged())
        {
            GenerateStackedText();
            _forceUpdateNextFrame = false;
            _lastLossyScale = transform.lossyScale;
        }
    }

    private bool AnyCachedMeshOverwritten()
    {
        // If TMP rebuilt and pushed its own source mesh back onto a renderer we manage (including
        // sprite slots, since we now push curved/scaled icon geometry there too), our stacked mesh
        // is no longer on screen and we must regenerate. Only a renderer showing a known TMP
        // source mesh counts as overwritten: null (an empty slot we cleared ourselves) or a
        // foreign mesh owned by another system must not trigger a rebuild, otherwise we'd
        // regenerate every frame fighting state we don't own.
        var textInfo = Text.textInfo;
        if (textInfo == null || _stackedMeshBySourceMesh.Count == 0)
            return false;

        int slotsToCheck = Mathf.Min(textInfo.materialCount, textInfo.meshInfo.Length);
        for (int m = 0; m < slotsToCheck; m++)
        {
            var renderer = GetCanvasRendererForMaterial(m, textInfo);
            if (renderer == null) continue;

            var currentMesh = renderer.GetMesh();
            if (currentMesh == null) continue;

            if (_stackedMeshBySourceMesh.ContainsKey(currentMesh))
                return true;
        }
        return false;
    }

    private void DetectSpriteSlots(TMP_TextInfo textInfo, int materialCount)
    {
        // Resize / reset the per-slot flags to match the current material count without
        // allocating beyond the high-water mark.
        while (_isSpriteSlot.Count < materialCount)
            _isSpriteSlot.Add(false);
        for (int i = 0; i < materialCount; i++)
            _isSpriteSlot[i] = false;

        int charCount = textInfo.characterCount;
        var charInfos = textInfo.characterInfo;
        if (charInfos == null) return;

        for (int i = 0; i < charCount; i++)
        {
            // TMP groups each material slot strictly as Character or Sprite, so a single sprite
            // character is enough to mark its slot. (A slot won't mix the two.)
            if (charInfos[i].elementType != TMP_TextElementType.Sprite)
                continue;
            int idx = charInfos[i].materialReferenceIndex;
            if (idx < 0 || idx >= materialCount) continue;
            _isSpriteSlot[idx] = true;
        }
    }

    private void RefreshSubMeshUIs()
    {
        _subMeshUIs.Clear();
        var t = Text.transform;
        for (int i = 0; i < t.childCount; i++)
        {
            if (t.GetChild(i).TryGetComponent<TMP_SubMeshUI>(out var sub))
                _subMeshUIs.Add(sub);
        }
    }

    private CanvasRenderer GetCanvasRendererForMaterial(int materialIndex, TMP_TextInfo textInfo)
    {
        if (materialIndex == 0)
            return Text.canvasRenderer;

        var targetMaterial = textInfo.meshInfo[materialIndex].material;
        for (int i = 0; i < _subMeshUIs.Count; i++)
        {
            var sub = _subMeshUIs[i];
            if (sub == null) continue;
            // Match by material reference; ordering of TMP_SubMeshUI children is generally
            // consistent with material indices but matching by material is more robust against
            // any children re-ordering.
            if (sub.sharedMaterial == targetMaterial)
                return sub.canvasRenderer;
        }
        return null;
    }

    private void GenerateStackedText()
    {
        if (Text == null)
            return;

        Text.ForceMeshUpdate();

        TMP_TextInfo textInfo = Text.textInfo;

        if (textInfo == null || textInfo.characterCount == 0)
            return;

        PopulateInvalidStackConfigs();

        // Re-run after PopulateInvalidStackConfigs so any auto-corrected entries in Stacks make
        // it into the working list this frame.
        RebuildWorkingStacks();

        var isExtraPaddingRequired = IsExtraPaddingRequired();
        if (isExtraPaddingRequired != Text.extraPadding)
            Text.extraPadding = isExtraPaddingRequired;

        int materialCount = textInfo.materialCount;
        if (materialCount <= 0)
            return;

        // Drop cache entries whose TMP source mesh was destroyed, and grab a fresh list of
        // TMP_SubMeshUI children to push the stacked geometry into.
        PruneDeadSourceMeshes();
        RefreshSubMeshUIs();

        // Identify which material slots render sprites (e.g. <sprite=3> icons) so we can skip
        // them below. Icons should render as single un-shadowed quads even when the surrounding
        // text is stacked.
        DetectSpriteSlots(textInfo, materialCount);

        int totalLayers = 1;
        for (int i = 0; i < _workingStacks.Count; i++)
        {
            var stackConfig = _workingStacks[i];
            if (!stackConfig.Enabled)
                continue;
            totalLayers += stackConfig.LayerCount;
        }

        GetNormalizedSoftnessAndDilate(MainTextDilate, MainTextSoftness, out float mainDilate, out float mainSoftness);
        var mainUV3 = new Vector2(mainDilate, mainSoftness);
        Color32 mainFallbackColor = _workingStacks.Count > 0 ? (Color32)_workingStacks[0].Color.Evaluate(0) : default;

        bool hasAnySwaps = HasAnyColorSwaps();
        if (hasAnySwaps)
            ParseCharColorTags();

        // --- BUILD ONE STACKED MESH PER MATERIAL ---
        // meshInfo[0] is the primary text mesh; meshInfo[1..N] are TMP fallback sub-meshes.
        // RTL / Arabic glyphs that are missing from the primary font atlas live in those
        // fallback sub-meshes, so we have to apply the stacking effect to every slot, not
        // just slot 0.
        for (int m = 0; m < materialCount; m++)
        {
            // Sprite (icon) slots still get processed — curve and scale must apply so icons
            // bend and resize along with the surrounding text — but the stack-layer build
            // below is skipped, so icons never get duplicated into shadow copies.
            bool isSpriteSlotHere = m < _isSpriteSlot.Count && _isSpriteSlot[m];

            Mesh sourceMesh = textInfo.meshInfo[m].mesh;
            if (sourceMesh == null || sourceMesh.vertexCount == 0)
            {
                // Nothing to draw for this slot. Clear any stale stacked mesh so the renderer
                // doesn't keep rendering old geometry.
                var staleRenderer = GetCanvasRendererForMaterial(m, textInfo);
                if (staleRenderer != null)
                    staleRenderer.SetMesh(null);
                continue;
            }

            // Non-allocating reads into cached lists.
            sourceMesh.GetVertices(_sourceVerts);
            sourceMesh.GetColors(_sourceColors);
            sourceMesh.GetUVs(0, _sourceUVs);
            sourceMesh.GetUVs(1, _sourceUV2s);
            sourceMesh.GetTriangles(_sourceTris, 0);

            int sourceVCount = _sourceVerts.Count;
            int sourceTriCount = _sourceTris.Count;

            if (sourceVCount * totalLayers > 65000)
            {
                if (Application.isEditor)
                    Debug.LogWarning($"[StackedText] Vertex limit exceeded on material slot {m}.");
                continue;
            }

            // --- APPLY CURVE OFFSETS (per material) ---
            if (IsCurveActive() && Curve.TryGetVertexOffsets(Text, m, _curveOffsets))
            {
                int loopCount = Mathf.Min(sourceVCount, _curveOffsets.Count);
                for (int v = 0; v < loopCount; v++)
                    _sourceVerts[v] += _curveOffsets[v];
            }

            // --- APPLY ROTATE OFFSETS (per material) ---
            // We also fetch each vertex's post-rotation local Z axis so stack depth offsets
            // (StackDepths) push along the rotated direction rather than world Z. Cleared on
            // slots without active rotation so the stack loop falls back to flat world-Z depth.
            _localZAxes.Clear();
            if (IsRotateActive())
            {
                if (Rotate.TryGetVertexOffsets(Text, m, _rotateOffsets))
                {
                    int loopCount = Mathf.Min(sourceVCount, _rotateOffsets.Count);
                    for (int v = 0; v < loopCount; v++)
                        _sourceVerts[v] += _rotateOffsets[v];
                }
                Rotate.TryGetLocalZAxes(Text, m, _localZAxes);
            }

            // --- APPLY SCALE OFFSETS (per material) ---
            // Applied LAST and computed against the current _sourceVerts (post curve+rotate) so
            // scale composes multiplicatively: at scale=0 the character truly collapses to its
            // pivot regardless of what curve/rotate did first. Reading the pristine TMP source
            // for this would re-introduce the original (v - pivot) and prevent collapse.
            if (IsScaleActive() && Scale.TryGetVertexOffsets(Text, m, _sourceVerts, _scaleOffsets))
            {
                int loopCount = Mathf.Min(sourceVCount, _scaleOffsets.Count);
                for (int v = 0; v < loopCount; v++)
                    _sourceVerts[v] += _scaleOffsets[v];
            }

            // --- FETCH PER-CHARACTER SCALES FOR STACK OFFSETS ---
            // Used in the stack loop below to multiply the entire stack vertOffset (X/Y offset +
            // local-Z depth) by each character's scale, so a scaled-down character takes its
            // stacks with it. Cleared when scale is inactive so the stack loop's gate falls back
            // to "no scaling".
            _charScales.Clear();
            if (IsScaleActive())
                Scale.TryGetCharacterScales(Text, m, _charScales);

            // --- PREPARE OUTPUT ---
            int totalVertCount = sourceVCount * totalLayers;
            int totalTriCount = sourceTriCount * totalLayers;
            ClearAndEnsureCapacity(_outVerts, totalVertCount);
            ClearAndEnsureCapacity(_outColors, totalVertCount);
            ClearAndEnsureCapacity(_outUVs, totalVertCount);
            ClearAndEnsureCapacity(_outUV2s, totalVertCount);
            ClearAndEnsureCapacity(_outUV3s, totalVertCount);
            ClearAndEnsureCapacity(_outTris, totalTriCount);

            // --- STACK LAYERS ---
            // Skipped on sprite slots — icons render as a single (curved/scaled) quad with no
            // shadow copies. The slot still gets the "main layer" pass below.
            if (!isSpriteSlotHere)
            {
                if (hasAnySwaps)
                    BuildVertexColorTagMap(textInfo, m, sourceVCount);

                for (int s = _workingStacks.Count - 1; s >= 0; s--)
                {
                    var stackConfig = _workingStacks[s];
                    if (!stackConfig.Enabled)
                        continue;

                    GetNormalizedSoftnessAndDilate(stackConfig.Dilate, stackConfig.Softness, out float dilate, out float softness);
                    var uv3 = new Vector2(dilate, softness);
                    var layerCount = stackConfig.LayerCount;
                    // Per-stack depth from the optional Rotate module. When non-zero, the offset
                    // is pushed along each vertex's character-local Z axis (post-rotation) so
                    // stacks stay "behind" each rotated character; otherwise it falls back to
                    // world Z via the default (0,0,1) entries in _localZAxes.
                    float stackDepth = IsRotateActive() ? Rotate.GetStackDepth(s) : 0f;
                    bool applyLocalDepth = stackDepth != 0f && _localZAxes.Count >= sourceVCount;
                    bool applyCharScale = _charScales.Count >= sourceVCount;
                    bool stackHasSwaps = hasAnySwaps && stackConfig.ColorSwaps != null && stackConfig.ColorSwaps.Count > 0;
                    for (int i = layerCount; i >= 1; i--)
                    {
                        float t = layerCount == 1 ? 1 : (i - 1) / ((float)layerCount - 1);
                        Color32 layerColor = stackConfig.Color.Evaluate(t);
                        Vector3 currentOffset = stackConfig.GetOffset(t);
                        int currentLayerVertStart = _outVerts.Count;

                        for (int v = 0; v < sourceVCount; v++)
                        {
                            Vector3 vertOffset = currentOffset;
                            if (applyLocalDepth)
                                vertOffset += stackDepth * _localZAxes[v];
                            if (applyCharScale)
                                vertOffset *= _charScales[v];
                            _outVerts.Add(_sourceVerts[v] + vertOffset);
                            _outUVs.Add(_sourceUVs[v]);
                            _outUV2s.Add(_sourceUV2s[v]);
                            _outUV3s.Add(uv3);

                            Color32 vertexColor = layerColor;
                            if (stackHasSwaps && _vertexHasTagColor[v] &&
                                TryGetSwappedColor(stackConfig.ColorSwaps, _vertexTagColors[v], out var swappedColor))
                                vertexColor = swappedColor;
                            _outColors.Add(vertexColor);
                        }

                        for (int tIdx = 0; tIdx < sourceTriCount; tIdx++)
                            _outTris.Add(_sourceTris[tIdx] + currentLayerVertStart);
                    }
                }
            }

            // --- MAIN TEXT LAYER ---
            // For sprite slots we always render the icon's source colors. ShowMainText only
            // governs whether the underlying *text* is hidden (for shadow-only effects); it
            // must not hide icons — they have no shadow stack to stand in for them.
            bool useSourceColorsForMain = isSpriteSlotHere || ShowMainText;
            int mainTextVertStart = _outVerts.Count;
            for (int v = 0; v < sourceVCount; v++)
            {
                _outVerts.Add(_sourceVerts[v]);
                _outUVs.Add(_sourceUVs[v]);
                _outUV2s.Add(_sourceUV2s[v]);
                _outUV3s.Add(mainUV3);
                _outColors.Add(useSourceColorsForMain ? _sourceColors[v] : mainFallbackColor);
            }

            for (int tIdx = 0; tIdx < sourceTriCount; tIdx++)
                _outTris.Add(_sourceTris[tIdx] + mainTextVertStart);

            // --- ASSIGN TO MESH ---
            var cachedMesh = GetOrCreateStackedMesh(sourceMesh, m);
            cachedMesh.Clear();
            cachedMesh.SetVertices(_outVerts);
            cachedMesh.SetColors(_outColors);
            cachedMesh.SetUVs(0, _outUVs);
            cachedMesh.SetUVs(1, _outUV2s);
            cachedMesh.SetUVs(3, _outUV3s);
            cachedMesh.SetTriangles(_outTris, 0);
            cachedMesh.RecalculateBounds();

            var renderer = GetCanvasRendererForMaterial(m, textInfo);
            if (renderer != null)
                renderer.SetMesh(cachedMesh);

            // Best-effort warning for fallback (e.g. Arabic) sub-meshes whose material doesn't
            // read UV3 dilate/softness. Stacking still works — softness/dilate per layer just
            // won't render on those glyphs unless the user swaps in a Distance Field Dilate
            // material for the fallback font. Sprite slots are excluded: we don't stack icons,
            // and sprite shaders intentionally don't use the dilate shader.
            if (m > 0 && !isSpriteSlotHere && !_hasWarnedAboutFallbackShader && Application.isPlaying)
            {
                var subMat = textInfo.meshInfo[m].material;
                if (subMat != null && subMat.shader != null && subMat.shader.name != RequiredShaderName)
                {
                    Debug.LogWarning(
                        $"[StackedText] Fallback sub-mesh material '{subMat.name}' uses shader " +
                        $"'{subMat.shader.name}' instead of '{RequiredShaderName}'. Offset/color " +
                        $"stacking will render correctly, but per-stack softness/dilate will not. " +
                        $"To fix: assign a material with the Distance Field Dilate shader to the " +
                        $"fallback font asset used by this sub-mesh.", this);
                    _hasWarnedAboutFallbackShader = true;
                }
            }
        }

        SaveLastUsedProperties();
    }

    private Mesh GetOrCreateStackedMesh(Mesh sourceMesh, int materialIndex)
    {
        if (_stackedMeshBySourceMesh.TryGetValue(sourceMesh, out var stackedMesh) && stackedMesh != null)
            return stackedMesh;

        stackedMesh = new Mesh { name = $"StackedText (slot {materialIndex})" };
        stackedMesh.MarkDynamic();
        _stackedMeshBySourceMesh[sourceMesh] = stackedMesh;
        return stackedMesh;
    }

    private void PruneDeadSourceMeshes()
    {
        // TMP can destroy and recreate its meshes (font/material swaps, sub-mesh cleanup); drop
        // those entries and destroy our paired mesh so the cache doesn't accumulate orphans.
        if (_stackedMeshBySourceMesh.Count == 0)
            return;

        _deadSourceMeshes.Clear();
        foreach (var (sourceMesh, stackedMesh) in _stackedMeshBySourceMesh)
        {
            if (sourceMesh != null)
                continue;

            _deadSourceMeshes.Add(sourceMesh);
            if (stackedMesh == null)
                continue;
            if (Application.isPlaying)
                Destroy(stackedMesh);
            else
                DestroyImmediate(stackedMesh);
        }

        for (int i = 0; i < _deadSourceMeshes.Count; i++)
            _stackedMeshBySourceMesh.Remove(_deadSourceMeshes[i]);
    }

    private static void ClearAndEnsureCapacity<T>(List<T> list, int capacity)
    {
        list.Clear();
        if (list.Capacity < capacity)
            list.Capacity = capacity;
    }

    private void PopulateInvalidStackConfigs()
    {
        // When the user adds a new entry, it may still hold the struct default
        // (LayerCount == 0, etc.) — replace those with sensible defaults so the user sees
        // something on screen instead of an empty/invalid stack.
        for (int i = 0; i < Stacks.Count; i++)
        {
            if (!Stacks[i].IsInvalid())
                continue;
            Stacks[i] = StackConfig.CreateDefault();
        }
    }

    private bool HasPropertiesChanged()
    {
        if (!Mathf.Approximately(_lastMainTextDilate, MainTextDilate))
            return true;
        if (!Mathf.Approximately(_lastMainTextSoftness, MainTextSoftness))
            return true;
        if (_lastShowMainText != ShowMainText)
            return true;
        if (_lastStackCount != _workingStacks.Count)
            return true;
        if (_lastCurveRef != Curve)
            return true;
        if (_lastCurveActive != IsCurveActive())
            return true;
        if (Curve != null && _lastCurveHash != Curve.GetParametersHash())
            return true;
        if (_lastScaleRef != Scale)
            return true;
        if (_lastScaleActive != IsScaleActive())
            return true;
        if (Scale != null && _lastScaleHash != Scale.GetParametersHash())
            return true;
        if (_lastRotateRef != Rotate)
            return true;
        if (_lastRotateActive != IsRotateActive())
            return true;
        if (Rotate != null && _lastRotateHash != Rotate.GetParametersHash())
            return true;
        if (_lastAnimatableStacksRef != AnimatableStacks)
            return true;
        if (_lastAnimatableStacksActive != IsAnimatableStacksActive())
            return true;

        if (_lastStacks.Count != _workingStacks.Count)
            return true;
        for (int i = 0; i < _workingStacks.Count; i++)
        {
            if (_lastStacks[i].HasChanged(_workingStacks[i]))
                return true;
        }
        return false;
    }

    private bool IsCurveActive()
    {
        return Curve != null && Curve.enabled && Curve.gameObject.activeInHierarchy;
    }

    private bool IsScaleActive()
    {
        return Scale != null && Scale.enabled && Scale.gameObject.activeInHierarchy;
    }

    private bool IsRotateActive()
    {
        return Rotate != null && Rotate.enabled && Rotate.gameObject.activeInHierarchy;
    }

    private bool IsAnimatableStacksActive()
    {
        return AnimatableStacks != null && AnimatableStacks.enabled && AnimatableStacks.gameObject.activeInHierarchy;
    }

    private void RebuildWorkingStacks()
    {
        _workingStacks.Clear();
        for (int i = 0; i < Stacks.Count; i++)
            _workingStacks.Add(Stacks[i]);
        if (IsAnimatableStacksActive())
            AnimatableStacks.AppendActiveStacks(_workingStacks);
    }

    private bool IsExtraPaddingRequired()
    {
        if (Text == null || Text.canvas == null)
            return false;

        var totalDilate = MathF.Abs(MainTextDilate);
        var totalSoftness = MathF.Abs(MainTextSoftness);
        for (int i = 0; i < _workingStacks.Count; i++)
        {
            var stack = _workingStacks[i];
            if (!stack.Enabled)
                continue;
            totalDilate += MathF.Abs(stack.Dilate);
            totalSoftness += MathF.Abs(stack.Softness);
        }

        return totalDilate + totalSoftness > 0.001f;
    }

    private bool HasAnyColorSwaps()
    {
        for (int i = 0; i < _workingStacks.Count; i++)
        {
            var stack = _workingStacks[i];
            if (!stack.Enabled)
                continue;
            if (stack.ColorSwaps != null && stack.ColorSwaps.Count > 0)
                return true;
        }
        return false;
    }

    private void ParseCharColorTags()
    {
        _charTagColors.Clear();
        _charHasTag.Clear();
        _colorTagStack.Clear();

        if (!Text.richText)
            return;

        var rawText = Text.text;
        int rawLength = rawText.Length;
        int pos = 0;
        while (pos < rawLength)
        {
            if (rawText[pos] == '<')
            {
                if (TryParseColorOpenTag(rawText, pos, out var tagColor, out int tagEnd))
                {
                    _colorTagStack.Push(tagColor);
                    pos = tagEnd + 1;
                    continue;
                }

                if (IsColorCloseTag(rawText, pos, out int closeEnd))
                {
                    if (_colorTagStack.Count > 0)
                        _colorTagStack.Pop();
                    pos = closeEnd + 1;
                    continue;
                }

                int closeBracket = rawText.IndexOf('>', pos + 1);
                if (closeBracket >= 0)
                {
                    pos = closeBracket + 1;
                    continue;
                }
            }

            bool hasTag = _colorTagStack.Count > 0;
            _charHasTag.Add(hasTag);
            _charTagColors.Add(hasTag ? _colorTagStack.Peek() : default);
            pos++;
        }
    }

    private void BuildVertexColorTagMap(TMP_TextInfo textInfo, int materialIndex, int sourceVertexCount)
    {
        ClearAndFillDefault(_vertexTagColors, sourceVertexCount);
        ClearAndFillDefault(_vertexHasTagColor, sourceVertexCount);

        int charCount = textInfo.characterCount;
        for (int i = 0; i < charCount; i++)
        {
            var charInfo = textInfo.characterInfo[i];
            if (!charInfo.isVisible)
                continue;
            if (charInfo.materialReferenceIndex != materialIndex)
                continue;
            if (i >= _charHasTag.Count || !_charHasTag[i])
                continue;

            int vi = charInfo.vertexIndex;
            if (vi + 3 >= sourceVertexCount)
                continue;

            var tagColor = _charTagColors[i];
            for (int j = 0; j < 4; j++)
            {
                _vertexTagColors[vi + j] = tagColor;
                _vertexHasTagColor[vi + j] = true;
            }
        }
    }

    private static bool TryParseColorOpenTag(string text, int startPos, out Color32 color, out int tagEndPos)
    {
        color = default;
        tagEndPos = startPos;

        int remaining = text.Length - startPos;
        if (remaining < 10)
            return false;

        if (text[startPos + 1] != 'c' || text[startPos + 2] != 'o' || text[startPos + 3] != 'l' ||
            text[startPos + 4] != 'o' || text[startPos + 5] != 'r' || text[startPos + 6] != '=')
            return false;

        int closePos = text.IndexOf('>', startPos + 7);
        if (closePos < 0)
            return false;

        int valueStart = startPos + 7;
        int valueLength = closePos - valueStart;
        if (valueLength <= 0)
            return false;

        var colorString = text.Substring(valueStart, valueLength);
        if (colorString[0] != '#')
            colorString = "#" + colorString;

        if (!ColorUtility.TryParseHtmlString(colorString, out var parsed))
            return false;

        color = parsed;
        tagEndPos = closePos;
        return true;
    }

    private static bool IsColorCloseTag(string text, int startPos, out int tagEndPos)
    {
        tagEndPos = startPos;

        int remaining = text.Length - startPos;
        if (remaining < 8)
            return false;

        if (text[startPos + 1] == '/' &&
            text[startPos + 2] == 'c' &&
            text[startPos + 3] == 'o' &&
            text[startPos + 4] == 'l' &&
            text[startPos + 5] == 'o' &&
            text[startPos + 6] == 'r' &&
            text[startPos + 7] == '>')
        {
            tagEndPos = startPos + 7;
            return true;
        }
        return false;
    }

    private static bool TryGetSwappedColor(List<ColorSwapPair> swaps, Color32 source, out Color32 target)
    {
        for (int i = 0; i < swaps.Count; i++)
        {
            var swap = swaps[i];
            if (swap.Source.r == source.r && swap.Source.g == source.g && swap.Source.b == source.b)
            {
                target = swap.Target;
                return true;
            }
        }
        target = default;
        return false;
    }

    private static void ClearAndFillDefault<T>(List<T> list, int count)
    {
        list.Clear();
        if (list.Capacity < count)
            list.Capacity = count;
        for (int i = 0; i < count; i++)
            list.Add(default);
    }

    #endregion

    #region Public API

    private void SaveLastUsedProperties()
    {
        _lastShowMainText = ShowMainText;
        _lastMainTextDilate = MainTextDilate;
        _lastMainTextSoftness = MainTextSoftness;
        _lastCurveRef = Curve;
        _lastCurveActive = IsCurveActive();
        _lastCurveHash = Curve != null ? Curve.GetParametersHash() : 0;
        _lastScaleRef = Scale;
        _lastScaleActive = IsScaleActive();
        _lastScaleHash = Scale != null ? Scale.GetParametersHash() : 0;
        _lastRotateRef = Rotate;
        _lastRotateActive = IsRotateActive();
        _lastRotateHash = Rotate != null ? Rotate.GetParametersHash() : 0;
        _lastAnimatableStacksRef = AnimatableStacks;
        _lastAnimatableStacksActive = IsAnimatableStacksActive();
        _lastStackCount = _workingStacks.Count;
        _lastStacks.Clear();
        for (int i = 0; i < _workingStacks.Count; i++)
            _lastStacks.Add(_workingStacks[i]);
    }

    /// <summary>
    /// Bulk-set the stack configs. Pass an empty or null collection to disable all stacking
    /// (the main layer still renders).
    /// </summary>
    public void SetStacks(IList<StackConfig> stacks)
    {
        Stacks.Clear();
        if (stacks != null)
        {
            for (int i = 0; i < stacks.Count; i++)
                Stacks.Add(stacks[i]);
        }

        _forceUpdateNextFrame = true;
        if (Text != null)
            Text.ForceMeshUpdate();
    }

    /// <summary>
    /// Forces the next render pass to rebuild the stacked mesh. Used by <see cref="StackedTextCurve"/>
    /// to push live updates from the editor when curve fields change.
    /// </summary>
    public void MarkDirty()
    {
        _forceUpdateNextFrame = true;
    }

    public void GetNormalizedSoftnessAndDilate(float dilate, float softness, out float normalizedDilate, out float normalizedSoftness)
    {
        var total = MathF.Max(softness + dilate, 1);
        normalizedDilate = dilate / total * 0.85f;
        normalizedSoftness = softness / total * 0.85f;
    }

    #endregion

    #region StackConfig

    [Serializable]
    public struct StackConfig
    {
        public bool Enabled;
        // Number of duplicated copies inside this single stack (used for depth/blur). Renamed
        // from StackCount to LayerCount so it doesn't collide with the component-level
        // StackCount that controls how many stacks are active. FormerlySerializedAs preserves
        // any existing serialized data from before the rename.
        [Range(1, 6)]
        [FormerlySerializedAs("StackCount")]
        public int LayerCount;
        public Gradient Color;
        public Vector2 StartOffset;
        public Vector2 EndOffset;
        [Range(0, 1)] public float Softness;
        [Range(-1f, 1f)] public float Dilate;
        public List<ColorSwapPair> ColorSwaps;

        public static StackConfig CreateDefault()
        {
            return new StackConfig
            {
                Enabled = true,
                LayerCount = 1,
                Color = new Gradient()
                {
                    colorKeys = new GradientColorKey[]
                    {
                        new(UnityEngine.Color.white, 0f),
                        new(UnityEngine.Color.black, 1f),
                    },
                    alphaKeys = new GradientAlphaKey[]
                    {
                        new(1f, 0f),
                        new(1f, 1f),
                    },
                },
                EndOffset = new Vector2(2f, -2f),
                ColorSwaps = new(),
            };
        }

        public Vector2 GetOffset(float t)
        {
            return Vector2.Lerp(StartOffset, EndOffset, t);
        }

        public bool IsInvalid()
        {
            return LayerCount < 1;
        }

        public bool HasChanged(StackConfig other)
        {
            return Enabled != other.Enabled ||
                LayerCount != other.LayerCount ||
                !Mathf.Approximately(Dilate, other.Dilate) ||
                !Mathf.Approximately(Softness, other.Softness) ||
                StartOffset != other.StartOffset ||
                EndOffset != other.EndOffset ||
                !Color.Equals(other.Color) ||
                HasColorSwapsChanged(other.ColorSwaps);
        }

        private bool HasColorSwapsChanged(List<ColorSwapPair> other)
        {
            int countA = ColorSwaps?.Count ?? 0;
            int countB = other?.Count ?? 0;
            if (countA != countB)
                return true;

            for (int i = 0; i < countA; i++)
            {
                var a = ColorSwaps[i];
                var b = other[i];
                if (a.Source.r != b.Source.r || a.Source.g != b.Source.g || a.Source.b != b.Source.b ||
                    a.Target.r != b.Target.r || a.Target.g != b.Target.g || a.Target.b != b.Target.b)
                    return true;
            }
            return false;
        }
    }

    [Serializable]
    public struct ColorSwapPair
    {
        public Color32 Source;
        public Color32 Target;
    }

    #endregion
}