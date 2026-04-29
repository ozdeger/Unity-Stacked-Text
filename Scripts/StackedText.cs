using System;
using System.Collections.Generic;
using NaughtyAttributes;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

[ExecuteInEditMode]
public class StackedText : MonoBehaviour
{
    #region Fields

    private const string RequiredShaderName = "TextMeshPro/Distance Field Dilate";
    public const int MaxStacks = 8;

    [SerializeField] private TMP_Text Text;
    [SerializeField] private bool ShowMainText = true;
    [Range(0, 1)] public float MainTextSoftness;
    [Range(-1, 1f)] public float MainTextDilate;

    [Tooltip("How many of the 8 stack slots below are active. Stacks beyond this count are " +
             "hidden in the inspector and skipped at runtime. Set to 0 to disable stacking " +
             "entirely (main layer still renders).")]
    [Range(0, MaxStacks)]
    public int StackCount = 0;

    [ShowIf(nameof(ShowStack0))] public StackConfig Stack0;
    [ShowIf(nameof(ShowStack1))] public StackConfig Stack1;
    [ShowIf(nameof(ShowStack2))] public StackConfig Stack2;
    [ShowIf(nameof(ShowStack3))] public StackConfig Stack3;
    [ShowIf(nameof(ShowStack4))] public StackConfig Stack4;
    [ShowIf(nameof(ShowStack5))] public StackConfig Stack5;
    [ShowIf(nameof(ShowStack6))] public StackConfig Stack6;
    [ShowIf(nameof(ShowStack7))] public StackConfig Stack7;

    private bool ShowStack0() => StackCount > 0;
    private bool ShowStack1() => StackCount > 1;
    private bool ShowStack2() => StackCount > 2;
    private bool ShowStack3() => StackCount > 3;
    private bool ShowStack4() => StackCount > 4;
    private bool ShowStack5() => StackCount > 5;
    private bool ShowStack6() => StackCount > 6;
    private bool ShowStack7() => StackCount > 7;

    public StackConfig GetStack(int index)
    {
        switch (index)
        {
            case 0: return Stack0;
            case 1: return Stack1;
            case 2: return Stack2;
            case 3: return Stack3;
            case 4: return Stack4;
            case 5: return Stack5;
            case 6: return Stack6;
            case 7: return Stack7;
            default: return default;
        }
    }

    private void SetStack(int index, StackConfig value)
    {
        switch (index)
        {
            case 0: Stack0 = value; break;
            case 1: Stack1 = value; break;
            case 2: Stack2 = value; break;
            case 3: Stack3 = value; break;
            case 4: Stack4 = value; break;
            case 5: Stack5 = value; break;
            case 6: Stack6 = value; break;
            case 7: Stack7 = value; break;
        }
    }

    [Header("Optional")]
    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, " +
             "its animation curve is applied to the text vertices before stacking.")]
    [SerializeField] private StackedTextCurve Curve;

    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, " +
             "its animation curve is used to scale each character around its baseline midpoint " +
             "before stacking.")]
    [SerializeField] private StackedTextScale Scale;

    // One cached mesh per TMP material slot (index 0 = primary, index 1+ = fallback sub-meshes
    // used by Arabic / RTL glyphs and any other character that is not in the primary font atlas).
    private readonly List<Mesh> _cachedMeshes = new();
    private readonly List<TMP_SubMeshUI> _subMeshUIs = new();
    // Per-material flag: true if the slot only renders TMP sprites (e.g. <sprite=3> icons).
    // Sprite slots are deliberately left untouched by the stacking pass so icons render as
    // single, un-shadowed quads alongside stacked text.
    private readonly List<bool> _isSpriteSlot = new();
    private int _lastMaterialCount;
    // Tracks whether we've already warned about a fallback (e.g. Arabic) sub-mesh whose material
    // doesn't use the Distance Field Dilate shader. Without that shader, the per-stack softness
    // and dilate values written into UV3 are ignored — offset + color stacking still works, but
    // softness/dilate variation per layer won't render on those glyphs. Warn once per session so
    // the log doesn't spam.
    private bool _hasWarnedAboutFallbackShader;
    private readonly List<Vector3> _curveOffsets = new();
    private readonly List<Vector3> _scaleOffsets = new();
    private readonly List<Vector3> _sourceVerts = new();
    private readonly List<Color32> _sourceColors = new();
    private readonly List<Vector2> _sourceUVs = new();
    private readonly List<Vector2> _sourceUV2s = new();
    private readonly List<int> _sourceTris = new();
    private readonly List<Vector3> _outVerts = new();
    private readonly List<Color32> _outColors = new();
    private readonly List<Vector2> _outUVs = new();
    private readonly List<Vector2> _outUV2s = new();
    private readonly List<Vector2> _outUV3s = new();
    private readonly List<int> _outTris = new();
    private bool _lastShowMainText;
    private float _lastMainTextDilate;
    private float _lastMainTextSoftness;
    private int _lastStackCount;
    private readonly StackConfig[] _lastStacks = new StackConfig[MaxStacks];
    private Vector3 _lastLossyScale;
    private bool _forceUpdateNextFrame;
    private StackedTextCurve _lastCurveRef;
    private bool _lastCurveActive;
    private int _lastCurveHash;
    private StackedTextScale _lastScaleRef;
    private bool _lastScaleActive;
    private int _lastScaleHash;

    #endregion

    #region Editor - Material Validation

