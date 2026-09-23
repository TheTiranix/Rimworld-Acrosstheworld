using System.Collections.Generic;

namespace RimCoopMod.Networking
{
    public enum PacketType : byte
    {
        Handshake = 0,
        WorldData = 1,
        PlayerJoined = 2,
        PlayerLeft = 3,
        PlayerUpdate = 4,
        TradeRequest = 5,
        AttackRequest = 6,
        Chat = 7,

        // ---- Colaborar / mapa compartido en vivo ----
        WatchRequest = 8,   // alguien quiere mirar mi base en vivo
        UnwatchRequest = 9, // dejó de mirarla
        MapSnapshot = 10,   // yo (dueño/host) mando el estado de mis pawns a quien me está mirando
        PawnOrder = 11,     // el que mira pide mover a UN pawn suyo dentro de mi mapa
        JoinRequest = 12,   // me mandan colonos (serializados) para que se sumen a mi base
        JoinResult = 13,    // confirmación/rechazo de ese traspaso

        BaseSnapshotRequest = 14, // pido una foto completa de construcciones/ítems de la base
        BaseSnapshot = 15,        // esa foto (se manda una vez al entrar y después periódicamente)

        PawnAppearanceRequest = 16, // pido el "look" completo de UN pawn (una sola vez, es pesado)
        PawnAppearance = 17,        // esa apariencia, para spawnearlo como pawn real (no cuadrado)

        // ---- Votación para pausar/despausar (nadie puede pausar solo) ----
        PauseVoteRequest = 18, // alguien propone pausar o despausar (se manda a todos)
        PauseVoteResponse = 19, // un jugador responde sí/no (va dirigido a quien propuso)
        PauseVoteResult = 20,   // el que propuso avisa el resultado final a todos, para aplicarlo

        BuildRequest = 21, // el que mira pide colocar un plano de construcción en la base real

        EventNotice = 22,       // el dueño avisa a quien lo mira de una carta/evento (incursión, plaga, etc.)
        PawnSettingRequest = 23, // el que mira pide cambiar una configuración de un colono suyo (prioridades, horario, área)
        SpeedChange = 24,        // alguien cambió la velocidad del juego (1x/2x/3x): se avisa a todos para que vayan al mismo ritmo
        PlayersRequest = 25,     // "decime dónde están las colonias de todos": el server responde con un PlayerUpdate por cada jugador asentado
        TradeMessage = 26,       // comercio entre jugadores (pedir stock, ofertar, aceptar, rechazar, transferir ítems)
        ResearchSync = 27,       // investigación compartida (proyectos terminados y progreso)
        WorldEvent = 28,         // condición del mapa real que se ve también en el mapa espejo de quien lo mira
        ServerInfo = 29,         // lo primero que manda el servidor: su versión de protocolo (para detectar mod/servidor desparejos)
        ModList = 30,            // lista de mods/DLC activos de cada jugador, para avisar si no coinciden
        QuestMessage = 31,       // misiones compartidas entre jugadores (invitar, aceptar, actualizar, terminar)
        ShipMessage = 32,        // naves comerciales compartidas entre colaboradores (aparece, cambia el stock, se va)
        SaveAll = 33,            // "guardemos todos la partida ahora": mantiene las partidas de los jugadores parejas

        // ---- Lista de servidores guardados (estilo Half-Life/CS) ----
        PingRequest = 34,  // conexión corta y aparte, sin handshake: "¿estás vivo?"
        PingResponse = 35  // el server contesta con su versión y cuántos jugadores tiene conectados AHORA
    }

    public class PingRequestPayload
    {
    }

    public class PingResponsePayload
    {
        public int ProtocolVersion;
        public int ConnectedPlayers;
        public string WorldSeed;
    }

