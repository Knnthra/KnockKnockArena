using TMPro;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The Counter-Strike corners: health as a big number bottom-left, the active
    /// weapon's ammo bottom-right. The action bar's job is what you CARRY; the two
    /// numbers you check mid-fight live at the screen edges where your eyes already
    /// are, big enough to read without looking.
    ///
    /// Part of the student base project: a picture of state pushed in from outside
    /// (<see cref="Set"/>), knowing nothing about where the state came from.
    /// </summary>
    public sealed class StatusHud : MonoBehaviour
    {
        [SerializeField] private TMP_Text _healthText;

        [Tooltip("The whole ammo plate, hidden for melee — a fist has no count.")]
        [SerializeField] private GameObject _ammoGroup;
        [SerializeField] private TMP_Text _ammoText;

        [Header("Low health")]
        [Tooltip("At or below this, the health number turns warning-coloured (CS red).")]
        [SerializeField] private int _lowHealthThreshold = 25;
        [SerializeField] private Color _lowHealthColor = new Color(0.9f, 0.15f, 0.15f);

        private Color _healthNormalColor = Color.white;

        private void Awake()
        {
            if (_healthText != null)
                _healthNormalColor = _healthText.color;
        }

        public void Set(int health, int ammo, bool showAmmo)
        {
            if (_healthText != null)
            {
                _healthText.text = health.ToString();
                _healthText.color = health <= _lowHealthThreshold
                    ? _lowHealthColor
                    : _healthNormalColor;
            }

            if (_ammoGroup != null)
                _ammoGroup.SetActive(showAmmo);
            if (_ammoText != null && showAmmo)
                _ammoText.text = ammo.ToString();
        }
    }
}
