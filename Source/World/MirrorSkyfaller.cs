using System;
using System.Collections.Generic;
using HarmonyLib;
using RimCoopMod.Networking;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimCoopMod.World
{
    /// <summary>
    /// Copia puramente visual de una nave, cápsula o meteorito que cae (o se va) en la base REAL de otro jugador, para que quien
    /// mira esa base vea la animación y no solo el resultado (los pawns y edificios que aparecen de golpe). No es un Skyfaller de
    /// verdad: un Skyfaller real en el espejo impactaría (explosiones, cráteres, techos que se caen) y sus clases hijas (cápsulas,
    /// naves) leen un contenido que acá no existe. Esta solo dibuja con el gráfico, las curvas y la sombra del def real (los copia
    /// de SkyfallerProperties, igual que Skyfaller.DrawAt), cuenta los ticks y se destruye sola.
    /// </summary>
    public class MirrorSkyfaller : Thing
    {
        public static ThingDef MirrorDef => DefDatabase<ThingDef>.GetNamed("RimCoop_MirrorSkyfaller");

        private const int DefaultLeaveTicks = 220;
        private const int SafetyMaxAgeTicks = 1500;

        private ThingDef _source;
        private int _ticks;
        private int _ticksMax = DefaultLeaveTicks;
        private int _ticksToDiscard = -1;
        private float _angle = Skyfaller.DefaultAngle;
        private int _age;
        private bool _soundPlayed;
        private Graphic _graphic;
        private Material _shadowMaterial;
        private int _kind;            // 0 común, 1 nave de pasajeros llegando, 2 nave de pasajeros yéndose (ver SkyfallerSnapshot.Kind)
        private Color? _hostColor;    // el color con que el dueño la dibuja (solo las de pasajeros: el de su edificio)

        // Odyssey, PassengerShuttleIncoming/Leaving: el ángulo no sale del def sino de hacia dónde mira la nave.
        private static readonly SimpleCurve PassengerArrivingAngle = new SimpleCurve { new CurvePoint(0f, 30f), new CurvePoint(1f, 0f) };
        private static readonly SimpleCurve PassengerLeavingAngle = new SimpleCurve { new CurvePoint(0f, 0f), new CurvePoint(1f, 20f) };

        public bool Reversed => _source?.skyfaller != null && _source.skyfaller.reversed;
        private int LeaveMapAfterTicks => _ticksToDiscard <= 0 ? DefaultLeaveTicks : _ticksToDiscard;

        public void Init(ThingDef source, SkyfallerSnapshot s)
        {
            _source = source;
            _ticks = s.TicksToImpact;
            _ticksMax = s.TicksToImpactMax > 0 ? s.TicksToImpactMax : DefaultLeaveTicks;
            _ticksToDiscard = s.TicksToDiscard;
            _angle = s.Angle;
            _kind = s.Kind;
            _hostColor = TryParseColorKey(s.ColorKey);
        }

        private static Color? TryParseColorKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var p = key.Split(':');
            if (p.Length != 4) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float r, g, b, a;
            if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out r) || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out g)
                || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, inv, out b) || !float.TryParse(p[3], System.Globalization.NumberStyles.Float, inv, out a)) return null;
            return new Color(r, g, b, a);
        }

        // Igual que PassengerShuttleIncoming/Leaving.GetAngle.
        private float PassengerAngle(float timeInAnimation)
        {
            var curve = _kind == 1 ? PassengerArrivingAngle : PassengerLeavingAngle;
            switch (Rotation.AsInt)
            {
                case 1: return Rotation.Opposite.AsAngle + curve.Evaluate(timeInAnimation);
                case 3: return Rotation.Opposite.AsAngle - curve.Evaluate(timeInAnimation);
                default: return Rotation.Opposite.AsAngle;
            }
        }

        public override Color DrawColor
        {
            get { return _hostColor ?? base.DrawColor; }
            set { base.DrawColor = value; }
        }

        /// <summary>El dueño manda su cuenta real en cada foto; solo se corrige si se desfasó de más (si no, saltaría en cada una).</summary>
        public void Resync(int ticksToImpact)
        {
            if (Math.Abs(_ticks - ticksToImpact) > 6) _ticks = ticksToImpact;
        }

        /// <summary>El dueño ya no la tiene (impactó o se fue): se despide con el sonido de impacto y se borra.</summary>
        public void Finish()
        {
            if (Destroyed) return;
            try
            {
                if (!Reversed && Spawned && _source?.skyfaller?.impactSound != null)
                    _source.skyfaller.impactSound.PlayOneShot(SoundInfo.InMap(new TargetInfo(Position, Map)));
            }
            catch { /* el sonido no es crítico */ }
            Destroy(DestroyMode.Vanish);
        }

        public override Graphic Graphic
        {
            get
            {
                if (_graphic == null && _source?.graphicData != null)
                    _graphic = _kind != 0 ? _source.graphicData.GraphicColoredFor(this) : _source.graphicData.Graphic;
                return _graphic ?? base.Graphic;
            }
        }

        private float TimeInAnimation => Reversed ? (float)_ticks / LeaveMapAfterTicks : 1f - (float)_ticks / _ticksMax;

        private float CurrentSpeed
        {
            get
            {
                var sp = _source.skyfaller;
                return sp.speedCurve == null ? sp.speed : sp.speedCurve.Evaluate(TimeInAnimation) * sp.speed;
            }
        }

        public override Vector3 DrawPos
        {
            get
            {
                if (_source?.skyfaller == null) return base.DrawPos;
                var sp = _source.skyfaller;
                bool flip = sp.flightFlippedHorizontally;
                switch (sp.movementType)
                {
                    case SkyfallerMovementType.ConstantSpeed:
                        return SkyfallerDrawPosUtility.DrawPos_ConstantSpeed(base.DrawPos, _ticks, _angle, CurrentSpeed, flip, null);
                    case SkyfallerMovementType.Decelerate:
                        return SkyfallerDrawPosUtility.DrawPos_Decelerate(base.DrawPos, _ticks, _angle, CurrentSpeed, flip, null);
                    default:
                        return SkyfallerDrawPosUtility.DrawPos_Accelerate(base.DrawPos, _ticks, _angle, CurrentSpeed, flip, null);
                }
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (_source?.skyfaller == null) { Destroy(DestroyMode.Vanish); return; }
            var sp = _source.skyfaller;

            if (Reversed) _ticks++; else _ticks--;
            _age++;

            if (!_soundPlayed && sp.anticipationSound != null && !sp.anticipationSound.sustain
                && (Reversed ? _ticks > sp.anticipationSoundTicks : _ticks < sp.anticipationSoundTicks))
            {
                _soundPlayed = true;
                try { sp.anticipationSound.PlayOneShot(new TargetInfo(Position, Map)); } catch { }
            }

            // Respaldo por si el dueño nunca avisa que terminó (se cortó la conexión justo ahí): no queda una nave colgada para siempre.
            bool animationOver = Reversed ? _ticks >= LeaveMapAfterTicks + 30 : _ticks < -30;
            if (animationOver || _age > SafetyMaxAgeTicks) Destroy(DestroyMode.Vanish);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (_source?.skyfaller == null) return;
            var sp = _source.skyfaller;

            float extraRotation = 0f;
            if (_kind != 0)
            {
                // Igual que PassengerShuttleIncoming/Leaving.GetDrawPositionAndRotation.
                _angle = PassengerAngle(TimeInAnimation);
                if (sp.rotationCurve != null)
                {
                    if (Rotation.AsInt == 1) extraRotation += sp.rotationCurve.Evaluate(TimeInAnimation);
                    else if (Rotation.AsInt == 3) extraRotation -= sp.rotationCurve.Evaluate(TimeInAnimation);
                }
                if (sp.zPositionCurve != null) drawLoc.z += sp.zPositionCurve.Evaluate(TimeInAnimation);
            }
            else
            {
                // Igual que Skyfaller.GetDrawPositionAndRotation: curvas de ángulo, rotación y desplazamiento del def real.
                if (sp.rotateGraphicTowardsDirection) extraRotation = _angle;
                if (sp.angleCurve != null) _angle = sp.angleCurve.Evaluate(TimeInAnimation);
                if (sp.rotationCurve != null) extraRotation += sp.rotationCurve.Evaluate(TimeInAnimation);
                if (sp.xPositionCurve != null) drawLoc.x += sp.xPositionCurve.Evaluate(TimeInAnimation);
                if (sp.zPositionCurve != null) drawLoc.z += sp.zPositionCurve.Evaluate(TimeInAnimation);
            }

            Graphic?.Draw(drawLoc, flip ? Rotation.Opposite : Rotation, this, extraRotation);

            if (_shadowMaterial == null && !string.IsNullOrEmpty(sp.shadow))
                _shadowMaterial = MaterialPool.MatFrom(sp.shadow, ShaderDatabase.Transparent);
            if (_shadowMaterial != null)
                Skyfaller.DrawDropSpotShadow(base.DrawPos, Rotation, _shadowMaterial, sp.shadowSize, _ticks);
        }
    }

    /// <summary>
    /// Registro de los Skyfaller reales que hay ahora en el juego (los que aparecen y desaparecen en cualquier mapa), para que el
    /// dueño de una base pueda contárselos a quien lo mira (ver CoopSessionManager.FillSkyfallers) sin recorrer todas las cosas
    /// del mapa en cada foto.
    /// </summary>
    public static class SkyfallerRegistry
    {
        public static readonly HashSet<Skyfaller> Active = new HashSet<Skyfaller>();
    }

    [HarmonyPatch(typeof(Skyfaller), nameof(Skyfaller.SpawnSetup))]
    public static class Skyfaller_SpawnSetup_Registry_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Skyfaller __instance) => SkyfallerRegistry.Active.Add(__instance);
    }

    [HarmonyPatch(typeof(Skyfaller), nameof(Skyfaller.Destroy))]
    public static class Skyfaller_Destroy_Registry_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Skyfaller __instance) => SkyfallerRegistry.Active.Remove(__instance);
    }
}
