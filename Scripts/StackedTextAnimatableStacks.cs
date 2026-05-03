using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Optional companion component for <see cref="StackedText"/>. Exposes 8 fixed
/// <see cref="StackedText.StackConfig"/> slots as named serialized fields so their primitive
/// sub-fields (offsets, softness, dilate, layer count, enabled) can be keyframed by Unity
/// Animation clips — something the dynamic <see cref="StackedText.Stacks"/> list cannot support
/// because list elements have unstable binding paths. When present (and enabled) on the same
/// GameObject, <see cref="StackedText"/> appends these slots to its rendered stack list each frame.
/// </summary>
[ExecuteInEditMode]
[DisallowMultipleComponent]
public class StackedTextAnimatableStacks : MonoBehaviour
{
    #region Fields

    public StackedText.StackConfig Stack0;
    public StackedText.StackConfig Stack1;
    public StackedText.StackConfig Stack2;
    public StackedText.StackConfig Stack3;
    public StackedText.StackConfig Stack4;
    public StackedText.StackConfig Stack5;
    public StackedText.StackConfig Stack6;
    public StackedText.StackConfig Stack7;

    #endregion

    #region Lifecycle

    private void Reset()
    {
        // Seed each slot with a disabled default. Eight identical-offset stacks all rendering
        // at once would just overlap, so unused slots stay disabled.
        var slots = new StackedText.StackConfig[8];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = MakeDisabledDefault();

        // If the sibling StackedText already has stacks configured, copy up to 8 of them as-is
        // so the user keeps their existing setup when migrating to the animatable module.
        if (TryGetComponent<StackedText>(out var stackedText) && stackedText.Stacks != null)
        {
            int copyCount = Mathf.Min(slots.Length, stackedText.Stacks.Count);
            for (int i = 0; i < copyCount; i++)
                slots[i] = stackedText.Stacks[i];
        }

        Stack0 = slots[0];
        Stack1 = slots[1];
        Stack2 = slots[2];
        Stack3 = slots[3];
        Stack4 = slots[4];
        Stack5 = slots[5];
        Stack6 = slots[6];
        Stack7 = slots[7];
    }

    private static StackedText.StackConfig MakeDisabledDefault()
    {
        var s = StackedText.StackConfig.CreateDefault();
        s.Enabled = false;
        return s;
    }

    private void OnEnable()
    {
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectAnimatableStacks();
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
        if (TryGetComponent<StackedText>(out var stackedText))
        {
            stackedText.TryAutoCollectAnimatableStacks();
            stackedText.MarkDirty();
        }
    }
#endif

    #endregion

    #region Public API

    /// <summary>
    /// Append every non-invalid slot to <paramref name="target"/> in order. Disabled slots are
    /// still appended so the host's change detection sees toggles driven by animation clips —
    /// the host's render pipeline already gates layer build on <see cref="StackedText.StackConfig.Enabled"/>.
    /// </summary>
    public void AppendActiveStacks(List<StackedText.StackConfig> target)
    {
        AppendIfValid(target, Stack0);
        AppendIfValid(target, Stack1);
        AppendIfValid(target, Stack2);
        AppendIfValid(target, Stack3);
        AppendIfValid(target, Stack4);
        AppendIfValid(target, Stack5);
        AppendIfValid(target, Stack6);
        AppendIfValid(target, Stack7);
    }

    private static void AppendIfValid(List<StackedText.StackConfig> target, StackedText.StackConfig stack)
    {
        if (stack.IsInvalid())
            return;
        target.Add(stack);
    }

    #endregion
}
