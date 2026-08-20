using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace UdonSharp.Tests.CE.Net
{
    public class SyncNamedInterpolationIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)]
        public Vector3 pos;
    }
}
