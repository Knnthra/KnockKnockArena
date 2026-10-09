using UnityEngine;

namespace KnockKnockArena.Gameplay
{
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
            float maxDistance = -1f, float pitchJitter = 0.05f)
        {
            if (clips == null || clips.Length == 0)
                return;
            Play(clips[Random.Range(0, clips.Length)], position, volume, maxDistance, pitchJitter);
        }

        public static void Play(AudioClip clip, Vector3 position, float volume = 1f,
            float maxDistance = -1f, float pitchJitter = 0.05f)
        {
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
