using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// One weapon slot in the action bar: its icon, its ammo, and a scale-up when it
    /// is the weapon in your hands. Ported from the source game, minus the reload
    /// indicator — this arena has ammo pools and no reloading, so a slot shows a
    /// plain count (24) rather than a magazine (12/12).
    ///
    /// Part of the student base project: it is a picture of state pushed in from
    /// outside, and knows nothing about where that state came from.
    /// </summary>
    public sealed class ActionButton : MonoBehaviour
    {
        [SerializeField] private Image _iconImage;
        [SerializeField] private Image _background;

        // TextMeshPro rather than the legacy Text: small UI text has to stay crisp
        // at any resolution, which is exactly what TMP's SDF rendering is for.
        // Ammo is NOT here — the count you check mid-fight lives on the StatusHud
        // in the screen corner, CS style. A slot shows what you carry, not how much.
        [SerializeField] private TMP_Text _keyText;

        [Header("Selection")]
        [SerializeField] private float _selectedScale = 1.2f;

        [Tooltip("Scale of a slot whose weapon you do NOT carry. The empty frame is " +
                 "a full bright square while icons have air around the weapon, so at " +
                 "equal scale an empty slot reads as LARGE as the selected one. " +
                 "Shrinking it gives the bar three readable steps: empty < owned < selected.")]
        [SerializeField] private float _missingScale = 0.8f;

        [Tooltip("Tint on the empty frame, dimming it behind the slots that matter.")]
        [SerializeField] private Color _missingFrameTint = new Color(1f, 1f, 1f, 0.55f);

        [SerializeField] private float _scaleSpeed = 8f;
        [SerializeField] private Color _ownedTint = Color.white;

        private float _targetScale = 1f;

        /// <summary>The number key that selects this slot, shown in the corner.</summary>
        public void SetKeyLabel(string label)
        {
            if (_keyText != null)
                _keyText.text = label;
        }

        /// <summary>
        /// Shows a weapon.
        ///
        /// The background image carries the EMPTY-SLOT sprite, and it means exactly
        /// that: "you do not carry this weapon". So the frame and the icon swap
        /// places rather than stack — owning the weapon replaces the empty frame
        /// with the weapon's icon (source game behaviour). A weapon you own but
        /// have no icon art for keeps the frame, so its slot does not vanish into
        /// a bare number.
        /// </summary>
        public void SetWeapon(Sprite icon, bool owned, bool selected)
        {
            bool showIcon = owned && icon != null;

            if (_iconImage != null)
            {
                _iconImage.sprite = icon;
                _iconImage.enabled = showIcon;
                _iconImage.color = _ownedTint;
            }

            if (_background != null)
            {
                _background.enabled = !showIcon;
                _background.color = owned ? _ownedTint : _missingFrameTint;
            }

            _targetScale = selected ? _selectedScale
                         : owned ? 1f
                         : _missingScale;
        }

        private void Update()
        {
            float current = transform.localScale.x;
            if (Mathf.Abs(current - _targetScale) > 0.001f)
                transform.localScale = Vector3.one * Mathf.Lerp(current, _targetScale, Time.deltaTime * _scaleSpeed);
        }
    }
}
