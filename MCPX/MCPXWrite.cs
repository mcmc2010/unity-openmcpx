using System;
using System.Collections.Specialized;
using System.Globalization;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace MCPX
{
    /// <summary>
    /// Write routes. Every change goes through Undo, so it can be reverted with Ctrl/Cmd+Z.
    /// </summary>
    public static class MCPXWrite
    {
        // POST /setprop?path=Root/Child&component=ParticleSystem&property=InitialModule.startSpeed.scalar&value=0.5
        // value formats: int/float/bool/string; enum by name or index; Vector2/3/4, Quaternion, Rect, Color as "a,b,c[,d]";
        // ObjectReference as asset path ("Assets/..."), or "null" to clear.
        public static MCPXResult SetProp(NameValueCollection q)
        {
            string path = q["path"], compName = q["component"], prop = q["property"], value = q["value"];
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(compName)
                || string.IsNullOrEmpty(prop) || value == null)
                return MCPXResult.Error(400, "required: path, component, property, value");

            var t = MCPXRoutes.FindByPath(path);
            if (t == null) return MCPXResult.Error(404, "object not found: " + path);

            Component comp = null;
            foreach (var c in t.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == compName) { comp = c; break; }
            }
            if (comp == null) return MCPXResult.Error(404, "component not found: " + compName);

            var so = new SerializedObject(comp);
            so.Update();
            var p = so.FindProperty(prop);
            if (p == null) return MCPXResult.Error(404, "property not found: " + prop);

            Undo.RecordObject(comp, "MCPX set " + prop);
            string err;
            try
            {
                if (!Assign(p, value, out err))
                    return MCPXResult.Error(400, err);
            }
            catch (Exception e)
            {
                return MCPXResult.Error(400, "bad value '" + value + "': " + e.Message);
            }
            so.ApplyModifiedProperties();

            return MCPXResult.Ok("{\"ok\":true,\"path\":" + MCPXJson.Str(path)
                + ",\"component\":" + MCPXJson.Str(compName)
                + ",\"property\":" + MCPXJson.Str(prop)
                + ",\"value\":" + MCPXJson.Str(value) + "}");
        }

        // POST /create?parent=Field&name=Floor&primitive=Plane&position=0,0,0&scale=5,1,5
        // primitive: Plane, Cube, Sphere, Capsule, Cylinder, Quad, or Empty. Omit parent to create at scene root.
        // Optional rotation=x,y,z (euler, local).
        // Creates the object under parent (local position/scale), registered for Undo.
        public static MCPXResult Create(NameValueCollection q)
        {
            string parentPath = q["parent"], name = q["name"], prim = q["primitive"];
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prim))
                return MCPXResult.Error(400, "required: name, primitive");

            bool empty = string.Equals(prim, "Empty", StringComparison.OrdinalIgnoreCase);
            PrimitiveType type = PrimitiveType.Cube;
            if (!empty && !Enum.TryParse(prim, true, out type))
                return MCPXResult.Error(400, "unknown primitive: " + prim + " (or Empty)");

            Transform parent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = MCPXRoutes.FindByPath(parentPath);
                if (parent == null) return MCPXResult.Error(404, "parent not found: " + parentPath);
            }

            var go = empty ? new GameObject(name) : GameObject.CreatePrimitive(type);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "MCPX create " + name);
            if (parent != null) go.transform.SetParent(parent, false);

            try
            {
                if (!string.IsNullOrEmpty(q["position"])) go.transform.localPosition = V3(q["position"]);
                if (!string.IsNullOrEmpty(q["rotation"])) go.transform.localEulerAngles = V3(q["rotation"]);
                if (!string.IsNullOrEmpty(q["scale"])) go.transform.localScale = V3(q["scale"]);
            }
            catch (Exception e)
            {
                Undo.DestroyObjectImmediate(go);
                return MCPXResult.Error(400, "bad vector: " + e.Message);
            }

            string path = MCPXRoutes.PathOf(go.transform);
            return MCPXResult.Ok("{\"ok\":true,\"path\":" + MCPXJson.Str(path) + "}");
        }

        // POST /addcomponent?path=Field/Popkart/CameraMount&type=Camera
        // type: simple class name of a Component (e.g. Camera, BoxCollider, Rigidbody). Undoable.
        public static MCPXResult AddComponent(NameValueCollection q)
        {
            string path = q["path"], typeName = q["type"];
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(typeName))
                return MCPXResult.Error(400, "required: path, type");

            var t = MCPXRoutes.FindByPath(path);
            if (t == null) return MCPXResult.Error(404, "object not found: " + path);

            Type found = null;
            int matches = 0;
            foreach (var type in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (type.IsAbstract || type.Name != typeName) continue;
                found = type;
                matches++;
            }
            if (matches == 0) return MCPXResult.Error(404, "component type not found: " + typeName);
            if (matches > 1) return MCPXResult.Error(400, "ambiguous component type: " + typeName);

            var comp = Undo.AddComponent(t.gameObject, found);
            if (comp == null) return MCPXResult.Error(400, "could not add " + typeName);
            return MCPXResult.Ok("{\"ok\":true,\"path\":" + MCPXJson.Str(path)
                + ",\"component\":" + MCPXJson.Str(comp.GetType().Name) + "}");
        }

        // POST /rename?path=UI/Joystick/0&name=Stick
        public static MCPXResult Rename(NameValueCollection q)
        {
            string path = q["path"], name = q["name"];
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
                return MCPXResult.Error(400, "required: path, name");

            var t = MCPXRoutes.FindByPath(path);
            if (t == null) return MCPXResult.Error(404, "object not found: " + path);

            Undo.RecordObject(t.gameObject, "MCPX rename to " + name);
            t.gameObject.name = name;

            return MCPXResult.Ok("{\"ok\":true,\"path\":" + MCPXJson.Str(MCPXRoutes.PathOf(t))
                + ",\"name\":" + MCPXJson.Str(name) + "}");
        }

        // POST /delete?path=UI/Joystick/0
        public static MCPXResult Delete(NameValueCollection q)
        {
            string path = q["path"];
            if (string.IsNullOrEmpty(path))
                return MCPXResult.Error(400, "required: path");

            var t = MCPXRoutes.FindByPath(path);
            if (t == null) return MCPXResult.Error(404, "object not found: " + path);

            Undo.DestroyObjectImmediate(t.gameObject);
            return MCPXResult.Ok("{\"ok\":true,\"deleted\":" + MCPXJson.Str(path) + "}");
        }

        static Vector3 V3(string s)
        {
            var p = s.Split(',');
            if (p.Length != 3) throw new FormatException("expected x,y,z");
            var inv = CultureInfo.InvariantCulture;
            return new Vector3(float.Parse(p[0], inv), float.Parse(p[1], inv), float.Parse(p[2], inv));
        }

        // POST /compile
        // Imports changed scripts and forces a compile, without needing the Unity window focused.
        // Returns sinceId: the log id before compiling, so the caller can read only new logs.
        public static MCPXResult Compile()
        {
            long sinceId = MCPXLogBuffer.LastId;
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
            return MCPXResult.Ok("{\"ok\":true,\"sinceId\":" + sinceId + "}");
        }

        // POST /menu?item=Tools/MCPX/Setup%20Exhaust%20Smoke%20(Popkart)
        // Restricted to menu items under Tools/MCPX/ to avoid running arbitrary editor commands.
        public static MCPXResult Menu(NameValueCollection q)
        {
            string item = q["item"];
            if (string.IsNullOrEmpty(item)) return MCPXResult.Error(400, "missing 'item' parameter");
            if (!item.StartsWith("Tools/MCPX/", StringComparison.Ordinal))
                return MCPXResult.Error(403, "only Tools/MCPX/ menu items are allowed");

            if (!EditorApplication.ExecuteMenuItem(item))
                return MCPXResult.Error(404, "menu item not found: " + item);
            return MCPXResult.Ok("{\"ok\":true,\"item\":" + MCPXJson.Str(item) + "}");
        }

        static bool Assign(SerializedProperty p, string v, out string err)
        {
            err = null;
            var inv = CultureInfo.InvariantCulture;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    p.longValue = long.Parse(v, inv);
                    return true;
                case SerializedPropertyType.Boolean:
                    p.boolValue = bool.Parse(v);
                    return true;
                case SerializedPropertyType.Float:
                    p.floatValue = float.Parse(v, inv);
                    return true;
                case SerializedPropertyType.String:
                    p.stringValue = v;
                    return true;
                case SerializedPropertyType.Enum:
                    {
                        var names = p.enumNames;
                        int idx = Array.IndexOf(names, v);
                        if (idx < 0) idx = int.Parse(v, inv);
                        if (idx < 0 || idx >= names.Length)
                        {
                            err = "enum index out of range; options: " + string.Join(", ", names);
                            return false;
                        }
                        p.enumValueIndex = idx;
                        return true;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var f = Floats(v, 2);
                        p.vector2Value = new Vector2(f[0], f[1]);
                        return true;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var f = Floats(v, 3);
                        p.vector3Value = new Vector3(f[0], f[1], f[2]);
                        return true;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var f = Floats(v, 4);
                        p.vector4Value = new Vector4(f[0], f[1], f[2], f[3]);
                        return true;
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        var f = Floats(v, 4);
                        p.quaternionValue = new Quaternion(f[0], f[1], f[2], f[3]);
                        return true;
                    }
                case SerializedPropertyType.Rect:
                    {
                        var f = Floats(v, 4);
                        p.rectValue = new Rect(f[0], f[1], f[2], f[3]);
                        return true;
                    }
                case SerializedPropertyType.Color:
                    {
                        var f = Floats(v, 4);
                        p.colorValue = new Color(f[0], f[1], f[2], f[3]);
                        return true;
                    }
                case SerializedPropertyType.ObjectReference:
                    {
                        if (v == "null") { p.objectReferenceValue = null; return true; }

                        if (v.StartsWith("Scene:", StringComparison.OrdinalIgnoreCase))
                        {
                            string sp = v.Substring("Scene:".Length).Trim();
                            var t = MCPXRoutes.FindByPath(sp);
                            if (t == null)
                            {
                                err = "scene object not found: " + sp;
                                return false;
                            }
                            var target = ResolveRef(t, p);
                            if (target == null)
                            {
                                err = "no component matching " + p.type + " on " + sp;
                                return false;
                            }
                            p.objectReferenceValue = target;
                            return true;
                        }

                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(v);
                        if (obj == null)
                        {
                            err = "asset not found: " + v;
                            return false;
                        }
                        var resolved = ResolveRef(obj, p);
                        if (resolved == null)
                        {
                            err = "no component matching " + p.type + " on asset " + v;
                            return false;
                        }
                        p.objectReferenceValue = resolved;
                        return true;
                    }
                default:
                    err = "unsupported property type: " + p.propertyType;
                    return false;
            }
        }

        // Match the property's declared object type (p.type is "PPtr<$TypeName>")
        // against a scene Transform or a prefab asset GameObject (returns its component).
        static UnityEngine.Object ResolveRef(UnityEngine.Object obj, SerializedProperty p)
        {
            if (obj == null) return null;
            string typeName = p.type ?? "";
            int i = typeName.IndexOf('$');
            if (i < 0) return obj;

            typeName = typeName.Substring(i + 1).TrimEnd('>');
            if (typeName == "Object" || typeName == obj.GetType().Name)
                return obj;

            GameObject go = obj as GameObject;
            if (go == null && obj is Component c) go = c.gameObject;
            if (go == null) return null;

            if (typeName == "GameObject") return go;
            if (typeName == "Transform") return go.transform;

            // Match against attached components by type name: unlike GetComponent(string),
            // this is unaffected by same-named classes in different namespaces.
            foreach (var comp in go.GetComponents<Component>())
                if (comp != null && comp.GetType().Name == typeName)
                    return comp;
            return null;
        }

        static float[] Floats(string v, int n)
        {
            var parts = v.Split(',');
            if (parts.Length != n) throw new FormatException($"expected {n} comma-separated numbers");
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            return r;
        }
    }
}
