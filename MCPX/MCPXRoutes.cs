using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPX
{
    /// <summary>Routes. All methods run on the Unity main thread.</summary>
    public static class MCPXRoutes
    {
        // GET  /status
        // GET  /hierarchy?depth=2
        // GET  /object?path=Root/Child
        // GET  /components?path=Root/Child&type=Renderer&depth=2
        // GET  /find?name=xxx
        // GET  /logs?count=50
        // POST /setprop?path=..&component=ParticleSystem&property=..&value=..   (write, Undoable;
        //      object refs: "Assets/..." asset path, "Scene:Root/Child" scene object, "null" to clear)
        // POST /rename?path=..&name=..                                           (write, Undoable)
        // POST /delete?path=..                                                   (write, Undoable)
        // POST /menu?item=Tools/MCPX/...                                       (write, MCPX menu only)
        // POST /compile                                                        (refresh assets + request script compile)
        // GET  /logs?count=50&since=ID                                         (entries with id > since)
        public static MCPXResult Dispatch(string method, string route, NameValueCollection q)
        {
            switch (route)
            {
                case "/status": return Status();
                case "/hierarchy": return Hierarchy(q);
                case "/object": return ObjectInfo(q);
                case "/components": return Components(q);
                case "/find": return Find(q);
                case "/logs": return MCPXResult.Ok(MCPXLogBuffer.ToJson(ParseInt(q["count"], 50), ParseLong(q["since"], 0)));
                case "/setprop":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.SetProp(q);
                case "/menu":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.Menu(q);
                case "/compile":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.Compile();
                case "/create":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.Create(q);
                case "/addcomponent":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.AddComponent(q);
                case "/rename":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.Rename(q);
                case "/delete":
                    if (method != "POST") return MCPXResult.Error(405, "use POST");
                    return MCPXWrite.Delete(q);
                default: return MCPXResult.Error(404, "unknown route: " + route);
            }
        }

        // ---------- routes ----------

        static MCPXResult Status()
        {
            var scene = SceneManager.GetActiveScene();
            var json = "{"
                + "\"unityVersion\":" + MCPXJson.Str(Application.unityVersion)
                + ",\"dataPath\":" + MCPXJson.Str(Application.dataPath)
                + ",\"scene\":" + MCPXJson.Str(scene.path)
                + ",\"isPlaying\":" + MCPXJson.B(EditorApplication.isPlaying)
                + ",\"isCompiling\":" + MCPXJson.B(EditorApplication.isCompiling)
                + ",\"port\":" + MCPXServer.Port
                + "}";
            return MCPXResult.Ok(json);
        }

        static MCPXResult Hierarchy(NameValueCollection q)
        {
            int depth = ParseInt(q["depth"], 2);
            var scene = SceneManager.GetActiveScene();
            var sb = new StringBuilder("[");
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                WriteNode(sb, roots[i].transform, 0, depth);
            }
            sb.Append(']');
            return MCPXResult.Ok("{\"scene\":" + MCPXJson.Str(scene.name) + ",\"roots\":" + sb.ToString() + "}");
        }

        static MCPXResult ObjectInfo(NameValueCollection q)
        {
            var t = FindByPath(q["path"]);
            if (t == null) return MCPXResult.Error(404, "object not found: " + q["path"]);
            var go = t.gameObject;
            var json = "{"
                + "\"name\":" + MCPXJson.Str(go.name)
                + ",\"path\":" + MCPXJson.Str(PathOf(t))
                + ",\"active\":" + MCPXJson.B(go.activeSelf)
                + ",\"activeInHierarchy\":" + MCPXJson.B(go.activeInHierarchy)
                + ",\"tag\":" + MCPXJson.Str(go.tag)
                + ",\"layer\":" + MCPXJson.Str(LayerMask.LayerToName(go.layer))
                + ",\"localPosition\":" + MCPXJson.V3(t.localPosition)
                + ",\"localEulerAngles\":" + MCPXJson.V3(t.localEulerAngles)
                + ",\"localScale\":" + MCPXJson.V3(t.localScale)
                + ",\"components\":" + ComponentNames(go)
                + "}";
            return MCPXResult.Ok(json);
        }

        static MCPXResult Components(NameValueCollection q)
        {
            var t = FindByPath(q["path"]);
            if (t == null) return MCPXResult.Error(404, "object not found: " + q["path"]);

            string typeFilter = q["type"];
            int maxDepth = ParseInt(q["depth"], 2);

            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                var typeName = c.GetType().Name;
                if (!string.IsNullOrEmpty(typeFilter)
                    && typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append("{\"type\":").Append(MCPXJson.Str(typeName))
                  .Append(",\"properties\":");
                using (var so = new SerializedObject(c))
                {
                    sb.Append(Props(so, maxDepth));
                }
                sb.Append('}');
            }
            sb.Append(']');

            return MCPXResult.Ok("{\"path\":" + MCPXJson.Str(PathOf(t)) + ",\"components\":" + sb.ToString() + "}");
        }

        static MCPXResult Find(NameValueCollection q)
        {
            string name = q["name"];
            if (string.IsNullOrEmpty(name)) return MCPXResult.Error(400, "missing 'name' parameter");

            var sb = new StringBuilder("[");
            int n = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                var scene = t.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (t.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (n >= 100) break;

                if (n > 0) sb.Append(',');
                n++;
                sb.Append("{\"name\":").Append(MCPXJson.Str(t.name))
                  .Append(",\"path\":").Append(MCPXJson.Str(PathOf(t)))
                  .Append(",\"scene\":").Append(MCPXJson.Str(scene.name))
                  .Append('}');
            }
            sb.Append(']');
            return MCPXResult.Ok("{\"count\":" + n + ",\"results\":" + sb.ToString() + "}");
        }

        // ---------- helpers ----------

        static int ParseInt(string s, int def)
        {
            return int.TryParse(s, out var v) ? v : def;
        }

        static long ParseLong(string s, long def)
        {
            return long.TryParse(s, out var v) ? v : def;
        }

        // Path format: "RootName/Child/Grandchild" (root = scene root object).
        internal static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                if (parts.Length == 1) return root.transform;
                return root.transform.Find(string.Join("/", parts, 1, parts.Length - 1));
            }
            return null;
        }

        internal static string PathOf(Transform t)
        {
            var s = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                s = t.name + "/" + s;
            }
            return s;
        }

        static string ComponentNames(GameObject go)
        {
            var parts = new List<string>();
            foreach (var c in go.GetComponents<Component>())
                parts.Add(MCPXJson.Str(c == null ? "<Missing>" : c.GetType().Name));
            return "[" + string.Join(",", parts) + "]";
        }

        static void WriteNode(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            sb.Append("{\"name\":").Append(MCPXJson.Str(t.name))
              .Append(",\"path\":").Append(MCPXJson.Str(PathOf(t)))
              .Append(",\"active\":").Append(MCPXJson.B(t.gameObject.activeSelf))
              .Append(",\"components\":").Append(ComponentNames(t.gameObject));

            if (depth < maxDepth && t.childCount > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < t.childCount; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteNode(sb, t.GetChild(i), depth + 1, maxDepth);
                }
                sb.Append(']');
            }
            else if (t.childCount > 0)
            {
                sb.Append(",\"childCount\":").Append(t.childCount);
            }
            sb.Append('}');
        }

        // ---------- serialized property reading ----------

        static string Props(SerializedObject so, int maxDepth)
        {
            var sb = new StringBuilder("{");
            var it = so.GetIterator();
            bool enter = true;
            bool first = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (it.name == "m_Script") continue;

                if (!first) sb.Append(',');
                first = false;
                sb.Append(MCPXJson.Str(it.name)).Append(':');
                WriteValue(sb, it, 0, maxDepth);
            }
            sb.Append('}');
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, SerializedProperty p, int depth, int maxDepth)
        {
            // Arrays and structs: guard depth, then expand.
            if (p.isArray)
            {
                if (depth >= maxDepth) { sb.Append("\"...\""); return; }
                int n = p.arraySize;
                int lim = Math.Min(n, 20);
                sb.Append("{\"_array\":").Append(n).Append(",\"items\":[");
                for (int i = 0; i < lim; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, p.GetArrayElementAtIndex(i), depth + 1, maxDepth);
                }
                sb.Append("]}");
                return;
            }

            if (p.propertyType == SerializedPropertyType.Generic)
            {
                if (depth >= maxDepth) { sb.Append("\"...\""); return; }
                sb.Append('{');
                var child = p.Copy();
                var end = p.GetEndProperty();
                bool enter = true;
                bool first = true;
                while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
                {
                    enter = false;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(MCPXJson.Str(child.name)).Append(':');
                    WriteValue(sb, child, depth + 1, maxDepth);
                }
                sb.Append('}');
                return;
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    sb.Append(p.longValue.ToString(CultureInfo.InvariantCulture));
                    break;
                case SerializedPropertyType.Boolean:
                    sb.Append(MCPXJson.B(p.boolValue));
                    break;
                case SerializedPropertyType.Float:
                    sb.Append(MCPXJson.F(p.doubleValue));
                    break;
                case SerializedPropertyType.String:
                    sb.Append(MCPXJson.Str(p.stringValue));
                    break;
                case SerializedPropertyType.Enum:
                    {
                        var names = p.enumNames;
                        int idx = p.enumValueIndex;
                        if (names != null && idx >= 0 && idx < names.Length)
                            sb.Append(MCPXJson.Str(names[idx]));
                        else
                            sb.Append(idx);
                        break;
                    }
                case SerializedPropertyType.Color:
                    {
                        var c = p.colorValue;
                        sb.Append(MCPXJson.Arr(c.r, c.g, c.b, c.a));
                        break;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var v = p.vector2Value;
                        sb.Append(MCPXJson.Arr(v.x, v.y));
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var v = p.vector3Value;
                        sb.Append(MCPXJson.Arr(v.x, v.y, v.z));
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var v = p.vector4Value;
                        sb.Append(MCPXJson.Arr(v.x, v.y, v.z, v.w));
                        break;
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        var q = p.quaternionValue;
                        sb.Append(MCPXJson.Arr(q.x, q.y, q.z, q.w));
                        break;
                    }
                case SerializedPropertyType.Rect:
                    {
                        var r = p.rectValue;
                        sb.Append(MCPXJson.Arr(r.x, r.y, r.width, r.height));
                        break;
                    }
                case SerializedPropertyType.ObjectReference:
                    WriteObjectRef(sb, p.objectReferenceValue);
                    break;
                default:
                    sb.Append(MCPXJson.Str("<" + p.propertyType + ">"));
                    break;
            }
        }

        static void WriteObjectRef(StringBuilder sb, UnityEngine.Object o)
        {
            if (o == null)
            {
                sb.Append("null");
                return;
            }

            string path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path))
            {
                if (o is Component c) path = PathOf(c.transform);
                else if (o is GameObject g) path = PathOf(g.transform);
            }

            sb.Append("{\"name\":").Append(MCPXJson.Str(o.name))
              .Append(",\"type\":").Append(MCPXJson.Str(o.GetType().Name))
              .Append(",\"path\":").Append(MCPXJson.Str(path ?? ""))
              .Append('}');
        }
    }

    public static class MCPXJson
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string B(bool b) => b ? "true" : "false";

        // Serialized floats are single precision; print 7 significant digits
        // to avoid noise like 0.9999998807907104.
        public static string F(double f)
        {
            if (double.IsNaN(f) || double.IsInfinity(f)) return "null";
            return ((float)f).ToString("G7", CultureInfo.InvariantCulture);
        }

        public static string Arr(params float[] v)
        {
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++) parts[i] = F(v[i]);
            return "[" + string.Join(",", parts) + "]";
        }

        public static string V3(Vector3 v) => Arr(v.x, v.y, v.z);
    }
}
