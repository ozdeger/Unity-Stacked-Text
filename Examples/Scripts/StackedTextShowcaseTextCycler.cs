using TMPro;
using UnityEngine;

namespace StackedTextExamples
{
    /// <summary>
    /// Showcase helper for the Color Swaps example: cycles the text through rich-text messages at
    /// runtime, so each stack's Color Swaps can be seen following the &lt;color&gt; tags as the
    /// words change.
    /// </summary>
    public class StackedTextShowcaseTextCycler : MonoBehaviour
    {
        [SerializeField] private TMP_Text Text;
        [SerializeField, TextArea] private string[] Messages = new string[0];
        [SerializeField, Min(0.1f)] private float Interval = 1.6f;

        private int _messageIndex;
        private float _nextSwitchTime;

        private void Reset()
        {
            TryGetComponent(out Text);
        }

        private void OnEnable()
        {
            _messageIndex = 0;
            _nextSwitchTime = Time.time + Interval;
            ShowMessage(_messageIndex);
        }

        private void Update()
        {
            if (Messages == null || Messages.Length == 0 || Time.time < _nextSwitchTime)
                return;

            _messageIndex = (_messageIndex + 1) % Messages.Length;
            _nextSwitchTime = Time.time + Interval;
            ShowMessage(_messageIndex);
        }

        private void ShowMessage(int index)
        {
            if (Text == null || Messages == null || index >= Messages.Length)
                return;

            Text.text = Messages[index];
        }
    }
}
