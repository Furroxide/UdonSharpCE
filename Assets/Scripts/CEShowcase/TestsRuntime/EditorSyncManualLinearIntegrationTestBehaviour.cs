using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class EditorSyncManualLinearIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(InterpolationMode.Linear)]
        public int value;
    }
}
