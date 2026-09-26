using UnityEngine;
using Verse;

namespace AIPawnControl
{
    /// <summary>Asks for the optional character note, then enables the mind.</summary>
    public class Dialog_EnableMind : Window
    {
        private readonly Pawn pawn;
        private string note = "";

        public override Vector2 InitialSize => new Vector2(500f, 320f);

        public Dialog_EnableMind(Pawn pawn)
        {
            this.pawn = pawn;
            forcePause = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 35f), "AIPawnControl_EnableTitle".Translate(pawn.LabelShort));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 50f), "AIPawnControl_EnableNoteLabel".Translate());
            note = Widgets.TextArea(new Rect(inRect.x, inRect.y + 90f, inRect.width, 140f), note);

            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 35f, inRect.width / 2f - 5f, 35f), "Cancel".Translate()))
                Close();
            if (Widgets.ButtonText(new Rect(inRect.x + inRect.width / 2f + 5f, inRect.yMax - 35f, inRect.width / 2f - 5f, 35f), "AIPawnControl_EnableConfirm".Translate()))
                Confirm();
        }

        public override void OnAcceptKeyPressed() => Confirm();

        private void Confirm()
        {
            MindManager.Instance?.Enable(pawn, note.Trim());
            Close();
        }
    }
}
