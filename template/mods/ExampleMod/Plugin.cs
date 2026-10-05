using BepInEx;
using UnityEngine;

namespace ExampleMod
{
    // publish.ps1 reads Guid, Name and Version from these three constants, so keep them in this exact form.
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.yourname.examplemod"; // unique forever: change "yourname" and the mod name
        public const string Name = "ExampleMod";
        public const string Version = "1.0.0";               // raise this every time you publish a change

        private void Awake()
        {
            Logger.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            // Example: press F9 in a world to see a message.
            if (Player.m_localPlayer != null && Input.GetKeyDown(KeyCode.F9))
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Hello from ExampleMod");
        }

        // If your mod patches the game with Harmony, undo it here so reloading in-game (F6) doesn't stack patches:
        // private void OnDestroy() => _harmony?.UnpatchSelf();
    }
}
