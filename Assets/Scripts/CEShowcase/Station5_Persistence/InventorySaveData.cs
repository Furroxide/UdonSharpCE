using UdonSharp.CE.Persistence;
using VRC.SDK3.Data;

namespace CEShowcase.Station5_Persistence
{
    [PlayerData("ce_demo_inventory", Version = 2)]
    public class InventorySaveData
    {
        public DataDictionary data;
    }
}
