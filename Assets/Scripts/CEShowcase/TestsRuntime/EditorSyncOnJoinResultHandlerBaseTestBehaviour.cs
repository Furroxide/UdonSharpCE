using UdonSharp;
using VRC.Udon.Common;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// A base behaviour with no [SyncOnJoin] state whose author handles deserialization through the
    /// <c>DeserializationResult</c> overload. See <see cref="EditorSyncOnJoinResultHandlerDerivedTestBehaviour"/>.
    /// </summary>
    public class EditorSyncOnJoinResultHandlerBaseTestBehaviour : UdonSharpBehaviour
    {
        public int baseHandlerRuns;

        public override void OnDeserialization(DeserializationResult result)
        {
            baseHandlerRuns++;
        }
    }
}
