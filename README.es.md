# Titulo

StoryLocationsReset

## Descripcion Corta

Un mod server-side para Vintage Story que ayuda a administradores a reiniciar localizaciones de historia generadas.

## Descripcion Larga

`StoryLocationsReset` es un mod auxiliar server-side para Vintage Story 1.22.x.

Escanea las regiones generadas del mapa buscando codigos conocidos de estructuras de historia y puede regenerar el rango de chunks alrededor de esas localizaciones. El objetivo es permitir que servidores multijugador refresquen contenido de historia para que mas de un grupo de jugadores pueda vivirlo con el paso del tiempo.

El mod esta pensado para actuar con cautela:

- Solo se ejecuta en el servidor
- Los clientes no necesitan instalarlo
- Usa los propios comandos de worldgen de Vintage Story para regenerar
- Puede saltarse reinicios si hay jugadores cerca de la localizacion
- Puede evacuar opcionalmente al spawn de cada jugador a quienes esten dentro del rango exacto de chunks a regenerar
- Tambien puede evacuar jugadores al reconectar si su posicion guardada cae dentro de una zona de historia reiniciada recientemente
- Cada localizacion tiene valores configurables `enabled` y `maxInstancesToReset`
- El reinicio automatico al arrancar el servidor es configurable y viene desactivado por defecto

Este mod debe tratarse como una ayuda de automatizacion, no como una garantia de reinicio completo de todos los estados internos posibles de historia o misiones.

## Instrucciones de Uso

1. Instala el mod en el servidor.
2. Arranca el servidor una vez para generar `ModConfig/storylocationsreset.json`.
3. Revisa la configuracion con cuidado antes de activar reinicios automaticos.
4. Usa `storylocationsreset.template.jsonc` como referencia comentada para ver las opciones disponibles y notas de seguridad.
5. Usa `/storyreset scan` para descubrir estructuras de historia configuradas.
6. Usa `/storyreset list` para listar localizaciones descubiertas.
7. Usa `/storyreset reset resonancearchive` para reiniciar un codigo concreto de localizacion.
8. Usa `/storyreset reset all` para reiniciar todas las localizaciones configuradas y activadas.

## Comandos

`/storyreset reload`

Recarga `ModConfig/storylocationsreset.json` sin reiniciar el servidor.

Usalo despues de editar la configuracion.

`/storyreset scan`

Escanea las regiones generadas del mapa y actualiza la lista interna del mod con las localizaciones de historia configuradas.

Este comando no reinicia nada. Es un comando seguro de descubrimiento.

`/storyreset list`

Primero escanea y despues muestra las localizaciones de historia configuradas que se han encontrado en las regiones generadas del mapa.

Usalo antes de subir `maxInstancesToReset`, activar una nueva localizacion o activar reinicios al arrancar el servidor.

`/storyreset reset <codigo>`

Primero escanea y despues reinicia las instancias encontradas para un codigo concreto de localizacion configurada.

Ejemplo: `/storyreset reset resonancearchive`

`/storyreset reset all`

Primero escanea y despues reinicia todas las localizaciones configuradas donde `enabled` sea `true`, respetando `maxInstancesToReset` y el radio de seguridad de jugadores.

Usalo con cuidado.

## Tengo que ejecutar scan o list manualmente?

No. Los comandos manuales de reinicio y los reinicios automaticos al arrancar escanean internamente antes de hacer nada.

Aun asi, `/storyreset scan` y `/storyreset list` son muy recomendables como comprobaciones de seguridad:

- Antes del primer reinicio manual en un mundo
- Antes de activar `runOnServerStart`
- Antes de activar codigos adicionales de localizacion
- Antes de poner `maxInstancesToReset` por encima de `1`
- Siempre antes de tocar `treasurehunter`

Para reinicios automaticos al arrancar, no necesitas ejecutar `/storyreset scan` manualmente en cada reinicio. El mod escanea en tiempo de ejecucion antes de aplicar el reset.

## Comportamiento de Evacuacion de Jugadores

Por defecto, el mod salta un reinicio si hay jugadores cerca o dentro del area a regenerar.

Si quieres que los reinicios ocurran incluso cuando haya jugadores dentro del rango exacto de chunks a regenerar, configura:

