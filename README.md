# RimCoop — multijugador cooperativo para RimWorld

Mod experimental (RimWorld **1.6**) donde cada jugador lleva **su propia colonia** en el **mismo planeta**,
y puede ver, visitar, ayudar, comerciar o atacar a los demás.

> **Estado: experimental.** Cada juego corre su propia simulación. Lo que ves de la base de otro
> jugador es una copia sincronizada por red (posiciones, trabajos, construcciones, ítems, zonas...),
> no una simulación única compartida como en el mod *Multiplayer*. Puede haber diferencias y errores.

## Qué hace

- **Mundo compartido:** el servidor reparte la seed; todos generan el mismo planeta.
- **Colonias en el mapa mundial:** ves dónde fundó cada jugador. Se actualizan al abrir "Mundo".
- **Entrar a otra base:** botón *Entrar a la base* — mapa real con colonos animados, construcciones, ítems,
  zonas, áreas, techos, plantas, clima y luz.
- **Colaborar:** mandás colonos tuyos a otra base. Solo vos podés darles órdenes ("Ese no es tu colono" al resto).
- **Construir y editar en la base ajena:** planos, zonas, áreas, recetas, interruptores... el dueño lo aplica. Los colonos que
  mandaste con *Colaborar* son pawns reales en la base del dueño y **ayudan a construir** solos según su prioridad de
  Construcción (la podés cambiar desde el espejo); el avance de cada obra se ve en el espejo. El espejo nunca construye por su
  cuenta: las obras las hace el juego del dueño.
