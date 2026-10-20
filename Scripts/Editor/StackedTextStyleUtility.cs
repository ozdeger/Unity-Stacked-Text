using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor helpers for creating <see cref="StackedTextStyle"/> assets from existing texts.
/// </summary>
public static class StackedTextStyleUtility
{
    [MenuItem("CONTEXT/StackedText/Create Style From Text…")]
    private static void CreateStyleFromTextMenu(MenuCommand command)
    {
        CreateStyleFromText(((Component)command.context).gameObject, true);
    }

    [MenuItem("CONTEXT/StackedTextStyleApplier/Create Style From Text…")]
    private static void CreateStyleFromApplierMenu(MenuCommand command)
    {
        CreateStyleFromText(((Component)command.context).gameObject, true);
    }

    /// <summary>
    /// Saves the look currently on screen (face colors, rendered stacks and shine) as a new style
    /// asset. With <paramref name="assign"/>, the style is assigned through the GameObject's
    /// <see cref="StackedTextStyleApplier"/>, which is added if missing.
    /// </summary>
    public static StackedTextStyle CreateStyleFromText(GameObject gameObject, bool assign)
    {
        var path = EditorUtility.SaveFilePanelInProject("Create Stacked Text Style", gameObject.name + " Style", "asset",
            "Choose where to save the new style.", GetDefaultFolder(gameObject));
        if (string.IsNullOrEmpty(path))
            return null;

        var style = ScriptableObject.CreateInstance<StackedTextStyle>();
        CaptureInto(style, gameObject);
        AssetDatabase.CreateAsset(style, path);
        AssetDatabase.SaveAssets();

        if (assign)
            AssignStyle(gameObject, style);

        EditorGUIUtility.PingObject(style);
        return style;
    }

    /// <summary>Copies <paramref name="style"/> to a new asset and assigns the copy through <paramref name="applier"/>.</summary>
    public static StackedTextStyle DuplicateStyle(StackedTextStyle style, StackedTextStyleApplier applier)
    {
        var sourcePath = AssetDatabase.GetAssetPath(style);
        var path = EditorUtility.SaveFilePanelInProject("Duplicate Stacked Text Style", style.name + " Copy", "asset",
            "Choose where to save the copy.", Path.GetDirectoryName(sourcePath));
        if (string.IsNullOrEmpty(path) || !AssetDatabase.CopyAsset(sourcePath, path))
            return null;

        var copy = AssetDatabase.LoadAssetAtPath<StackedTextStyle>(path);
        if (applier != null)
            AssignStyle(applier.gameObject, copy);
        EditorGUIUtility.PingObject(copy);
        return copy;
    }

    /// <summary>
    /// Fills <paramref name="style"/> with what <paramref name="gameObject"/> currently renders and
    /// turns on the matching Override toggles.
    /// </summary>
    public static void CaptureInto(StackedTextStyle style, GameObject gameObject)
    {
        if (gameObject.TryGetComponent<TMP_Text>(out var text))
        {
            style.OverrideFace = true;
            style.FaceColor = text.color;
            style.EnableVertexGradient = text.enableVertexGradient;
            style.FaceGradient = text.colorGradientPreset != null
                ? ToVertexGradient(text.colorGradientPreset)
                : text.colorGradient;
        }

        if (gameObject.TryGetComponent<StackedText>(out var stackedText))
        {
            List<StackedText.StackConfig> source = stackedText.Stacks;
            if (gameObject.TryGetComponent<StackedTextStyleApplier>(out var applier) && applier.TryGetStyleStacks(out var styleStacks))
                source = styleStacks;

            style.OverrideStacks = true;
            style.Stacks = new List<StackedText.StackConfig>(source.Count);
            foreach (var stack in source)
                style.Stacks.Add(stack.Clone());
        }

        if (gameObject.TryGetComponent<StackedTextShine>(out var shine))
        {
            style.OverrideShine = true;
            style.ShineEnabled = shine.enabled;
            style.Shine = shine.ActiveSettings.Clone();
        }
    }

    private static void AssignStyle(GameObject gameObject, StackedTextStyle style)
    {
        if (!gameObject.TryGetComponent<StackedTextStyleApplier>(out var applier))
            applier = Undo.AddComponent<StackedTextStyleApplier>(gameObject);

        using var serializedObject = new SerializedObject(applier);
        serializedObject.FindProperty("Style").objectReferenceValue = style;
        serializedObject.ApplyModifiedProperties();
    }

    private static VertexGradient ToVertexGradient(TMP_ColorGradient preset)
    {
        switch (preset.colorMode)
        {
            case ColorMode.Single:
                return new VertexGradient(preset.topLeft);
            case ColorMode.HorizontalGradient:
                return new VertexGradient(preset.topLeft, preset.topRight, preset.topLeft, preset.topRight);
            case ColorMode.VerticalGradient:
                return new VertexGradient(preset.topLeft, preset.topLeft, preset.bottomLeft, preset.bottomLeft);
            default:
                return new VertexGradient(preset.topLeft, preset.topRight, preset.bottomLeft, preset.bottomRight);
        }
    }

    private static string GetDefaultFolder(GameObject gameObject)
    {
        // Next to the prefab when editing one, otherwise the project root.
        var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
        if (string.IsNullOrEmpty(prefabPath))
            prefabPath = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage()?.assetPath;
        return string.IsNullOrEmpty(prefabPath) ? "Assets" : Path.GetDirectoryName(prefabPath);
    }
}
