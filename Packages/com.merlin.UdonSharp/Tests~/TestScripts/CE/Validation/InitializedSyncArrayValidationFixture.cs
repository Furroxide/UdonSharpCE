using UdonSharp;
using UdonSharp.CE.Net;

namespace UdonSharp.Tests.CE.Validation
{
    public class InitializedSyncArrayValidationFixture : UdonSharpBehaviour
    {
        [Sync(Authority.Owner)]
        public int[] values = new int[4];
    }
}
