using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// A behaviour whose [SyncOnJoin] state is split across payloads by <c>Group</c>, plus one ungrouped
    /// field so the default payload keeps its unsuffixed names alongside the named ones.
    ///
    /// It declares its own <c>OnDeserialization</c> because that is the shape every showcase carrier has:
    /// the generated apply calls must be injected into a handler the author already wrote, one per group,
    /// rather than synthesized.
    /// </summary>
    public class EditorSyncOnJoinGroupedTestBehaviour : UdonSharpBehaviour
    {
        /// <summary>Cold state: large, and rewritten only when the world regenerates.</summary>
        [SyncOnJoin(Group = "terrain", Priority = 10)]
        public int[] heightmap = new int[32];

        [SyncOnJoin(Group = "terrain", Priority = 20)]
        public int terrainVersion;

        /// <summary>Hot state: small, and rewritten constantly. Bundling the two is what Group undoes.</summary>
        [SyncOnJoin(Group = "score")]
        public int score;

        /// <summary>No group named, so this rides the default payload.</summary>
        [SyncOnJoin]
        public string label = "";

        /// <summary>Proves the author's own handler still runs after the glue was injected into it.</summary>
        public int deserializations;

        public override void OnDeserialization()
        {
            deserializations++;
        }
    }
}
