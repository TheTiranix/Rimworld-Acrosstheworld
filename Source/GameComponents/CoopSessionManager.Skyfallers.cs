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
    /// <summary>
    /// Naves, cápsulas y meteoritos que caen o se van en la base de un jugador: quien la mira ve la animación (ver MirrorSkyfaller)
    /// en vez de que los pawns y edificios aparezcan de golpe. Royalty (naves de transporte, visitantes), cápsulas de incursiones,
    /// Odyssey (rocas de lava), meteoritos y chatarra de naves del juego base.
    /// </summary>
    public partial class CoopSessionManager
    {
        // (de quién es la base) -> (id del Skyfaller real -> su copia visual)
        private readonly Dictionary<int, Dictionary<int, MirrorSkyfaller>> _mirrorSkyfallers = new Dictionary<int, Dictionary<int, MirrorSkyfaller>>();

        private static readonly List<Skyfaller> _tmpSkyfallers = new List<Skyfaller>();

        /// <summary>Dueño: cuenta los Skyfaller que hay ahora en mi base.</summary>
        private static void FillSkyfallers(MapSnapshotPayload payload, Map map)
        {
            if (SkyfallerRegistry.Active.Count == 0) return;

            _tmpSkyfallers.Clear();
            _tmpSkyfallers.AddRange(SkyfallerRegistry.Active);
            foreach (var sf in _tmpSkyfallers)
            {
                try
                {
                    if (sf == null || sf.Destroyed) { SkyfallerRegistry.Active.Remove(sf); continue; }
                    if (!sf.Spawned || sf.Map != map) continue;
                    // Sin gráfico propio en el def (se tomaría del contenido) no hay con qué dibujarlo.
                    if (sf.def.graphicData == null || sf.def.skyfaller == null) continue;

                    // Las naves de pasajeros (Odyssey) calculan su ángulo según hacia dónde miran y se pintan con el color de su
                    // edificio: se marcan para que la copia haga lo mismo.
                    int kind = sf is PassengerShuttleIncoming ? 1 : sf is PassengerShuttleLeaving ? 2 : 0;
                    string colorKey = "";
                    if (kind != 0)
                    {
                        try { var c = sf.DrawColor; colorKey = Inv(c.r) + ":" + Inv(c.g) + ":" + Inv(c.b) + ":" + Inv(c.a); }
                        catch { /* sin edificio todavía: se dibuja con el color del def */ }
                    }

                    payload.Skyfallers.Add(new SkyfallerSnapshot
                    {
                        Id = sf.thingIDNumber,
                        DefName = sf.def.defName,
                        X = sf.Position.x,
                        Z = sf.Position.z,
                        Rot = sf.Rotation.AsInt,
                        TicksToImpact = sf.ticksToImpact,
                        TicksToImpactMax = Traverse.Create(sf).Field("ticksToImpactMax").GetValue<int>(),
                        TicksToDiscard = sf.ticksToDiscard,
                        Angle = sf.angle,
                        Kind = kind,
                        ColorKey = colorKey
                    });
                }
                catch (Exception e)
                {
                    CoopLog.Warning(Loc.T("SessionManager_Skyfallers.01", e.Message));
                }
            }
            _tmpSkyfallers.Clear();
        }

        /// <summary>Espejo: crea, mantiene al día y borra las copias visuales según lo que el dueño dice que está cayendo ahora.</summary>
        private void SyncMirrorSkyfallers(Map map, MapSnapshotPayload snapshot)
        {
            int host = snapshot.HostPlayerId;
            if (!_mirrorSkyfallers.TryGetValue(host, out var known))
            {
                known = new Dictionary<int, MirrorSkyfaller>();
                _mirrorSkyfallers[host] = known;
            }

            var seen = new HashSet<int>();
            if (snapshot.Skyfallers != null)
            {
                foreach (var s in snapshot.Skyfallers)
                {
                    seen.Add(s.Id);

                    if (known.TryGetValue(s.Id, out var existing))
                    {
                        // Ya la tuvimos: si sigue viva se la corrige; si terminó sola (o falló al crearse) no se vuelve a crear
                        // mientras el dueño aún la lista.
                        if (existing != null && !existing.Destroyed) existing.Resync(s.TicksToImpact);
                        continue;
                    }

                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(s.DefName);
                    var cell = new IntVec3(s.X, 0, s.Z);
                    if (def?.skyfaller == null || def.graphicData == null || !cell.InBounds(map)) continue;

                    try
                    {
                        var copy = (MirrorSkyfaller)ThingMaker.MakeThing(MirrorSkyfaller.MirrorDef);
                        copy.Init(def, s);
                        GenSpawn.Spawn(copy, cell, map, new Rot4(s.Rot));
                        known[s.Id] = copy;
                    }
                    catch (Exception e)
                    {
                        CoopLog.Warning(Loc.T("SessionManager_Skyfallers.02", s.DefName, e.Message));
                        known[s.Id] = null; // no reintentar en cada foto
                    }
                }
            }

            foreach (int id in known.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                known[id]?.Finish();
                known.Remove(id);
            }
        }
    }
}
