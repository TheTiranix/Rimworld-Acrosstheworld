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

- Puerto por defecto: **34500** (TCP). Si jugás por internet, abrí/redirigí ese puerto.
- La seed y la configuración quedan en `ServerData/` junto al `.exe`. Escribí `salir` para apagarlo.
- El servidor solo reenvía mensajes: no ejecuta el juego.

## Cómo jugar

1. Todos: menú principal → **RimCoop: Conectar** → IP, puerto y nombre → *Conectar*.
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
