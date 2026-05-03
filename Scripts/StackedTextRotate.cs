using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Optional companion component for <see cref="StackedText"/>. When present (and enabled) on the
/// same GameObject, <see cref="StackedText"/> will sample this component's <see cref="Curve"/> and
/// rotate each character's vertices around its baseline midpoint on the Y axis (curve output is
/// in degrees) based on the character's horizontal position within the text bounds. Disable or
/// remove the component to render text without per-character rotation.
/// </summary>
[ExecuteInEditMode]
[DisallowMultipleComponent]
public class StackedTextRotate : MonoBehaviour
{
    #region Fields

    [Tooltip("Per-character Y-axis rotation as a normalized fraction of a quarter turn, sampled " +
             "along the text's normalized x axis. Y = 0 leaves the character unrotated, Y = 1 is " +
             "a 90° rotation, Y = 0.5 is 45°, Y = -1 is -90°, Y = 4 is one full 360° rotation, etc.")]
    public AnimationCurve Curve = AnimationCurve.Constant(0f, 1f, 0f);

    [Tooltip("Shifts the curve evaluation along the X axis (value is divided by 10 internally).")]
    public float Phase = 0f;

    [Tooltip("If true, the curve is sampled mirrored along X (right-to-left). Leave off for " +
             "direct left-to-right sampling, which is the default and matches StackedTextCurve.")]
    public bool MirrorAlongX = false;

    [Tooltip("Per-stack Z offset (in TMP local units) applied to every layer of the matching " +
             "StackedText stack, indexed by stack order. Use negative values to push stacks " +
             "behind the main text so they recede when characters rotate on the Y axis. Stacks " +
             "without a corresponding entry get Z = 0.")]
    public List<float> StackDepths = new();

    #endregion

    #region Lifecycle

    private void OnEnable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectRotate();
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
        // Ask the sibling StackedText to rebuild so rotation tweaks show up immediately in the editor.
        // Note: AnimationCurve drags inside the curve editor don't always fire OnValidate, so
        // StackedText also detects parameter changes via GetParametersHash() each render.
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectRotate();
            stackedText.MarkDirty();
        }
    }
