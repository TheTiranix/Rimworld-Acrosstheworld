using System.Collections.Generic;
using System.Linq;
using RimCoopMod.GameComponents;
using RimCoopMod.Networking;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>Armar una oferta de comercio: qué le das al otro jugador y qué le pedís de su stock.</summary>
    public class Dialog_TradeOffer : Window
    {
        public static Dialog_TradeOffer Current;

        private readonly int _partnerId;
        private readonly string _partnerName;
        private readonly Dictionary<ThingDef, int> _myStock;
        private Dictionary<ThingDef, int> _partnerStock;
        private readonly Dictionary<ThingDef, int> _give = new Dictionary<ThingDef, int>();
        private readonly Dictionary<ThingDef, int> _want = new Dictionary<ThingDef, int>();
        private readonly Dictionary<string, string> _buffers = new Dictionary<string, string>();
        private Vector2 _scrollMine, _scrollTheirs;

        public override Vector2 InitialSize => new Vector2(760f, 560f);

        public Dialog_TradeOffer(int partnerId, string partnerName)
        {
            _partnerId = partnerId;
            _partnerName = partnerName;
            _myStock = CoopSessionManager.AggregateStock(Find.AnyPlayerHomeMap);
            doCloseX = true;
            absorbInputAroundWindow = false;
            Current = this;
            CoopClient.Instance.SendTradeMessage(partnerId, "stockreq", "");
        }

        public override void PostClose()
        {
            base.PostClose();
            if (Current == this) Current = null;
        }

        public void SetPartnerStock(int fromPlayerId, string csv)
        {
            if (fromPlayerId != _partnerId) return;
            _partnerStock = new Dictionary<ThingDef, int>();
            foreach (var kv in CoopSessionManager.ParseSimpleItems(csv))
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key);
                if (def != null) _partnerStock[def] = kv.Value;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), $"Comerciar con {_partnerName}");
            Text.Font = GameFont.Small;

            float colW = (inRect.width - 20f) / 2f;
            float top = 44f;
            float listH = inRect.height - top - 60f;

            DrawColumn(new Rect(0f, top, colW, listH), "Vos das", _myStock, _give, ref _scrollMine, "g");
            if (_partnerStock == null)
                Widgets.Label(new Rect(colW + 20f, top, colW, 30f), "Pidiendo el stock de " + _partnerName + "…");
            else
                DrawColumn(new Rect(colW + 20f, top, colW, listH), "Vos pedís", _partnerStock, _want, ref _scrollTheirs, "w");

            bool anything = _give.Values.Any(v => v > 0) || _want.Values.Any(v => v > 0);
            if (Widgets.ButtonText(new Rect(inRect.width / 2f - 100f, inRect.height - 44f, 200f, 36f), "Enviar oferta") && anything)
            {
                CoopSessionManager.SendOffer(_partnerId, CoopSessionManager.SimpleItemsToCsv(_give), CoopSessionManager.SimpleItemsToCsv(_want));
                Messages.Message("Oferta enviada a " + _partnerName + ".", RimWorld.MessageTypeDefOf.NeutralEvent, false);
                Close();
            }
        }

        private void DrawColumn(Rect rect, string title, Dictionary<ThingDef, int> stock, Dictionary<ThingDef, int> chosen, ref Vector2 scroll, string tag)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), title);
            var outRect = new Rect(rect.x, rect.y + 26f, rect.width, rect.height - 26f);
            var items = stock.OrderBy(kv => kv.Key.label).ToList();
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, items.Count * 30f);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;
            foreach (var kv in items)
            {
                var row = new Rect(0f, y, viewRect.width, 28f);
                Widgets.Label(new Rect(row.x, row.y, row.width - 130f, 28f), $"{kv.Key.LabelCap} ({kv.Value})");

                string key = tag + kv.Key.defName;
                if (!_buffers.TryGetValue(key, out var buf)) buf = "0";
                chosen.TryGetValue(kv.Key, out int amount);
                Widgets.TextFieldNumeric(new Rect(row.xMax - 120f, row.y, 100f, 26f), ref amount, ref buf, 0, kv.Value);
                _buffers[key] = buf;
                chosen[kv.Key] = amount;
                y += 30f;
            }
            Widgets.EndScrollView();
        }
    }

    /// <summary>Una oferta de otro jugador: se ve qué da y qué pide, y se acepta o se rechaza.</summary>
    public class Dialog_TradeIncoming : Window
    {
        private readonly int _fromId;
        private readonly string _fromName;
        private readonly int _offerId;
        private readonly string _giveCsv;
        private readonly string _wantCsv;

        public override Vector2 InitialSize => new Vector2(460f, 400f);

        public Dialog_TradeIncoming(int fromId, string fromName, int offerId, string giveCsv, string wantCsv)
        {
            _fromId = fromId;
            _fromName = fromName;
            _offerId = offerId;
            _giveCsv = giveCsv;
            _wantCsv = wantCsv;
            doCloseX = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
        }

        private static string Describe(string csv)
        {
            var items = CoopSessionManager.ParseSimpleItems(csv);
            if (items.Count == 0) return "  (nada)";
            return string.Join("\n", items.Select(kv => "  • " + (DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key)?.LabelCap.ToString() ?? kv.Key) + " x" + kv.Value));
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), $"{_fromName} te ofrece un trato");
            Text.Font = GameFont.Small;

            Widgets.Label(new Rect(0f, 44f, inRect.width, inRect.height - 110f),
                "Te da:\n" + Describe(_giveCsv) + "\n\nA cambio te pide:\n" + Describe(_wantCsv));

            if (Widgets.ButtonText(new Rect(0f, inRect.height - 40f, inRect.width / 2f - 6f, 36f), "Aceptar"))
            {
                CoopSessionManager.AcceptOffer(_fromId, _fromName, _offerId, _wantCsv);
                Close();
            }
            if (Widgets.ButtonText(new Rect(inRect.width / 2f + 6f, inRect.height - 40f, inRect.width / 2f - 6f, 36f), "Rechazar"))
            {
                CoopClient.Instance.SendTradeMessage(_fromId, "reject", "", _offerId);
                Close();
            }
        }
    }

    /// <summary>Elegir con qué fuerza atacar la base de otro jugador.</summary>
    public class Dialog_AttackStrength : Window
    {
        private readonly int _targetId;
        private readonly string _targetName;
        private float _points = 400f;

        public override Vector2 InitialSize => new Vector2(420f, 240f);

        public Dialog_AttackStrength(int targetId, string targetName)
        {
            _targetId = targetId;
            _targetName = targetName;
            doCloseX = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), $"Atacar a {_targetName}");
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 44f, inRect.width, 50f), $"Se le manda una incursión enemiga real de {(int)_points} puntos de fuerza.");
            _points = Widgets.HorizontalSlider(new Rect(0f, 100f, inRect.width, 24f), _points, 100f, 3000f, false, null, "100", "3000");

            if (Widgets.ButtonText(new Rect(inRect.width / 2f - 90f, inRect.height - 44f, 180f, 36f), "Atacar"))
            {
                CoopClient.Instance.SendAttackRequest(_targetId, (int)_points);
                Messages.Message($"Incursión enviada contra {_targetName}.", RimWorld.MessageTypeDefOf.NeutralEvent, false);
                Close();
            }
        }
    }
}
