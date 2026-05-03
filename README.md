<p align="center">
  <img src="Images/example_stacked_text.png" alt="Stacked Text Example" width="720"/>
</p>

**Stylized layered text effects for TextMeshPro in Unity**

StackedText is a lightweight Unity component that generates stacked, multi-layered text with customizable colors, offsets, softness, and dilation — all driven by a single `TMP_Text` component. Optional sibling modules add per-character bending, scaling, Y-axis rotation, and animation-clip-friendly stack slots. Perfect for game titles, UI headers, and stylized labels.

---

## Examples

<p align="center">
  <img src="Images/example_the_queen.png" alt="The Queen" width="380"/>
  <img src="Images/example_flowers.png" alt="Flowers" width="380"/>
</p>
<p align="center">
  <img src="Images/example_blue_factory.png" alt="Blue Factory" width="380"/>
</p>

---

## Features

- **Single Drawcall** — Embeds the parameters into texcoord3 channel to achieve single drawcall.
- **Multiple Stack Layers** — Add as many stacks as you need, each with independent settings.
- **Per-Layer Gradient Colors** — Assign a `Gradient` to each stack for smooth color transitions across layers.
- **Configurable Offsets** — Set start and end offsets per stack to control the direction and depth of the effect.
- **Softness & Dilation** — Fine-tune the edge softness and thickness of each layer independently.
- **Optional modules** — Mix and match `StackedTextCurve` (arc-bend), `StackedTextScale` (per-character scale), `StackedTextRotate` (per-character Y-axis rotation + stack depth), and `StackedTextAnimatableStacks` (8 named stack slots that can be keyframed by Animation clips). All modules are sibling components — add the ones you need.
- **Editor Preview** — Runs in Edit Mode via `[ExecuteInEditMode]`, so you see results instantly without entering Play Mode.
- **Zero Allocation at Runtime** — Reuses cached lists and meshes to avoid GC pressure during updates.
- **Automatic Material Setup** — Detects and creates a compatible `Distance Field Dilate` material if one isn't assigned.
- **Fallback asset & Icon support** — Supports fallback assets & TMP icons by default. Compatible with RTL languages as well.
- **Animation Clip Support** — Change every numeric field via Animation clips to create dynamic effects, including the 8 fixed slots on `StackedTextAnimatableStacks`.

---

## Requirements

| Dependency         | Version |
|--------------------|---|
| Unity              | 2021.3+ |
| TextMeshPro        | Built-in (via Package Manager) |

---

## Quick Start

1. Add a **TextMeshPro** text object to your scene (UI or World Space).
2. Add the **StackedText** component to the same GameObject.
3. The `Text` field auto-populates. If not, drag your `TMP_Text` reference in.
4. Add entries to the **Stacks** list to create new stack layers.
5. Configure each stack's **Color**, **Start/End Offset**, **Softness**, and **Dilate** to taste.
6. (Optional) Add any of the sibling modules to the same GameObject:
   - **StackedTextCurve** — bend the text along an arc.
   - **StackedTextScale** — scale each character along an `AnimationCurve` sampled by horizontal position.
   - **StackedTextRotate** — rotate each character on its Y axis along a curve, and optionally push individual stacks "behind" along the rotated local Z.
   - **StackedTextAnimatableStacks** — exposes 8 fixed `StackConfig` fields by name so an `Animator` can keyframe them.

The `StackedText` component auto-collects all sibling modules on enable and on validate; you usually don't need to wire references manually.

---

## Stack Configuration

Each `StackConfig` entry exposes the following:

| Property | Description |
|---|---|
| **Enabled** | Toggle this stack on or off. |
| **Layer Count** | Number of sub-layers in this stack (1–6). More layers = smoother gradient transitions. |
| **Color** | A `Gradient` sampled across the sub-layers. |
| **Start Offset** | Position offset of the first (back-most) sub-layer. |
| **End Offset** | Position offset of the last (front-most) sub-layer. |
| **Softness** | Edge softness of the stack layers (0–1). |
| **Dilate** | Thickness adjustment of the stack layers (-1 to 1). |

The **main text** sits on top of all stacks and has its own **MainTextSoftness** and **MainTextDilate** controls. Toggle **Show Main Text** off to hide the front layer and display only the stacks.

---

## Curve Component (`StackedTextCurve`)

Bends the text vertices along an arc by sampling an `AnimationCurve` over the text's normalized X axis.

| Property | Description |
|---|---|
| **Curve** | An `AnimationCurve` defining the arc shape. |
| **Curve Scale** | Multiplier for the curve's vertical displacement. |
| **Keep Text Centered** | Offsets the curve so the midpoint stays at the baseline. |
| **Reference Width** | Overrides the text bounds width for curve calculations. Useful for consistent arcs across varying text lengths. |

