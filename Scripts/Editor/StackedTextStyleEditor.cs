using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="StackedTextStyle"/>: one box per section (Face, Stacks, Shine) with its
/// Override toggle in the header; a section's fields are shown only while it is overridden.
/// </summary>
[CustomEditor(typeof(StackedTextStyle))]
[CanEditMultipleObjects]
public class StackedTextStyleEditor : Editor
{
    private SerializedProperty _overrideFace;
    private SerializedProperty _faceColor;
    private SerializedProperty _enableVertexGradient;
    private SerializedProperty _faceGradient;
    private SerializedProperty _overrideStacks;
    private SerializedProperty _stacks;
    private SerializedProperty _overrideShine;
    private SerializedProperty _shineEnabled;
    private SerializedProperty _shine;
    private int _sectionIndent;

    private void OnEnable()
    {
        _overrideFace = serializedObject.FindProperty(nameof(StackedTextStyle.OverrideFace));
        _faceColor = serializedObject.FindProperty(nameof(StackedTextStyle.FaceColor));
        _enableVertexGradient = serializedObject.FindProperty(nameof(StackedTextStyle.EnableVertexGradient));
        _faceGradient = serializedObject.FindProperty(nameof(StackedTextStyle.FaceGradient));
        _overrideStacks = serializedObject.FindProperty(nameof(StackedTextStyle.OverrideStacks));
        _stacks = serializedObject.FindProperty(nameof(StackedTextStyle.Stacks));
        _overrideShine = serializedObject.FindProperty(nameof(StackedTextStyle.OverrideShine));
        _shineEnabled = serializedObject.FindProperty(nameof(StackedTextStyle.ShineEnabled));
        _shine = serializedObject.FindProperty(nameof(StackedTextStyle.Shine));
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        if (BeginSection("Face", "TMP face color and vertex gradient.", _overrideFace))
        {
            EditorGUILayout.PropertyField(_faceColor);
            EditorGUILayout.PropertyField(_enableVertexGradient);
            if (_enableVertexGradient.boolValue || _enableVertexGradient.hasMultipleDifferentValues)
                EditorGUILayout.PropertyField(_faceGradient, true);
        }
        EndSection();

        if (BeginSection("Stacks", "Rendered instead of the StackedText component's own Stacks list.", _overrideStacks))
            EditorGUILayout.PropertyField(_stacks, true);
        EndSection();

        if (BeginSection("Shine", "Drives the StackedTextShine module.", _overrideShine))
        {
            EditorGUILayout.PropertyField(_shineEnabled, new GUIContent("Shine On"));
            if (_shineEnabled.boolValue || _shineEnabled.hasMultipleDifferentValues)
                DrawChildren(_shine);
        }
        EndSection();

        serializedObject.ApplyModifiedProperties();
    }

    private bool BeginSection(string title, string tooltip, SerializedProperty overrideToggle)
    {
        _sectionIndent = EditorGUI.indentLevel;
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUI.showMixedValue = overrideToggle.hasMultipleDifferentValues;
        EditorGUI.BeginChangeCheck();
        bool value = EditorGUILayout.ToggleLeft(new GUIContent(title, tooltip), overrideToggle.boolValue, EditorStyles.boldLabel);
        if (EditorGUI.EndChangeCheck())
            overrideToggle.boolValue = value;
        EditorGUI.showMixedValue = false;

        bool isOpen = overrideToggle.boolValue || overrideToggle.hasMultipleDifferentValues;
        if (isOpen)
            EditorGUI.indentLevel++;
        return isOpen;
    }

    private void EndSection()
    {
        EditorGUI.indentLevel = _sectionIndent;
        EditorGUILayout.EndVertical();
    }

    private static void DrawChildren(SerializedProperty property)
    {
        var iterator = property.Copy();
        var end = property.GetEndProperty();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
        {
            EditorGUILayout.PropertyField(iterator, true);
            enterChildren = false;
        }
    }
}
