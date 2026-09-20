using System;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>Royalty: usar un permiso (ayuda militar, cargamento, obreros, nave, bombardeo) desde el mapa espejo lo ejecuta el dueño en su base real.</summary>
    public partial class CoopSessionManager
    {
        /// <summary>Host: un colono de otro jugador usó un permiso. Se ejecuta como si lo hubiera usado él en mi mapa.</summary>
        private void UsePermitOnHost(Pawn pawn, string permitDefName, string targetCsv, Map map)
        {
            try
            {
                var permitDef = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(permitDefName);
                var permit = pawn.royalty?.AllFactionPermits.FirstOrDefault(fp => fp.Permit == permitDef);
                if (permit == null) { CoopLog.Warning($"[RimCoop] {pawn.LabelShortCap} no tiene el permiso {permitDefName}."); return; }
                if (permit.OnCooldown) { CoopClient.Instance.SendJoinResult(_pawnOwners.TryGetValue(pawn.thingIDNumber, out var o) ? o : 0, false, "Ese permiso está en enfriamiento."); return; }

                var worker = permit.Permit.Worker as RoyalTitlePermitWorker_Targeted;
                if (worker == null) return;

                var xz = (targetCsv ?? "").Split(',');
                if (xz.Length != 2 || !int.TryParse(xz[0], out int x) || !int.TryParse(xz[1], out int z)) return;

                var t = Traverse.Create(worker);
                t.Field("caller").SetValue(pawn);
                t.Field("map").SetValue(map);
                t.Field("free").SetValue(false);
                if (t.Field("calledFaction").FieldExists()) t.Field("calledFaction").SetValue(permit.Faction);
                if (t.Field("faction").FieldExists()) t.Field("faction").SetValue(permit.Faction);

                worker.OrderForceTarget(new LocalTargetInfo(new IntVec3(x, 0, z)));
                CoopLog.Message($"[RimCoop] Permiso {permitDefName} usado por {pawn.LabelShortCap} en ({x},{z}).");
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error usando el permiso {permitDefName}: {e.Message}");
            }
        }
    }
}
