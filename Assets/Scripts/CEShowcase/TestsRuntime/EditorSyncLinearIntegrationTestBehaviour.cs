using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    public class EditorSyncLinearIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, InterpolationMode.Linear)]
        public Vector3 pos;
    }
}
