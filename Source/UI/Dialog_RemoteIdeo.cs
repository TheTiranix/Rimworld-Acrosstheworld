using RimCoopMod.GameComponents;
using UnityEngine;
using Verse;

namespace RimCoopMod.UI
{
    /// <summary>La ideología (memes, preceptos y roles) de la colonia de otro jugador, ver CoopSessionManager.Ideology.cs.</summary>
    public class Dialog_RemoteIdeo : Window
    {
        private readonly string _title;
        private readonly string _body;
        private Vector2 _scroll;

        public override Vector2 InitialSize => new Vector2(540f, 580f);

        public Dialog_RemoteIdeo(string playerName, string summary)
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            _title = Loc.T("Ideo.Title", playerName);
            _body = CoopSessionManager.FormatIdeoSummary(summary);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width - 30f, 34f), _title);
            Text.Font = GameFont.Small;

            var outRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f);
            float height = Text.CalcHeight(_body, outRect.width - 20f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, height + 8f);
            Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, height), _body);
            Widgets.EndScrollView();
        }
    }
}
