using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    public class EditorSyncLinearIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(InterpolationMode.Linear)]
        public Vector3 pos;
    }
}
