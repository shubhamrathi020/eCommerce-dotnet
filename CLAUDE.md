# eCommerce .NET API: Claude Instructions

The ASP.NET Core implementation of the shop API. It must answer exactly like `eCommerce-api` (NestJS), which owns the PostgreSQL schema
and the integration specs. Solo project; the user pushes (never run `git push`).

## Sibling repositories
- `eCommerce`: Angular apps, requirements (`brds/`), project log, steering docs. Read `steering/rules.md` there before a big task.
- `eCommerce-api`: NestJS backend, the source of truth for behaviour, schema and specs.
- `eCommerce-go`, `eCommerce-node`: the other implementations of the same API. `eCommerce-go/testdata` holds the golden rules file.
- `eCommerce-contracts`: `@ecom/contracts`, shared types and pure rules (TypeScript). `src/EcomApi/Domain` is its C# port, held to
  `golden.json`; a rule changed there must be re-ported and the golden file regenerated (`node testdata/gen/gen.mjs` in `eCommerce-go`).
- `eCommerce-react`: React apps that can talk to any backend.

## Rules of this repository
- Behaviour parity beats idiom. When Nest does something odd (statuses, messages, cookie formats, header names), copy it.
- The entities describe tables Prisma created; never add a migration here. Timestamps are naive UTC (`timestamp(3)`), columns are camelCase.
- A new route needs the same guard order as Nest: blanket rate limit, bucket limit, authentication, CSRF, body validation, handler.
  Guards are resource filters (they run before model binding); `ValidateModelFilter` turns any binding problem into the one generic 400.
- Request DTOs use `required` members, `[MaxLength]`, `[OneOf]` and the strict JSON options (unknown properties are refused).
- Commit after each finished unit. Never push. Files containing apostrophes in shell heredocs break the tool; use the editor tools.

## Commands
- The SDK lives in `%USERPROFILE%\.dotnet` (not on PATH): `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`.
- `dotnet test -c Release` runs the 21 tests. `dotnet build -c Release` makes `src/EcomApi/bin/Release/net10.0/EcomApi.exe`.
- The shared specs: `API_BIN=<path to EcomApi.exe> pnpm exec vitest run test/` from `eCommerce-api/apps/api` (needs the Docker
  infrastructure from `eCommerce-api` running). Specs run one file at a time against one database. Set `ECOM_LOG_FILE=<path>` to also
  append the server's log to a file when the harness hides its output. Stop a leftover server by image name (`taskkill /IM EcomApi.exe`).

## Lessons
- Npgsql rejects `DateTime` of the wrong kind: values to `timestamp` columns must be `Unspecified` (EF value converters do this);
  in hand-written SQL use `timezone('UTC', now())`.
- EF Core makes a new service provider per `DbContext` if the options differ each time (a `new` translator in `MapEnum`): use statics.
- MongoDB's driver maps a property called `Id` to `_id`; embedded documents with an `id` field need `[BsonElement("id")]`.
- Express answers HEAD from GET routes, lower-case matches routes and adds a weak ETag to every body; `UseExpressSemantics` does the
  same, and `UseRouting` must come after it so routing sees the rewritten method.
- A native MongoDB on 127.0.0.1 shadows the Docker one for `localhost` in .NET (Node resolves `::1` first); use `[::1]`.