- **Comercio** entre jugadores (ofertas con aceptar/rechazar) y **ataques** con incursiones reales.
- **Chat global** (tecla `\`), **pausa por votación** y **velocidad compartida**.

## Instalación (jugadores)

1. Instalá [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest).
2. Bajá `RimCoop-Mod-*.zip` de la sección **Releases** y descomprimí la carpeta `RimCoop` dentro de
   `RimWorld/Mods/`.
3. Activá **Harmony** y **RimCoop** (en ese orden) en el launcher.

## Servidor

Bajá `RimCoop-Server-*.zip`, descomprimilo y ejecutá `RimCoopServer.exe` (Windows; en Linux/macOS con Mono).

- Al abrirlo pide elegir una **partida**: cada una tiene su propia seed, puerto y lista de jugadores,
  así podés tener una para un grupo de amigos y otra totalmente distinta para otro grupo sin que se
  mezclen ni se pisen. Quedan en `ServerData/<nombre-de-la-partida>/` junto al `.exe`.
- Puerto por defecto: **34500** (TCP), configurable por partida. Si jugás por internet, abrí/redirigí ese puerto.
- Sin menú (scripts, `.bat`, varios servidores a la vez): `RimCoopServer.exe --partida amigos3 --puerto 34501 --seed ABC123`.
  Si la partida ya existe se continúa y `--puerto`/`--seed` se ignoran.
- **Comandos de la consola** (escribí `ayuda`): `jugadores` (quién está conectado), `kick <nombre|id> [motivo]` (lo saca, puede
  volver), `ban <nombre|id> [motivo]` (lo saca y no lo deja volver), `unban <nombre>`, `bans` y `salir`. Los nombres con espacios
  van entre comillas. Los baneados se guardan por partida en `ServerData/<partida>/banned_players.txt`.
- **Búsqueda en LAN:** el servidor responde solo a la pestaña LAN del mod (UDP **34599**, el firewall tiene que dejarlo pasar).
- El servidor solo reenvía mensajes: no ejecuta el juego.

## Cómo jugar

1. Todos: menú principal → **RimCoop: Conectar** → IP, puerto y nombre → *Conectar*, o *Servidores guardados...*: pestaña
   **Guardados** (apodo + IP + puerto, favoritos primero, y por cada uno cuánta gente hay, el ping, el nombre de la partida y la
   seed, actualizado solo cada 15 s) y pestaña **LAN** (busca sola los servidores de tu red local, sin tipear la IP).
2. Crear un mundo nuevo: se usa la seed del servidor. Elegí tu sitio; ya ves las bases de los demás.
3. Al fundar, tu colonia aparece en el mapa mundial de los otros.
4. Clic en la base de otro jugador: *Entrar a la base*, *Colaborar*, *Comerciar*, *Atacar*.

## Compilar

Requiere .NET SDK y RimWorld instalado. Los `.csproj` referencian `D:\RimWorld\RimWorldWin64_Data\Managed\`;
ajustá esa ruta a tu instalación.

```
dotnet build Source/RimCoopMod.csproj -c Release   # el mod (Assemblies/RimCoopMod.dll)
dotnet build server/RimCoopServer.csproj -c Release # el servidor
```

## Compatibilidad con DLC

Probado con **Royalty, Ideology, Biotech, Anomaly y Odyssey** activos. El mod usa solo lo que el juego expone
en cada caso y se adapta a los DLC que tengas:

- Al conectarse, cada jugador manda su lista de DLC/mods; si no coinciden, el mod avisa exactamente qué falta
  de cada lado (lo que dependa de eso puede no aparecer del otro lado). **Conviene que todos usen los mismos DLC y mods.**
- **Biotech:** se copia la contaminación del terreno, los xenogenes y el xenotipo de los colonos, los recursos de genes
  (hemógeno), el crecimiento de los niños (edad biológica, cambio de etapa de vida, puntos de crecimiento, necesidades de
  aprendizaje y juego) y los embarazos (como cualquier otra condición de salud; el parto solo lo dispara el dueño real).
  Los mechs se ven en el mapa espejo con su batería (energía), y en su panel se ve el supervisor y el modo de trabajo; el
  mecanitor muestra su ancho de banda y cuántos mechs tiene a cargo. El vínculo real mecanitor-mech no cruza entre partidas
  (es un objeto de la partida del dueño), así que los grupos de control y el modo de trabajo solo se pueden cambiar desde
  el juego del dueño.
- **Ideology:** se copia el estilo visual de muebles, edificios y ropa, y el nombre de la ideología de
  cada colono se muestra en su panel de inspección (el objeto Ideo en sí es de la partida del dueño y
  no cruza entre juegos, así que no se sincronizan sus preceptos ni se puede "editarla" desde el espejo).
- Los trabajos que dependen de rituales/ceremonias no se imitan en el mapa espejo (se ve el resultado, no el ritual).
- **Odyssey:** "mi base" es siempre el asentamiento de la superficie que se avisó al servidor, no cualquier mapa propio;
  así una nave gravitatoria que despega (y crea mapas nuevos) no confunde lo que ven los demás. Si la nave **muda la
  base a otro tile** (y abandona la anterior), se re-avisa la ubicación nueva: los demás ven el punto moverse y, si la estaban
  mirando, el mapa espejo viejo se descarta y se vuelve a entrar solo al lugar nuevo (la cámara los sigue). Las naves y colonias extra en otros mapas
  (órbita, otros tiles) **no se espejan** como mapa, y el vacío tampoco; se resumen como texto en el panel de la base
  ("Odyssey: 1 nave(s) gravitatoria(s), 2 mapa(s) propio(s) más (1 en órbita)"). El **motor gravitatorio no se espeja**
  como edificio: el juego decide qué mapas son "base propia" mirando si hay un motor (sin mirar la facción), y uno
  espejado haría que el mapa espejo cuente como base tuya (incidentes apuntándole, colonos contados de más).
- **Anomaly:** las entidades contenidas en plataformas de contención se ven en el mapa espejo, con su nivel de actividad,
  su progreso de estudio y su modo de contención (los textos del panel salen solos de los componentes del propio
  títere), y los colonos que se vuelven mutantes (ghoul) se actualizan. El estudio de estructuras también se copia.
  Los ghouls/shamblers ajenos no aparecen en tu barra de colonos. El **monolito real no se espeja** como edificio
  (registraría su propia instancia como el monolito de tu partida y rompería el tuyo): su nivel se ve como texto
  en el panel de la base del jugador. Los incidentes de Anomaly llegan como cartas, igual que el resto.
- **Royalty:** se copian y se mantienen al día los psicasts (habilidades), el enfoque psíquico, el calor neural, los títulos
  con su favor, los herederos y los permisos. Los psicasts y los permisos que uses con un colono tuyo en el mapa espejo
  los ejecuta el dueño en su base real. Las naves de transporte posadas y los árboles de anima se ven como cualquier
  edificio o planta. **Misiones compartidas:** en las bases de jugadores con los que colaboras, *Compartir misión* invita
  a una de tus misiones en curso. Quien se suma recibe la misión **entera** (nombre, descripción, objetivos, partes,
  tiempos y estado, siempre al día) y la ve idéntica en su pestaña de misiones; la copia no "juega" por su cuenta:
  la corre solo el dueño. Los objetos de recompensa y el favor real que da la misión llegan a **todos** los que se
  sumaron, y las incursiones y amenazas que dispara suben un **35 % por cada jugador sumado**. Los objetivos que
  apuntan a algo del mundo del dueño (un sitio, un mapa, un colono suyo) se ven, pero "ir a verlo" no encuentra el
  objeto en el juego de los demás. Otras recompensas (colonos, cambios de relación con facciones) son solo del dueño.
  Las ceremonias de investidura y otros rituales no se imitan en el mapa espejo (los participantes se ven
  en su posición, sin la animación del ritual), ni la animación de las naves aterrizando o despegando.

## Guardar y cargar

Cada jugador tiene su propia partida. Lo que se guarda con ella: los colaboradores, las misiones compartidas, quién es
dueño de cada colono que te mandaron, las **ofertas de comercio** (las que mandaste y las que te mandaron y no respondiste),
las **invitaciones a misiones** pendientes y el vínculo de las **naves comerciales compartidas**. Al cargar, las bases de los
demás y sus mapas espejo se limpian y se vuelven a pedir al servidor, y los ids de jugador son estables por nombre (también
entre reinicios del servidor). Lo pendiente se vuelve a mostrar (o se retoma) apenas el otro jugador está conectado; una oferta
o invitación se puede dejar para más tarde con *Decidir después* (Esc no la descarta), y no se puede aceptar si quien la mandó
está desconectado (su respuesta se perdería).

- **Colonos enviados entre jugadores (sin duplicados ni pérdidas):** un colono enviado con *Colaborar* existe en UNA sola
  partida a la vez, pero cada jugador guarda la suya. El **servidor recuerda quién tiene cada colono** (con una copia de cuando
  se mandó, en `ServerData/<partida>/colonists.bin`) y al conectarse cada jugador la reconcilia: si cargaste una partida de
  antes de mandarlo, la copia duplicada que tenías en casa se saca; si cargaste una de antes de recibirlo, se reconstruye desde
  la copia del servidor. Un colono que murió (o se fue del mapa) se da de baja y no se "revive". Para que esto proteja una
  partida hay que **guardarla una vez con esta versión** antes de mandar colonos; los colonos prestados antes de esta versión
  siguen sin seguimiento. *Guardar todos* (botón en el chat) sigue siendo buena idea. Desde la consola del servidor, `colonos`
  muestra el registro y `colono borrar <id>` destraba algo a mano.
- Las ofertas de comercio expiran a los 30 días de juego.

## Comercio con naves comerciales (Imperio y otros)

Cuando le llega una nave comercial orbital a un jugador, sus colaboradores conectados la reciben también con el mismo stock.
Cada uno comercia con sus colonos y su plata desde su consola de comunicaciones; si alguien compra o vende, el stock cambia
en todos, y cuando la nave se va, se va de todos. Los esclavos y animales a la venta no viajan. Las demás
comunicaciones con el Imperio (relaciones, pedidos de ayuda por la consola) siguen siendo de cada jugador.

## Limitaciones conocidas

- No hay simulación determinista compartida: fecha/hora, investigación y riqueza son de cada partida.
- Los eventos aleatorios ocurren en el juego de su dueño. Quien mira esa base los ve también en el mapa espejo (enemigos, plagas de cultivo, clima y condiciones como eclipse o lluvia tóxica), pero no le afectan a su colonia principal.
- Copiar colonos entre juegos usa el sistema de guardado de RimWorld y deja errores en el log
  (edad, ideología, adicciones) que no impiden jugar.
- El servidor confía en lo que mandan los clientes (sin anti-trampas) y no hay autenticación.
- Solo probado en RimWorld 1.6 con el juego base.

## Licencia

**Todos los derechos reservados.** No es software de código abierto: el código se publica solo para
que pueda leerse, y las versiones compiladas de *Releases* se pueden usar para jugar (uso personal, no
comercial). No se puede copiar, redistribuir, modificar ni usar comercialmente sin permiso del autor.
Ver [LICENSE](LICENSE).
