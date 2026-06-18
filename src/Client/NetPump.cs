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
        private float _pingTimer;
        private const float PingInterval = 1f;

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

            // Round-trip ping probe (~1/s) — measures originator→king→originator latency.
            _pingTimer += Time.unscaledDeltaTime;
            if (_pingTimer >= PingInterval) { _pingTimer = 0f; net.SendPing(); }

            // Lazily create the tab when the powers UI is available.
            MultiplayerTab.TryCreate();

            if (Input.GetKeyDown(KeyCode.F9))
                MultiplayerWindow.Toggle();

#if DEBUG
            // Camera/zoom diagnostics: log the exact zoom+LOD state whenever the wheel
            // is scrolled (so we can see if scroll input even reaches this client, and
            // whether _target_zoom moves) and on demand via F10.
            if (Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f)
                Dev.CameraDiag.Dump($"scroll dy={Input.mouseScrollDelta.y:F2}");
            if (Input.GetKeyDown(KeyCode.F10))
                Dev.CameraDiag.Dump("F10");
            Dev.MoveDiag.Tick(Time.unscaledDeltaTime);
            Dev.CityDiag.Tick(Time.unscaledDeltaTime);
#endif
        }

        private void OnApplicationQuit() => NetClient.Instance?.Stop();
    }
}
