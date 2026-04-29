using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

[ExecuteInEditMode]
public class StackedText : MonoBehaviour
{
    #region Fields

    private const string RequiredShaderName = "TextMeshPro/Distance Field Dilate";

    [SerializeField] private TMP_Text Text;
    [SerializeField] private bool ShowMainText = true;
    [Range(0, 1)] public float MainTextSoftness;
    [Range(-1, 1f)] public float MainTextDilate;
    [SerializeField] public List<StackConfig> Stacks = new();

    [Header("Optional")]
    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, " +
             "its animation curve is applied to the text vertices before stacking.")]
    [SerializeField] private StackedTextCurve Curve;

    [Tooltip("Optional sibling component. If assigned (or present on this GameObject) and enabled, " +
             "its animation curve is used to scale each character around its baseline midpoint " +
             "before stacking.")]
    [SerializeField] private StackedTextScale Scale;

    private Mesh _cachedMesh;
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
    private readonly List<StackConfig> _lastStacks = new();
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
        bool meshOverwritten = _cachedMesh != null && Text.canvasRenderer.GetMesh() != _cachedMesh;

        if (_forceUpdateNextFrame || scaleChanged || meshOverwritten || HasPropertiesChanged())
        {
            GenerateStackedText();
            _forceUpdateNextFrame = false;
            _lastLossyScale = transform.lossyScale;
        }
    }

    private void GenerateStackedText()
    {
        if (Text == null)
            return;

        Text.ForceMeshUpdate();

        TMP_TextInfo textInfo = Text.textInfo;

        if (textInfo == null || textInfo.characterCount == 0)
            return;

        Mesh sourceMesh = Text.mesh;

        if (sourceMesh == null || sourceMesh.vertexCount == 0)
            return;

        PopulateInvalidStackConfigs();

        var isExtraPaddingRequired = IsExtraPaddingRequired();
        if (isExtraPaddingRequired != Text.extraPadding)
            Text.extraPadding = isExtraPaddingRequired;

        // Non-allocating mesh data reads into cached lists
        sourceMesh.GetVertices(_sourceVerts);
        sourceMesh.GetColors(_sourceColors);
        sourceMesh.GetUVs(0, _sourceUVs);
        sourceMesh.GetUVs(1, _sourceUV2s);
        sourceMesh.GetTriangles(_sourceTris, 0);

        int sourceVCount = _sourceVerts.Count;
        int sourceTriCount = _sourceTris.Count;
        int totalLayers = 1;
        foreach (var stackConfig in Stacks)
        {
            if (!stackConfig.Enabled)
                continue;
            totalLayers += stackConfig.StackCount;
        }

        if (sourceVCount * totalLayers > 65000)
        {
            if (Application.isEditor)
                Debug.LogWarning("[StackedText] Vertex limit exceeded.");
            return;
        }

        // --- APPLY CURVE OFFSETS ---
        if (IsCurveActive() && Curve.TryGetVertexOffsets(Text, _curveOffsets))
        {
            int loopCount = Mathf.Min(sourceVCount, _curveOffsets.Count);
            for (int v = 0; v < loopCount; v++)
                _sourceVerts[v] += _curveOffsets[v];
        }

        // --- APPLY SCALE OFFSETS ---
        if (IsScaleActive() && Scale.TryGetVertexOffsets(Text, _scaleOffsets))
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
        for (int s = Stacks.Count - 1; s >= 0; s--)
        {
            var stackConfig = Stacks[s];
            if (!stackConfig.Enabled)
                continue;

            GetNormalizedSoftnessAndDilate(stackConfig.Dilate, stackConfig.Softness, out float dilate, out float softness);
            var uv3 = new Vector2(dilate, softness);
            var stackCount = stackConfig.StackCount;
            for (int i = stackCount; i >= 1; i--)
            {
                float t = stackCount == 1 ? 1 : (i - 1) / ((float)stackCount - 1);
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

        GetNormalizedSoftnessAndDilate(MainTextDilate, MainTextSoftness, out float mainDilate, out float mainSoftness);

        // --- MAIN TEXT LAYER ---
        int mainTextVertStart = _outVerts.Count;
        Color32 mainFallbackColor = Stacks.Count > 0 ? (Color32)Stacks[0].Color.Evaluate(0) : default;
        var mainUV3 = new Vector2(mainDilate, mainSoftness);
        for (int v = 0; v < sourceVCount; v++)
        {
            _outVerts.Add(_sourceVerts[v]);
            _outUVs.Add(_sourceUVs[v]);
            _outUV2s.Add(_sourceUV2s[v]);
            _outUV3s.Add(mainUV3);
            _outColors.Add(ShowMainText ? _sourceColors[v] : mainFallbackColor);
        }

        for (int tIdx = 0; tIdx < sourceTriCount; tIdx++)
            _outTris.Add(_sourceTris[tIdx] + mainTextVertStart);

        // --- ASSIGN TO MESH ---
        if (_cachedMesh == null)
        {
            _cachedMesh = new Mesh();
            _cachedMesh.MarkDynamic();
        }

        _cachedMesh.Clear();
        _cachedMesh.SetVertices(_outVerts);
        _cachedMesh.SetColors(_outColors);
        _cachedMesh.SetUVs(0, _outUVs);
        _cachedMesh.SetUVs(1, _outUV2s);
        _cachedMesh.SetUVs(3, _outUV3s);
        _cachedMesh.SetTriangles(_outTris, 0);
        _cachedMesh.RecalculateBounds();

        Text.canvasRenderer.SetMesh(_cachedMesh);

        SaveLastUsedProperties();
    }

    private static void ClearAndEnsureCapacity<T>(List<T> list, int capacity)
    {
        list.Clear();
        if (list.Capacity < capacity)
            list.Capacity = capacity;
    }

    private void PopulateInvalidStackConfigs()
    {
        for (int i = Stacks.Count - 1; i >= 0; i--)
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
        if (_lastStacks.Count != Stacks.Count)
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

        for (int i = 0; i < Stacks.Count; i++)
        {
            if (_lastStacks[i].HasChanged(Stacks[i]))
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
        foreach (var stack in Stacks)
        {
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
        _lastStacks.Clear();
        _lastStacks.AddRange(Stacks);
    }

    public void SetStacks(List<StackConfig> stacks)
    {
        if (stacks == null || stacks == Stacks)
            return;

        Stacks = stacks;
        _forceUpdateNextFrame = true;
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
        [Range(1, 6)] public int StackCount;
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
                StackCount = 1,
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
            return StackCount < 1;
        }

        public bool HasChanged(StackConfig other)
        {
            return Enabled != other.Enabled ||
                StackCount != other.StackCount ||
                !Mathf.Approximately(Dilate, other.Dilate) ||
                !Mathf.Approximately(Softness, other.Softness) ||
                StartOffset != other.StartOffset ||
                EndOffset != other.EndOffset ||
                !Color.Equals(other.Color);
        }
    }

    #endregion
}