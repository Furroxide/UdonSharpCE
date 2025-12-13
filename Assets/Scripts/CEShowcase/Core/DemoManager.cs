using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Core
{
    /// <summary>
    /// Central coordinator for all CE demo stations.
    /// Manages station activation, global state, and provides shared utilities.
    /// 
    /// The CE Laboratory showcases the full power of UdonSharpCE:
    /// - Station 1: Bullet Storm (ECS + Pooling via CEWorld/CEPool)
    /// - Station 2: Crowd Simulation (Spatial partitioning via CEGrid)
    /// - Station 3: Leaderboard (Collections via CEDictionary)
    /// - Station 4: Cutscene Theater (Async via UdonTask)
    /// - Station 5: Persistent Inventory (Persistence via CEPersistence)
    /// - Station 6: Procgen Lab (Generation via CERandom/CENoise)
    /// - Station 7: Network Hub (Multiplayer via CE.Net)
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DemoManager : UdonSharpBehaviour
    {
        [Header("Station References")]
        [SerializeField] private GameObject _station1_BulletHell;
        [SerializeField] private GameObject _station2_Flocking;
        [SerializeField] private GameObject _station3_Leaderboard;
        [SerializeField] private GameObject _station4_Cutscene;
        [SerializeField] private GameObject _station5_Persistence;
        [SerializeField] private GameObject _station6_Procgen;
        [SerializeField] private GameObject _station7_Networking;
        
        [Header("Performance Monitor")]
        [SerializeField] private PerformanceMonitor _performanceMonitor;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _welcomeText;
        [SerializeField] private TMPro.TextMeshProUGUI _featureListText;
        
        // Station states
        private bool _station1Active;
        private bool _station2Active;
        private bool _station3Active;
        private bool _station4Active;
        private bool _station5Active;
        private bool _station6Active;
        private bool _station7Active;
        
        void Start()
        {
            CELogger.Info("DemoManager", "CE Showcase Demo World initialized - 7 stations available");
            
            // Initialize all stations as inactive
            SetAllStationsActive(false);
            
            // Display welcome message
            UpdateWelcomeText();
            UpdateFeatureList();
        }
        
        private void UpdateWelcomeText()
        {
            if (_welcomeText == null) return;
            
            _welcomeText.text = 
                "<b>Welcome to the CE Laboratory!</b>\n\n" +
                "Experience the full power of UdonSharpCE:\n" +
                "Pure C# development for VRChat worlds.\n\n" +
                "<size=80%>Visit each station to see CE features in action.</size>";
        }
        
        private void UpdateFeatureList()
        {
            if (_featureListText == null) return;
            
            _featureListText.text = 
                "<b>STATIONS</b>\n\n" +
                "<color=#FF6600>1. Bullet Storm</color> - ECS + Pooling\n" +
                "   <size=70%>CEWorld, CEPool - 2000+ entities</size>\n\n" +
                "<color=#00FF00>2. Crowd Sim</color> - Spatial Partitioning\n" +
                "   <size=70%>CEGrid - 500+ flocking agents</size>\n\n" +
                "<color=#00FFFF>3. Leaderboard</color> - Collections\n" +
                "   <size=70%>CEDictionary - O(1) lookups</size>\n\n" +
                "<color=#FF00FF>4. Theater</color> - Async/Await\n" +
                "   <size=70%>UdonTask - Clean sequential code</size>\n\n" +
                "<color=#FFFF00>5. Inventory</color> - Persistence\n" +
                "   <size=70%>CEPersistence - Save/Load data</size>\n\n" +
                "<color=#FF8800>6. Procgen Lab</color> - Generation\n" +
                "   <size=70%>CERandom, CENoise - Deterministic</size>\n\n" +
                "<color=#8888FF>7. Network Hub</color> - Multiplayer\n" +
                "   <size=70%>[Sync], [Rpc], RateLimiter</size>";
        }
        
        /// <summary>
        /// Toggle a specific station on/off
        /// </summary>
        public void ToggleStation(int stationIndex)
        {
            switch (stationIndex)
            {
                case 1:
                    _station1Active = !_station1Active;
                    if (_station1_BulletHell) _station1_BulletHell.SetActive(_station1Active);
                    CELogger.Info("DemoManager", "Station 1 (Bullet Hell): " + (_station1Active ? "ON" : "OFF"));
                    break;
                case 2:
                    _station2Active = !_station2Active;
                    if (_station2_Flocking) _station2_Flocking.SetActive(_station2Active);
                    CELogger.Info("DemoManager", "Station 2 (Flocking): " + (_station2Active ? "ON" : "OFF"));
                    break;
                case 3:
                    _station3Active = !_station3Active;
                    if (_station3_Leaderboard) _station3_Leaderboard.SetActive(_station3Active);
                    CELogger.Info("DemoManager", "Station 3 (Leaderboard): " + (_station3Active ? "ON" : "OFF"));
                    break;
                case 4:
                    _station4Active = !_station4Active;
                    if (_station4_Cutscene) _station4_Cutscene.SetActive(_station4Active);
                    CELogger.Info("DemoManager", "Station 4 (Cutscene): " + (_station4Active ? "ON" : "OFF"));
                    break;
                case 5:
                    _station5Active = !_station5Active;
                    if (_station5_Persistence) _station5_Persistence.SetActive(_station5Active);
                    CELogger.Info("DemoManager", "Station 5 (Persistence): " + (_station5Active ? "ON" : "OFF"));
                    break;
                case 6:
                    _station6Active = !_station6Active;
                    if (_station6_Procgen) _station6_Procgen.SetActive(_station6Active);
                    CELogger.Info("DemoManager", "Station 6 (Procgen): " + (_station6Active ? "ON" : "OFF"));
                    break;
                case 7:
                    _station7Active = !_station7Active;
                    if (_station7_Networking) _station7_Networking.SetActive(_station7Active);
                    CELogger.Info("DemoManager", "Station 7 (Networking): " + (_station7Active ? "ON" : "OFF"));
                    break;
            }
        }
        
        /// <summary>
        /// Set all stations active or inactive
        /// </summary>
        public void SetAllStationsActive(bool active)
        {
            _station1Active = active;
            _station2Active = active;
            _station3Active = active;
            _station4Active = active;
            _station5Active = active;
            _station6Active = active;
            _station7Active = active;
            
            if (_station1_BulletHell) _station1_BulletHell.SetActive(active);
            if (_station2_Flocking) _station2_Flocking.SetActive(active);
            if (_station3_Leaderboard) _station3_Leaderboard.SetActive(active);
            if (_station4_Cutscene) _station4_Cutscene.SetActive(active);
            if (_station5_Persistence) _station5_Persistence.SetActive(active);
            if (_station6_Procgen) _station6_Procgen.SetActive(active);
            if (_station7_Networking) _station7_Networking.SetActive(active);
        }
        
        /// <summary>
        /// Get the performance monitor reference
        /// </summary>
        public PerformanceMonitor GetPerformanceMonitor()
        {
            return _performanceMonitor;
        }
        
        // ========================================
        // UI BUTTON CALLBACKS
        // ========================================
        
        public void OnStation1Button() => ToggleStation(1);
        public void OnStation2Button() => ToggleStation(2);
        public void OnStation3Button() => ToggleStation(3);
        public void OnStation4Button() => ToggleStation(4);
        public void OnStation5Button() => ToggleStation(5);
        public void OnStation6Button() => ToggleStation(6);
        public void OnStation7Button() => ToggleStation(7);
        
        public void OnActivateAllButton() => SetAllStationsActive(true);
        public void OnDeactivateAllButton() => SetAllStationsActive(false);
        
        // ========================================
        // STATION STATUS
        // ========================================
        
        public bool IsStationActive(int stationIndex)
        {
            switch (stationIndex)
            {
                case 1: return _station1Active;
                case 2: return _station2Active;
                case 3: return _station3Active;
                case 4: return _station4Active;
                case 5: return _station5Active;
                case 6: return _station6Active;
                case 7: return _station7Active;
                default: return false;
            }
        }
        
        public int GetActiveStationCount()
        {
            int count = 0;
            if (_station1Active) count++;
            if (_station2Active) count++;
            if (_station3Active) count++;
            if (_station4Active) count++;
            if (_station5Active) count++;
            if (_station6Active) count++;
            if (_station7Active) count++;
            return count;
        }
    }
}
