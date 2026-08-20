using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace UdonSharp.Tests.CE.Net
{
    public class SyncLinearIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, InterpolationMode.Linear)]
        public Vector3 pos;
    }
}
