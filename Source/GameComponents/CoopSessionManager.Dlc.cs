using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimCoopMod.Networking;
using RimCoopMod.World;
using RimWorld;
using Verse;

namespace RimCoopMod.GameComponents
{
    /// <summary>Soporte específico de DLC: Odyssey (qué mapa es "mi base"), Anomaly (entidades contenidas) y Royalty (psicasts, títulos).</summary>
    public partial class CoopSessionManager
    {
        // =====================================================================
        // Odyssey: una nave gravitatoria crea mapas nuevos (órbita, otras colonias). "Mi base" tiene que ser SIEMPRE el
        // asentamiento en la superficie que se avisó al servidor, no "cualquier mapa propio": si no, se mandaba una foto
        // de la órbita (o de otra base) a quien mira la base principal.
        // =====================================================================

        public static Map LocalBaseMap
        {
            get
            {
                var instance = Current.Game?.GetComponent<CoopSessionManager>();
                if (instance != null && instance._localTile >= 0)
                {
                    foreach (var m in Find.Maps)
                        if (m.IsPlayerHome && (int)m.Tile == instance._localTile) return m;
                }

                foreach (var m in Find.Maps)
                    if (m.IsPlayerHome && !(m.Biome?.inVacuum ?? false) && GetHostPlayerIdForMap(m) < 0) return m;

                return Find.AnyPlayerHomeMap;
            }
        }

        // =====================================================================
        // Anomaly: entidades contenidas en plataformas de contención. No están "spawneadas" (viven dentro del edificio),
        // así que no salían en la lista de pawns del mapa.
        // =====================================================================

        private static IEnumerable<Pawn> HeldEntities(Map map)
        {
            if (!ModsConfig.AnomalyActive || map == null) yield break;
            foreach (var platform in map.listerBuildings.allBuildingsColonist.OfType<Building_HoldingPlatform>())
            {
                var held = platform.HeldPawn;
                if (held != null && !held.Dead) yield return held;
            }
        }

        /// <summary>Devuelve true si el títere quedó (o ya estaba) dentro de la plataforma espejo.</summary>
        private static bool HoldPuppetOnPlatform(Pawn puppet, PawnSnapshot ps, Map map)
        {
            try
            {
                if (!puppet.Spawned && puppet.holdingOwner != null) return true; // ya está adentro

                var cell = new IntVec3(ps.X, 0, ps.Z);
                var platform = cell.InBounds(map) ? cell.GetThingList(map).OfType<Building_HoldingPlatform>().FirstOrDefault() : null;
                var container = platform?.GetComp<CompEntityHolder>()?.Container;
                if (container == null) return false; // la plataforma todavía no llegó al espejo: queda visible al lado

                if (puppet.Spawned) puppet.DeSpawn();
                if (container.TryAdd(puppet))
                {
                    puppet.GetComp<CompHoldingPlatformTarget>()?.Notify_HeldOnPlatform(container);
                    return true;
                }
                GenSpawn.Spawn(puppet, cell, map);
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo meter a la entidad {puppet.LabelShortCap} en la plataforma espejo: {e.Message}");
            }
            return false;
        }

        /// <summary>La entidad ya no está contenida en el mundo real: se la saca de la plataforma espejo.</summary>
        private static void ReleaseHeldPuppet(Pawn puppet, PawnSnapshot ps, Map map)
        {
            try
            {
                if (puppet.Spawned || puppet.holdingOwner == null || puppet.Dead) return;
                puppet.holdingOwner.Remove(puppet);
                puppet.GetComp<CompHoldingPlatformTarget>()?.Notify_ReleasedFromPlatform();
                var cell = new IntVec3(ps.X, 0, ps.Z);
                GenSpawn.Spawn(puppet, cell.InBounds(map) ? cell : map.Center, map);
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] No se pudo sacar a la entidad {puppet.LabelShortCap} de la plataforma espejo: {e.Message}");
            }
        }

        // =====================================================================
        // Royalty: psicasts (habilidades), enfoque psíquico, calor neural y títulos.
        // (Lanzar un psicast sigue sin imitarse en el espejo: se ve el resultado, no el lanzamiento.)
        // =====================================================================

