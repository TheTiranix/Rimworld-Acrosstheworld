using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimCoopMod
{
    /// <summary>
    /// Corre todo el tiempo (también antes de que exista una partida en curso). Hace dos cosas:
    /// 1) mientras no hay partida jugándose (elegir sitio de la colonia nueva), procesa los avisos
    ///    de red que hacen falta para ver las bases de los demás;
    /// 2) cada vez que se abre el mapa mundial (botón "Mundo") o la pantalla de elegir sitio,
    ///    le pide al servidor dónde están las colonias de todos, en vez de esperar a que alguien
    ///    "haga algo" para que se actualicen.
    /// </summary>
    public class CoopUpdater : MonoBehaviour
    {
        private const float RefreshWhileVisibleSeconds = 8f;
        private float _nextRequest;
        private bool _wasVisible;

        public static void Install()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                var go = new GameObject("RimCoopUpdater");
                go.AddComponent<CoopUpdater>();
                DontDestroyOnLoad(go);
            });
        }

        private void Update()
        {
            if (!CoopClient.Instance.IsConnected)
            {
                _wasVisible = false;
                return;
            }

            if (Current.ProgramState != ProgramState.Playing)
            {
                CoopSessionManager.ProcessPacketsOutsidePlay();
            }

            bool visible = IsWorldVisible();
            float now = Time.realtimeSinceStartup;
            if (visible && (!_wasVisible || now >= _nextRequest))
            {
                CoopClient.Instance.SendPlayersRequest();
                _nextRequest = now + RefreshWhileVisibleSeconds;
            }
            _wasVisible = visible;
        }

        private static bool IsWorldVisible()
        {
            try
            {
                if (Current.ProgramState == ProgramState.Playing) return WorldRendererUtility.WorldRendered;
                return Find.WindowStack != null && Find.WindowStack.WindowOfType<Page_SelectStartingSite>() != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
