using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Core
{
    /// <summary>
    /// Central coordinator for all CE demo stations.
    /// Manages station activation, global state, and provides shared utilities.
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
        
        [Header("Performance Monitor")]
        [SerializeField] private PerformanceMonitor _performanceMonitor;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _welcomeText;
        
        // Station states
        private bool _station1Active;
        private bool _station2Active;
        private bool _station3Active;
        private bool _station4Active;
        private bool _station5Active;
        
        void Start()
        {
            CELogger.Info("DemoManager", "CE Showcase Demo World initialized");
            
            // Initialize all stations as inactive
            SetAllStationsActive(false);
            
            // Display welcome message
            if (_welcomeText != null)
            {
                _welcomeText.text = "Welcome to the CE Laboratory!\n\n" +
                    "Visit each station to see UdonSharpCE in action:\n\n" +
                    "Station 1: Bullet Storm (ECS + Pooling)\n" +
                    "Station 2: Crowd Simulation (Flocking)\n" +
                    "Station 3: Leaderboard (Collections)\n" +
                    "Station 4: Cutscene Theater (Async)\n" +
                    "Station 5: Persistent Inventory";
            }
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
            
            if (_station1_BulletHell) _station1_BulletHell.SetActive(active);
            if (_station2_Flocking) _station2_Flocking.SetActive(active);
            if (_station3_Leaderboard) _station3_Leaderboard.SetActive(active);
            if (_station4_Cutscene) _station4_Cutscene.SetActive(active);
            if (_station5_Persistence) _station5_Persistence.SetActive(active);
        }
        
        /// <summary>
        /// Get the performance monitor reference
        /// </summary>
        public PerformanceMonitor GetPerformanceMonitor()
        {
            return _performanceMonitor;
        }
        
        // UI Button callbacks
        public void OnStation1Button() => ToggleStation(1);
        public void OnStation2Button() => ToggleStation(2);
        public void OnStation3Button() => ToggleStation(3);
        public void OnStation4Button() => ToggleStation(4);
        public void OnStation5Button() => ToggleStation(5);
        
        public void OnActivateAllButton() => SetAllStationsActive(true);
        public void OnDeactivateAllButton() => SetAllStationsActive(false);
    }
}