        private static void FillRoyalty(Pawn pawn, PawnSnapshot s)
        {
            if (!ModsConfig.RoyaltyActive) return;

            s.AbilitiesCsv = pawn.abilities == null ? "" : string.Join(";", pawn.abilities.AllAbilitiesForReading.Where(a => a?.def != null).Select(a => a.def.defName));

            s.TitlesCsv = pawn.royalty == null ? "" : string.Join(";", pawn.royalty.AllTitlesForReading
                .Where(t => t?.faction != null && t.def != null)
                .Select(t => t.faction.def.defName + "," + t.def.defName + "," + pawn.royalty.GetFavor(t.faction)));

            s.PermitsCsv = pawn.royalty == null ? "" : string.Join(";", pawn.royalty.AllFactionPermits
                .Where(fp => fp?.Faction != null && fp.Permit != null)
                .Select(fp => fp.Faction.def.defName + "," + fp.Permit.defName));

            if (pawn.psychicEntropy != null && pawn.psychicEntropy.NeedsPsyfocus)
                s.PsyCsv = "focus=" + Inv(pawn.psychicEntropy.CurrentPsyfocus) + ";heat=" + Inv(pawn.psychicEntropy.EntropyValue);
        }

        private static void ApplyRoyalty(Pawn puppet, PawnSnapshot ps)
        {
            if (!ModsConfig.RoyaltyActive) return;
            try
            {
                if (puppet.abilities != null && ps.AbilitiesCsv != null)
                {
                    var wanted = new HashSet<string>(ps.AbilitiesCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                    foreach (var a in puppet.abilities.AllAbilitiesForReading.ToList())
                        if (a?.def != null && !wanted.Remove(a.def.defName)) puppet.abilities.RemoveAbility(a.def);
                    foreach (var name in wanted)
                    {
                        var def = DefDatabase<AbilityDef>.GetNamedSilentFail(name);
                        if (def != null) puppet.abilities.GainAbility(def);
                    }
                }

                if (puppet.royalty != null && !string.IsNullOrEmpty(ps.TitlesCsv))
                {
                    foreach (var entry in ps.TitlesCsv.Split(';'))
                    {
                        var f = entry.Split(',');
                        if (f.Length != 3) continue;
                        var faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.def.defName == f[0]);
                        var title = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(f[1]);
                        if (faction == null || title == null) continue;
                        if (puppet.royalty.GetCurrentTitle(faction) != title) puppet.royalty.SetTitle(faction, title, false, false, false);
                        if (int.TryParse(f[2], out int favor) && puppet.royalty.GetFavor(faction) != favor) puppet.royalty.SetFavor(faction, favor, false);
                    }
                }

                if (puppet.royalty != null && ps.PermitsCsv != null)
                {
                    var wantedPermits = new HashSet<string>(ps.PermitsCsv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

                    // Los que el colono real ya no tiene se sacan de la lista interna (no hay método público para quitar uno solo).
                    var owned = Traverse.Create(puppet.royalty).Field("factionPermits").GetValue<List<FactionPermit>>();
                    owned?.RemoveAll(fp => fp?.Faction != null && fp.Permit != null && !wantedPermits.Contains(fp.Faction.def.defName + "," + fp.Permit.defName));

                    foreach (var entry in wantedPermits)
                    {
                        var f = entry.Split(',');
                        if (f.Length != 2) continue;
                        var faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.def.defName == f[0]);
                        var permit = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(f[1]);
                        if (faction != null && permit != null && !puppet.royalty.HasPermit(permit, faction)) puppet.royalty.AddPermit(permit, faction);
                    }
                }

                if (puppet.psychicEntropy != null && !string.IsNullOrEmpty(ps.PsyCsv))
                {
                    foreach (var kv in ps.PsyCsv.Split(';'))
                    {
                        var p = kv.Split('=');
                        if (p.Length != 2) continue;
                        if (p[0] == "focus") Traverse.Create(puppet.psychicEntropy).Field("currentPsyfocus").SetValue(ParseF(p[1]));
                        else if (p[0] == "heat") Traverse.Create(puppet.psychicEntropy).Field("currentEntropy").SetValue(ParseF(p[1]));
                    }
                }
            }
            catch (Exception e)
            {
                CoopLog.Warning($"[RimCoop] Error copiando psicasts/títulos de {puppet.LabelShortCap}: {e.Message}");
            }
        }
    }
}
