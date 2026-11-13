using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// A [SyncOnJoin] carrier that handles no event itself. See
    /// <see cref="EditorSyncOnJoinResultOverrideTestBehaviour"/>.
    /// </summary>
    public class EditorSyncOnJoinCarrierBaseTestBehaviour : UdonSharpBehaviour
    {
        [SyncOnJoin]
        public int phase;
    }
}
