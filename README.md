# MCPX

**A tiny HTTP bridge to the live Unity Editor — inspect and modify the running scene from curl, scripts, or AI agents.**

MCPX runs a small HTTP server inside the Unity Editor on `127.0.0.1:8765`. Query the scene hierarchy, dump component properties, read console logs, edit values, spawn primitives, trigger script compiles — all through plain HTTP with JSON responses. Designed so that a shell script (or an LLM agent) can drive the editor without ever focusing the Unity window.

- **Zero dependencies.** Three C# files and a POSIX shell wrapper. No packages, no external libraries.
- **Read & write.** Queries *and* scene modifications, every write routed through Unity's Undo system (Ctrl/Cmd+Z reverts any change).
- **Agent-friendly.** Deterministic JSON, meaningful HTTP status codes, URL-encoded query params, exit codes that mean something.
- **Safe by default.** Listens on localhost only; mutating routes require POST; menu execution is restricted to a `Tools/MCPX/` sandbox.
- **Small.** ~1,100 lines total, readable top to bottom.

Tested with Unity 2022.3 LTS (uses only long-stable editor APIs: `HttpListener`, `SerializedObject`, `Undo`, `EditorApplication`).

## Why MCPX? — and when to use a full MCP server instead

MCPX is the second take at driving Unity from the outside. The first was [Unity MCP Server](https://gitee.com/unreal2012/unity-mcp-server) (same author), a complete [MCP](https://modelcontextprotocol.io)-compliant server with 29 high-level tools, natural-language control from Claude / Cursor / CodeBuddy, and coverage far beyond the scene: uGUI building, prefabs, assets, script files, playmode, screenshots, even player builds. It works well — but it is a *system*: a Node.js bridge process, a TCP hop into Unity, npm install/build steps, and per-IDE MCP configuration.

MCPX is the opposite bet: **the smallest thing that can drive the editor**. No protocol, no runtime, no packages — three C# files that answer HTTP, plus a curl wrapper. If an agent can run a shell command, it can drive the editor; no MCP client required.

**Prefer MCPX when:** you work in a terminal or agent loop, you want zero dependencies, you script the editor (compile loops, batch scene tweaks, CI smoke tests), you want precise `SerializedProperty`-level access, or you intend to read and hack on the tool itself.

**Prefer [Unity MCP Server](https://gitee.com/unreal2012/unity-mcp-server) when:** you want the complete MCP experience — tools auto-discovered by your AI IDE, pure natural-language control, and the high-level domains MCPX doesn't attempt (UI widgets, prefabs, assets, script CRUD, playmode, screenshots, builds). If you need *full MCP*, use that one.

| | MCPX | [Unity MCP Server](https://gitee.com/unreal2012/unity-mcp-server) |
|---|---|---|
| Interface | Plain HTTP/JSON, `127.0.0.1:8765` | MCP (stdio) → TCP bridge → Unity (`:8080`) |
| Dependencies | None (3 C# files + `curl`) | Node.js 18+, npm, Unity UPM package |
| Setup | Copy folder into `Assets/Editor/` | UPM install + MCP config per IDE |
| Surface | 13 focused routes | 29 high-level tools |
| Property access | Any `SerializedProperty` path | Curated property setters |
| Scope | Scene inspect/edit, compile, logs | + UI, prefabs, assets, scripts, playmode, screenshots, builds |
| MCP-native | No — anything that speaks HTTP works | Yes — Claude / Cursor / CodeBuddy … |
| Size | ~1,100 lines, read in one sitting | Full TypeScript project |

**MCPX pros:** zero dependencies; tiny, auditable, trivially forkable; usable from curl, CI, or any agent harness — not just MCP clients; exact serialized-property-level reads and writes; every write Undoable; meaningful exit codes for scripting.

**MCPX cons:** not MCP-native, so clients need the API described to them (e.g. via `AGENTS.md`) instead of tool auto-discovery; no high-level domain tools (no prefabs, assets, UI builders, builds, playmode, screenshots, scene save/load); serialized property paths are internal names and can shift between Unity versions; single active scene.

**Unity MCP Server pros:** native tool discovery in AI IDEs and natural-language-first workflow; broad coverage (29 tools: GameObjects, transforms, components, uGUI, prefabs, assets, scripts, builds, playmode, screenshots); UPM one-line install; LAN-ready design; larger maintained codebase with tests.

**Unity MCP Server cons:** requires a Node.js runtime and a separate bridge process to keep alive; more moving parts (TCP + reconnect + config files) to install and debug per IDE; heavier to read or modify; protocol overhead for trivial tasks like reading a single property.

## Install

Copy the `MCPX/` folder into your project anywhere under an `Editor/` folder, e.g.:

```
Assets/Editor/MCPX/
├── MCPXServer.cs     # HTTP listener + main-thread dispatch + log ring buffer
├── MCPXRoutes.cs     # read routes + JSON serialization
├── MCPXWrite.cs      # write routes (all Undoable)
└── mcpx.sh           # thin curl CLI wrapper
```

That's it. On editor load, the server starts automatically and logs:

```
[MCPX] listening on http://127.0.0.1:8765/
```

There is also a `Tools/MCPX/Restart Server` menu item if you ever need to bounce it.

## Quick start

```sh
sh Assets/Editor/MCPX/mcpx.sh status                    # is the editor alive?
sh Assets/Editor/MCPX/mcpx.sh hierarchy 'depth=2'       # what's in the scene?
sh Assets/Editor/MCPX/mcpx.sh find 'name=Player'        # locate an object
sh Assets/Editor/MCPX/mcpx.sh object 'path=Main%20Camera'
sh Assets/Editor/MCPX/mcpx.sh logs 'count=50'           # recent console output
sh Assets/Editor/MCPX/mcpx.sh selftest                  # exercise every route
```

All responses are JSON. Query strings must be URL-encoded (`space` → `%20`); paths use `Root/Child/Grandchild` form where the root is a scene top-level object.

## CLI

`mcpx.sh` is a thin `curl` wrapper — use it directly, or crib from it when integrating anything else.

```
sh mcpx.sh <route> [query]     # prints the JSON body; exit 0 only on HTTP 200
```

- Write routes (`setprop`, `create`, `addcomponent`, `rename`, `delete`, `menu`, `compile`) are sent as `POST`.
- `MCPX_PORT` overrides the port (default `8765`) if you changed the server constant.
- `sh mcpx.sh selftest` runs every route read-only and prints `PASS`/`FAIL` per case — useful as a smoke test in CI or after upgrading Unity.

### compile — edit scripts without switching windows

```sh
sh mcpx.sh compile
```

Triggers `AssetDatabase.Refresh` + a script compile, polls until Unity finishes (up to 120 s), then prints any `error CS…` diagnostics captured since the request. Exits `1` on compile errors — so an agent can loop: edit C# → `compile` → fix → `compile` until green.

## HTTP API

Base URL: `http://127.0.0.1:8765`

### Read (GET)

| Route | Params | Returns |
|---|---|---|
| `/status` | — | Unity version, data path, active scene, `isPlaying`, `isCompiling`, port |
| `/hierarchy` | `depth` (default 2) | Scene root objects as a tree: name, active state, components, children |
| `/object` | `path` | Name, path, active, tag, layer, local position/euler/scale, component names |
| `/components` | `path`, `type` (optional name filter), `depth` (default 2) | Serialized properties of each component, nested to `depth` |
| `/find` | `name` | Objects whose name contains `name` (case-insensitive, max 100) with paths and scenes |
| `/logs` | `count` (default 50), `since` (id) | Console entries newer than `since`: `id`, `type`, `message`, `stack` |

### Write (POST, all Undoable)

| Route | Params | Effect |
|---|---|---|
| `/setprop` | `path`, `component`, `property`, `value` | Set a serialized property |
| `/create` | `name`, `primitive`, `parent?`, `position?`, `scale?`, `rotation?` | Spawn `Plane/Cube/Sphere/Capsule/Cylinder/Quad/Empty` (local transform under `parent`, or scene root) |
| `/addcomponent` | `path`, `type` | Add a component by simple class name (`Camera`, `Rigidbody`, …) |
| `/rename` | `path`, `name` | Rename a GameObject |
| `/delete` | `path` | Delete a GameObject |
| `/compile` | — | Refresh assets and request a script compile |
| `/menu` | `item` | Execute a menu item — **`Tools/MCPX/…` only** (403 otherwise) |

GET on a write route returns `405`; unknown routes return `404`.

### `setprop` value formats

| Kind | Format | Example |
|---|---|---|
| number / bool / string | literal | `value=0.5`, `value=true` |
| enum | name or index | `value=Add` |
| Vector2/3/4, Quaternion, Rect, Color | comma-separated | `value=1,2,3` |
| object reference | asset path | `value=Assets/Prefabs/Tree.prefab` |
| object reference | scene object | `value=Scene:Field/Popkart` |
| clear reference | — | `value=null` |

`property` is the *serialized* property name, not the C# name — e.g. `InitialModule.startSpeed.scalar`, `m_Iso`. Read `/components` first to discover exact names.

## Examples

```sh
# Change the FOV of the main camera
sh mcpx.sh setprop 'path=Main%20Camera&component=Camera&property=m_FieldOfView&value=60'

# Build a little scene: a floor with a scaled plane
sh mcpx.sh create 'parent=Field&name=Floor&primitive=Plane&position=0,0,0&scale=5,1,5'

# Wire up a camera rig
sh mcpx.sh addcomponent 'path=Field%2FTank&type=Rigidbody'

# Rename / clean up
sh mcpx.sh rename 'path=UI%2FJoystick%2F0&name=Stick'
sh mcpx.sh delete 'path=UI%2FJoystick%2FTmp'

# Run a project-specific setup macro (must live under Tools/MCPX/)
sh mcpx.sh menu 'item=Tools/MCPX/Setup%20Exhaust%20Smoke%20(Popkart)'

# Or talk to it with plain curl
curl 'http://127.0.0.1:8765/logs?count=20&since=100'
```

## Design notes

- **Main-thread safety.** Unity's API is not thread-safe, so requests arrive on a thread-pool thread, get queued, and are executed on the Unity main thread via `EditorApplication.update`. Each request waits up to 10 s (HTTP `504` on timeout).
- **Everything is Undoable.** `Undo.RecordObject`, `Undo.RegisterCreatedObjectUndo`, `Undo.DestroyObjectImmediate`, `Undo.AddComponent` — mistakes are one keystroke away from reverted.
- **Logs.** Console output is captured via `Application.logMessageReceivedThreaded` into a 300-entry ring buffer exposed at `/logs`. Ids are monotonic; pass `since` to poll for new entries (this is how `compile` finds `error CS…` lines).
- **No auth, by design.** The server binds to `127.0.0.1` only and never exposes a network interface. Don't change the bind address unless you know what you're doing — write routes mutate your scene.

## Limitations

- Single active scene only (additive scenes are ignored by path lookup).
- `addcomponent` matches components by simple class name; ambiguous names resolve to the first match.
- `setprop` addresses `SerializedProperty` paths, which are internal-ish names and can change between Unity versions.
- No asset pipeline writes (importing/deleting assets) — this is a *scene* control surface.

## License

MIT — see [LICENSE](LICENSE).
