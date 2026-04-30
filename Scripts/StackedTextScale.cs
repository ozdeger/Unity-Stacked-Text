using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Optional companion component for <see cref="StackedText"/>. When present (and enabled) on the
/// same GameObject, <see cref="StackedText"/> will sample this component's <see cref="Curve"/> and
/// scale each character's vertices around its baseline midpoint based on the character's
/// horizontal position within the text bounds. Disable or remove the component to render text at
/// uniform scale.
/// </summary>
[ExecuteInEditMode]
[DisallowMultipleComponent]
public class StackedTextScale : MonoBehaviour
{
    #region Fields

    [Tooltip("Per-character scale multiplier sampled along the text's normalized x axis. " +
             "Y = 1 leaves the character at its original size, Y > 1 enlarges it, Y < 1 shrinks it.")]
    public AnimationCurve Curve = AnimationCurve.Constant(0f, 1f, 1f);

    [Tooltip("Shifts the curve evaluation along the X axis (value is divided by 10 internally).")]
    public float Phase = 0f;

    [Tooltip("If true, the curve is sampled mirrored along X (right-to-left). Leave off for " +
             "direct left-to-right sampling, which is the default and matches StackedTextCurve.")]
    public bool MirrorAlongX = false;

    [Tooltip("Optional override of the text bounds width used to normalize character positions. " +
             "If <= 0, the actual text bounds are used. Use this to keep scale stable across text changes.")]
    public float ReferenceWidth = 0f;

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectScale();
            stackedText.MarkDirty();
        }
    }

    private void OnDisable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
            stackedText.MarkDirty();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Ask the sibling StackedText to rebuild so scale tweaks show up immediately in the editor.
        // Note: AnimationCurve drags inside the curve editor don't always fire OnValidate, so
        // StackedText also detects parameter changes via GetParametersHash() each render.
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectScale();
            stackedText.MarkDirty();
        }
    }
#endif

    #endregion

    #region Public API

    /// <summary>
    /// Computes per-vertex offsets that, when added to the source TMP mesh vertices for the given
    /// <paramref name="materialIndex"/>, scale each character around its baseline midpoint
    /// according to <see cref="Curve"/>. The output list is sized to that sub-mesh's vertex count
    /// so RTL / Arabic fallback glyphs (rendered through TMP sub-meshes) are also scaled.
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, int materialIndex, List<Vector3> vertexOffsets)
    {
        if (text == null)
            return false;
        return TryGetScaledVertexOffsets(text, materialIndex, Curve, Phase, MirrorAlongX, ReferenceWidth, vertexOffsets);
    }

    /// <summary>
    /// Backwards-compatible overload that targets the primary material (index 0).
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, List<Vector3> vertexOffsets)
    {
        return TryGetVertexOffsets(text, 0, vertexOffsets);
    }

    /// <summary>
    /// Returns a hash that changes whenever any parameter that affects the output offsets changes
    /// (Curve keyframes/tangents, Phase, MirrorAlongX, ReferenceWidth). Used by <see cref="StackedText"/>
    /// to detect edits even when Unity does not fire <c>OnValidate</c> (e.g. live drags inside the
    /// AnimationCurve editor).
    /// </summary>
    public int GetParametersHash()
    {
        unchecked
        {
            int hash = 13;
            hash = hash * 31 + Phase.GetHashCode();
            hash = hash * 31 + MirrorAlongX.GetHashCode();
            hash = hash * 31 + ReferenceWidth.GetHashCode();
            if (Curve != null)
            {
                hash = hash * 31 + Curve.length;
                hash = hash * 31 + (int)Curve.preWrapMode;
                hash = hash * 31 + (int)Curve.postWrapMode;
                for (int i = 0; i < Curve.length; i++)
                {
                    var key = Curve[i];
                    hash = hash * 31 + key.time.GetHashCode();
                    hash = hash * 31 + key.value.GetHashCode();
                    hash = hash * 31 + key.inTangent.GetHashCode();
                    hash = hash * 31 + key.outTangent.GetHashCode();
                    hash = hash * 31 + key.weightedMode.GetHashCode();
                    hash = hash * 31 + key.inWeight.GetHashCode();
                    hash = hash * 31 + key.outWeight.GetHashCode();
                }
            }
            return hash;
        }
    }

    public static bool TryGetScaledVertexOffsets(TMP_Text text, int materialIndex, AnimationCurve curve, float phase, bool mirrorAlongX, float referenceWidth, List<Vector3> vertexOffsets)
    {
        if (curve == null)
            return false;

        TMP_TextInfo textInfo = text.textInfo;
        int characterCount = textInfo != null ? textInfo.characterCount : 0;

        if (characterCount == 0 || textInfo.meshInfo == null || textInfo.meshInfo.Length == 0)
            return false;

        if (materialIndex < 0 || materialIndex >= textInfo.meshInfo.Length)
            return false;

        var targetVertices = textInfo.meshInfo[materialIndex].vertices;
        if (targetVertices == null)
            return false;

        StackedTextCurve.GetBounds(text, referenceWidth, out var boundsMaxX, out var boundsMinX);

        int requiredLength = targetVertices.Length;
        vertexOffsets.Clear();
        if (vertexOffsets.Capacity < requiredLength)
            vertexOffsets.Capacity = requiredLength;
        for (int i = 0; i < requiredLength; i++)
            vertexOffsets.Add(default);

        float range = boundsMaxX - boundsMinX;
        if (Mathf.Approximately(range, 0f))
            return true;

        float phaseShift = phase / 10f;

        for (int i = 0; i < characterCount; i++)
        {
            var charInfo = textInfo.characterInfo[i];
            if (!charInfo.isVisible)
                continue;

            // Only process characters that belong to this material's sub-mesh.
            if (charInfo.materialReferenceIndex != materialIndex)
                continue;

            int vertexIndex = charInfo.vertexIndex;
            var sourceVertices = targetVertices;

            if (vertexIndex + 3 >= sourceVertices.Length)
                continue;

            Vector3 v0 = sourceVertices[vertexIndex + 0];
            Vector3 v1 = sourceVertices[vertexIndex + 1];
            Vector3 v2 = sourceVertices[vertexIndex + 2];
            Vector3 v3 = sourceVertices[vertexIndex + 3];

            Vector3 offsetToMidBaseline = new Vector3((v0.x + v2.x) / 2f, charInfo.baseLine, 0f);

            float x0 = (offsetToMidBaseline.x - boundsMinX) / range;
            float sampleX = (mirrorAlongX ? 1f - x0 : x0) + phaseShift;
            float scale = curve.Evaluate(sampleX);
            float scaleDelta = scale - 1f;

            vertexOffsets[vertexIndex + 0] = (v0 - offsetToMidBaseline) * scaleDelta;
            vertexOffsets[vertexIndex + 1] = (v1 - offsetToMidBaseline) * scaleDelta;
            vertexOffsets[vertexIndex + 2] = (v2 - offsetToMidBaseline) * scaleDelta;
            vertexOffsets[vertexIndex + 3] = (v3 - offsetToMidBaseline) * scaleDelta;
        }
        return true;
    }

    #endregion
}
