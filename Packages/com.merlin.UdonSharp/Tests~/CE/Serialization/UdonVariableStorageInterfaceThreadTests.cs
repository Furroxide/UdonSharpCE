using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UdonSharp.Compiler;
using UdonSharp.Serialization;
using UnityEngine;
using VRC.Udon;

namespace UdonSharp.CE.Tests.Serialization
{
    public class UdonVariableStorageInterfaceThreadTests
    {
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();
        }

        [Test]
        public void ClearCache_And_GetElementStorage_AreThreadSafe()
        {
            GameObject go = new GameObject("UdonVariableStorageThreadTest");
            _createdObjects.Add(go);

            UdonBehaviour behaviour = go.AddComponent<UdonBehaviour>();
            var programAsset = ScriptableObject.CreateInstance<UdonSharp.UdonSharpProgramAsset>();
            _createdObjects.Add(programAsset);

            programAsset.fieldDefinitions = new Dictionary<string, FieldDefinition>
            {
                {
                    "value",
                    new FieldDefinition("value", typeof(int), typeof(int), null, false, new List<Attribute>())
                }
            };
            behaviour.programSource = programAsset;

            MethodInfo clearCache = typeof(UdonVariableStorageInterface).GetMethod("ClearCache", BindingFlags.Static | BindingFlags.NonPublic);
            ExceptionDispatchInfo capturedException = null;

            Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    if ((i & 7) == 0)
                    {
                        clearCache.Invoke(null, null);
                    }

                    var storage = new UdonVariableStorageInterface(behaviour);
                    if (storage.GetElementStorage("value") == null)
                    {
                        throw new InvalidOperationException("Expected storage for 'value'.");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref capturedException, ExceptionDispatchInfo.Capture(ex), null);
                }
            });

            if (capturedException != null)
                capturedException.Throw();
        }
    }
}
