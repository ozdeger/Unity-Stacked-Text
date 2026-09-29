using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace StackedTextExamples
{
    /// <summary>
    /// Showcase helper for the Runtime API example: swaps whole stack setups at runtime through
    /// <see cref="StackedText.SetStacks"/>, together with the text's fill gradient, cycling through
    /// a list of presets.
    /// </summary>
    public class StackedTextShowcasePresetCycler : MonoBehaviour
    {
        [Serializable]
        public class Preset
        {
            public string Name;
            public Color FillTop = Color.white;
            public Color FillBottom = Color.white;
            public List<StackedText.StackConfig> Stacks = new();
        }

        [SerializeField] private StackedText Target;
        [SerializeField] private TMP_Text Text;
        [SerializeField] private List<Preset> Presets = new();
        [SerializeField, Min(0.1f)] private float Interval = 1.5f;

        private int _presetIndex;
        private float _nextSwitchTime;

        private void Reset()
        {
            TryGetComponent(out Target);
            TryGetComponent(out Text);
        }

        private void OnEnable()
        {
            _presetIndex = 0;
            _nextSwitchTime = Time.time + Interval;
            ApplyPreset(_presetIndex);
        }

        private void Update()
        {
            if (Presets.Count == 0 || Time.time < _nextSwitchTime)
                return;

            _presetIndex = (_presetIndex + 1) % Presets.Count;
            _nextSwitchTime = Time.time + Interval;
            ApplyPreset(_presetIndex);
        }

        private void ApplyPreset(int index)
        {
            if (Target == null || index >= Presets.Count)
                return;

            var preset = Presets[index];
            if (Text != null)
            {
                Text.enableVertexGradient = true;
                Text.colorGradient = new VertexGradient(preset.FillTop, preset.FillTop, preset.FillBottom, preset.FillBottom);
            }
            Target.SetStacks(preset.Stacks);
        }
    }
}
