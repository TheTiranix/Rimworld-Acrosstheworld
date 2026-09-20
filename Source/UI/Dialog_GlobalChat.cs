using RimCoopMod.GameComponents;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>
    /// Ventana de chat global, no modal: se puede dejar abierta y seguir jugando
    /// (cámara y pausa no se ven afectadas). Se abre/cierra con RimCoop_ToggleChat (F7 por defecto).
    /// </summary>
    public class Dialog_GlobalChat : Window
    {
        private static Dialog_GlobalChat _openInstance;
        public static bool IsChatOpen => _openInstance != null;

        private Vector2 _scrollPos;
        private string _input = "";
        private const string ControlName = "RimCoop_ChatInput";
        private bool _focusPending;

        public override Vector2 InitialSize => new Vector2(420f, 320f);

        protected override float Margin => 8f;

        public Dialog_GlobalChat()
        {
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            preventCameraMotion = false;
            draggable = true;
            layer = WindowLayer.GameUI;
        }

        public static void Toggle()
        {
            if (IsChatOpen)
            {
                _openInstance.Close();
                return;
            }

            var dialog = new Dialog_GlobalChat();
            Find.WindowStack.Add(dialog);
        }

        public override void PreOpen()
        {
            base.PreOpen();
            _openInstance = this;
            _focusPending = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (_openInstance == this) _openInstance = null;
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            windowRect.x = 15f;
            windowRect.y = Verse.UI.screenHeight - InitialSize.y - 60f;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;

            var logRect = new Rect(0f, 0f, inRect.width, inRect.height - 35f);
            DrawChatLog(logRect);

            var saveRect = new Rect(inRect.width - 128f, 2f, 124f, 22f);
            if (Widgets.ButtonText(saveRect, "Guardar todos"))
                CoopSessionManager.RequestSaveAll();
            TooltipHandler.TipRegion(saveRect, "Todos los jugadores guardan su partida ahora mismo (mantiene parejos los colonos que se mandaron entre sí).");

            var inputRect = new Rect(0f, inRect.height - 28f, inRect.width - 65f, 28f);
            var sendRect = new Rect(inRect.width - 60f, inRect.height - 28f, 60f, 28f);

            GUI.SetNextControlName(ControlName);
            _input = Widgets.TextField(inputRect, _input);

            bool pressedEnter = Event.current.type == EventType.KeyDown &&
                                 (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                                 GUI.GetNameOfFocusedControl() == ControlName;

            if (Widgets.ButtonText(sendRect, "Enviar") || pressedEnter)
            {
                TrySend();
                if (pressedEnter) Event.current.Use();
            }

            if (_focusPending)
            {
                GUI.FocusControl(ControlName);
                _focusPending = false;
            }
        }

        private void DrawChatLog(Rect rect)
        {
            Widgets.DrawMenuSection(rect);

            var log = CoopSessionManager.ChatLog;
            float lineHeight = Text.LineHeight;
            float viewHeight = System.Math.Max(rect.height, log.Count * lineHeight + 10f);
            var viewRect = new Rect(0f, 0f, rect.width - 16f, viewHeight);

            Widgets.BeginScrollView(rect, ref _scrollPos, viewRect);

            float y = 0f;
            foreach (var line in log)
            {
                var lineRect = new Rect(4f, y, viewRect.width - 4f, lineHeight);
                Widgets.Label(lineRect, line);
                y += lineHeight;
            }

            Widgets.EndScrollView();
        }

        private void TrySend()
        {
            if (string.IsNullOrWhiteSpace(_input)) return;

            CoopSessionManager.SendChatMessage(_input);
            _input = "";
            _scrollPos.y = float.MaxValue;
            _focusPending = true;
        }
    }
}
