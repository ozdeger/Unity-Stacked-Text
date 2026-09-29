using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StackedTextExamples
{
    /// <summary>
    /// Showcase helper: a Time.timeScale control, a slider plus a text field for exact values, so the
    /// animated examples can be slowed down, frozen or sped up. The previous time scale is restored
    /// when the control is disabled.
    /// </summary>
    public class StackedTextShowcaseTimeScale : MonoBehaviour
    {
        [SerializeField] private Slider Slider;
        [SerializeField] private TMP_InputField ValueField;
        [Tooltip("Upper limit for typed values; the slider covers its own min/max range.")]
        [SerializeField, Min(0f)] private float MaxTypedScale = 10f;

        private float _previousTimeScale = 1f;

        private void OnEnable()
        {
            _previousTimeScale = Time.timeScale;
            if (Slider != null)
                Slider.onValueChanged.AddListener(OnSliderChanged);
            if (ValueField != null)
                ValueField.onEndEdit.AddListener(OnInputSubmitted);
            Apply(Time.timeScale);
        }

        private void Start()
        {
            EnsureInputModule();
        }

        private void OnDisable()
        {
            if (Slider != null)
                Slider.onValueChanged.RemoveListener(OnSliderChanged);
            if (ValueField != null)
                ValueField.onEndEdit.RemoveListener(OnInputSubmitted);
            Time.timeScale = _previousTimeScale;
        }

        private void OnSliderChanged(float value)
        {
            Apply(value);
        }

        private void OnInputSubmitted(string text)
        {
            // Accept "0.5", "0,5" and "0.5x"; anything unparsable just restores the current value.
            var cleaned = text.Trim().TrimEnd('x', 'X').Replace(',', '.');
            if (float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                Apply(value);
            else
                Apply(Time.timeScale);
        }

        private void Apply(float value)
        {
            value = Mathf.Clamp(value, 0f, MaxTypedScale);
            Time.timeScale = value;
            if (Slider != null)
                Slider.SetValueWithoutNotify(value);
            if (ValueField != null)
                ValueField.SetTextWithoutNotify(value.ToString("0.00", CultureInfo.InvariantCulture));
        }

        // The scene ships a bare EventSystem so it works with either input backend: the Input System
        // package's UI module when that package drives input, the legacy StandaloneInputModule otherwise.
        private static void EnsureInputModule()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
                eventSystem = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            if (eventSystem.GetComponent<BaseInputModule>() != null)
                return;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var inputSystemModule = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
            {
                eventSystem.gameObject.AddComponent(inputSystemModule);
                return;
            }
#endif
            eventSystem.gameObject.AddComponent<StandaloneInputModule>();
        }
    }
}
