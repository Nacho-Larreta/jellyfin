# Jellyfin Server

Backend server principal del ecosistema Jellyfin.

## Rol

- Expone la API y el websocket.
- Maneja auth, authorization, sesiones, API keys y Quick Connect.
- Persiste datos del server.
- Puede hostear `jellyfin-web/dist` si se ejecuta con contenido web disponible.

## Stack

- .NET SDK `10.0.x`
- ASP.NET Core
- EF Core
- SQLite
- analyzers: StyleCop, BannedApiAnalyzers, SerilogAnalyzer, SmartAnalyzers, IDisposableAnalyzers

## Setup local

### Prerequisitos

- `dotnet` 10
- `ffmpeg`
- opcional: `jellyfin-web/dist` si queres hostear el web client desde el server
- en este workspace ya existe SDK local encapsulado:
  - `../dotnetw`
  - `./dotnetw`
- el wrapper del workspace exporta `DOTNET_USE_POLLING_FILE_WATCHER=1` porque el provider de config con `reloadOnChange` se cuelga en este macOS host si se usa file watching nativo

### Comandos utiles

```bash
./dotnetw restore Jellyfin.sln
./dotnetw build Jellyfin.sln --configuration Release
./dotnetw test Jellyfin.sln --configuration Release
./dotnetw publish Jellyfin.Server/Jellyfin.Server.csproj --configuration Release --output ../.artifacts/jellyfin-server-publish
./dotnetw run --project Jellyfin.Server --webdir /absolute/path/to/jellyfin-web/dist
./dotnetw run --project Jellyfin.Server --nowebclient
```

### Devcontainer

Hay devcontainer en `.devcontainer/devcontainer.json` con imagen .NET 10, install de ffmpeg y extensiones recomendadas.

## Archivos para orientarse rapido

- `Jellyfin.Server/Program.cs`
- `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`
- `Emby.Server.Implementations/ApplicationHost.cs`
- `Emby.Server.Implementations/HttpServer/Security/AuthService.cs`
- `src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite/SqliteDatabaseProvider.cs`
- `tests/`

## Auth y sesiones

- La auth entra por el esquema custom definido en `ApiServiceCollectionExtensions`.
- Las policies de authorization se definen ahi mismo.
- `AuthService` valida token, estado autenticado y usuario deshabilitado.
- `ApplicationHost` registra `IQuickConnect`.
- El backend tambien distingue permisos por policy, claims y API key.

## Persistencia y base de datos

- El provider principal del repo es SQLite.
- `SqliteDatabaseProvider` arma la connection string y por default usa `jellyfin.db` en el `DataPath`.
- El provider implementa tareas de optimizacion, backups rapidos y restore.
- Hay migraciones tanto en la capa de DB como en `Jellyfin.Server/Migrations`.
- No depende de Postgres, MySQL, Redis, RabbitMQ ni otros servicios externos para arrancar.
- El "seed" real del sistema nuevo no viene de fixtures SQL: nace del startup wizard, la creacion del primer usuario y migraciones/config defaults como plugin repo y cast receivers.

## Integracion con otros repos

- `jellyfin-web` puede ser hosteado por este repo usando `--webdir`.
- `jellyfin-webos` consume manifest/public system info del server y despues delega al web UI hosteado.
- `jellyfin-tizen` y `jellyfin-androidtv` consumen su API.
- Si queres controlar Docker/NAS como artefacto principal, este repo solo no alcanza: ahi entra `jellyfin-packaging`.
- Si se cambia auth o Quick Connect, revisar clientes web y Android TV.

## CI/CD

- `ci-tests.yml`: corre `dotnet test Jellyfin.sln` en Ubuntu, macOS y Windows.
- `ci-codeql-analysis.yml`: analisis de seguridad.
- `ci-compat.yml`: checks de compatibilidad.
- workflows de OpenAPI: generan, comparan y publican el spec.
- El repo no incluye un Dockerfile oficial del server; solo hay assets de deployment como el template de unRAID.
- La distribucion oficial del contenedor `jellyfin/jellyfin` existe, pero el source of truth de packaging fue movido a `jellyfin-packaging`.

## Dockerizacion y deployment manual

- En este repo no hay pipeline oficial para construir la imagen Docker del server.
- Para uso personal en NAS conviene forkear/clonar `jellyfin-packaging` y versionar ahi tu build Docker custom.
- Si queres deploy manual desde source:
  - compilar el server
  - preparar `jellyfin-web/dist` por separado si queres UI hosteada
  - mover el binario y config/media paths al host destino
- Hay referencia de deployment bajo `deployment/unraid/docker-templates/`.
- Estrategia recomendada si queres independencia de Jellyfin oficial:
  - mantener `jellyfin` como fork de codigo fuente
  - mantener `jellyfin-web` como fork del frontend
  - sumar `jellyfin-packaging` para generar tu propia imagen y tags Docker

## Calidad y tests

- `TreatWarningsAsErrors=true` en `Directory.Build.props`.
- `.editorconfig` y `stylecop.json` estan activos.
- Hay 16 proyectos de test bajo `tests/`.
- El repo tiene una base de calidad bastante mas madura que los wrappers de TV.

## Smoke check realizado en este host

- Se instalo `.NET 10.0.203` de forma local en `../.dotnet` y se encapsulo con `./dotnetw`.
- `./dotnetw restore Jellyfin.sln`: OK
- `./dotnetw build Jellyfin.sln --configuration Release --no-restore`: OK
- `./dotnetw publish Jellyfin.Server/Jellyfin.Server.csproj --configuration Release --output ../.artifacts/jellyfin-server-publish`: OK
- Artefacto publicado verificado en `../.artifacts/jellyfin-server-publish` (~206 MB).
- `./dotnetw test Jellyfin.sln --configuration Release --no-build`: OK
  - solucion completa en verde
  - solo quedaron skips esperados en suites multiplataforma e integración
  - el hang previo quedo aislado al provider de configuración con file watching nativo; con el wrapper local pasa completo
- Smoke de runtime framework-dependent publicado:
  - el `publish/` responde correctamente si se ejecuta via `./dotnetw ./jellyfin.dll ...`
  - `GET /health` devolvio `Degraded`
  - `GET /System/Info/Public` devolvio metadata valida del server con `startupWizardCompleted=false`
  - el binario `./jellyfin` no es self-contained: si lo corres directo sin `DOTNET_ROOT`, no encuentra el runtime local
- Probe local de bootstrap:
  - `Program.CreateAppConfiguration(...)` se cuelga en este host cuando `AddJsonFile(..., reloadOnChange: true)` usa file watching nativo
  - `DOTNET_USE_POLLING_FILE_WATCHER=1` destraba el bootstrap sin tocar source

## Riesgos y notas

- El README todavia habla de .NET 9, pero `global.json` fija .NET 10.
- El server depende de `ffmpeg` instalado.
- La UI web no vive en este repo; si falta `dist/`, el modo hosteado no arranca bien.
- `build Release` de la solucion no deja listo un `publish --no-build` del server: dos proyectos (`Jellyfin.MediaEncoding.Hls` y `Jellyfin.MediaEncoding.Keyframes`) quedan fuera del output esperado si no recompilas en publish.
- El baseline local queda funcional solo si el entorno usa `DOTNET_USE_POLLING_FILE_WATCHER=1`; sin eso, el bootstrap puede colgarse antes de inicializar Kestrel o los integration tests.
