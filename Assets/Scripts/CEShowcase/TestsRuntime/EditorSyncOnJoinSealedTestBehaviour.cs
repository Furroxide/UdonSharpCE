using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// A sealed [SyncOnJoin] carrier. The generated glue used to declare its members <c>virtual</c>, which
    /// is CS0549 on a sealed class -- and generated code that does not compile takes every program in the
    /// project down with it, not just this one.
    ///
    /// The const field is here for CE0032: the glue leaves it out of the payload, and the warning has to
    /// say so, because its type is a supported one and the type check never fires for it.
    /// </summary>
    public sealed class EditorSyncOnJoinSealedTestBehaviour : UdonSharpBehaviour
    {
        [SyncOnJoin]
        public const int SchemaVersion = 2;

        [SyncOnJoin]
        public int score;

        [SyncOnJoin(Group = "detail")]
        public string label = "";
    }
}
