using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Data
{
    /// <summary>
    /// Marks a class as a data model that can be serialized to/from VRChat DataDictionary.
    ///
    /// In Phase 1, this attribute serves as documentation and must be paired with manual
    /// registration via CEDataBridge.Register&lt;T&gt;().
    ///
    /// Future phases will support automatic code generation for conversion methods.
    /// </summary>
    /// <example>
    /// <code>
    /// [DataModel]
    /// public class InventoryItem
    /// {
    ///     [DataField("id")] public int itemId;
    ///     [DataField("qty")] public int quantity;
    ///     [DataField("name")] public string itemName;
    /// }
    ///
    /// // Manual registration (Phase 1):
    /// CEDataBridge.Register&lt;InventoryItem&gt;(
    ///     toData: item =&gt; {
    ///         var d = new DataDictionary();
    ///         d["id"] = item.itemId;
    ///         d["qty"] = item.quantity;
    ///         d["name"] = item.itemName;
    ///         return d;
    ///     },
    ///     fromData: d =&gt; new InventoryItem {
    ///         itemId = d["id"].Int,
    ///         quantity = d["qty"].Int,
    ///         itemName = d["name"].String
    ///     }
    /// );
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class DataModelAttribute : Attribute
    {
        /// <summary>
        /// Optional name override for the model in serialization.
        /// If not specified, the class name is used.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Schema version for migration support.
        /// Increment when making breaking changes to field layout.
        /// </summary>
        public int Version { get; set; } = 1;

        public DataModelAttribute() { }

        public DataModelAttribute(string name)
        {
            Name = name;
        }
    }
}
