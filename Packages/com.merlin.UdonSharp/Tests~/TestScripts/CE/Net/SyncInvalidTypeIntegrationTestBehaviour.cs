using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace UdonSharp.Tests.CE.Net
{
    public class SyncInvalidTypeIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner)]
        public Object obj;
    }
}
