using System.Linq;
using RimCoopMod.GameComponents;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>Elegir cuál de mis misiones en curso compartir con otro jugador.</summary>
    public class Dialog_ShareQuest : Window
    {
        private readonly int _playerId;
        private readonly string _playerName;
        private Vector2 _scroll;

        public override Vector2 InitialSize => new Vector2(560f, 460f);

        public Dialog_ShareQuest(int playerId, string playerName)
        {
            _playerId = playerId;
            _playerName = playerName;
            doCloseX = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), $"Compartir una misión con {_playerName}");
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 36f, inRect.width, 60f),
                $"Cada jugador que se suma sube la dificultad de la misión un {CoopSessionManager.QuestDifficultyPerPlayer:P0}. " +
                "Ellos ven la misma misión en su pestaña de misiones y ayudan con sus colonos en tu base.");

            var quests = CoopSessionManager.ShareableQuests();
            var outRect = new Rect(0f, 100f, inRect.width, inRect.height - 100f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, System.Math.Max(quests.Count, 1) * 44f);

            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            if (quests.Count == 0) Widgets.Label(new Rect(0f, 0f, viewRect.width, 30f), "No tenés misiones en curso para compartir.");
            float y = 0f;
            foreach (var q in quests)
            {
                Widgets.Label(new Rect(0f, y + 6f, viewRect.width - 120f, 30f), q.name);
                if (Widgets.ButtonText(new Rect(viewRect.width - 110f, y, 110f, 34f), "Compartir"))
                {
                    CoopSessionManager.ShareQuestWith(_playerId, q);
                    Close();
                }
                y += 44f;
            }
            Widgets.EndScrollView();
        }
    }

    /// <summary>Recibiste la invitación a una misión de otro jugador.</summary>
    public class Dialog_QuestInvite : Window
    {
        private readonly global::RimCoopMod.Networking.QuestMessagePayload _invite;
        private Vector2 _scroll;

        public int QuestId => _invite.QuestId;
        public string FromName => _invite.FromPlayerName;

        public override Vector2 InitialSize => new Vector2(560f, 480f);

        public Dialog_QuestInvite(global::RimCoopMod.Networking.QuestMessagePayload invite)
        {
            _invite = invite;
            doCloseX = false;
            closeOnClickedOutside = false;
            closeOnCancel = false; // Esc no la descarta en silencio: queda pendiente hasta que se responda
            absorbInputAroundWindow = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), $"{_invite.FromPlayerName} te invita a una misión");
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 36f, inRect.width, 24f), _invite.Name);

            float mult = CoopSessionManager.QuestMultiplier(_invite.Participants + 1);
            Widgets.Label(new Rect(0f, 62f, inRect.width, 48f),
                $"Si te sumás, su dificultad pasa a ×{mult:0.##} (+{CoopSessionManager.QuestDifficultyPerPlayer:P0} por jugador). " +
                "La vas a ver en tu pestaña de misiones, y para cumplirla ayudás con tus colonos en su base.");

            var outRect = new Rect(0f, 116f, inRect.width, inRect.height - 170f);
            float h = Text.CalcHeight(_invite.Description, outRect.width - 20f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, h);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, h), _invite.Description);
            Widgets.EndScrollView();

            float third = inRect.width / 3f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, third - 6f, 36f), "Sumarme"))
            {
                if (CoopSessionManager.AcceptQuestInvite(_invite))
                {
                    CoopSessionManager.ResolveQuestInvite(_invite);
                    Close();
                }
            }
            if (Widgets.ButtonText(new Rect(third + 3f, inRect.height - 40f, third - 6f, 36f), "Decidir después"))
            {
                Close(); // sigue pendiente: se vuelve a mostrar (también después de guardar y cargar) cuando el otro esté conectado
            }
            if (Widgets.ButtonText(new Rect(third * 2f + 6f, inRect.height - 40f, third - 6f, 36f), "Rechazar"))
            {
                global::RimCoopMod.Networking.CoopClient.Instance.SendQuestMessage(_invite.FromPlayerId, "decline", _invite.QuestId, _invite.Name, "", 0, 0, 0);
                CoopSessionManager.ResolveQuestInvite(_invite);
                Close();
            }
        }
    }
}
