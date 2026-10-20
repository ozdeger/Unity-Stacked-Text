using UnityEngine;

/// <summary>
/// Draws an object reference field as a searchable dropdown that lists every asset of the field's
/// type in the project by name, plus <c>None</c> and <c>Create New…</c>. The field still
/// serializes a plain asset reference, so renaming or moving an asset never breaks it and builds
/// include the referenced asset automatically.
/// </summary>
public class AssetDropdownAttribute : PropertyAttribute
{
}
