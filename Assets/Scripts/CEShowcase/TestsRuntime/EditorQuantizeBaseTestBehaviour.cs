using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// Base half of a hierarchy where both behaviours quantize a field. See
    /// <see cref="EditorQuantizeDerivedTestBehaviour"/>.
    /// </summary>
    public class EditorQuantizeBaseTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, Quantize = 0.1f)]
        public float baseValue;
    }
}
