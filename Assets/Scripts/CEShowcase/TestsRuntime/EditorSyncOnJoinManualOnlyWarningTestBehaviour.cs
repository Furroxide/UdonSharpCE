using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    public class EditorSyncOnJoinManualOnlyWarningTestBehaviour : UdonSharpBehaviour
    {
        [SyncOnJoin]
        public GameObject target;
    }
}