---

## Scale Component (`StackedTextScale`)

Scales each character around its baseline midpoint based on its position along the text's normalized X axis. The curve's `Y` value is used directly as the scale multiplier (1 = original size, 0 = collapsed, 2 = double).

| Property | Description |
|---|---|
| **Curve** | An `AnimationCurve` whose value is the scale multiplier per character. |
| **Phase** | Shifts the curve evaluation along the X axis (value is divided by 10 internally). |
| **MirrorAlongX** | Sample the curve right-to-left instead of left-to-right. |
| **Reference Width** | Overrides the text bounds width used to normalize character positions. |

Scale is applied **after** Curve and Rotate so it composes multiplicatively — at scale = 0 the character collapses cleanly to its pivot regardless of which other modules are active.

---

## Rotate Component (`StackedTextRotate`)

Rotates each character around its baseline midpoint on the **Y axis** based on its position along the text. Designed to look correct under a perspective camera (Z displacement is meaningful); under Screen Space – Overlay you'll see the X-squash component but not depth.

| Property | Description |
|---|---|
| **Curve** | An `AnimationCurve` whose value is the rotation in **quarter turns**: `1` = 90°, `0.5` = 45°, `4` = a full 360°. |
| **Phase** | Shifts the curve evaluation along the X axis (value is divided by 10 internally). |
| **MirrorAlongX** | Sample the curve right-to-left instead of left-to-right. |
| **StackDepths** | Optional `List<float>` that adds a per-stack Z offset, indexed by stack order in `StackedText.Stacks`. The offset is pushed along each character's **rotated local Z axis**, so as a character rotates its stacks stay "behind" it. Use negative values to recede. Stacks without a corresponding entry get Z = 0. |

---

## Animatable Stacks Component (`StackedTextAnimatableStacks`)

Unity's Animation system can keyframe **named serialized fields** on a component but cannot reliably address individual elements inside a `List<T>` — list-element binding paths are not stable across resizes. This module exposes **8 fixed `StackConfig` fields** named `Stack0` through `Stack7` so each one (and its sub-fields like `StartOffset`, `Dilate`, `Enabled`, etc.) can be bound by an `Animator`.

When the module is present and enabled on the same GameObject as `StackedText`, its 8 slots are appended to the rendered stack list each frame, exactly as if the user had added them to `Stacks`.

| Property | Description |
|---|---|
| **Stack0 … Stack7** | Eight `StackConfig` slots with the same fields as a regular stack. Each slot's `Enabled`, `LayerCount`, `StartOffset`, `EndOffset`, `Softness`, and `Dilate` can be keyframed; the `Color` gradient is set in the inspector but cannot be animated by clips (intrinsic Unity limitation). |

**Reset behavior** — the first time you add the module to a GameObject, `Reset()` copies up to 8 entries from the sibling `StackedText.Stacks` list into `Stack0`…`Stack7` so you keep your existing setup. Unused slots are seeded as disabled defaults. To avoid double-rendering, clear `StackedText.Stacks` after copying if you want the module to fully take over.

---

## API

```csharp
// Replace all stacks at runtime
stackedText.SetStacks(new List<StackedText.StackConfig>
{
    StackedText.StackConfig.CreateDefault()
});
```

---

## How It Works

`StackedText` hooks into `Canvas.willRenderCanvases` and rebuilds a combined mesh whenever the text, properties, or transform change.

Each frame, for each TMP material slot, the pipeline runs:

1. **Curve** offsets are computed and added to the source vertices.
2. **Rotate** offsets are applied; each vertex's character-local Z axis (post-rotation) is also captured for use by stack-depth pushes.
3. **Scale** is applied **last**, against the post-curve / post-rotate vertices — this ensures uniform multiplicative behaviour (scale = 0 always collapses to the pivot, regardless of which other modules are active).
4. For each stack (the regular `Stacks` list followed by any active `StackedTextAnimatableStacks` slots), the source mesh vertices are duplicated with per-layer color and offset, plus the optional `StackDepths[s]` value pushed along each vertex's local Z axis.
5. Per-layer `softness` and `dilate` are packed into UV3 for the shader.
6. The final mesh is assigned directly to the `CanvasRenderer`, bypassing TMP's default rendering without modifying the original text data.

The whole effect renders as a **single draw call**, making it mobile-friendly. Sprite (icon) slots are automatically detected and excluded from the stack-layer build so `<sprite=...>` icons render as single un-shadowed quads alongside stacked text.

---
