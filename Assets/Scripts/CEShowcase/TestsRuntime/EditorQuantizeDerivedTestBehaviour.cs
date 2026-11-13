using UdonSharp;
using UdonSharp.CE.Net;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// Both this behaviour and its base quantize a field, so each gets a rounding helper. Under one shared
    /// helper name the derived one repeated a base method's name and parameters without overriding it,
    /// which UdonSharp rejects ("does not yet support hiding base methods") -- while the same source built
    /// cleanly as plain C#.
    /// </summary>
    public class EditorQuantizeDerivedTestBehaviour : EditorQuantizeBaseTestBehaviour
    {
        [Sync(Authority.Owner, Quantize = 0.5f)]
        public float derivedValue;
    }
}