```json
{
  "skipIfPlayersNearby": false,
  "evacuatePlayersInsideResetAreaToSpawn": true
}
```

Cuando este modo esta activo:

- Los jugadores dentro del rango exacto de chunks a reiniciar son movidos antes de empezar la regeneracion.
- El destino normal es el spawn resuelto del propio jugador.
- Si ese spawn resuelto tambien esta dentro del area de reinicio, se borra el spawn personal del jugador.
- Tras borrar un spawn personal inseguro, el jugador es movido al spawn global del mundo.

Este comportamiento es una proteccion anti-abuso intencionada. Evita que un jugador bloquee el reinicio de una localizacion de historia quedandose acampado dentro o poniendo su spawn personal en esa zona.

Los administradores deberian avisar claramente de esta regla a los jugadores antes de activar reinicios automaticos.

Evacuacion al reconectar:

- Cuando `evacuatePlayersOnJoinFromRecentlyResetAreas` esta en `true`, el mod recuerda los rangos de chunks reiniciados recientemente.
- Si un jugador reconecta dentro de uno de esos rangos, se le mueve al spawn usando las mismas reglas de spawn seguro.
- Los registros se conservan durante `recentResetAreaRetentionHours` y se limpian automaticamente.
- Esto protege a jugadores que cerraron sesion dentro de una localizacion de historia antes de que ocurriera un reinicio del servidor.

Modo peligroso sin proteccion:

```json
{
  "skipIfPlayersNearby": false,
  "evacuatePlayersInsideResetAreaToSpawn": false
}
```

Con esta combinacion, los reinicios pueden ejecutarse mientras haya jugadores dentro del area regenerada. Esto puede atraparlos, hacerlos caer, causar desincronizacion o interrumpir su partida. Si un servidor usa esta politica, los administradores deberian avisarlo claramente a los jugadores.

Importante:

- Haz copia de seguridad del mundo antes de usar este mod.
- La operacion de reinicio regenera rangos de chunks.
- Los jugadores cerca de una localizacion configurada pueden bloquear el reinicio si `skipIfPlayersNearby` esta activado.
- El rango de chunks a regenerar se calcula a partir de la caja real de la estructura generada.
- `evacuatePlayersInsideResetAreaToSpawn` viene desactivado por defecto. Activalo solo si prefieres mover jugadores en lugar de saltar el reinicio cuando esten dentro del area exacta regenerada.
- Normalmente los jugadores se mueven a su propio spawn. Si su spawn resuelto esta dentro del area de reinicio, se borra su spawn personal y se usa el spawn global del mundo como alternativa.
- `treasurehunter` puede existir muchas veces en un mundo, asi que viene desactivado y limitado a `maxInstancesToReset: 0` por defecto.
- Subir `maxInstancesToReset` por encima de `1` puede lanzar varias operaciones `/wgen regenrange` en un solo comando o arranque del servidor. Usa valores altos solo despues de revisar `/storyreset list`.
- Ten especial cuidado con `treasurehunter`: activarlo sin un limite estricto de instancias puede regenerar muchas localizaciones descubiertas.

Compatibilidad:

- Vintage Story `1.22.x`

## Changelog 1.0.0

- Primer prototipo server-side
- Anadidos codigos configurables de localizaciones de historia
- Anadidos valores por localizacion `enabled` y `maxInstancesToReset`
- Limitado `treasurehunter` a cero reinicios por defecto porque puede tener muchas instancias
- Anadida advertencia clara de seguridad para reinicios multi-instancia
- Anadida plantilla de configuracion comentada
- Anadidos comandos de escaneo, listado, recarga y reinicio
- Anadido reinicio opcional al arrancar el servidor
- Anadida comprobacion de seguridad por proximidad de jugadores
- Anadida evacuacion opcional al spawn para jugadores dentro del area exacta a regenerar
- Se borran los puntos de spawn personales dentro del area de reinicio y se usa el spawn global como alternativa
- Anadida evacuacion al reconectar desde areas reiniciadas recientemente
- Anadido pequeno estado persistente de areas reiniciadas con limpieza automatica por retencion
- Cambiado el calculo del area de reinicio para usar directamente la caja real de la estructura generada
- Usa internamente `/wgen story removeschematiccount` y `/wgen regenrange`
