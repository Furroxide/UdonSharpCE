using System.Reflection;
using NUnit.Framework;
using UdonSharp.CE.Tests;
using UnityEngine;

namespace UdonSharp.CE.Tests.Net
{
    public class QuantizeEmitterTests
    {
        [Test]
        public void QuantizeEmitter_RoundsQuantizedFieldsDuringOnPreSerialization()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class QuantizeFixture : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Quantize = 0.1f)]
    public float value = 0.1234f;
}";

            string transformed = EditorTestUtilities.RunOptimizer("UdonSharp.CE.Editor.Optimizers.QuantizePreSerializeEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_ApplyQuantizePreSerialization"));
            Assert.That(transformed, Does.Contain("OnPreSerialization"));

            Assembly assembly = EditorTestUtilities.CompileAssembly(transformed, "QuantizeEmitterTests");
            Type type = assembly.GetType("QuantizeFixture");
            object instance = System.Activator.CreateInstance(type);

            type.GetMethod("OnPreSerialization").Invoke(instance, null);

            float value = (float)type.GetField("value").GetValue(instance);
            Assert.That(value, Is.EqualTo(0.1f).Within(0.0001f));
        }

        [Test]
        public void QuantizeEmitter_ClassifiesAliasTypesFromSemanticModel()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using Vec = UnityEngine.Vector3;

public class QuantizeAliasFixture : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Quantize = 0.5f)]
    public Vec value = new Vec(0.2f, 0.74f, -0.76f);
}";

            string transformed = EditorTestUtilities.RunOptimizer("UdonSharp.CE.Editor.Optimizers.QuantizePreSerializeEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_ApplyQuantizePreSerialization"));

            Assembly assembly = EditorTestUtilities.CompileAssembly(transformed, "QuantizeAliasEmitterTests");
            Type type = assembly.GetType("QuantizeAliasFixture");
            object instance = System.Activator.CreateInstance(type);

            type.GetMethod("OnPreSerialization").Invoke(instance, null);

            Vector3 value = (Vector3)type.GetField("value").GetValue(instance);
            Assert.That(value.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(value.y, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(value.z, Is.EqualTo(-1f).Within(0.0001f));
        }
    }
}
