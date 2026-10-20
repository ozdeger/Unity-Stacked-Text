using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Drawer for <see cref="AssetDropdownAttribute"/>: a searchable dropdown listing every asset of
/// the field's type in the project by name, with None, Create New… and drag-and-drop support. The
/// button next to it opens the assigned asset in a floating inspector.
/// </summary>
[CustomPropertyDrawer(typeof(AssetDropdownAttribute))]
public class AssetDropdownDrawer : PropertyDrawer
{
    private const float EditButtonWidth = 22f;
    private const float Spacing = 2f;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var assetType = GetAssetType(fieldInfo.FieldType);
        if (property.propertyType != SerializedPropertyType.ObjectReference || assetType == null)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        EditorGUI.BeginProperty(position, label, property);

        var fieldRect = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
        var buttonRect = new Rect(fieldRect.x, fieldRect.y, fieldRect.width - EditButtonWidth - Spacing, fieldRect.height);
        var editRect = new Rect(buttonRect.xMax + Spacing, fieldRect.y, EditButtonWidth, fieldRect.height);

        var current = property.hasMultipleDifferentValues ? null : property.objectReferenceValue;
        var caption = new GUIContent(
            property.hasMultipleDifferentValues ? "—" : current != null ? current.name : "None",
            current != null ? AssetPreview.GetMiniThumbnail(current) : null,
            current != null ? AssetDatabase.GetAssetPath(current) : $"No {ObjectNames.NicifyVariableName(assetType.Name)} assigned");

        HandleDragAndDrop(buttonRect, property, assetType);

        // Keep the label's indent from shifting the button content.
        int indent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        if (EditorGUI.DropdownButton(buttonRect, caption, FocusType.Keyboard))
        {
            var dropdown = new AssetDropdown(new AdvancedDropdownState(), assetType,
                property.serializedObject.targetObjects, property.propertyPath, current);
            dropdown.Show(buttonRect);
        }

        using (new EditorGUI.DisabledScope(current == null))
        {
            if (GUI.Button(editRect, GetEditButtonContent()))
            {
#if UNITY_2022_1_OR_NEWER
                EditorUtility.OpenPropertyEditor(current);
#else
                Selection.activeObject = current;
#endif
            }
        }
        EditorGUI.indentLevel = indent;

