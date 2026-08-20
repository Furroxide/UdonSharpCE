using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Guardrails for two failure classes that produce no error message at all: a CE attribute that
    /// silently fails to construct (so the feature it drives quietly does not exist), and a scene whose
    /// behaviour hosts are disabled at load (so no synced behaviour ever receives an Udon event).
    /// </summary>
    public class CEAttributeAndSceneGuardrailTests
    {
        // -----------------------------------------------------------------------------------------
        // Attribute construction — the path every CE feature depends on
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// An audit claimed <c>[LocalOnly]</c> was inert because
        /// <c>LocalOnlyAttribute(string message = null)</c> gives Roslyn a single null constructor
        /// argument and <c>Activator.CreateInstance(type, args)</c> supposedly cannot bind an all-null
        /// argument array — so the attribute would be dropped by the binder's blanket catch and its
        /// analyzer would never fire.
        ///
        /// That premise is false, and this test pins it down so nobody re-litigates it:
        /// <c>Type.DefaultBinder</c> does bind null to a reference-type parameter, and there is only one
        /// candidate constructor.
        ///
        /// What the test also pins down is the case that genuinely would fail — an EMPTY argument array —
        /// because that is what a future refactor of the binder could accidentally start passing.
        /// </summary>
        [Test]
        public void CEAttributesWithOptionalParameters_ConstructThroughTheBinderPath()
        {
            Type localOnly = typeof(LocalOnlyAttribute);

            Assert.That(Activator.CreateInstance(localOnly, new object[] { null }), Is.InstanceOf<LocalOnlyAttribute>(),
                "[LocalOnly] with no explicit argument reaches the binder as one defaulted null argument. " +
                "If this ever fails, [LocalOnly] is silently inert and LocalOnlyAnalyzer never fires.");

            Assert.That(Activator.CreateInstance(localOnly, new object[] { "message" }), Is.InstanceOf<LocalOnlyAttribute>());

            Assert.Throws<MissingMethodException>(
                () => Activator.CreateInstance(localOnly, new object[0]),
                "Documents the shape that genuinely cannot bind: the binder must pass one argument per " +
                "resolved constructor parameter, including defaulted ones, never an empty array.");
        }

        /// <summary>
        /// The binder used to swallow every attribute-construction failure with
        /// <c>catch (Exception) { return null; }</c>, which is indistinguishable from 'the symbol does not
        /// have that attribute'. Since every CE feature is attribute-driven, that turned any such failure
        /// into an invisible feature outage. The failure must now be reported.
        /// </summary>
        [Test]
        public void BinderReportsAttributeConstructionFailures_RatherThanDroppingThem()
        {
            Assembly editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "UdonSharp.Editor");
            Assert.That(editorAssembly, Is.Not.Null);

            Type symbolType = editorAssembly.GetType("UdonSharp.Compiler.Symbols.Symbol");
            Assert.That(symbolType, Is.Not.Null);

            MethodInfo tryCreate = symbolType.GetMethod("TryCreateAttributeInstance",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.That(tryCreate, Is.Not.Null, "TryCreateAttributeInstance no longer exists.");

            ParameterInfo[] parameters = tryCreate.GetParameters();
            Assert.That(parameters.Any(p => p.IsOut), Is.True,
                "TryCreateAttributeInstance must report WHY construction failed via an out parameter. " +
                "Without it the caller cannot distinguish 'no such attribute' from 'attribute broke', and " +
                "a broken CE attribute is once again an invisible outage.");
        }

        // -----------------------------------------------------------------------------------------
        // Scene invariants
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A disabled UdonBehaviour in VRChat does not run Update and does not receive OnDeserialization,
        /// OnPlayerJoined/Left/Restored, SendCustomNetworkEvent or SendCustomEvent. Shipping a scene whose
        /// synced behaviour hosts start disabled is therefore the same as switching sync off for the whole
        /// world — and it presents to the author simply as "nothing syncs".
        /// </summary>
        [Test]
        public void NoUdonSharpBehaviourHost_IsDisabledInTheLoadedScene()
        {
            var behaviours = UnityEngine.Object.FindObjectsOfType<UdonSharpBehaviour>(true);

            if (behaviours.Length == 0)
                Assert.Ignore("No UdonSharpBehaviours in the loaded scene(s).");

            var disabled = behaviours
                .Where(b => b != null && !b.gameObject.activeInHierarchy)
                .Select(b => PathOf(b.transform) + " [" + b.GetType().Name + "]")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.That(disabled, Is.Empty,
                "These behaviour hosts start disabled, so they receive no Udon events at all:\n" +
                string.Join("\n", disabled) +
                "\n\nToggle renderers/colliders/canvases instead of the GameObject that hosts the behaviour.");
        }

        /// <summary>
        /// Every scene UdonBehaviour must have both a programSource and a serializedProgramAsset. A null
        /// serializedProgramAsset means the behaviour runs no program at all while still looking wired up
        /// in the inspector.
        /// </summary>
        [Test]
        public void EverySceneUdonBehaviour_HasAProgramAndASerializedProgram()
        {
            var behaviours = UnityEngine.Object.FindObjectsOfType<VRC.Udon.UdonBehaviour>(true);

            if (behaviours.Length == 0)
                Assert.Ignore("No UdonBehaviours in the loaded scene(s).");

            var broken = new List<string>();

            foreach (VRC.Udon.UdonBehaviour behaviour in behaviours)
            {
                if (behaviour == null)
                    continue;

                if (behaviour.programSource == null)
                    broken.Add(PathOf(behaviour.transform) + ": programSource is null");
                else if (behaviour.programSource.SerializedProgramAsset == null)
                    broken.Add(PathOf(behaviour.transform) + ": serializedProgramAsset is null");
            }

            Assert.That(broken, Is.Empty, "Scene UdonBehaviours with no runnable program:\n" + string.Join("\n", broken));
        }

        /// <summary>
        /// The retired <c>[SyncOnJoin]</c> coordinator was a hidden scene object
        /// (<c>HideFlags.HideInHierarchy</c>) whose absence or staleness produced no diagnostic. Nothing
        /// should recreate it.
        /// </summary>
        [Test]
        public void NoLegacySyncOnJoinCoordinator_RemainsInTheScene()
        {
            var survivors = new List<string>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name.IndexOf("__CE_SyncOnJoinCoordinator", StringComparison.Ordinal) >= 0)
                            survivors.Add(scene.name + ":" + PathOf(t));
                    }
                }
            }

            Assert.That(survivors, Is.Empty,
                "A legacy SyncOnJoin coordinator object is still present:\n" + string.Join("\n", survivors));
        }

        /// <summary>
        /// A Billboard particle renderer with Allow Roll enabled rolls with the camera, so in VR the
        /// whole particle field tilts with the player's head and reads as the world tilting. VRChat's
        /// build panel reports this only as advisory information, never as an error, so it is easy to
        /// add a particle system and ship the finding. Unity defaults allowRoll to true, which makes
        /// every new particle system a fresh regression.
        /// <c>CEParticleRollGuard</c> (Assets/Editor/CECleanupTools.cs) clears it on save; this test
        /// fails the run if one reaches the scene by another route (imported prefab, hand-edited YAML).
        /// </summary>
        [Test]
        public void NoBillboardParticleSystem_RollsWithTheCamera()
        {
            var renderers = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>(true);

            if (renderers.Length == 0)
                Assert.Ignore("No particle systems in the loaded scene(s).");

            var rolling = renderers
                .Where(r => r != null && r.renderMode == ParticleSystemRenderMode.Billboard && r.allowRoll)
                .Select(r => PathOf(r.transform))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.That(rolling, Is.Empty,
                "These billboard particle systems roll with the camera, which VRChat flags as immersion " +
                "breaking in VR:\n" + string.Join("\n", rolling) +
                "\n\nRun CE Tools/Cleanup/Disable Particle Roll, or just re-save the scene.");
        }

        private static string PathOf(Transform transform)
        {
            string path = transform.name;

            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }
    }
}
