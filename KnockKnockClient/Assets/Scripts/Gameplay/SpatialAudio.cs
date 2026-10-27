using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>What a sound is, so the settings menu (GameMenu) can turn a kind off or down.</summary>
    public enum SoundKind { Other, Footstep, Weapon }

    /// <summary>
    /// One-shot 3D sounds with a range that fits the arena: full volume up close,
    /// fading linearly to silence at <see cref="MaxDistance"/>. Footsteps, shots and
    /// explosions all go through here, so "how far can I hear things" is one number.
    ///
    /// Why not AudioSource.PlayClipAtPoint: its default logarithmic rolloff reaches
    /// 500 m — on a 144 m map that is "everything everywhere", and footsteps across
    /// the arena sounded as loud as your own. A game wants the opposite: hearing
    /// someone means they are close.
    ///
    /// The listener is the local player (NetworkBootstrap places it), so distance
    /// here means distance from YOU, not from the camera high above.
    /// </summary>
    public static class SpatialAudio
    {
        // The settings menu's sound settings (GameMenu), kept in PlayerPrefs between runs.
        public static bool FootstepsEnabled = PlayerPrefs.GetInt("kka.sound.footsteps", 1) == 1;
        public static float FootstepsVolume = PlayerPrefs.GetFloat("kka.sound.footstepsVolume", 1f);
        public static bool WeaponsEnabled = PlayerPrefs.GetInt("kka.sound.weapons", 1) == 1;
        public static float WeaponsVolume = PlayerPrefs.GetFloat("kka.sound.weaponsVolume", 1f);

        public static void SaveSettings()
        {
            PlayerPrefs.SetInt("kka.sound.footsteps", FootstepsEnabled ? 1 : 0);
            PlayerPrefs.SetFloat("kka.sound.footstepsVolume", FootstepsVolume);
            PlayerPrefs.SetInt("kka.sound.weapons", WeaponsEnabled ? 1 : 0);
            PlayerPrefs.SetFloat("kka.sound.weaponsVolume", WeaponsVolume);
            PlayerPrefs.Save();
        }

        // the menu's factor for a kind: 0 when it is turned off
        private static float Factor(SoundKind kind) => kind switch
        {
            SoundKind.Footstep => FootstepsEnabled ? FootstepsVolume : 0f,
            SoundKind.Weapon => WeaponsEnabled ? WeaponsVolume : 0f,
            _ => 1f,
        };

        /// <summary>Within this distance a sound plays at full volume.</summary>
        public static float MinDistance = 4f;

        /// <summary>Beyond this distance a sound is silent. About a third of the map:
        /// you hear the fight next door, not the one across the arena.</summary>
        public static float MaxDistance = 50f;

        /// <summary>Plays a random clip from <paramref name="clips"/> at a world position.
        /// <paramref name="volume"/> is a scale, 1 = the file as recorded; above 1 amplifies.
        /// A small pitch spread keeps rapid repeats (an AK burst, footsteps) from
        /// sounding like a machine.</summary>
        public static void Play(AudioClip[] clips, Vector3 position, float volume = 1f,
            float maxDistance = -1f, float pitchJitter = 0.05f, SoundKind kind = SoundKind.Other)
        {
            if (clips == null || clips.Length == 0)
                return;
            Play(clips[Random.Range(0, clips.Length)], position, volume, maxDistance, pitchJitter, kind);
        }

        public static void Play(AudioClip clip, Vector3 position, float volume = 1f,
            float maxDistance = -1f, float pitchJitter = 0.05f, SoundKind kind = SoundKind.Other)
        {
            if (kind == SoundKind.Other && WeaponGrip.IsWeaponSound(clip))
                kind = SoundKind.Weapon;   // a weapon's fire or impact sound, played without saying so
            volume *= Factor(kind);
            if (clip == null || volume <= 0f)
                return;

            GameObject go = new GameObject("Sound " + clip.name);
            go.transform.position = position;
            AudioSource source = go.AddComponent<AudioSource>();
            source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            source.spatialBlend = 1f;                       // fully 3D
            source.rolloffMode = AudioRolloffMode.Linear;   // silent at maxDistance, not 500 m away
            source.minDistance = MinDistance;
            source.maxDistance = maxDistance > 0f ? maxDistance : MaxDistance;
            source.dopplerLevel = 0f;                       // no pitch wobble from fast dashes
            // PlayOneShot's volume SCALE may exceed 1 (AudioSource.volume stops at 1),
            // which is how the source game's quiet punch files played at "10".
            source.PlayOneShot(clip, volume);
            Object.Destroy(go, clip.length / Mathf.Max(0.01f, source.pitch) + 0.1f);
        }
    }
}
