using KnockKnockArena.Shared.Simulation;
using UnityEngine;

namespace KnockKnockArena.Gameplay
{
    /// <summary>
    /// Ties the visual map in this scene to the collision data that came out of the
    /// same bake. The World Painter writes arena.json and drops this component on the
    /// baked arena root, carrying that file and the hash it had at bake time.
    ///
    /// Two jobs:
    ///
    /// 1. It loads the arena before anything else runs, so the scene has real bounds,
    ///    walls and a ground height with no server in sight. That keeps the base
    ///    project playable after the netcode folder is deleted.
    ///
    /// 2. It is what the client checks against the server at join. If the server is
    ///    running a different arena — or an edited copy of this one — the hashes
    ///    disagree and the client says so, instead of letting someone play a match
    ///    where the walls they can see are not the walls that stop them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArenaMapStamp : MonoBehaviour
    {
        [Tooltip("arena.json, written by the bake. This is the collision data — the " +
                 "scene geometry around it is only the picture of it.")]
        [SerializeField] private TextAsset _arenaJson;

        [Tooltip("The hash the bake computed, for reading by eye. The authority is the " +
                 "file above: the hash is recomputed from it on load.")]
        [SerializeField] private string _bakedHash;

        /// <summary>The arena this scene was baked from, or null if it failed to load.</summary>
        public ArenaMapData Map { get; private set; }

        /// <summary>Why the map did not load, or empty when it did.</summary>
        public string LoadError { get; private set; } = "";

        private void Awake()
        {
            if (_arenaJson == null)
            {
                LoadError = "no arena.json assigned — re-bake the map, or assign the file by hand";
                Debug.LogError($"{name}: {LoadError}", this);
                return;
            }

            try
            {
                Map = ArenaMapData.Parse(_arenaJson.text);
            }
            catch (System.Exception ex)
            {
                LoadError = ex.Message;
                Debug.LogError($"{name}: could not load {_arenaJson.name}: {ex.Message}", this);
                return;
            }

            ArenaMap.Load(Map);

            if (!string.IsNullOrEmpty(_bakedHash) &&
                ArenaMapData.ParseHash(_bakedHash) != Map.Hash)
            {
                // The file changed after the bake, so the scene geometry and the
                // collision data are no longer the same map.
                LoadError = $"{_arenaJson.name} hashes to {ArenaMapData.FormatHash(Map.Hash)} " +
                            $"but this scene was baked against {_bakedHash} — re-bake the map";
                Debug.LogError($"{name}: {LoadError}", this);
            }
        }

        /// <summary>Called by the bake; not meant for hand-editing in the Inspector.</summary>
        public void SetBakeOutput(TextAsset arenaJson, uint hash)
        {
            _arenaJson = arenaJson;
            _bakedHash = ArenaMapData.FormatHash(hash);
        }
    }
}
