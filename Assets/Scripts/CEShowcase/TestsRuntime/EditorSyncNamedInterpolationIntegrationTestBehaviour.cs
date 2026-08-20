using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace CESyncRuntimeTests
{
    /// <summary>
    /// The named-argument interpolation spelling, in an assembly Unity actually imports.
    ///
    /// Its sibling EditorSyncLinearIntegrationTestBehaviour covers the positional form. The named form
    /// travels a different route in two places, and neither is exercised by the positional one: the C#
    /// compiler stores it in the attribute's NamedArguments rather than its ConstructorArguments, and
    /// UdonSharp's Symbol.TryCreateAttributeInstance applies those named arguments by reflection *after*
    /// Activator.CreateInstance has already run the constructor. The interpolation therefore only reaches
    /// the wire while SyncAttribute assigns through the Interpolation property, whose setter is what
    /// drives the inherited NetworkSyncType. Assign the backing field instead and this field silently
    /// serializes as UdonSyncMode.None while SyncAttribute.Interpolation still reads Linear -- which also
    /// disarms CE0010's Manual-mode check, because that check reads the property and the pipeline reads
    /// NetworkSyncType.
    ///
    /// Until this file the only occurrence of the named spelling anywhere was under Tests~/, which Unity
    /// does not import and therefore never compiles.
    /// </summary>
    public class EditorSyncNamedInterpolationIntegrationTestBehaviour : UdonSharpBehaviour
    {
        [Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)]
        public Vector3 pos;
    }
}
