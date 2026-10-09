using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Marks a spot in the scene (or a paintable prefab) as a player spawn point.
    /// The arena export reads its world XZ position into ArenaMap.SpawnPoints.
    /// Authoring content, not netcode — any visual under it is cosmetic.
    /// </summary>
    public sealed class SpawnPointMarker : MonoBehaviour
    {
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position + Vector3.up, new Vector3(1f, 2f, 1f));
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 2f);
        }
    }
}
