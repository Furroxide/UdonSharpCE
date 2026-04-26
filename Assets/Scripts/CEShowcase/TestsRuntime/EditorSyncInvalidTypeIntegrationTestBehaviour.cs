using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    public class EditorSyncInvalidTypeIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync]
        public Object obj;
    }
}
