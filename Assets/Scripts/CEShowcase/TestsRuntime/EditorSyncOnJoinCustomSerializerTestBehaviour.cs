using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    public class EditorSyncOnJoinCustomSerializerTestBehaviour : UdonSharpBehaviour
    {
        [SyncOnJoin]
        [SyncSerializer(nameof(SerializeTarget), nameof(DeserializeTarget))]
        public GameObject target;

        public string SerializeTarget()
        {
            return "";
        }

        public void DeserializeTarget(string data)
        {
        }
    }
}
