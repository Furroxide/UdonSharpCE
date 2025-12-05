using System;
using UnityEngine;
using VRC.SDK3.Data;

namespace UdonSharp.CE.Data
{
    /// <summary>
    /// Converter functions for a registered data model type.
    /// </summary>
    public class DataModelConverter<T>
    {
        public Func<T, DataDictionary> ToData;
        public Func<DataDictionary, T> FromData;

        public DataModelConverter(Func<T, DataDictionary> toData, Func<DataDictionary, T> fromData)
        {
            ToData = toData;
            FromData = fromData;
        }
    }

    /// <summary>
    /// Central API for data model registration and conversion.
    ///
    /// In Phase 1, models must be manually registered using Register&lt;T&gt;().
    /// Future phases will support automatic registration via source generation.
    /// </summary>
    /// <example>
    /// <code>
    /// // Define your model
    /// [DataModel]
    /// public class PlayerData
    /// {
    ///     [DataField("name")] public string playerName;
    ///     [DataField("score")] public int score;
    /// }
    ///
    /// // Register in Start() or Awake()
    /// void Start()
    /// {
    ///     CEDataBridge.Register&lt;PlayerData&gt;(
    ///         toData: p => {
    ///             var d = new DataDictionary();
    ///             d["name"] = p.playerName;
    ///             d["score"] = p.score;
    ///             return d;
    ///         },
    ///         fromData: d => new PlayerData {
    ///             playerName = d["name"].String,
    ///             score = (int)d["score"].Double  // JSON numbers are doubles
    ///         }
    ///     );
    /// }
    ///
    /// // Usage
    /// PlayerData data = new PlayerData { playerName = "Alice", score = 100 };
    /// DataDictionary dict = CEDataBridge.ToData(data);
    /// string json = CEDataBridge.ToJson(data);
    /// PlayerData restored = CEDataBridge.FromJson&lt;PlayerData&gt;(json);
    /// </code>
    /// </example>
    public static class CEDataBridge
    {
        // Note: In Udon, we can't use a Dictionary<Type, object> for generic storage.
        // Instead, we use a simple approach where each type T stores its converter
        // in a static generic class. This works because UdonSharp compiles generics
        // as concrete types.

        #region Registration

        /// <summary>
        /// Registers converter functions for a data model type.
        /// Call this in Start() or Awake() before using ToData/FromData.
        /// </summary>
        /// <typeparam name="T">The data model type</typeparam>
        /// <param name="toData">Function to convert T to DataDictionary</param>
        /// <param name="fromData">Function to convert DataDictionary to T</param>
        public static void Register<T>(
            Func<T, DataDictionary> toData,
            Func<DataDictionary, T> fromData)
        {
            if (toData == null || fromData == null)
            {
                Debug.LogError($"[CE.Data] CEDataBridge.Register<{typeof(T).Name}>: converters cannot be null");
                return;
            }

            DataModelStorage<T>.Converter = new DataModelConverter<T>(toData, fromData);
            Debug.Log($"[CE.Data] Registered data model: {typeof(T).Name}");
        }

        /// <summary>
        /// Checks if a data model type has been registered.
        /// </summary>
        public static bool IsRegistered<T>()
        {
            return DataModelStorage<T>.Converter != null;
        }

        #endregion

        #region Conversion

        /// <summary>
        /// Converts a data model instance to a DataDictionary.
        /// The type must be registered via Register&lt;T&gt;() first.
        /// </summary>
        public static DataDictionary ToData<T>(T model)
        {
            var converter = DataModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Data] CEDataBridge.ToData<{typeof(T).Name}>: type not registered. Call Register<{typeof(T).Name}>() first.");
                return new DataDictionary();
            }

            if (model == null)
            {
                return new DataDictionary();
            }

