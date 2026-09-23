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
- **Construir y editar en la base ajena:** planos, zonas, áreas, recetas, interruptores... el dueño lo aplica.
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
- Escribí `salir` para apagarlo.
- El servidor solo reenvía mensajes: no ejecuta el juego.

## Cómo jugar

1. Todos: menú principal → **RimCoop: Conectar** → IP, puerto y nombre → *Conectar* (o *Servidores
   guardados...* para elegir de una lista con apodo y ver cuánta gente hay conectada antes de entrar).
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
- **Biotech:** se copia la contaminación del terreno y los xenogenes de los colonos.
- **Ideology:** se copia el estilo visual de muebles, edificios y ropa, y el nombre de la ideología de
  cada colono se muestra en su panel de inspección (el objeto Ideo en sí es de la partida del dueño y
  no cruza entre juegos, así que no se sincronizan sus preceptos ni se puede "editarla" desde el espejo).
- Los trabajos que dependen de rituales/ceremonias no se imitan en el mapa espejo (se ve el resultado, no el ritual).
- **Odyssey:** "mi base" es siempre el asentamiento de la superficie que se avisó al servidor, no cualquier mapa propio;
  así una nave gravitatoria que despega (y crea mapas nuevos) no confunde lo que ven los demás. Las naves y colonias
  extra en otros mapas (órbita, otros tiles) **no se espejan**, y el vacío tampoco.
- **Anomaly:** las entidades contenidas en plataformas de contención se ven en el mapa espejo.
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

Cada jugador tiene su propia partida. Lo que se guarda con ella: los colaboradores, las misiones compartidas y quién es
dueño de cada colono que te mandaron. Al cargar, las bases de los demás y sus mapas espejo se limpian y se vuelven a
pedir al servidor, y los ids de jugador son estables por nombre (también entre reinicios del servidor).

- **Ojo con los colonos que se mandaron entre jugadores:** un colono enviado con *Colaborar* existe en UNA sola partida. Si
  un jugador carga una partida vieja (de antes de mandarlo o de recibirlo), ese colono puede quedar **duplicado o perdido**.
  Para evitarlo, usá **Guardar todos** (botón en el chat): todos los jugadores guardan a la vez.
- Las ofertas de comercio y las invitaciones a misiones que estaban pendientes se pierden al cargar.
- El vínculo de una nave comercial compartida no se guarda: al cargar, la nave sigue en cada partida pero ya no se sincroniza.

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
