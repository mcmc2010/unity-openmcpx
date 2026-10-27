#!/bin/sh
# MCPX CLI: thin curl wrapper around the Unity-side HTTP server.
#
# Usage:
#   sh mcpx.sh status
#   sh mcpx.sh hierarchy 'depth=2'
#   sh mcpx.sh object 'path=Main%20Camera'
#   sh mcpx.sh components 'path=Main%20Camera&depth=2'
#   sh mcpx.sh find 'name=Player'
#   sh mcpx.sh logs 'count=50'
#   sh mcpx.sh selftest          # run all routes, report pass/fail
#
# Write (modifies the open scene, Undoable):
#   sh mcpx.sh setprop 'path=Field/Popkart/Particle%20System&component=ParticleSystem&property=...&value=...'
#   sh mcpx.sh rename 'path=UI/Joystick/0&name=Stick'
#   sh mcpx.sh delete 'path=UI/Joystick/Tmp'
#   sh mcpx.sh menu 'item=Tools/MCPX/Setup%20Exhaust%20Smoke%20(Popkart)'   # Tools/MCPX/ items only
#
# setprop value formats: number/bool/string; enum name or index; vectors and colors "a,b,c[,d]";
# object references: asset path ("Assets/..."), scene object ("Scene:Root/Child"), or "null" to clear.
#
# After editing .cs files, compile without focusing the Unity window:
#   sh mcpx.sh compile      # triggers compile, waits, prints CS errors (exit 1 if any)
#
# Create a primitive (Undoable):
#   sh mcpx.sh create 'parent=Field&name=Floor&primitive=Plane&position=0,0,0&scale=5,1,5'
#
# Add a component to an existing object (Undoable):
#   sh mcpx.sh addcomponent 'path=Field/Popkart/CameraMount&type=Camera'
#

# Query strings must already be URL-encoded (spaces -> %20).
# Exit code is 0 on HTTP 200, 1 otherwise (connection error or route error).
# Set MCPX_PORT to override the default port (8765).

PORT="${MCPX_PORT:-8765}"
BASE="http://127.0.0.1:$PORT"

# call <route> [query]: prints body to stdout, returns 0 only on HTTP 200.
# Write routes (setprop, menu) use POST.
call() {
  url="$BASE/$1${2:+?$2}"
  method=GET
  case "$1" in setprop|menu|compile|create|addcomponent|rename|delete) method=POST ;; esac
  # -d '' sends Content-Length: 0; HttpListener rejects POST without it (411).
  out=$(curl -sS -X "$method" -d '' -w '\n%{http_code}' "$url") || return 1
  code=$(printf '%s' "$out" | tail -n 1)
  printf '%s\n' "$out" | sed '$d'
  [ "$code" = "200" ]
}

selftest() {
  fail=0
  # Non-mutating checks only. Each spec: route|query|expected(ok|fail).
  # "fail" cases must return non-200.
  for spec in \
    "status||ok" \
    "hierarchy|depth=1|ok" \
    "find|name=Main|ok" \
    "object|path=Main%20Camera|ok" \
    "components|path=Main%20Camera&depth=2|ok" \
    "logs|count=5|ok" \
    "object|path=__no_such_object__|fail" \
    "bogus||fail" \
    "setprop|path=Main%20Camera|fail" \
    "rename|path=Main%20Camera|fail" \
    "delete|path=__no_such_object__|fail" \
    "menu|item=Edit/Play|fail" \
    "compile||ok"; do
    r=$(echo "$spec" | cut -d'|' -f1)
    q=$(echo "$spec" | cut -d'|' -f2)
    want=$(echo "$spec" | cut -d'|' -f3)
    if call "$r" "$q" >/dev/null 2>&1; then got=ok; else got=fail; fi
    if [ "$got" = "$want" ]; then
      echo "PASS  $r ${q:+($q)}"
    else
      echo "FAIL  $r ${q:+($q)} (expected $want, got $got)"
      fail=1
    fi
  done
  return $fail
}

# compile: request a script compile, wait for it to finish, report CS errors
# that were logged after the request.
compile() {
  out=$(call compile) || { echo "$out" >&2; return 1; }
  since=$(printf '%s' "$out" | sed -n 's/.*"sinceId":\([0-9]*\).*/\1/p')
  sleep 1
  waited=0
  while :; do
    st=$(call status 2>/dev/null)
    case "$st" in *'"isCompiling":false'*) break ;; esac
    waited=$((waited + 1))
    if [ "$waited" -ge 120 ]; then
      echo "timeout: Unity still compiling after 120s" >&2
      return 1
    fi
    sleep 1
  done
  logs=$(call logs "count=300&since=${since:-0}") || return 1
  errs=$(printf '%s' "$logs" | grep -o 'error CS[^"\\]*' | sort -u)
  if [ -n "$errs" ]; then
    printf '%s\n' "$errs"
    echo "COMPILE FAILED"
    return 1
  fi
  echo "compile ok"
}

route="$1"
query="$2"

if [ -z "$route" ]; then
  echo "usage: sh mcpx.sh <status|hierarchy|object|components|find|logs|setprop|menu|compile|selftest> [query]" >&2
  exit 1
fi

if [ "$route" = "selftest" ]; then
  selftest
  exit $?
fi

if [ "$route" = "compile" ]; then
  compile
  exit $?
fi

call "$route" "$query"
