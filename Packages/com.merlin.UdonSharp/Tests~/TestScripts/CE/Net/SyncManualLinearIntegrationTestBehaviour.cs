using UdonSharp;
using UdonSharp.CE.Net;

namespace UdonSharp.Tests.CE.Net
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class SyncManualLinearIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, InterpolationMode.Linear)]
        public int value;
    }
}
