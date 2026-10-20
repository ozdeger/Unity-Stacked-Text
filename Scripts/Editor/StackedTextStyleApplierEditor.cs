using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="StackedTextStyleApplier"/>: the style dropdown, setup warnings, buttons
/// to create styles, and the assigned style's settings inline so it can be tuned in place.
/// </summary>
[CustomEditor(typeof(StackedTextStyleApplier))]
[CanEditMultipleObjects]
public class StackedTextStyleApplierEditor : Editor
{
    private SerializedProperty _style;
    private SerializedProperty _text;
    private SerializedProperty _stackedText;
    private SerializedProperty _shine;
    private Editor _styleEditor;
    private static bool s_showReferences;
    private static bool s_showStyle = true;

    private void OnEnable()
    {
        _style = serializedObject.FindProperty("Style");
        _text = serializedObject.FindProperty("Text");
        _stackedText = serializedObject.FindProperty("StackedText");
        _shine = serializedObject.FindProperty("Shine");
    }

    private void OnDisable()
    {
        if (_styleEditor != null)
            DestroyImmediate(_styleEditor);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.PropertyField(_style);
        s_showReferences = EditorGUILayout.Foldout(s_showReferences, "References", true);
        if (s_showReferences)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_text);
            EditorGUILayout.PropertyField(_stackedText);
            EditorGUILayout.PropertyField(_shine);
            EditorGUI.indentLevel--;
        }
        serializedObject.ApplyModifiedProperties();

        if (targets.Length != 1)
            return;

        var applier = (StackedTextStyleApplier)target;
        var style = applier.CurrentStyle;

        DrawSetupMessages(applier, style);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("New Style From Text…", "Save what this text renders now as a new style asset and assign it.")))
                StackedTextStyleUtility.CreateStyleFromText(applier.gameObject, true);
            using (new EditorGUI.DisabledScope(style == null))
            {
                if (GUILayout.Button(new GUIContent("Duplicate Style…", "Copy the assigned style to a new asset and assign the copy.")))
                    StackedTextStyleUtility.DuplicateStyle(style, applier);
            }
        }

        if (style == null)
            return;

        EditorGUILayout.Space();
        s_showStyle = EditorGUILayout.Foldout(s_showStyle, $"Style: {style.name}", true);
        if (!s_showStyle)
            return;

        EditorGUILayout.HelpBox("Edits are saved to the style asset and change every text that uses it.", MessageType.None);
        CreateCachedEditor(style, typeof(StackedTextStyleEditor), ref _styleEditor);
        _styleEditor.OnInspectorGUI();
    }

    private static void DrawSetupMessages(StackedTextStyleApplier applier, StackedTextStyle style)
    {
        if (style == null)
            return;

        var gameObject = applier.gameObject;
        bool hasStackedText = gameObject.TryGetComponent<StackedText>(out _);

        if (style.OverrideStacks && !hasStackedText)
            EditorGUILayout.HelpBox("This style sets stacks, but there is no StackedText component on this GameObject.", MessageType.Warning);

        if (style.OverrideShine && style.ShineEnabled && !gameObject.TryGetComponent<StackedTextShine>(out _))
        {
            EditorGUILayout.HelpBox("This style turns the shine on, but there is no StackedTextShine component. " +
                                    "It is added automatically in Play Mode; add it now to preview the shine.", MessageType.Warning);
            if (GUILayout.Button("Add StackedTextShine"))
            {
                var shine = Undo.AddComponent<StackedTextShine>(gameObject);
                // Off unless a style turns it on, so removing the style removes the shine.
                Undo.RecordObject(shine, "Add StackedTextShine");
                shine.enabled = false;
                applier.Apply();
            }
        }

        if (style.OverrideFace)
            EditorGUILayout.HelpBox("The face color and vertex gradient are set by this style. " +
                                    "The text's own colors come back when the style is removed.", MessageType.Info);
    }
}
