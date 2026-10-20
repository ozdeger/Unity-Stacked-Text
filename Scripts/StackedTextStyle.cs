using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// A reusable look for <see cref="StackedText"/>: face color / vertex gradient, the stack list and
/// the shine. Each style is its own asset, so new styles are created from the Project window
/// (Create › Stacked Text › Style) without touching code. Assign one through
/// <see cref="StackedTextStyleApplier"/>, whose inspector lists every style asset in the project
/// by name.
///
/// A style only changes the sections whose Override toggle is on; a new, blank style changes
/// nothing until a section is turned on.
/// </summary>
[CreateAssetMenu(fileName = "StackedTextStyle", menuName = "Stacked Text/Style", order = 200)]
public class StackedTextStyle : ScriptableObject
{
    #region Fields

    [Tooltip("Drive the text's face color and vertex gradient. When off, the text keeps its own TMP colors.")]
    public bool OverrideFace;
    [Tooltip("TMP face (vertex) color.")]
    public Color FaceColor = Color.white;
    [Tooltip("Enables TMP's four-corner vertex gradient. Any Color Gradient Preset on the text is cleared while the style is applied.")]
    public bool EnableVertexGradient;
    public VertexGradient FaceGradient = new(Color.white);

    [Tooltip("Render this style's stacks instead of the StackedText component's own Stacks list. " +
             "The component's list is left untouched and comes back when the style is removed.")]
    public bool OverrideStacks;
    public List<StackedText.StackConfig> Stacks = new();

    [Tooltip("Drive the StackedTextShine module. When off, the text's own shine setup is left untouched.")]
    public bool OverrideShine;
    [Tooltip("Turn the shine on (with the settings below) or force it off.")]
    public bool ShineEnabled = true;
    public StackedTextShine.ShineSettings Shine = new();

    #endregion

    #region Change Notification

    /// <summary>
    /// Raised when a style's values change: on every inspector edit, and whenever
    /// <see cref="NotifyChanged"/> is called after editing a style from code. Every
    /// <see cref="StackedTextStyleApplier"/> using the style re-applies it.
    /// </summary>
    public static event Action<StackedTextStyle> Changed;

    /// <summary>
    /// Call after changing a style's fields from code so the texts using it pick up the change.
    /// </summary>
    public void NotifyChanged()
    {
        Changed?.Invoke(this);
    }

    private void OnValidate()
    {
        // A new list entry holds the struct default (LayerCount == 0); seed it with the same
        // defaults StackedText gives entries added to its own Stacks list.
        for (int i = 0; i < Stacks.Count; i++)
        {
            if (Stacks[i].IsInvalid())
                Stacks[i] = StackedText.StackConfig.CreateDefault();
        }

        NotifyChanged();
    }

    #endregion
}
