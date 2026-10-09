using KnockKnockArena.Shared.Protocol.Udp;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// The weapon bar: one slot per weapon, showing which you own and which is in
    /// your hands. Ported from the source game's action bar; the difference is that
    /// this arena carries several weapons switched by number key rather than one per
    /// hand. The COUNTS live on the StatusHud in the screen corners (CS style) — a
    /// slot shows what you carry, not how much.
    ///
    /// Everything shown is PUSHED IN: the weapons you own are the server's state,
    /// and this component never reaches for it. That keeps the bar in the base
    /// project with no netcode reference.
    /// </summary>
    public sealed class ActionBar : MonoBehaviour
    {
        [System.Serializable]
        public struct Slot
        {
            public WeaponId Weapon;
            public ActionButton Button;
            [Tooltip("Icon for this weapon. Empty keeps the slot frame, so it does " +
                     "not vanish into a bare key number.")]
            public Sprite Icon;
        }

        [Tooltip("One entry per weapon the bar can show, in the order they appear. The " +
                 "number key that selects a slot is its position here.")]
        [SerializeField] private Slot[] _slots;

        private void Start()
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Button != null)
                    _slots[i].Button.SetKeyLabel((i + 1).ToString());
        }

        /// <summary>Redraws the bar: which slots you own, and which is drawn.</summary>
        public void Refresh(byte ownedWeapons, WeaponId activeWeapon)
        {
            if (_slots == null)
                return;

            foreach (Slot slot in _slots)
            {
                if (slot.Button == null)
                    continue;

                bool owned = (ownedWeapons & 1 << (int)slot.Weapon) != 0;
                slot.Button.SetWeapon(slot.Icon, owned, slot.Weapon == activeWeapon);
            }
        }
    }
}
