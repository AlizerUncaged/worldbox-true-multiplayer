using UnityEngine;
using WorldboxMP.Client.Net;
using WorldboxMP.Client.UI;

namespace WorldboxMP.Client
{
    /// <summary>
    /// Persistent MonoBehaviour that drives the network client each frame, retries
    /// the connection, lazily creates the multiplayer tab once the game UI exists,
    /// and toggles the manager window with a hotkey (F9).
    /// </summary>
    public class NetPump : MonoBehaviour
    {
        private float _reconnectTimer;
        private const float ReconnectInterval = 5f;

        public static void Spawn()
        {
            var go = new GameObject("WBMP_NetPump");
            DontDestroyOnLoad(go);
            go.AddComponent<NetPump>();
        }

        private void Update()
        {
            var net = NetClient.Instance;
            if (net == null) return;

            net.Poll();
            Mirror.MirrorManager.Instance?.Pump(Time.unscaledDeltaTime);

            // Auto-reconnect.
            if (net.State == ConnState.Disconnected)
            {
                _reconnectTimer += Time.unscaledDeltaTime;
                if (_reconnectTimer >= ReconnectInterval)
                {
                    _reconnectTimer = 0f;
                    net.Connect();
                }
            }
            else _reconnectTimer = 0f;

            // Lazily create the tab when the powers UI is available.
            MultiplayerTab.TryCreate();

            if (Input.GetKeyDown(KeyCode.F9))
                MultiplayerWindow.Instance?.Toggle();
        }

        private void OnApplicationQuit() => NetClient.Instance?.Stop();
    }
}
