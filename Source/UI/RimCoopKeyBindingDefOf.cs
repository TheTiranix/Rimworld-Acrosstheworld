using RimWorld;
using Verse;

namespace RimCoopMod.UI
{
    [DefOf]
    public static class RimCoopKeyBindingDefOf
    {
        public static KeyBindingDef RimCoop_ToggleChat;

        static RimCoopKeyBindingDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(RimCoopKeyBindingDefOf));
        }
    }
}