            return converter.ToData(model);
        }

        /// <summary>
        /// Converts a DataDictionary to a data model instance.
        /// The type must be registered via Register&lt;T&gt;() first.
        /// </summary>
        public static T FromData<T>(DataDictionary data)
        {
            var converter = DataModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Data] CEDataBridge.FromData<{typeof(T).Name}>: type not registered. Call Register<{typeof(T).Name}>() first.");
                return default;
            }

            if (data == null)
            {
                return default;
            }

            return converter.FromData(data);
        }

        #endregion

        #region JSON Serialization

        /// <summary>
        /// Serializes a data model to a JSON string.
        /// The type must be registered via Register&lt;T&gt;() first.
        /// </summary>
        public static string ToJson<T>(T model)
        {
            DataDictionary data = ToData(model);
            if (VRCJson.TrySerializeToJson(data, JsonExportType.Minify, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError($"[CE.Data] CEDataBridge.ToJson<{typeof(T).Name}>: failed to serialize");
            return "{}";
        }

        /// <summary>
        /// Serializes a data model to a JSON string with optional beautification.
        /// </summary>
        public static string ToJson<T>(T model, bool beautify)
        {
            DataDictionary data = ToData(model);
            JsonExportType exportType = beautify ? JsonExportType.Beautify : JsonExportType.Minify;
            if (VRCJson.TrySerializeToJson(data, exportType, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError($"[CE.Data] CEDataBridge.ToJson<{typeof(T).Name}>: failed to serialize");
            return "{}";
        }

        /// <summary>
        /// Deserializes a JSON string to a data model.
        /// The type must be registered via Register&lt;T&gt;() first.
        /// </summary>
        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return default;
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataDictionary)
                {
                    return FromData<T>(token.DataDictionary);
                }
            }

            Debug.LogError($"[CE.Data] CEDataBridge.FromJson<{typeof(T).Name}>: failed to deserialize");
            return default;
        }

        #endregion

        #region List Helpers

        /// <summary>
        /// Converts a CEList of data models to a DataList.
        /// </summary>
        public static DataList ToDataList<T>(CEList<T> list)
        #if COMPILER_UDONSHARP
            where T : IComparable
        #endif
        {
            if (list == null)
            {
                return new DataList();
            }

            DataList result = new DataList();
            int count = list.Count;

            for (int i = 0; i < count; i++)
            {
                DataDictionary itemData = ToData(list[i]);
                result.Add(new DataToken(itemData));
            }

            return result;
        }

        /// <summary>
        /// Converts a DataList to a CEList of data models.
        /// </summary>
        public static CEList<T> FromDataList<T>(DataList dataList)
        #if COMPILER_UDONSHARP
            where T : IComparable
        #endif
        {
            if (dataList == null)
            {
                return new CEList<T>();
            }

            int count = dataList.Count;
            CEList<T> result = new CEList<T>(count);

            for (int i = 0; i < count; i++)
            {
                if (dataList.TryGetValue(i, out DataToken token))
                {
                    if (token.TokenType == TokenType.DataDictionary)
                    {
                        T item = FromData<T>(token.DataDictionary);
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Serializes a CEList of data models to a JSON array string.
        /// </summary>
        public static string ListToJson<T>(CEList<T> list)
        #if COMPILER_UDONSHARP
            where T : IComparable
        #endif
        {
            DataList dataList = ToDataList(list);
            if (VRCJson.TrySerializeToJson(dataList, JsonExportType.Minify, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError($"[CE.Data] CEDataBridge.ListToJson<{typeof(T).Name}>: failed to serialize");
            return "[]";
        }

        /// <summary>
        /// Deserializes a JSON array string to a CEList of data models.
        /// </summary>
        public static CEList<T> ListFromJson<T>(string json)
        #if COMPILER_UDONSHARP
            where T : IComparable
        #endif
        {
            if (string.IsNullOrEmpty(json))
            {
                return new CEList<T>();
            }

            if (VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                if (token.TokenType == TokenType.DataList)
                {
                    return FromDataList<T>(token.DataList);
                }
            }

            Debug.LogError($"[CE.Data] CEDataBridge.ListFromJson<{typeof(T).Name}>: failed to deserialize");
            return new CEList<T>();
        }

        #endregion
    }

    /// <summary>
    /// Generic static storage for data model converters.
    /// Each concrete type T gets its own storage slot.
    /// </summary>
    internal static class DataModelStorage<T>
    {
        public static DataModelConverter<T> Converter;
    }
}