    public class ShipMessagePayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public string Kind;        // ship | update | depart
        public int OriginPlayerId; // quién recibió la nave originalmente
        public int ShipId;         // loadID de la nave en el juego de origen
        public string Name;
        public string DefName;     // TraderKindDef
        public string FactionDef;
        public int Ticks;          // ticks que le quedan a la nave
        public int Seed;           // semilla del factor de precio
        public string Goods;       // "def,stuff,cantidad,calidad,vida;..."
    }

    public class SaveAllPayload
    {
        public int FromPlayerId;
        public string FromPlayerName;
        public string SaveName;
    }

    public class QuestMessagePayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public string FromPlayerName;
        public string Kind;        // invite | accept | decline | update | end
        public int QuestId;        // id de la misión en el juego del dueño
        public string Name;
        public string Description;
        public int State;          // Verse QuestState como int
        public int Rating;         // challengeRating
        public int Participants;   // jugadores sumados (sin contar al dueño)
        public int OwnerTicks;     // TicksGame del dueño al armar el mensaje (para alinear los relojes de cada juego)
    }

    public class ModListPayload
    {
        public int FromPlayerId;
        public int ToPlayerId; // 0 = a todos
        public string FromPlayerName;
        public string ModIds;  // packageIds separados por coma
    }

    public static class ProtocolInfo
    {
        /// <summary>Subir cada vez que cambia el formato de un paquete. Mod y servidor tienen que coincidir.</summary>
        public const int Version = 19;
    }

    public class ServerInfoPayload
    {
        public int ProtocolVersion;
    }

    /// <summary>
    /// Paquete genérico. Payload guarda el objeto real (HandshakePayload, WorldDataPayload, etc.)
    /// La serialización a bytes la hace NetIO, sin depender de ninguna librería externa.
    /// </summary>
    public class Packet
    {
        public PacketType Type;
        public object Payload;

        public static Packet Create<T>(PacketType type, T payload)
        {
            return new Packet { Type = type, Payload = payload };
        }

        public T GetPayload<T>()
        {
            return (T)Payload;
        }
    }

    // ---- Payloads ----

    public class HandshakePayload
    {
        public string PlayerName;
        public int ProtocolVersion; // va al final del paquete: un servidor viejo lo ignora (y un cliente viejo no lo manda = 0)
    }

    public class WorldDataPayload
    {
        public string Seed;
        public float PlanetCoverage;
        public string OverallRainfall;
        public string OverallTemperature;
        public string OverallPopulation;
        public List<PlayerBaseInfo> ExistingPlayers = new List<PlayerBaseInfo>();
        public int AssignedPlayerId;
    }

    public class PlayerBaseInfo
    {
        public int PlayerId;
        public string PlayerName;
        public int Tile = -1; // -1 = todavía no asentado
        public int ColonistCount;
        public int Wealth;    // riqueza de su colonia (aprox.), para mostrarla y guiar la fuerza de un ataque
    }

    public class PlayerUpdatePayload
    {
        public int PlayerId;
        public string PlayerName;
        public int Tile;
        public int ColonistCount;
        public int Wealth;
    }

    public class TradeOrAttackPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public string FromPlayerName;
        public int Points; // solo ataques: fuerza de la incursión que se manda
    }

    public class TradeMessagePayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public string FromPlayerName;
        public string Kind;   // stockreq | stock | offer | accept | reject | transfer
        public int OfferId;
        public string Data;   // según Kind (listas de ítems "def,stuff,cantidad,calidad,vida;...")
    }

    public class ChatPayload
    {
        public int PlayerId;
        public string PlayerName;
        public string Message;
    }

    // ---- Colaborar / mapa compartido en vivo ----

    public class WatchRequestPayload
    {
        public int FromPlayerId; // quien quiere mirar
        public int ToPlayerId;   // dueño de la base a mirar
    }

    /// <summary>Estado de un solo pawn dentro de la base, para dibujarlo del lado del que mira.</summary>
    public class PawnSnapshot
    {
        public int PawnId; // id local del dueño real (thingIDNumber en su juego); solo él lo interpreta
        public string Label;
        public int OwnerPlayerId; // -1 = no es colono de ningún jugador (fauna/hostil)
        public int X;
        public int Z;
        public int Rot;         // hacia dónde mira (Rot4.AsInt)
        public bool HeldOnPlatform; // Anomaly: es una entidad contenida en una plataforma de contención (no está "spawneada")
        public bool Moving;     // está caminando (para que el títere lo siga a pie en vez de teletransportarse)
        public bool HasMedium;  // si false, arma/ropa/inventario/necesidades no vienen en esta foto (se mandan cada tanto)
        public string JobLabel;
        public bool Downed;
        public bool Dead;
        public bool Hostile;
        public bool Animal;
        public string EquippedWeaponDefName;      // null/"" si no tiene nada equipado
        public string EquippedWeaponStuffDefName; // null si no aplica

        // El trabajo que está haciendo AHORA (no solo el que vos le ordenaste): se reenvía para
        // que el títere lo ejecute con su propio Tick() y se vea caminar/trabajar de verdad, en
        // vez de solo teletransportarse. Vacío = sin trabajo o no se pudo determinar el target.
        public string CurJobDefName;
        public int CurJobTargetAThingId; // id del DUEÑO real; -1 = el target es una celda
        public int CurJobTargetAX;
        public int CurJobTargetAZ;
        public bool CurJobHasTargetB;
        public int CurJobTargetBThingId;
        public int CurJobTargetBX;
        public int CurJobTargetBZ;
        public int CurJobCount = -1;

        // Lo que lleva encima el pawn real (mochila) y en las manos, para que el títere pueda
        // ejecutar trabajos que usan eso (ej. comer una ración del inventario).
        // Formato: "thingId,defName,stuffDefName,count;..." (id del DUEÑO real).
        public string InventoryCsv;
        public string CarriedCsv;

        // Ropa puesta: "defName,stuffDefName,calidad,vida;..." y calidad del arma equipada (-1 = sin calidad).
        public string ApparelCsv;
        public int EquippedWeaponQuality = -1;
        public int EquippedWeaponHitPoints;

        // Necesidades (siempre): "food=0.5;rest=0.3;mood=0.6".
        public string NeedsCsv;

        // Datos que cambian poco: solo se mandan cada tanto (HasSlowData = true). Si es false, el
        // que mira conserva lo último que recibió.
        public bool HasSlowData;
        public string HediffsCsv;    // "defName,partIndex,severity;..."
        public string MemoriesCsv;   // "thoughtDefName,stageIndex;..."
        public string WorkPrioCsv;   // "workTypeDefName=prioridad;..."
        public string TimetableCsv;  // 24 defNames de TimeAssignmentDef separados por coma
        public string AreaLabel;     // área permitida asignada ("" = sin restricción)
        public string SkillsCsv;     // "skillDef,nivel,xp,pasión;..."
        public string TraitsCsv;     // "traitDef,grado;..."
        public string TrainingCsv;   // animales: "trainableDef,quiere,pasos;..."
        public int MasterPawnId = -1; // animales: id real de su amo (-1 = nadie)
        public string BondsCsv;      // ids reales de los pawns con los que tiene un vínculo
        public string GuestStatus;   // "" | Guest | Prisoner | Slave
        public string GuestMode;     // modo de interacción del prisionero/esclavo
        public float GuestResistance;
        public float GuestWill;
        public bool IsPlayerFaction; // el pawn real es de la facción del jugador
        public string GenesCsv;      // Biotech: xenogenes ("defName;defName;...")
        public string AbilitiesCsv;  // Royalty: psicasts y demás habilidades ("defName;...")
        public string TitlesCsv;     // Royalty: títulos ("factionDef,titleDef,favor;...")
        public string PsyCsv;        // Royalty: "focus=0.5;heat=0.2"
        public string PermitsCsv;    // Royalty: permisos ("factionDef,permitDef;...")
        public string IdeoName;      // Ideology: nombre de la ideología (solo texto: el objeto Ideo real es de ESA partida y no cruza)

        // Biotech. Los títeres no simulan nada (Tick salteado), así que crecer, cambiar de etapa de vida o
        // gastar hemógeno solo pasa si el dueño lo manda.
        public long AgeBiologicalTicks;  // niños que crecen (bebé -> niño -> adolescente -> adulto) y cambian de cuerpo
        public float GrowthPoints = -1f; // puntos de crecimiento (para los momentos de crecimiento)
        public string XenotypeDefName;   // "Baseliner", "Sanguophage", etc.
        public string XenotypeName;      // nombre propio si es un xenotipo personalizado
        public string GeneResourcesCsv;  // "geneDef=valor;..." (hemógeno y otros recursos de genes)
        public string BiotechInfo;       // texto para el panel: ancho de banda del mecanitor / supervisor y modo de trabajo del mech
    }

    public class MapSnapshotPayload
    {
        public int HostPlayerId; // de quién es la base
        public int ToPlayerId;   // a quién se le manda esta foto (se completa antes de cada envío)
        public int MapWidth;
        public int MapHeight;
        public string WeatherDefName; // clima actual de la base real
        public float SkyGlow;         // brillo del cielo real (0=noche, 1=mediodía), para que se vea la misma hora
        public List<PawnSnapshot> Pawns = new List<PawnSnapshot>();
        public List<InteractionEvent> Interactions = new List<InteractionEvent>(); // charlas/insultos recientes, para mostrar la burbuja
    }

    public class InteractionEvent
    {
        public int InitiatorId;
        public int RecipientId;
        public string DefName;
    }

    /// <summary>
    /// Una orden real de RimWorld (no solo "moverse"), para poder mandar ataques, cosecha,
    /// cacería, etc. igual que el clic derecho normal del juego. Los targets pueden ser una
    /// celda o un Thing puntual (ej. el animal al que atacar) — si es un Thing, se manda su id
    /// del lado del DUEÑO real del mapa (ver CoopSessionManager.ResolveHostThingId).
    /// </summary>
    public class PawnOrderPayload
    {
        public int FromPlayerId; // quien ordena (tiene que coincidir con el dueño real del pawn)
        public int ToPlayerId;   // dueño del mapa donde está el pawn
        public int PawnId;
        public string JobDefName;
        public int TargetAThingId; // -1 = el target es solo una celda, no un Thing puntual
        public int TargetAX;
        public int TargetAZ;
        public bool HasTargetB;
        public int TargetBThingId;
        public int TargetBX;
        public int TargetBZ;
        public string AbilityDefName; // si la orden es lanzar una habilidad/psicast: cuál (el trabajo real se arma del lado del dueño)
        public int Count = -1; // job.count: cuántas unidades (ej. cuánto recoger del piso). Sin esto "tomar" llegaba con cantidad 0 y no recogía nada.
    }

    public class JoinRequestPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public List<string> SerializedPawns = new List<string>();
        // Dueño real de cada colono (mismo índice que SerializedPawns), por NOMBRE (el id cambia entre sesiones).
        // Vacío = "el dueño soy yo, quien lo manda" (caso normal). Si no es vacío es porque yo mismo lo tenía
        // prestado de otro jugador y se lo estoy reenviando/devolviendo: el destino tiene que respetar a ese
        // dueño en vez de anotarme a mí. Si el nombre coincide con el del destino, el colono "vuelve a casa"
        // y queda libre (sin dueño remoto) ahí.
        public List<string> OwnerNames = new List<string>();
    }

    public class JoinResultPayload
    {
        public int ToPlayerId; // a quién se le avisa (el que mandó los colonos)
        public int FromPlayerId; // quién responde: con él se empieza a colaborar (y a compartir investigación)
        public bool Success;
        public string Message;
    }

    public class BaseSnapshotRequestPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
    }

    /// <summary>Una construcción o ítem sobre el mapa, para reconstruirlo del lado de quien mira.</summary>
    public class ThingSnapshot
    {
        public int ThingId; // thingIDNumber del dueño real; solo él lo interpreta
        public string DefName;
        public string StuffDefName; // null si no aplica (madera, acero, etc. del material)
        public int X;
        public int Z;
        public int Rotation; // Rot4.AsInt
        public int StackCount;
        public int HitPoints;
        public string StyleDefName; // Ideology: estilo visual del objeto (ThingStyleDef)
        public string StateStr; // estado del objeto: interruptor, batería, combustible, recetas, puerta fija...
    }

    /// <summary>Zona de cultivo o almacenamiento. Las zonas no son Things, así que viajan aparte.</summary>
    public class ZoneSnapshot
    {
        public string Kind;        // "stockpile" | "growing"
        public string Label;
        public string PlantDefName; // solo growing
        public string CellsCsv;    // "x,z;x,z;..."
        public int ZoneId;         // Zone.ID del dueño real
        public string SettingsStr; // stockpile: reglas de almacenamiento; growing: "plant=<def>"
    }

    /// <summary>Área del jugador: "home", "snow", "buildroof", "noroof", "pollution" o "allowed" (con nombre).</summary>
    public class AreaSnapshot
    {
        public string Kind;
        public string Label;
        public string CellsCsv;
    }

    /// <summary>Reglas de almacenamiento de un edificio (estante, etc.), identificado por su celda.</summary>
    public class StoreSnapshot
    {
        public int X;
        public int Z;
        public string SettingsStr;
    }

    public class BaseSnapshotPayload
    {
        public int HostPlayerId;
        public int ToPlayerId;
        public List<ThingSnapshot> Things = new List<ThingSnapshot>();
        public bool IsDelta;   // true = solo lo que cambió desde la última foto (+ RemovedThingIds); false = foto completa
        public bool HasLayers = true; // false en los deltas: zonas, áreas, techos, pisos, nieve y plantas viajan solo en la foto completa
        public List<int> RemovedThingIds = new List<int>();
        public List<ZoneSnapshot> Zones = new List<ZoneSnapshot>();
        public List<AreaSnapshot> Areas = new List<AreaSnapshot>();
        public List<StoreSnapshot> Stores = new List<StoreSnapshot>();
        public List<RoofSnapshot> Roofs = new List<RoofSnapshot>(); // techos ya construidos (y naturales), comprimidos por tramos
        public List<RoofSnapshot> Terrains = new List<RoofSnapshot>(); // pisos construidos (capa superior del terreno), por tramos
        public List<RoofSnapshot> Snow = new List<RoofSnapshot>();     // nieve acumulada: DefName = nivel 1..10, por tramos
        public List<RoofSnapshot> Pollution = new List<RoofSnapshot>(); // Biotech: celdas contaminadas, por tramos
        public string ConditionsCsv; // condiciones activas del mapa real (eclipse, lluvia tóxica...): "defName|ticksLeft;..."
        public bool HasPlants;                                         // las plantas pesan: se mandan cada tanto, no en cada foto
        public List<RoofSnapshot> Plants = new List<RoofSnapshot>();   // DefName = planta; RunsCsv = "id,x,z,crecimiento;..." 
    }

    /// <summary>Todos los techos de un mismo tipo. RunsCsv = "z,x0,x1;..." (tramos horizontales).</summary>
    public class RoofSnapshot
    {
        public string DefName;
        public string RunsCsv;
    }

    public class PawnAppearanceRequestPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public int PawnId; // thingIDNumber del dueño real
    }

    public class PawnAppearancePayload
    {
        public int HostPlayerId;
        public int ToPlayerId;
        public int PawnId;
        public string SerializedPawn; // mismo formato que usa el traspaso de "Colaborar"
    }

    public class PauseVoteRequestPayload
    {
        public int FromPlayerId;
        public string FromPlayerName;
        public bool ProposePause; // true = propone pausar, false = propone despausar
    }

    public class PauseVoteResponsePayload
    {
        public int FromPlayerId;
        public int ToPlayerId; // quien propuso, para juntar los votos ahí
        public bool Accept;
    }

    public class PauseVoteResultPayload
    {
        public bool Approved;
        public bool ProposePause;
    }

    public class BuildRequestPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public string DefName;      // ThingDef de lo que se quiere construir (pared, mueble, etc.)
        public string StuffDefName; // material, si aplica
        public int X;
        public int Z;
        public int Rotation; // Rot4.AsInt
    }

    public class EventNoticePayload
    {
        public int ToPlayerId;
        public int FromPlayerId;
        public string LetterDefName;
        public string Label;
        public string Text;
    }

    public class PawnSettingPayload
    {
        public int FromPlayerId;
        public int ToPlayerId;
        public int PawnId;    // id del dueño real
        public string Kind;   // "workprio" | "timetable" | "area"
        public string Key;    // workTypeDefName | hora
        public string Value;  // prioridad | TimeAssignmentDef | etiqueta del área
    }

    public class SpeedChangePayload
    {
        public int FromPlayerId;
        public int Speed; // Verse.TimeSpeed como int
    }

    public class ResearchSyncPayload
    {
        public int FromPlayerId;
        public int ToPlayerId; // la investigación solo se comparte con quienes colaboran: va dirigida, no a todos
        public string FromPlayerName;
        public string Kind; // finished | progress | full
        public string Data; // finished: defName | progress: "def=valor;..." | full: "F:def,def#P:def=valor;..."
    }

    public class WorldEventPayload
    {
        public int FromPlayerId;
        public int ToPlayerId; // solo va a quien está mirando esa base (su mapa espejo), no a todos
        public string FromPlayerName;
        public string DefName;  // GameConditionDef
        public int Duration;    // ticks que le quedan
    }
}