        EditorGUI.EndProperty();
    }

    private static GUIContent s_editButtonContent;

    private static GUIContent GetEditButtonContent()
    {
        if (s_editButtonContent == null)
        {
            var icon = EditorGUIUtility.FindTexture(EditorGUIUtility.isProSkin ? "d_editicon.sml" : "editicon.sml");
            s_editButtonContent = icon != null
                ? new GUIContent(icon, "Edit the asset in a floating inspector")
                : new GUIContent("…", "Edit the asset in a floating inspector");
        }
        return s_editButtonContent;
    }

    private static Type GetAssetType(Type fieldType)
    {
        if (fieldType.IsArray)
            fieldType = fieldType.GetElementType();
        else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
            fieldType = fieldType.GetGenericArguments()[0];

        return fieldType != null && typeof(Object).IsAssignableFrom(fieldType) ? fieldType : null;
    }

    private static void HandleDragAndDrop(Rect rect, SerializedProperty property, Type assetType)
    {
        var evt = Event.current;
        if ((evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) || !rect.Contains(evt.mousePosition))
            return;

        Object dragged = null;
        foreach (var obj in DragAndDrop.objectReferences)
        {
            if (obj != null && assetType.IsInstanceOfType(obj) && EditorUtility.IsPersistent(obj))
            {
                dragged = obj;
                break;
            }
        }
        if (dragged == null)
            return;

        DragAndDrop.visualMode = DragAndDropVisualMode.Link;
        if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            property.objectReferenceValue = dragged;
            GUI.changed = true;
        }
        evt.Use();
    }

    #region Dropdown

    private class AssetDropdown : AdvancedDropdown
    {
        private readonly Type _assetType;
        private readonly Object[] _targets;
        private readonly string _propertyPath;
        private readonly Object _current;

        private class Item : AdvancedDropdownItem
        {
            public Object Asset;
            public bool IsCreate;

            public Item(string name) : base(name)
            {
            }
        }

        public AssetDropdown(AdvancedDropdownState state, Type assetType, Object[] targets, string propertyPath, Object current)
            : base(state)
        {
            _assetType = assetType;
            _targets = targets;
            _propertyPath = propertyPath;
            _current = current;
            minimumSize = new Vector2(240f, 320f);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var typeName = ObjectNames.NicifyVariableName(_assetType.Name);
            var root = new AdvancedDropdownItem(typeName + "s");

            root.AddChild(new Item("None"));
            foreach (var entry in AssetCatalog.Get(_assetType))
            {
                root.AddChild(new Item(entry.DisplayName)
                {
                    Asset = entry.Asset,
                    icon = entry.Asset == _current ? EditorGUIUtility.FindTexture("FilterSelectedOnly") : AssetPreview.GetMiniThumbnail(entry.Asset),
                });
            }

            if (typeof(ScriptableObject).IsAssignableFrom(_assetType) && !_assetType.IsAbstract)
            {
                root.AddSeparator();
                root.AddChild(new Item($"Create New {typeName}…") { IsCreate = true });
            }

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            var selected = (Item)item;
            if (selected.IsCreate)
                // Open the save panel after the dropdown window has closed.
                EditorApplication.delayCall += CreateNewAsset;
            else
                Assign(selected.Asset);
        }

        private void CreateNewAsset()
        {
            var typeName = ObjectNames.NicifyVariableName(_assetType.Name);
            var folder = _current != null ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(_current)) : "Assets";
            var path = EditorUtility.SaveFilePanelInProject($"Create {typeName}", $"New {typeName}", "asset",
                $"Choose where to save the new {typeName}.", folder);
            if (string.IsNullOrEmpty(path))
                return;

            var asset = ScriptableObject.CreateInstance(_assetType);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Assign(asset);
            EditorGUIUtility.PingObject(asset);
        }

        private void Assign(Object asset)
        {
            var targets = Array.FindAll(_targets, t => t != null);
            if (targets.Length == 0)
                return;

            using var serializedObject = new SerializedObject(targets);
            var property = serializedObject.FindProperty(_propertyPath);
            if (property == null)
                return;

            property.objectReferenceValue = asset;
            serializedObject.ApplyModifiedProperties();
        }
    }

    #endregion

    #region Asset Catalog

    /// <summary>Every asset of a type in the project, cached until the project changes.</summary>
    private static class AssetCatalog
    {
        public struct Entry
        {
            public Object Asset;
            public string DisplayName;
        }

        private static readonly Dictionary<Type, List<Entry>> s_cache = new();

        static AssetCatalog()
        {
            EditorApplication.projectChanged += s_cache.Clear;
        }

        public static List<Entry> Get(Type assetType)
        {
            if (!s_cache.TryGetValue(assetType, out var entries))
            {
                entries = Find(assetType);
                s_cache[assetType] = entries;
            }
            return entries;
        }

        private static List<Entry> Find(Type assetType)
        {
            var assets = new List<(Object Asset, string Path)>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + assetType.Name))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset != null && assetType.IsInstanceOfType(asset))
                        assets.Add((asset, path));
                }
            }

            assets.Sort((a, b) =>
            {
                int byName = string.Compare(a.Asset.name, b.Asset.name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
            });

            // Assets sharing a name get their folder appended so they can be told apart.
            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (asset, _) in assets)
                nameCounts[asset.name] = nameCounts.TryGetValue(asset.name, out int count) ? count + 1 : 1;

            var entries = new List<Entry>(assets.Count);
            foreach (var (asset, path) in assets)
            {
                var displayName = nameCounts[asset.name] > 1
                    ? $"{asset.name} ({Path.GetDirectoryName(path)?.Replace('\\', '/')})"
                    : asset.name;
                entries.Add(new Entry { Asset = asset, DisplayName = displayName });
            }
            return entries;
        }
    }

    #endregion
}
