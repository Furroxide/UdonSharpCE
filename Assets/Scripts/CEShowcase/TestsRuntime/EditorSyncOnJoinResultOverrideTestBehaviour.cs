using UdonSharp;
using VRC.Udon.Common;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// The mirror image of <see cref="EditorSyncOnJoinResultHandlerDerivedTestBehaviour"/>: the base carries
    /// the [SyncOnJoin] state and this derived behaviour's author handles the <c>DeserializationResult</c>
    /// overload. The glue gave the base a parameterless <c>OnDeserialization()</c>, and this behaviour's
    /// program then exported <c>_onDeserialization</c> twice.
    /// </summary>
    public class EditorSyncOnJoinResultOverrideTestBehaviour : EditorSyncOnJoinCarrierBaseTestBehaviour
    {
        public int derivedHandlerRuns;

        public override void OnDeserialization(DeserializationResult result)
        {
            derivedHandlerRuns++;
        }
    }
}
