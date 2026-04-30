using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Optional companion component for <see cref="StackedText"/>. When present (and enabled) on the
/// same GameObject, <see cref="StackedText"/> will sample this component's <see cref="Curve"/> and
/// bend the text vertices along it. Disable or remove the component to render flat text.
/// </summary>
[ExecuteInEditMode]
[DisallowMultipleComponent]
public class StackedTextCurve : MonoBehaviour
{
    #region Fields

    public AnimationCurve Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    public float CurveScale = 1f;
    public bool KeepTextCentered;
    public float ReferenceWidth;
#if UNITY_EDITOR
    public bool DrawCurveGizmos = true;
#endif

    #endregion

    #region Editor Hooks

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Ask the sibling StackedText to rebuild so curve tweaks show up immediately in the editor.
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectCurve();
            stackedText.MarkDirty();
        }
    }

    private void OnEnable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectCurve();
            stackedText.MarkDirty();
        }
    }

    private void OnDisable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
            stackedText.MarkDirty();
    }
#endif

    #endregion

    #region Public API
    
    /// <summary>
    /// Returns a hash that changes whenever any parameter that affects the output offsets changes
    /// (Curve keyframes/tangents, CurveScale, KeepTextCentered, ReferenceWidth). Used by
    /// <see cref="StackedText"/> to detect edits even when Unity does not fire <c>OnValidate</c>
    /// (e.g. live drags inside the AnimationCurve editor, or values driven by Animation clips).
    /// </summary>
    public int GetParametersHash()
    {
        unchecked
        {
            int hash = 13;
            hash = hash * 31 + CurveScale.GetHashCode();
            hash = hash * 31 + KeepTextCentered.GetHashCode();
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

    /// <summary>
    /// Computes per-vertex offsets that, when added to the source TMP mesh vertices for the given
    /// <paramref name="materialIndex"/>, bend the text along this component's curve. The output
    /// list is cleared and populated with one offset per source vertex of that sub-mesh, so RTL /
    /// Arabic fallback glyphs (which TMP routes through sub-meshes) are bent the same way as the
    /// primary text. Returns false if the text has no characters or mesh data yet.
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, int materialIndex, List<Vector3> vertexOffsets)
    {
        if (text == null)
            return false;
        return TryGetCurvedVertexOffsets(text, materialIndex, Curve, CurveScale, ReferenceWidth, KeepTextCentered, vertexOffsets);
    }

    /// <summary>
    /// Backwards-compatible overload that targets the primary material (index 0). Prefer the
    /// overload that takes an explicit materialIndex so fallback (e.g. Arabic) sub-meshes are also
    /// covered.
    /// </summary>
    public bool TryGetVertexOffsets(TMP_Text text, List<Vector3> vertexOffsets)
    {
        return TryGetVertexOffsets(text, 0, vertexOffsets);
    }

    private static bool TryGetCurvedVertexOffsets(TMP_Text text, int materialIndex, AnimationCurve curve, float curveScale, float referenceWidth, bool stabilizeY, List<Vector3> vertexOffsets)
    {
        TMP_TextInfo textInfo = text.textInfo;
        int characterCount = textInfo != null ? textInfo.characterCount : 0;

        if (characterCount == 0 || textInfo.meshInfo == null || textInfo.meshInfo.Length == 0)
            return false;

        if (materialIndex < 0 || materialIndex >= textInfo.meshInfo.Length)
            return false;

        var targetVertices = textInfo.meshInfo[materialIndex].vertices;
        if (targetVertices == null)
            return false;

        GetBounds(text, referenceWidth, out var boundsMaxX, out var boundsMinX);

        int requiredLength = targetVertices.Length;
        vertexOffsets.Clear();
        if (vertexOffsets.Capacity < requiredLength)
            vertexOffsets.Capacity = requiredLength;
        for (int i = 0; i < requiredLength; i++)
            vertexOffsets.Add(default);

        float yGlobalOffset = 0;
        if (stabilizeY)
            yGlobalOffset = curve.Evaluate(0.5f) * text.bounds.size.x * curveScale * 0.1f;

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

            Vector3 offsetToMidBaseline = new Vector3((v0.x + v2.x) / 2, charInfo.baseLine, 0);

            float x0 = (offsetToMidBaseline.x - boundsMinX) / (boundsMaxX - boundsMinX);
            float x1 = x0 + 0.0001f;
            float y0 = curve.Evaluate(x0) * text.bounds.size.x * curveScale * 0.1f;
            float y1 = curve.Evaluate(x1) * text.bounds.size.x * curveScale * 0.1f;

            Vector3 horizontal = Vector3.right;
            Vector3 tangent = new Vector3(x1 * (boundsMaxX - boundsMinX) + boundsMinX, y1) -
                              new Vector3(offsetToMidBaseline.x, y0);

            float dot = Mathf.Acos(Vector3.Dot(horizontal, tangent.normalized)) * Mathf.Rad2Deg;
            Vector3 cross = Vector3.Cross(horizontal, tangent);
            float angle = cross.z > 0 ? dot : 360 - dot;

            Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(0, y0 - yGlobalOffset, 0), Quaternion.Euler(0, 0, angle), Vector3.one);

            Vector3 t0 = matrix.MultiplyPoint3x4(v0 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t1 = matrix.MultiplyPoint3x4(v1 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t2 = matrix.MultiplyPoint3x4(v2 - offsetToMidBaseline) + offsetToMidBaseline;
            Vector3 t3 = matrix.MultiplyPoint3x4(v3 - offsetToMidBaseline) + offsetToMidBaseline;

            vertexOffsets[vertexIndex + 0] = t0 - v0;
            vertexOffsets[vertexIndex + 1] = t1 - v1;
            vertexOffsets[vertexIndex + 2] = t2 - v2;
            vertexOffsets[vertexIndex + 3] = t3 - v3;
        }
        return true;
    }

    public static void GetBounds(TMP_Text text, float referenceWidth, out float boundsMaxX, out float boundsMinX)
    {
        boundsMinX = text.bounds.min.x;
        boundsMaxX = text.bounds.max.x;

        if (referenceWidth > 0)
        {
            boundsMinX = Mathf.Min(boundsMinX, -referenceWidth / 2f);
            boundsMaxX = Mathf.Max(boundsMaxX, referenceWidth / 2f);
        }
    }

    #endregion

    #region Gizmos

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!DrawCurveGizmos || Curve == null || Curve.length == 0)
            return;

        if (!TryGetComponent<TMP_Text>(out var text) || text.textInfo == null)
            return;

        GetBounds(text, ReferenceWidth, out var boundsMaxX, out var boundsMinX);
        float boundsWidth = boundsMaxX - boundsMinX;
        if (boundsWidth <= 0f)
            return;
        
        float yScale = text.bounds.size.x * CurveScale * 0.1f;
        float yGlobalOffset = KeepTextCentered ? Curve.Evaluate(0.5f) * yScale : 0f;

        var prevMatrix = Gizmos.matrix;
        var prevColor = Gizmos.color;
        Gizmos.matrix = transform.localToWorldMatrix;
        
        Gizmos.color = new Color(1f, 1f, 1f, 0.25f);
        float yMin = text.bounds.min.y;
        float yMax = text.bounds.max.y;
        var bl = new Vector3(boundsMinX, yMin, 0f);
        var br = new Vector3(boundsMaxX, yMin, 0f);
        var tl = new Vector3(boundsMinX, yMax, 0f);
        var tr = new Vector3(boundsMaxX, yMax, 0f);
        Gizmos.DrawLine(bl, tl);
        Gizmos.DrawLine(tl, tr);
        Gizmos.DrawLine(tr, br);
        Gizmos.DrawLine(br, bl);
        
        Gizmos.color = new Color(1f, 1f, 1f, 0.15f);
        Gizmos.DrawLine(new Vector3(boundsMinX, -yGlobalOffset, 0f),
                        new Vector3(boundsMaxX, -yGlobalOffset, 0f));
        
        Gizmos.color = Color.green;
        const int segments = 96;
        Vector3 prev = default;
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float x = Mathf.Lerp(boundsMinX, boundsMaxX, t);
            float y = Curve.Evaluate(t) * yScale - yGlobalOffset;
            var point = new Vector3(x, y, 0f);

            if (i > 0)
                Gizmos.DrawLine(prev, point);
            prev = point;
        }
        
        Gizmos.color = Color.yellow;
        float dotRadius = boundsWidth * 0.005f;
        for (int i = 0; i < Curve.length; i++)
        {
            var key = Curve[i];
            float x = Mathf.Lerp(boundsMinX, boundsMaxX, Mathf.Clamp01(key.time));
            float y = key.value * yScale - yGlobalOffset;
            Gizmos.DrawSphere(new Vector3(x, y, 0f), dotRadius);
        }

        Gizmos.matrix = prevMatrix;
        Gizmos.color = prevColor;
    }
#endif

    #endregion
}
