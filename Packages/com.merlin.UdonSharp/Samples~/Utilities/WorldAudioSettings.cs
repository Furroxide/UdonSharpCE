
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Sets world audio settings for voice and avatar audio on Start
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/World Audio Settings")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class WorldAudioSettings : UdonSharpBehaviour
    {
        [Header("Player voice")]
        [Tooltip("Adjusts the player volume")]
        [Range(0, 24)]
        public float voiceGain = 15f;
        
        [Tooltip("The end of the range for hearing a user's voice")]
        public float voiceFar = 25f;
        
        [Tooltip("The near radius in meters where player audio starts to fall off, it is recommended to keep this at 0")]
        public float voiceNear = 0f;
        
        [Tooltip("The volumetric radius for the player voice, this should be left at 0 unless you know what you're doing")]
        public float voiceVolumetricRadius = 0f;
        
        [Tooltip("Disables the low-pass filter when players are far away")]
        public bool voiceDisableLowpass = false;

        [Header("Avatar audio")]
        [Tooltip("The maximum gain allowed on avatar audio sources")]
        [Range(0, 10)]
        public float avatarMaxAudioGain = 10f;
        
        [Tooltip("The maximum end of avatar audio range, a value of 0 will effectively mute avatar audio")]
        public float avatarMaxFarRadius = 40f;
        
        [Tooltip("The maximum for the radius where avatar audio starts to fall off")]
        public float avatarMaxNearRadius = 40f;
        
        [Tooltip("The max volumetric radius for avatar audio sources")]
        public float avatarMaxVolumetricRadius = 40f;
        
        [Tooltip("Forces avatars to have spatialized audio")]
        public bool avatarForceSpacialization = false;
        
        [Tooltip("Disables custom curves on avatar audio sources")]
        public bool avatarDisableCustomCurve = false;

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!player.isLocal) 
                ApplyAudioSettings(player);
        }

        void Start()
        {
            VRCPlayerApi[] players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
            VRCPlayerApi.GetPlayers(players);

            foreach (VRCPlayerApi player in players)
            {
                if (player != null && player.IsValid() && !player.isLocal)
                    ApplyAudioSettings(player);
            }
        }

        void ApplyAudioSettings(VRCPlayerApi player)
        {
            player.SetVoiceGain(voiceGain);
            player.SetVoiceDistanceFar(voiceFar);
            player.SetVoiceDistanceNear(voiceNear);
            player.SetVoiceVolumetricRadius(voiceVolumetricRadius);
            player.SetVoiceLowpass(!voiceDisableLowpass);
            
            player.SetAvatarAudioGain(avatarMaxAudioGain);
            player.SetAvatarAudioFarRadius(avatarMaxFarRadius);
            player.SetAvatarAudioNearRadius(avatarMaxNearRadius);
            player.SetAvatarAudioVolumetricRadius(avatarMaxVolumetricRadius);
            player.SetAvatarAudioForceSpatial(avatarForceSpacialization);
            player.SetAvatarAudioCustomCurve(!avatarDisableCustomCurve);
        }
    }
}


























