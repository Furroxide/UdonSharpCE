using System.Reflection;
using NUnit.Framework;
using UdonSharp.CE.Tests;
using UnityEngine;

namespace UdonSharp.CE.Tests.Net
{
    public class SyncOnJoinEmitterTests
    {
        [Test]
        public void SyncOnJoinEmitter_GeneratesGlueAndRoundTripsState()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinFixture : UdonSharpBehaviour
{
    [SyncOnJoin]
    public int gamePhase;

    [SyncOnJoin]
    public bool[] completed;
}";

            string transformed = EditorTestUtilities.RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Serialize"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Deserialize_Trigger"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinInbound"));
            Assert.That(transformed, Does.Contain("SyncOnJoinGenerated"));

            Assembly assembly = EditorTestUtilities.CompileAssembly(transformed, "SyncOnJoinEmitterTests");
            Type type = assembly.GetType("SyncOnJoinFixture");

            object sourceInstance = System.Activator.CreateInstance(type);
            type.GetField("gamePhase").SetValue(sourceInstance, 7);
            type.GetField("completed").SetValue(sourceInstance, new[] { true, false, true });
            type.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);

            string payload = (string)type.GetField("_CE_SyncOnJoinPayload").GetValue(sourceInstance);
            Assert.That(payload, Is.Not.Null.And.Not.Empty);

            object destinationInstance = System.Activator.CreateInstance(type);
            type.GetField("_CE_SyncOnJoinInbound").SetValue(destinationInstance, payload);
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            Assert.That((int)type.GetField("gamePhase").GetValue(destinationInstance), Is.EqualTo(7));
            CollectionAssert.AreEqual(
                new[] { true, false, true },
                (bool[])type.GetField("completed").GetValue(destinationInstance));
        }

        [Test]
        public void SyncOnJoinEmitter_ClassifiesAliasTypesFromSemanticModel()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using Vec = UnityEngine.Vector3;

public class SyncOnJoinAliasFixture : UdonSharpBehaviour
{
    [SyncOnJoin]
    public Vec spawn;
}";

            string transformed = EditorTestUtilities.RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Serialize"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Deserialize_Trigger"));

            Assembly assembly = EditorTestUtilities.CompileAssembly(transformed, "SyncOnJoinAliasEmitterTests");
            Type type = assembly.GetType("SyncOnJoinAliasFixture");

            object sourceInstance = System.Activator.CreateInstance(type);
            type.GetField("spawn").SetValue(sourceInstance, new Vector3(1.25f, -2f, 3.5f));
            type.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);

            string payload = (string)type.GetField("_CE_SyncOnJoinPayload").GetValue(sourceInstance);
            object destinationInstance = System.Activator.CreateInstance(type);
            type.GetField("_CE_SyncOnJoinInbound").SetValue(destinationInstance, payload);
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            Vector3 result = (Vector3)type.GetField("spawn").GetValue(destinationInstance);
            Assert.That(result.x, Is.EqualTo(1.25f).Within(0.0001f));
            Assert.That(result.y, Is.EqualTo(-2f).Within(0.0001f));
            Assert.That(result.z, Is.EqualTo(3.5f).Within(0.0001f));
        }
    }
}