#if UNITY_EDITOR
    private void OnValidate()
    {
        Text ??= GetComponent<TMP_Text>();
        TryAutoCollectCurve();
        TryAutoCollectScale();
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

        if (_forceUpdateNextFrame || scaleChanged || meshOverwritten || HasPropertiesChanged())
        {
            GenerateStackedText();
            _forceUpdateNextFrame = false;
            _lastLossyScale = transform.lossyScale;
        }
    }

    private bool AnyCachedMeshOverwritten()
    {
        // If TMP rebuilt and re-assigned its own mesh on any slot we manage (including sprite
        // slots, since we now push curved/scaled icon geometry there too), our cached mesh is
        // no longer on screen and we must regenerate.
        var textInfo = Text.textInfo;
        if (textInfo == null || _cachedMeshes.Count == 0)
            return false;

        int slotsToCheck = Mathf.Min(Mathf.Min(_lastMaterialCount, textInfo.materialCount), _cachedMeshes.Count);
        for (int m = 0; m < slotsToCheck; m++)
        {
            var renderer = GetCanvasRendererForMaterial(m, textInfo);
            if (renderer == null) continue;

            var cached = _cachedMeshes[m];
            if (cached == null) continue;

            if (renderer.GetMesh() != cached)
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

        var isExtraPaddingRequired = IsExtraPaddingRequired();
        if (isExtraPaddingRequired != Text.extraPadding)
            Text.extraPadding = isExtraPaddingRequired;

        int materialCount = textInfo.materialCount;
        if (materialCount <= 0)
            return;

        // Make sure we have one cached output mesh per material slot, and a fresh list of
        // TMP_SubMeshUI children to push the stacked geometry into.
        EnsureCachedMeshCapacity(materialCount);
        RefreshSubMeshUIs();

        // Identify which material slots render sprites (e.g. <sprite=3> icons) so we can skip
        // them below. Icons should render as single un-shadowed quads even when the surrounding
        // text is stacked.
        DetectSpriteSlots(textInfo, materialCount);

        int totalLayers = 1;
        for (int i = 0; i < StackCount; i++)
        {
            var stackConfig = GetStack(i);
            if (!stackConfig.Enabled)
                continue;
            totalLayers += stackConfig.LayerCount;
        }

        GetNormalizedSoftnessAndDilate(MainTextDilate, MainTextSoftness, out float mainDilate, out float mainSoftness);
        var mainUV3 = new Vector2(mainDilate, mainSoftness);
        Color32 mainFallbackColor = StackCount > 0 ? (Color32)GetStack(0).Color.Evaluate(0) : default;

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

            // --- APPLY SCALE OFFSETS (per material) ---
            if (IsScaleActive() && Scale.TryGetVertexOffsets(Text, m, _scaleOffsets))
            {
                int loopCount = Mathf.Min(sourceVCount, _scaleOffsets.Count);
                for (int v = 0; v < loopCount; v++)
                    _sourceVerts[v] += _scaleOffsets[v];
            }

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
                for (int s = StackCount - 1; s >= 0; s--)
                {
                    var stackConfig = GetStack(s);
                    if (!stackConfig.Enabled)
                        continue;

                    GetNormalizedSoftnessAndDilate(stackConfig.Dilate, stackConfig.Softness, out float dilate, out float softness);
                    var uv3 = new Vector2(dilate, softness);
                    var layerCount = stackConfig.LayerCount;
                    for (int i = layerCount; i >= 1; i--)
                    {
                        float t = layerCount == 1 ? 1 : (i - 1) / ((float)layerCount - 1);
                        Color32 layerColor = stackConfig.Color.Evaluate(t);
                        Vector3 currentOffset = stackConfig.GetOffset(t);
                        int currentLayerVertStart = _outVerts.Count;

                        for (int v = 0; v < sourceVCount; v++)
                        {
                            _outVerts.Add(_sourceVerts[v] + currentOffset);
                            _outUVs.Add(_sourceUVs[v]);
                            _outUV2s.Add(_sourceUV2s[v]);
                            _outUV3s.Add(uv3);
                            _outColors.Add(layerColor);
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
            var cachedMesh = _cachedMeshes[m];
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

        _lastMaterialCount = materialCount;
        SaveLastUsedProperties();
    }

    private void EnsureCachedMeshCapacity(int materialCount)
    {
        while (_cachedMeshes.Count < materialCount)
        {
            var newMesh = new Mesh { name = $"StackedText (slot {_cachedMeshes.Count})" };
            newMesh.MarkDynamic();
            _cachedMeshes.Add(newMesh);
        }
    }

    private static void ClearAndEnsureCapacity<T>(List<T> list, int capacity)
    {
        list.Clear();
        if (list.Capacity < capacity)
            list.Capacity = capacity;
    }

    private void PopulateInvalidStackConfigs()
    {
        // When the user bumps StackCount, previously-untouched slots may still hold the struct
        // default (LayerCount == 0, etc.) — replace those with sensible defaults so the user
        // sees something on screen instead of an empty/invalid stack.
        for (int i = 0; i < StackCount; i++)
        {
            if (!GetStack(i).IsInvalid())
                continue;
            SetStack(i, StackConfig.CreateDefault());
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
        if (_lastStackCount != StackCount)
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

        for (int i = 0; i < StackCount; i++)
        {
            if (_lastStacks[i].HasChanged(GetStack(i)))
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

    private bool IsExtraPaddingRequired()
    {
        if (Text == null || Text.canvas == null)
            return false;

        var totalDilate = MathF.Abs(MainTextDilate);
        var totalSoftness = MathF.Abs(MainTextSoftness);
        for (int i = 0; i < StackCount; i++)
        {
            var stack = GetStack(i);
            if (!stack.Enabled)
                continue;
            totalDilate += MathF.Abs(stack.Dilate);
            totalSoftness += MathF.Abs(stack.Softness);
        }

        return totalDilate + totalSoftness > 0.001f;
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
        _lastStackCount = StackCount;
        for (int i = 0; i < MaxStacks; i++)
            _lastStacks[i] = GetStack(i);
    }

    /// <summary>
    /// Bulk-set the stack configs. Up to <see cref="MaxStacks"/> entries are copied into the
    /// fixed slots; <see cref="StackCount"/> is set to the count provided. Pass an empty or
    /// null collection to disable all stacking (this method does not clear the contents of
    /// existing slots — they're just hidden by setting StackCount to 0).
    /// </summary>
    public void SetStacks(IList<StackConfig> stacks)
    {
        if (stacks == null)
        {
            StackCount = 0;
        }
        else
        {
            int count = Mathf.Min(stacks.Count, MaxStacks);
            for (int i = 0; i < count; i++)
                SetStack(i, stacks[i]);
            StackCount = count;
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
                !Color.Equals(other.Color);
        }
    }

    #endregion
}