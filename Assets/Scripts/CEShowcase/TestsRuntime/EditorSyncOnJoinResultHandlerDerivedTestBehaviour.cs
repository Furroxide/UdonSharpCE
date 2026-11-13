using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// A [SyncOnJoin] carrier whose base handles <c>OnDeserialization(DeserializationResult)</c>. The glue
    /// used to give it a parameterless <c>OnDeserialization()</c>: both overloads export as the one Udon
    /// event <c>_onDeserialization</c>, so the program had it twice and UdonSharp refused it.
    /// </summary>
    public class EditorSyncOnJoinResultHandlerDerivedTestBehaviour : EditorSyncOnJoinResultHandlerBaseTestBehaviour
    {
        [SyncOnJoin]
        public int phase;
    }
}
