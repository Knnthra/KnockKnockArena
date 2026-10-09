using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The dash ability icon: bright when the dash is ready; while it recharges, a
    /// dark radial overlay sweeps away clockwise as the cooldown runs out, with the
    /// seconds left on top. A short pop when it comes back tells you without looking.
    ///
    /// Part of the student base project: a picture of state pushed in from outside
    /// (<see cref="Set"/>), knowing nothing about where the state came from. The
    /// reference client feeds it the PREDICTED dash state, so the sweep starts the
    /// moment you press dash rather than a round trip later.
    /// </summary>
    [ExecuteAlways]
    public sealed class DashCooldownIcon : MonoBehaviour
    {
        [Tooltip("The dash icon itself. Assign your own sprite here.")]
        [SerializeField] private Image _icon;

        [Tooltip("Dark overlay on top of the icon, Image Type = Filled, Radial 360. Its sprite, " +
                 "colour and aspect are your own choice; only its RECT follows the icon, so the " +
                 "sweep always sits exactly on top of it.")]
        [SerializeField] private Image _overlay;

        [Tooltip("Seconds left, shown only while recharging. Optional.")]
        [SerializeField] private TMP_Text _secondsText;

        [Header("Look")]
        [Tooltip("Icon tint while recharging — dimmed, so 'not ready' reads at a glance.")]
        [SerializeField] private Color _coolingTint = new Color(0.55f, 0.55f, 0.55f, 1f);

        [Tooltip("Scale pop when the dash comes back.")]
        [SerializeField] private float _readyPopScale = 1.25f;
        [SerializeField] private float _readyPopSeconds = 0.2f;

        private Color _readyTint = Color.white;
        private bool _wasReady = true;
        private float _popTimer;

        private void Awake()
        {
            if (_icon != null)
                _readyTint = _icon.color;
            MatchOverlayToIcon();
            if (_overlay != null)
            {
                _overlay.type = Image.Type.Filled;
                _overlay.fillMethod = Image.FillMethod.Radial360;
                _overlay.fillOrigin = (int)Image.Origin360.Top;
                _overlay.fillClockwise = true;
                _overlay.fillAmount = 0f;
            }
            if (_secondsText != null)
                _secondsText.text = "";
        }

        /// <param name="cooldownTicksLeft">Ticks until the next dash may start (0 = ready).</param>
        /// <param name="cooldownTotalTicks">The full cooldown, for the sweep fraction.</param>
        /// <param name="tickRate">Ticks per second, to show seconds.</param>
        public void Set(int cooldownTicksLeft, int cooldownTotalTicks, float tickRate)
        {
            bool ready = cooldownTicksLeft <= 0;
            float fraction = ready || cooldownTotalTicks <= 0
                ? 0f
                : Mathf.Clamp01((float)cooldownTicksLeft / cooldownTotalTicks);

            if (_overlay != null)
                _overlay.fillAmount = fraction;
            if (_icon != null)
                _icon.color = ready ? _readyTint : _coolingTint;
            if (_secondsText != null)
                _secondsText.text = ready ? "" : Mathf.CeilToInt(cooldownTicksLeft / tickRate).ToString();

            if (ready && !_wasReady)
                _popTimer = _readyPopSeconds;
            _wasReady = ready;
        }

        /// <summary>
        /// Keeps the overlay's RECT on top of the icon's: same anchors, position, size,
        /// scale and rotation. Done every frame (also in edit mode), so resizing or
        /// moving the icon takes the sweep with it. The overlay's sprite is left alone
        /// — it is chosen separately.
        /// </summary>
        private void MatchOverlayToIcon()
        {
            if (_icon == null || _overlay == null)
                return;

            RectTransform from = _icon.rectTransform;
            RectTransform to = _overlay.rectTransform;
            // Write only on a difference: assigning equal values every frame in edit
            // mode would keep marking the scene as modified.
            if (to.parent == from.parent)
            {
                if (to.anchorMin != from.anchorMin) to.anchorMin = from.anchorMin;
                if (to.anchorMax != from.anchorMax) to.anchorMax = from.anchorMax;
                if (to.pivot != from.pivot) to.pivot = from.pivot;
                if (to.anchoredPosition != from.anchoredPosition) to.anchoredPosition = from.anchoredPosition;
                if (to.sizeDelta != from.sizeDelta) to.sizeDelta = from.sizeDelta;
                if (to.localScale != from.localScale) to.localScale = from.localScale;
                if (to.localRotation != from.localRotation) to.localRotation = from.localRotation;
            }
        }

        private void LateUpdate() => MatchOverlayToIcon();

        private void Update()
        {
            if (!Application.isPlaying)
                return;
            if (_popTimer <= 0f)
            {
                transform.localScale = Vector3.one;
                return;
            }
            _popTimer -= Time.deltaTime;
            float t = 1f - Mathf.Clamp01(_popTimer / _readyPopSeconds); // 0 -> 1
            float pop = Mathf.Sin(t * Mathf.PI);                          // up and back
            transform.localScale = Vector3.one * Mathf.Lerp(1f, _readyPopScale, pop);
        }
    }
}
