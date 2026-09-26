# RimCoop — cooperative multiplayer for RimWorld

🇪🇸 [Versión en español](README.es.md)

Experimental mod (RimWorld **1.6**) where every player runs **their own colony** on the **same planet**,
and can see, visit, help, trade with or attack the others.

> **Status: experimental.** Every game runs its own simulation. What you see of another player's base
> is a copy synchronized over the network (positions, jobs, buildings, items, zones...), not a single shared
> simulation like in the *Multiplayer* mod. There may be differences and bugs.

## What it does

- **Shared world:** the server hands out the seed; everyone generates the same planet.
- **Colonies on the world map:** you see where every player settled. They refresh when you open "World".
- **Enter another base:** *Enter the base* button — a real map with animated colonists, buildings, items,
  zones, areas, roofs, plants, weather and light.
- **Collaborate:** you send your own colonists to another base. Only you can give them orders ("That is not your colonist" for everyone else).
- **Build and edit in someone else's base:** blueprints, zones, areas, bills, switches... the owner applies them. The colonists
  you sent with *Collaborate* are real pawns in the owner's base and **help build** on their own according to their
  Construction priority (you can change it from the mirror); the progress of every job is visible in the mirror. The mirror never
  builds by itself: the jobs are done by the owner's game.
- **Trade** between players (offers with accept/reject) and **attacks** with real raids.
- **Global chat** (`\` key), **pause by vote** and **shared game speed**.
- **English and Spanish**, switchable at any time (see below).

## Language

The mod is fully available in **English and Spanish**. By default it uses the game's language; change it in *Options → Mod
settings → RimCoop* or with the **Language** button in the connect and server windows (it applies instantly). The server
too: `--lang es|en` when starting it, or the `language es|en` command (it is remembered). Every player sees the texts in their
own language, even if the others play in a different one.

## Installation (players)

1. Install [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest).
2. Download `RimCoop-Mod-*.zip` from the **Releases** section and unzip the `RimCoop` folder into
   `RimWorld/Mods/`.
3. Enable **Harmony** and **RimCoop** (in that order) in the launcher.

## Server

Download `RimCoop-Server-*.zip`, unzip it and run `RimCoopServer.exe` (Windows; on Linux/macOS with Mono).

- When it starts it asks you to choose a **save**: each one has its own seed, port and player list,
  so you can have one for a group of friends and a completely different one for another group without them
  mixing or overwriting each other. They are stored in `ServerData/<save-name>/` next to the `.exe`.
- Default port: **34500** (TCP), configurable per save. If you play over the internet, open/forward that port.
- Without the menu (scripts, `.bat`, several servers at once): `RimCoopServer.exe --partida friends3 --puerto 34501 --seed ABC123`
  (`--save` / `--port` also work). If the save already exists it is continued and `--puerto`/`--seed` are ignored.
- **Console commands** (type `help`): `players` (who is connected), `kick <name|id> [reason]` (removes them, they can come
  back), `ban <name|id> [reason]` (removes them and does not let them come back), `unban <name>`, `bans`, `language es|en` and
  `exit`. Names with spaces go in quotes. Bans are stored per save in `ServerData/<save>/banned_players.txt`.
- **LAN search:** the server answers the mod's LAN tab by itself (UDP **34599**, the firewall must let it through).
- The server only forwards messages: it does not run the game.

## How to play

1. Everyone: main menu → **RimCoop: Connect** → IP, port and name → *Connect*, or *Saved servers...*: the
   **Saved** tab (nickname + IP + port, favorites first, and for each one how many people are on it, the ping, the save name and the
   seed, refreshed automatically every 15 s) and the **LAN** tab (finds the servers on your local network by itself, no need to type the IP).
2. Create a new world: the server's seed is used. Choose your spot; you already see the other players' bases.
3. When you settle, your colony shows up on the others' world map.
4. Click another player's base: *Enter the base*, *Collaborate*, *Trade*, *Attack*.

## Building from source

Requires the .NET SDK and RimWorld installed. The `.csproj` files reference `D:\RimWorld\RimWorldWin64_Data\Managed\`;
adjust that path to your installation.

```
dotnet build Source/RimCoopMod.csproj -c Release   # the mod (Assemblies/RimCoopMod.dll)
dotnet build server/RimCoopServer.csproj -c Release # the server
```

Texts live in `Source/Localization/Strings.g.cs` (`Loc.T("key", args)`), one table per language.

## DLC compatibility

Tested with **Royalty, Ideology, Biotech, Anomaly and Odyssey** active. The mod only uses what the game exposes
in each case and adapts to the DLC you have:

- On connecting, every player sends their DLC/mod list; if they do not match, the mod tells you exactly what is missing
  on each side (anything that depends on it may not show up on the other side). **It is best if everyone uses the same DLC and mods.**
- **Biotech:** terrain pollution, xenogenes and xenotype of the colonists, gene resources (hemogen), children's
  growth (biological age, life stage change, growth points, learning and play needs) and pregnancies (like any other health
  condition; only the real owner triggers the birth) are copied. Mechs show up on the mirror map with their battery (energy), and
  their panel shows the overseer and work mode; the mechanitor shows their bandwidth and how many mechs they control. The real
  mechanitor-mech link does not cross between games (it is an object of the owner's game), so control groups and work mode can only be
  changed from the owner's game.
- **Ideology:** the visual style of furniture, buildings and clothes is copied, and each colonist's ideology name is shown in
  their inspect panel (the Ideo object itself belongs to the owner's game and does not cross between games, so its precepts are not
  synchronized and it cannot be "edited" from the mirror).
- Jobs that depend on rituals/ceremonies are not imitated on the mirror map (you see the result, not the ritual).
- **Odyssey:** "my base" is always the surface settlement that was announced to the server, not any map you own;
  this way a gravship that takes off (and creates new maps) does not confuse what the others see. If the ship **moves the
  base to another tile** (and abandons the old one), the new location is announced again: the others see the dot move and, if they were
  watching it, the old mirror map is discarded and they enter the new place by themselves (the camera follows them). Extra ships and colonies on other maps
  (orbit, other tiles) **are not mirrored** as a map, and neither is the vacuum; they are summarized as text in the base panel
  ("Odyssey: 1 gravship(s), 2 more own map(s) (1 in orbit)"). The **gravity engine is not mirrored**
  as a building: the game decides which maps are "own base" by looking for an engine (without looking at the faction), and a mirrored
  one would make the mirror map count as your own base (incidents aimed at it, colonists counted twice).
- **Anomaly:** entities held in containment platforms are shown on the mirror map, with their activity level,
  study progress and containment mode (the panel texts come by themselves from the puppet's own components),
  and colonists who become mutants (ghouls) are updated. Structure study is copied as well.
  Other players' ghouls/shamblers do not show up in your colonist bar. The **real monolith is not mirrored** as a building
  (it would register its own instance as your game's monolith and break yours): its level is shown as text
  in the player's base panel. Anomaly incidents arrive as letters, like everything else.
- **Royalty:** psycasts (abilities), psyfocus, neural heat, titles with their favor, heirs and permits are copied and kept up to date.
  Psycasts and permits you use with one of your colonists on the mirror map are executed by the owner in their real base.
  Landed shuttles and anima trees show up like any other building or plant. **Shared quests:** at the bases of players you
  collaborate with, *Share quest* invites them to one of your ongoing quests. Whoever joins receives the **whole** quest (name,
  description, objectives, parts, timers and state, always up to date) and sees it identical in their quests tab; the copy does not
  "play" on its own: only the owner runs it. Reward items and the real favor the quest gives reach **everyone** who
  joined, and the raids and threats it triggers go up **35% for each player who joined**. Objectives that point to something in
  the owner's world (a site, a map, one of their colonists) are shown, but "go see it" does not find the object in the
  others' game. Other rewards (colonists, faction relation changes) belong to the owner only.
  Investiture ceremonies and other rituals are not imitated on the mirror map (participants are seen at their
  position, without the ritual animation), nor is the animation of ships landing or taking off.

## Saving and loading

Every player has their own save. What is stored with it: the collaborators, the shared quests, who owns every colonist
that was sent to you, the **trade offers** (the ones you sent and the ones you received and did not answer), the pending
**quest invitations** and the link of the **shared trade ships**. On load, the other players' bases and their mirror maps are cleared
and requested again from the server, and player ids are stable by name (also across server restarts). Whatever is pending is shown
again (or resumed) as soon as the other player is connected; an offer or invitation can be left for later with *Decide later*
(Esc does not dismiss it), and it cannot be accepted if whoever sent it is disconnected (their answer would be lost).

- **Colonists sent between players (no duplicates or losses):** a colonist sent with *Collaborate* exists in ONE save at a
  time, but each player stores their own. The **server remembers who has each colonist** (with a copy from when it was
  sent, in `ServerData/<save>/colonists.bin`) and every player reconciles it on connecting: if you loaded a save from before
  sending them, the duplicate copy you had at home is removed; if you loaded one from before receiving them, they are rebuilt from
  the server's copy. A colonist who died (or left the map) is deregistered and is not "revived". For this to protect a
  save you have to **save it once with this version** before sending colonists; colonists lent before this version
  remain untracked. *Save all* (button in the chat) is still a good idea. From the server console, `colonists`
  shows the registry and `colonist delete <id>` unsticks something by hand.
- Trade offers expire after 30 game days.

## Trading with trade ships (Empire and others)

When an orbital trade ship arrives for a player, their connected collaborators receive it too with the same stock.
Each one trades with their colonists and their silver from their comms console; if someone buys or sells, the stock changes
for everyone, and when the ship leaves, it leaves for everyone. Slaves and animals for sale do not travel. The other
communications with the Empire (relations, calls for help from the console) remain each player's own.

## Known limitations

- There is no shared deterministic simulation: date/time, research and wealth belong to each save.
- Random events happen in their owner's game. Whoever watches that base sees them on the mirror map too (enemies, crop blights, weather and conditions like eclipse or toxic fallout), but they do not affect their main colony.
- Copying colonists between games uses RimWorld's save system and leaves errors in the log
  (age, ideology, addictions) that do not prevent playing.
- The server trusts what the clients send (no anti-cheat) and there is no authentication.
- Only tested on RimWorld 1.6.

## License

**All rights reserved.** It is not open-source software: the code is published only so it can be read, and
the compiled versions in *Releases* may be used to play (personal, non-commercial use). It may not be copied, redistributed,
modified or used commercially without the author's permission. See [LICENSE](LICENSE).
