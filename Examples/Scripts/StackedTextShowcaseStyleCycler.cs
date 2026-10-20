using System.Collections.Generic;
using UnityEngine;

namespace StackedTextExamples
{
    /// <summary>
    /// Showcase helper for the Styles &amp; Shine example: cycles through a list of
    /// <see cref="StackedTextStyle"/> assets at runtime with <see cref="StackedTextStyleApplier.SetStyle"/>.
    /// </summary>
    public class StackedTextShowcaseStyleCycler : MonoBehaviour
    {
        [SerializeField] private StackedTextStyleApplier Target;
        [SerializeField] private List<StackedTextStyle> Styles = new();
        [SerializeField, Min(0.1f)] private float Interval = 2f;

        private int _styleIndex;
        private float _nextSwitchTime;

        private void Reset()
        {
            TryGetComponent(out Target);
        }

        private void OnEnable()
        {
            _styleIndex = 0;
            _nextSwitchTime = Time.time + Interval;
            ApplyStyle(_styleIndex);
        }

        private void Update()
        {
            if (Styles.Count == 0 || Time.time < _nextSwitchTime)
                return;

            _styleIndex = (_styleIndex + 1) % Styles.Count;
            _nextSwitchTime = Time.time + Interval;
            ApplyStyle(_styleIndex);
        }

        private void ApplyStyle(int index)
        {
            if (Target == null || index >= Styles.Count)
                return;

            Target.SetStyle(Styles[index]);
        }
    }
}