#endif

    #endregion

    #region Public API

    /// <summary>
    /// Computes per-vertex offsets that, when added to the source TMP mesh vertices for the given
    /// <paramref name="materialIndex"/>, rotate each character around its baseline midpoint on the
    /// Y axis according to <see cref="Curve"/>. The output list is sized to that sub-mesh's vertex
    /// count so RTL / Arabic fallback glyphs (rendered through TMP sub-meshes) are also rotated.
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, int materialIndex, List<Vector3> vertexOffsets)
    {
        if (text == null)
            return false;
        return TryGetRotatedVertexOffsets(text, materialIndex, Curve, Phase, MirrorAlongX, vertexOffsets);
    }

    /// <summary>
    /// Backwards-compatible overload that targets the primary material (index 0).
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, List<Vector3> vertexOffsets)
    {
        return TryGetVertexOffsets(text, 0, vertexOffsets);
    }

    /// <summary>
    /// Returns the Z offset for the stack at <paramref name="stackIndex"/> (matching
    /// <see cref="StackedText.Stacks"/> order). Returns 0 if the index has no entry in
    /// <see cref="StackDepths"/>.
    /// </summary>
    public float GetStackDepth(int stackIndex)
    {
        if (StackDepths == null || stackIndex < 0 || stackIndex >= StackDepths.Count)
            return 0f;
        return StackDepths[stackIndex];
    }

    /// <summary>
    /// Fills <paramref name="outAxes"/> with each vertex's character-local Z axis expressed in
    /// the text's local frame. For a character rotated θ around its Y axis, the local Z axis is
    /// (sin θ, 0, cos θ). Vertices belonging to non-rotated characters (or characters not part of
    /// this material slot) receive (0, 0, 1). Used by <see cref="StackedText"/> to push per-stack
    /// depth offsets along the rotated local Z direction so stacks stay "behind" each character
    /// after rotation rather than lying flat on world Z.
    /// </summary>
    public bool TryGetLocalZAxes(TMP_Text text, int materialIndex, List<Vector3> outAxes)
    {
        if (text == null)
            return false;
        return TryComputeLocalZAxes(text, materialIndex, Curve, Phase, MirrorAlongX, outAxes);
    }

    public static bool TryComputeLocalZAxes(TMP_Text text, int materialIndex, AnimationCurve curve, float phase, bool mirrorAlongX, List<Vector3> outAxes)
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

        StackedTextCurve.GetBounds(text, 0f, out var boundsMaxX, out var boundsMinX);

        int requiredLength = targetVertices.Length;
        outAxes.Clear();
        if (outAxes.Capacity < requiredLength)
            outAxes.Capacity = requiredLength;
        Vector3 worldZ = new Vector3(0f, 0f, 1f);
        for (int i = 0; i < requiredLength; i++)
            outAxes.Add(worldZ);

        float range = boundsMaxX - boundsMinX;
        if (Mathf.Approximately(range, 0f))
            return true;

        float phaseShift = phase / 10f;

        for (int i = 0; i < characterCount; i++)
        {
            var charInfo = textInfo.characterInfo[i];
            if (!charInfo.isVisible)
                continue;

            if (charInfo.materialReferenceIndex != materialIndex)
                continue;

            int vertexIndex = charInfo.vertexIndex;
            if (vertexIndex + 3 >= targetVertices.Length)
                continue;

            Vector3 v0 = targetVertices[vertexIndex + 0];
            Vector3 v2 = targetVertices[vertexIndex + 2];
            float pivotX = (v0.x + v2.x) / 2f;

            float x0 = (pivotX - boundsMinX) / range;
            float sampleX = (mirrorAlongX ? 1f - x0 : x0) + phaseShift;
            float quarterTurns = curve.Evaluate(sampleX);

            if (Mathf.Approximately(quarterTurns, 0f))
                continue;

            float radians = quarterTurns * (Mathf.PI / 2f);
            // World-space Z=(0,0,1) rotated θ around Y → (sin θ, 0, cos θ).
            Vector3 localZ = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));

            outAxes[vertexIndex + 0] = localZ;
            outAxes[vertexIndex + 1] = localZ;
            outAxes[vertexIndex + 2] = localZ;
            outAxes[vertexIndex + 3] = localZ;
        }
        return true;
    }

    /// <summary>
    /// Returns a hash that changes whenever any parameter that affects the output offsets changes
    /// (Curve keyframes/tangents, Phase, MirrorAlongX, StackDepths). Used by <see cref="StackedText"/>
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
            if (StackDepths != null)
            {
                hash = hash * 31 + StackDepths.Count;
                for (int i = 0; i < StackDepths.Count; i++)
                    hash = hash * 31 + StackDepths[i].GetHashCode();
            }
            return hash;
        }
    }

    public static bool TryGetRotatedVertexOffsets(TMP_Text text, int materialIndex, AnimationCurve curve, float phase, bool mirrorAlongX, List<Vector3> vertexOffsets)
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

        StackedTextCurve.GetBounds(text, 0f, out var boundsMaxX, out var boundsMinX);

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

            float pivotX = (v0.x + v2.x) / 2f;

            float x0 = (pivotX - boundsMinX) / range;
            float sampleX = (mirrorAlongX ? 1f - x0 : x0) + phaseShift;
            // Curve value is in quarter turns: 1 == 90°, 0.5 == 45°, 4 == one full 360° rotation.
            float quarterTurns = curve.Evaluate(sampleX);
            if (Mathf.Approximately(quarterTurns, 0f))
                continue;

            float radians = quarterTurns * (Mathf.PI / 2f);
            float cosT = Mathf.Cos(radians);
            float sinT = Mathf.Sin(radians);
            float cosMinusOne = cosT - 1f;

            // Y-axis rotation around (pivotX, *, 0) — relative.y is unchanged, relative.z is 0,
            // so offset.x = relative.x * (cos - 1) and offset.z = -relative.x * sin.
            float rx0 = v0.x - pivotX;
            float rx1 = v1.x - pivotX;
            float rx2 = v2.x - pivotX;
            float rx3 = v3.x - pivotX;

            vertexOffsets[vertexIndex + 0] = new Vector3(rx0 * cosMinusOne, 0f, -rx0 * sinT);
            vertexOffsets[vertexIndex + 1] = new Vector3(rx1 * cosMinusOne, 0f, -rx1 * sinT);
            vertexOffsets[vertexIndex + 2] = new Vector3(rx2 * cosMinusOne, 0f, -rx2 * sinT);
            vertexOffsets[vertexIndex + 3] = new Vector3(rx3 * cosMinusOne, 0f, -rx3 * sinT);
        }
        return true;
    }

    #endregion
}
