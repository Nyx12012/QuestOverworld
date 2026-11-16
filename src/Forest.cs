using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Alta.Forests;
using Alta.Networking;
using Alta.Static;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace QuestOverworld
{
    public static class Data
    {
        public const string Resource = "forest-p1.json";
        static JObject root;
        static Dictionary<int, JObject> objects;
        static string hash;

        public static JObject Root { get { Load(); return root; } }
        public static Dictionary<int, JObject> Objects { get { Load(); return objects; } }
        public static string Hash { get { Load(); return hash; } }

        public static void Load()
        {
            if (root != null) return;
            byte[] bytes;
            using (Stream s = typeof(Data).Assembly.GetManifestResourceStream(Resource))
            {
                if (s == null) throw new InvalidOperationException(Resource + " is not inside the DLL");
                bytes = new byte[s.Length];
                int read = 0;
                while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
            }
            using (SHA256 sha = SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
            }
            Use(JObject.Parse(Encoding.UTF8.GetString(bytes)));
        }

        public static void Use(JObject data)
        {
            root = data;
            objects = new Dictionary<int, JObject>();
            foreach (JObject o in (JArray)data["objects"]) objects[(int)o["id"]] = o;
            if (hash == null) hash = "external";
        }

        public static bool IsScene(JObject o)
        {
            return (string)o["src"][0] == (string)Root["source"]["scene"];
        }

        public static string Kind(JObject o)
        {
            return (string)o["kind"];
        }

        public static string Cls(JObject o)
        {
            return (string)o["cls"];
        }

        public static JObject Fields(JObject o)
        {
            return (JObject)o["fields"];
        }

        public static int LocalId(JToken r)
        {
            JObject o = r as JObject;
            if (o == null || o["local"] == null) return 0;
            return (int)o["local"];
        }
    }

    public class Report
    {
        public List<string> Unresolved = new List<string>();
        public List<string> Problems = new List<string>();
        public List<string> Skipped = new List<string>();
        public int Components;
        public int Presets;
        public int Assets;
        public int Checks;

        public void Problem(string text)
        {
            if (Problems.Count < 200) Problems.Add(text);
        }
    }

    public static class Types
    {
        static Dictionary<string, Type> byName = new Dictionary<string, Type>();

        public static Type Script(string name)
        {
            Type t;
            if (byName.TryGetValue(name, out t)) return t;
            t = AccessTools.TypeByName(name);
            byName[name] = t;
            return t;
        }

        public static Type Native(string cls)
        {
            switch (cls)
            {
                case "GameObject": return typeof(GameObject);
                case "Transform": return typeof(Transform);
                case "MeshFilter": return typeof(MeshFilter);
                case "MeshRenderer": return typeof(MeshRenderer);
                case "Material": return typeof(Material);
            }
            return null;
        }

        public static Type Of(JObject o)
        {
            string kind = Data.Kind(o);
            if (kind == "native") return Native(Data.Cls(o));
            return Script((string)o["script"]);
        }

        public static Type OfAsset(JObject a)
        {
            string cls = (string)a["cls"];
            if (cls == "MonoBehaviour") return Script((string)a["script"]);
            return Native(cls);
        }

        public static FieldInfo Field(Type t, string name)
        {
            for (Type c = t; c != null && c != typeof(object); c = c.BaseType)
            {
                FieldInfo f = c.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        public static bool IsList(Type t)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
        }

        public static Type Element(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            if (IsList(t)) return t.GetGenericArguments()[0];
            return null;
        }

        public static int StructSize(Type t)
        {
            if (t == typeof(Vector2) || t == typeof(Vector2Int)) return 2;
            if (t == typeof(Vector3) || t == typeof(Vector3Int)) return 3;
            if (t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color) || t == typeof(Color32) || t == typeof(Rect)) return 4;
            if (t == typeof(Bounds)) return 6;
            if (t == typeof(LayerMask)) return 1;
            return 0;
        }
    }

    public static class Validate
    {
        public static Report Run(JObject data)
        {
            Data.Use(data);
            Report rep = new Report();
            foreach (JObject o in Data.Objects.Values)
            {
                string where = Where(o);
                Type t = Types.Of(o);
                rep.Checks++;
                if (t == null)
                {
                    rep.Problem(where + ": type not found");
                    continue;
                }
                string kind = Data.Kind(o);
                if (kind == "mb" && !typeof(MonoBehaviour).IsAssignableFrom(t)) rep.Problem(where + ": not a MonoBehaviour");
                if (kind == "so" && !typeof(ScriptableObject).IsAssignableFrom(t)) rep.Problem(where + ": not a ScriptableObject");
                if (kind == "native") continue;
                foreach (JProperty p in Data.Fields(o).Properties())
                {
                    FieldInfo f = Types.Field(t, p.Name);
                    rep.Checks++;
                    if (f == null)
                    {
                        rep.Problem(where + "." + p.Name + ": no such field on " + t.FullName);
                        continue;
                    }
                    Shape(p.Value, f.FieldType, where + "." + p.Name, rep);
                }
            }
            return rep;
        }

        public static string Where(JObject o)
        {
            return "#" + (int)o["id"] + " " + ((string)o["script"] ?? (string)o["cls"]) + (o["name"] != null && (string)o["name"] != "" ? " '" + (string)o["name"] + "'" : "");
        }

        static void Shape(JToken v, Type t, string where, Report rep)
        {
            rep.Checks++;
            if (typeof(Object).IsAssignableFrom(t))
            {
                Ref(v, t, where, rep);
                return;
            }
            if (typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(t))
            {
                if (!(v is JArray) || ((JArray)v).Count != 0) rep.Problem(where + ": events are not supported");
                return;
            }
            if (t == typeof(string))
            {
                if (v.Type != JTokenType.String) rep.Problem(where + ": expected text");
                return;
            }
            if (t == typeof(bool))
            {
                if (v.Type != JTokenType.Boolean) rep.Problem(where + ": expected true or false");
                return;
            }
            if (t.IsEnum || (t.IsPrimitive && t != typeof(float) && t != typeof(double)))
            {
                if (v.Type != JTokenType.Integer) rep.Problem(where + ": expected a whole number for " + t.Name);
                return;
            }
            if (t == typeof(float) || t == typeof(double))
            {
                if (v.Type != JTokenType.Float && v.Type != JTokenType.Integer) rep.Problem(where + ": expected a number");
                return;
            }
            int size = Types.StructSize(t);
            if (size > 0)
            {
                if (!(v is JArray) || ((JArray)v).Count != size) rep.Problem(where + ": expected " + size + " numbers for " + t.Name);
                return;
            }
            if (t == typeof(AnimationCurve))
            {
                JObject c = v as JObject;
                if (c == null || !(c["keys"] is JArray) || c["pre"] == null || c["post"] == null) rep.Problem(where + ": expected a curve");
                return;
            }
            Type el = Types.Element(t);
            if (el != null)
            {
                JArray a = v as JArray;
                if (a == null)
                {
                    rep.Problem(where + ": expected a list");
                    return;
                }
                for (int i = 0; i < a.Count; i++) Shape(a[i], el, where + "[" + i + "]", rep);
                return;
            }
            JObject obj = v as JObject;
            if (obj == null || t.IsPrimitive || typeof(Delegate).IsAssignableFrom(t))
            {
                rep.Problem(where + ": cannot fill a " + t.FullName);
                return;
            }
            foreach (JProperty p in obj.Properties())
            {
                FieldInfo f = Types.Field(t, p.Name);
                if (f == null)
                {
                    rep.Problem(where + "." + p.Name + ": no such field on " + t.FullName);
                    continue;
                }
                Shape(p.Value, f.FieldType, where + "." + p.Name, rep);
            }
        }

        static void Ref(JToken v, Type t, string where, Report rep)
        {
            if (v.Type == JTokenType.Null) return;
            JObject r = v as JObject;
            if (r == null)
            {
                rep.Problem(where + ": expected a reference");
                return;
            }
            if (r["local"] != null)
            {
                JObject target;
                if (!Data.Objects.TryGetValue((int)r["local"], out target))
                {
                    rep.Problem(where + ": points at a missing object");
                    return;
                }
                Type tt = Types.Of(target);
                if (tt == null || !t.IsAssignableFrom(tt)) rep.Problem(where + ": " + (tt == null ? "?" : tt.Name) + " does not fit " + t.Name);
                return;
            }
            if (r["asset"] != null)
            {
                JObject a = (JObject)r["asset"];
                Type at = Types.OfAsset(a);
                if (at == null)
                {
                    rep.Problem(where + ": asset type not found");
                    return;
                }
                bool fits = t.IsAssignableFrom(at) || (at == typeof(GameObject) && typeof(Component).IsAssignableFrom(t)) || (typeof(Component).IsAssignableFrom(at) && t == typeof(GameObject));
                if (!fits) rep.Problem(where + ": asset " + at.Name + " does not fit " + t.Name);
                if (a["path"] == null && a["via"] == null && a["piece"] == null && a["sound"] == null && a["name"] == null && a["go"] == null) rep.Problem(where + ": asset has no way to be found");
                if (a["sound"] != null && at != typeof(SoundEffectSet)) rep.Problem(where + ": only a SoundEffectSet is found through the sound map");
                return;
            }
            if (r["scene"] == null && r["skip"] == null) rep.Problem(where + ": unknown reference kind");
        }
    }

    public static class Sounds
    {
        public static SoundEffectSet Get(int type)
        {
            AudioManager manager = Alta.Utilities.SingletonBehaviour<AudioManager>.Instance;
            SoundEffectsMap map = manager == null ? null : Hooks.SoundMapField.GetValue(manager) as SoundEffectsMap;
            SoundEffectSet set;
            if (map != null && map.SoundEventMap.TryGetValue((SoundEffect)type, out set)) return set;
            return null;
        }
    }

    public static class Builder
    {
        static readonly Dictionary<int, Object> assets = new Dictionary<int, Object>();
        static readonly Dictionary<string, Object> outside = new Dictionary<string, Object>();
        static Dictionary<int, Object> scene = new Dictionary<int, Object>();
        static GameObject assetHolder;
        static GameObject rootGo;
        static Report report;
        static Stopwatch sinceBuild;
        static bool readyLogged;
        static int outsideCount;
        public static ForestGenerator Generator;
        public static Report LastReport { get { return report; } }

        public static bool Active
        {
            get { return Generator != null; }
        }

        public static Dictionary<uint, NetworkEntity> Build(NetworkScene networkScene)
        {
            Stopwatch timer = Stopwatch.StartNew();
            report = Validate.Run(Data.Root);
            foreach (string p in report.Problems) Mod.Log.Warning("forest: data problem: " + p);
            int dataProblems = report.Problems.Count;
            scene = new Dictionary<int, Object>();
            List<JObject> sceneObjects = new List<JObject>();
            List<JObject> assetObjects = new List<JObject>();
            foreach (JObject o in Data.Objects.Values)
            {
                if (Data.IsScene(o)) sceneObjects.Add(o);
                else assetObjects.Add(o);
            }
            if (assetHolder == null)
            {
                assetHolder = new GameObject("QuestOverworld Forest Assets");
                assetHolder.SetActive(false);
                assetHolder.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Object.DontDestroyOnLoad(assetHolder);
                Create(assetObjects, assets, assetHolder.transform, networkScene);
                Fill(assetObjects, assets, networkScene);
                Activate(assetObjects, assets, 0);
            }
            foreach (JObject o in assetObjects)
            {
                if (Data.Kind(o) == "so" && assets.ContainsKey((int)o["id"])) report.Presets++;
            }
            int rootId = (int)Data.Root["root"];
            Create(sceneObjects, scene, networkScene.transform, networkScene);
            rootGo = scene[rootId] as GameObject;
            Fill(sceneObjects, scene, networkScene);
            Activate(sceneObjects, scene, rootId);
            Dictionary<uint, NetworkEntity> made = new Dictionary<uint, NetworkEntity>();
            foreach (JObject e in (JArray)Data.Root["embedded"])
            {
                Object o;
                NetworkEntity entity = scene.TryGetValue((int)e["entity"], out o) ? o as NetworkEntity : null;
                if (entity == null) report.Problem("embedded " + (string)e["name"] + ": no NetworkEntity was made");
                else made[(uint)e["hash"]] = entity;
            }
            rootGo.SetActive(IsActive(Data.Objects[rootId]));
            Generator = rootGo.GetComponent<ForestGenerator>();
            CheckAwake(rootGo);
            Mod.Log.Msg("forest: " + report.Components + " components, " + report.Presets + " presets, " + report.Assets + " assets, " + report.Unresolved.Count + " unresolved, built in " + timer.ElapsedMilliseconds + " ms");
            foreach (string u in report.Unresolved) Mod.Log.Warning("forest: unresolved " + u);
            foreach (string s in report.Skipped) Mod.Log.Msg("forest: left out " + s);
            for (int i = dataProblems; i < report.Problems.Count; i++) Mod.Log.Warning("forest: build problem: " + report.Problems[i]);
            sinceBuild = Stopwatch.StartNew();
            readyLogged = false;
            return made;
        }

        public static void Discard()
        {
            Generator = null;
            if (rootGo != null) Object.Destroy(rootGo);
            rootGo = null;
        }

        public static string Name(uint hash)
        {
            foreach (JObject e in (JArray)Data.Root["embedded"])
            {
                if ((uint)e["hash"] == hash) return (string)e["name"];
            }
            return "hash " + hash;
        }

        static bool IsActive(JObject go)
        {
            return (int)Data.Fields(go)["m_IsActive"] != 0;
        }

        static void Create(List<JObject> set, Dictionary<int, Object> made, Transform holder, NetworkScene networkScene)
        {
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "native" || Data.Cls(o) != "GameObject") continue;
                JObject f = Data.Fields(o);
                GameObject go = new GameObject((string)f["m_Name"]);
                go.SetActive(false);
                go.layer = (int)(uint)f["m_Layer"];
                if ((int)f["m_Tag"] != 0) report.Problem(Validate.Where(o) + ": tag " + (int)f["m_Tag"] + " not set");
                made[(int)o["id"]] = go;
                foreach (JToken c in (JArray)f["m_Component"])
                {
                    int cid = Data.LocalId(c);
                    if (cid != 0 && Data.Kind(Data.Objects[cid]) == "native" && Data.Cls(Data.Objects[cid]) == "Transform") made[cid] = go.transform;
                }
            }
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "native" || Data.Cls(o) != "Transform") continue;
                JObject f = Data.Fields(o);
                Transform t = (Transform)made[(int)o["id"]];
                JObject father = f["m_Father"] as JObject;
                Transform parent = holder;
                if (father != null && father["local"] != null) parent = (Transform)made[(int)father["local"]];
                else if (father != null && father["scene"] != null) parent = networkScene.transform;
                t.SetParent(parent, false);
                t.localRotation = Quat(f["m_LocalRotation"]);
                t.localPosition = Vec3(f["m_LocalPosition"]);
                t.localScale = Vec3(f["m_LocalScale"]);
            }
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "native" || Data.Cls(o) != "Transform") continue;
                JArray kids = (JArray)Data.Fields(o)["m_Children"];
                for (int i = 0; i < kids.Count; i++)
                {
                    int kid = Data.LocalId(kids[i]);
                    if (kid != 0 && made.ContainsKey(kid)) ((Transform)made[kid]).SetSiblingIndex(i);
                }
            }
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "native" || Data.Cls(o) != "GameObject") continue;
                GameObject go = (GameObject)made[(int)o["id"]];
                foreach (JToken c in (JArray)Data.Fields(o)["m_Component"])
                {
                    int cid = Data.LocalId(c);
                    if (cid == 0)
                    {
                        report.Skipped.Add((string)c["skip"] + " on " + go.name);
                        continue;
                    }
                    JObject co = Data.Objects[cid];
                    if (Data.Kind(co) == "native" && Data.Cls(co) == "Transform") continue;
                    Type t = Types.Of(co);
                    Component added = t == null ? null : Unclaimed(go, t, made);
                    if (added == null && t != null) added = go.AddComponent(t);
                    if (added == null)
                    {
                        report.Problem(Validate.Where(co) + ": could not add to " + go.name);
                        continue;
                    }
                    made[cid] = added;
                    if (Data.Kind(co) == "mb" && Data.IsScene(co)) report.Components++;
                }
            }
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "so") continue;
                Type t = Types.Of(o);
                if (t == null) continue;
                ScriptableObject so = ScriptableObject.CreateInstance(t);
                so.name = (string)o["name"];
                so.hideFlags = HideFlags.DontUnloadUnusedAsset;
                made[(int)o["id"]] = so;
            }
        }

        static Component Unclaimed(GameObject go, Type t, Dictionary<int, Object> made)
        {
            foreach (Component c in go.GetComponents(t))
            {
                if (!made.ContainsValue(c)) return c;
            }
            return null;
        }

        static void Fill(List<JObject> set, Dictionary<int, Object> made, NetworkScene networkScene)
        {
            Resolver resolver = new Resolver(made, networkScene, report);
            foreach (JObject o in set)
            {
                Object target;
                if (!made.TryGetValue((int)o["id"], out target)) continue;
                string kind = Data.Kind(o);
                string where = Validate.Where(o);
                if (kind == "native")
                {
                    if (Data.Cls(o) == "MeshRenderer") Renderer((MeshRenderer)target, Data.Fields(o), where, resolver);
                    continue;
                }
                Behaviour b = target as Behaviour;
                if (b != null) b.enabled = (bool)o["enabled"];
                Type t = target.GetType();
                foreach (JProperty p in Data.Fields(o).Properties())
                {
                    FieldInfo f = Types.Field(t, p.Name);
                    if (f == null) continue;
                    try
                    {
                        object v;
                        if (resolver.Convert(p.Value, f.FieldType, where + "." + p.Name, out v)) f.SetValue(target, v);
                    }
                    catch (Exception e)
                    {
                        report.Problem(where + "." + p.Name + ": " + e.Message);
                    }
                }
            }
            report.Assets = outsideCount;
        }

        static void Renderer(MeshRenderer r, JObject f, string where, Resolver resolver)
        {
            r.enabled = (int)f["m_Enabled"] != 0;
            r.shadowCastingMode = (ShadowCastingMode)(int)f["m_CastShadows"];
            r.receiveShadows = (int)f["m_ReceiveShadows"] != 0;
            r.allowOcclusionWhenDynamic = (int)f["m_DynamicOccludee"] != 0;
            r.motionVectorGenerationMode = (MotionVectorGenerationMode)(int)f["m_MotionVectors"];
            r.lightProbeUsage = (LightProbeUsage)(int)f["m_LightProbeUsage"];
            r.reflectionProbeUsage = (ReflectionProbeUsage)(int)f["m_ReflectionProbeUsage"];
            r.renderingLayerMask = (uint)f["m_RenderingLayerMask"];
            r.rendererPriority = (int)f["m_RendererPriority"];
            r.sortingLayerID = (int)f["m_SortingLayerID"];
            r.sortingOrder = (int)f["m_SortingOrder"];
            JArray mats = (JArray)f["m_Materials"];
            Material[] list = new Material[mats.Count];
            for (int i = 0; i < mats.Count; i++)
            {
                object m;
                resolver.Convert(mats[i], typeof(Material), where + ".m_Materials[" + i + "]", out m);
                list[i] = m as Material;
            }
            r.sharedMaterials = list;
        }

        static void Activate(List<JObject> set, Dictionary<int, Object> made, int rootId)
        {
            foreach (JObject o in set)
            {
                if (Data.Kind(o) != "native" || Data.Cls(o) != "GameObject") continue;
                int id = (int)o["id"];
                if (id == rootId) continue;
                ((GameObject)made[id]).SetActive(IsActive(o));
            }
        }

        static void CheckAwake(GameObject root)
        {
            if (Generator == null)
            {
                report.Problem("no ForestGenerator on " + root.name);
                return;
            }
            bool listed = false;
            foreach (ForestGenerator g in ForestGenerator.All)
            {
                if (g == Generator) listed = true;
            }
            int modifiers = root.GetComponentsInChildren<NodeMapModifier>(false).Length;
            int generation = root.GetComponentsInChildren<IForestGeneration>(false).Length;
            Array haveModifiers = Hooks.ModifiersField.GetValue(Generator) as Array;
            Array haveGeneration = Hooks.GenerationField.GetValue(Generator) as Array;
            int m = haveModifiers == null ? -1 : haveModifiers.Length;
            int g2 = haveGeneration == null ? -1 : haveGeneration.Length;
            Mod.Log.Msg("forest: generator awake " + (listed ? "and listed" : "but NOT listed") + ", " + m + "/" + modifiers + " node map modifiers, " + g2 + "/" + generation + " generation parts, seed " + Seed(Generator));
            if (!listed || m != modifiers || g2 != generation) report.Problem("ForestGenerator.Awake saw a different tree than the one built");
        }

        public static int Seed(ForestGenerator g)
        {
            return (int)Hooks.SeedField.GetValue(g);
        }

        public static void Watch()
        {
            if (Generator == null || readyLogged || sinceBuild == null) return;
            ForestGeneration gen = Generator.GetGenerated(false);
            if (gen == null) return;
            readyLogged = true;
            Mod.Log.Msg("forest: generation ready " + sinceBuild.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s after the build: seed " + gen.Seed + ", " + gen.NodeCount + " nodes, " + gen.Edges.Count + " edges, bounds " + gen.Bounds);
        }

        public static Vector3 Vec3(JToken v)
        {
            return new Vector3(F(v[0]), F(v[1]), F(v[2]));
        }

        public static Quaternion Quat(JToken v)
        {
            return new Quaternion(F(v[0]), F(v[1]), F(v[2]), F(v[3]));
        }

        public static float F(JToken v)
        {
            return (float)(double)v;
        }

        public class Resolver
        {
            readonly Dictionary<int, Object> made;
            readonly NetworkScene networkScene;
            readonly Report rep;

            public Resolver(Dictionary<int, Object> made, NetworkScene networkScene, Report rep)
            {
                this.made = made;
                this.networkScene = networkScene;
                this.rep = rep;
            }

            public bool Convert(JToken v, Type t, string where, out object result)
            {
                result = null;
                if (typeof(Object).IsAssignableFrom(t))
                {
                    result = Ref(v, t, where);
                    return true;
                }
                if (typeof(UnityEngine.Events.UnityEventBase).IsAssignableFrom(t)) return false;
                if (t == typeof(string))
                {
                    result = (string)v;
                    return true;
                }
                if (t == typeof(bool))
                {
                    result = (bool)v;
                    return true;
                }
                if (t.IsEnum)
                {
                    result = Enum.ToObject(t, (long)v);
                    return true;
                }
                if (t == typeof(float))
                {
                    result = (float)(double)v;
                    return true;
                }
                if (t == typeof(double))
                {
                    result = (double)v;
                    return true;
                }
                if (t.IsPrimitive)
                {
                    result = System.Convert.ChangeType((long)v, t, CultureInfo.InvariantCulture);
                    return true;
                }
                if (Types.StructSize(t) > 0)
                {
                    result = UnityStruct(v, t);
                    return true;
                }
                if (t == typeof(AnimationCurve))
                {
                    result = Curve((JObject)v);
                    return true;
                }
                Type el = Types.Element(t);
                if (el != null)
                {
                    JArray a = (JArray)v;
                    IList list;
                    if (t.IsArray) list = Array.CreateInstance(el, a.Count);
                    else list = (IList)Activator.CreateInstance(t);
                    for (int i = 0; i < a.Count; i++)
                    {
                        object x;
                        Convert(a[i], el, where + "[" + i + "]", out x);
                        if (t.IsArray) list[i] = x;
                        else list.Add(x);
                    }
                    result = list;
                    return true;
                }
                object inst = New(t);
                foreach (JProperty p in ((JObject)v).Properties())
                {
                    FieldInfo f = Types.Field(t, p.Name);
                    if (f == null) continue;
                    object x;
                    if (Convert(p.Value, f.FieldType, where + "." + p.Name, out x)) f.SetValue(inst, x);
                }
                result = inst;
                return true;
            }

            static object New(Type t)
            {
                if (t.IsValueType) return Activator.CreateInstance(t);
                try
                {
                    return Activator.CreateInstance(t, true);
                }
                catch (MissingMethodException)
                {
                    return FormatterServices.GetUninitializedObject(t);
                }
            }

            static object UnityStruct(JToken v, Type t)
            {
                JArray a = (JArray)v;
                if (t == typeof(LayerMask))
                {
                    LayerMask m = new LayerMask();
                    m.value = (int)(long)a[0];
                    return m;
                }
                if (t == typeof(Vector2)) return new Vector2(F(a[0]), F(a[1]));
                if (t == typeof(Vector3)) return new Vector3(F(a[0]), F(a[1]), F(a[2]));
                if (t == typeof(Vector4)) return new Vector4(F(a[0]), F(a[1]), F(a[2]), F(a[3]));
                if (t == typeof(Quaternion)) return new Quaternion(F(a[0]), F(a[1]), F(a[2]), F(a[3]));
                if (t == typeof(Color)) return new Color(F(a[0]), F(a[1]), F(a[2]), F(a[3]));
                if (t == typeof(Color32)) return new Color32((byte)(long)a[0], (byte)(long)a[1], (byte)(long)a[2], (byte)(long)a[3]);
                if (t == typeof(Rect)) return new Rect(F(a[0]), F(a[1]), F(a[2]), F(a[3]));
                if (t == typeof(Vector2Int)) return new Vector2Int((int)(long)a[0], (int)(long)a[1]);
                if (t == typeof(Vector3Int)) return new Vector3Int((int)(long)a[0], (int)(long)a[1], (int)(long)a[2]);
                Vector3 center = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                Vector3 extent = new Vector3(F(a[3]), F(a[4]), F(a[5]));
                return new Bounds(center, extent * 2f);
            }

            static AnimationCurve Curve(JObject c)
            {
                JArray keys = (JArray)c["keys"];
                Keyframe[] frames = new Keyframe[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    JArray k = (JArray)keys[i];
                    Keyframe f = new Keyframe(F(k[0]), F(k[1]), F(k[2]), F(k[3]), F(k[5]), F(k[6]));
                    f.weightedMode = (WeightedMode)(int)(long)k[4];
                    frames[i] = f;
                }
                AnimationCurve curve = new AnimationCurve(frames);
                curve.preWrapMode = Wrap((int)c["pre"]);
                curve.postWrapMode = Wrap((int)c["post"]);
                return curve;
            }

            static WrapMode Wrap(int serialized)
            {
                if (serialized == 0) return WrapMode.PingPong;
                if (serialized == 1) return WrapMode.Loop;
                return WrapMode.ClampForever;
            }

            Object Ref(JToken v, Type t, string where)
            {
                if (v.Type == JTokenType.Null) return null;
                JObject r = (JObject)v;
                if (r["local"] != null)
                {
                    int id = (int)r["local"];
                    Object o;
                    if (!made.TryGetValue(id, out o) && !assets.TryGetValue(id, out o))
                    {
                        rep.Unresolved.Add(where + " -> local #" + id);
                        return null;
                    }
                    return Fit(o, t, where);
                }
                if (r["skip"] != null)
                {
                    rep.Skipped.Add((string)r["skip"] + " at " + where);
                    return null;
                }
                if (r["scene"] != null) return Fit(Scene((JObject)r["scene"], where), t, where);
                if (r["asset"] != null)
                {
                    string key = string.Join(":", new string[] { (string)r["src"][0], (string)r["src"][1] });
                    Object found;
                    if (!outside.TryGetValue(key, out found))
                    {
                        found = Asset((JObject)r["asset"]);
                        if (found != null)
                        {
                            outside[key] = found;
                            outsideCount++;
                        }
                    }
                    if (found == null)
                    {
                        rep.Unresolved.Add(where + " -> " + r["asset"].ToString(Newtonsoft.Json.Formatting.None));
                        return null;
                    }
                    return Fit(found, t, where);
                }
                rep.Problem(where + ": unknown reference");
                return null;
            }

            Object Fit(Object o, Type t, string where)
            {
                if (o == null) return null;
                if (t.IsInstanceOfType(o)) return o;
                GameObject go = o as GameObject;
                if (go != null && typeof(Component).IsAssignableFrom(t))
                {
                    Component c = go.GetComponent(t);
                    if (c != null) return c;
                }
                Component comp = o as Component;
                if (comp != null && t == typeof(GameObject)) return comp.gameObject;
                rep.Unresolved.Add(where + " -> " + o.GetType().Name + " '" + o.name + "' does not fit " + t.Name);
                return null;
            }

            Object Scene(JObject s, string where)
            {
                string cls = (string)s["cls"];
                if (cls == "Transform") return networkScene.transform;
                Type t = Types.Script((string)s["script"]);
                string go = (string)s["go"];
                if (t == null) return null;
                foreach (Object o in Object.FindObjectsOfType(t))
                {
                    Component c = o as Component;
                    if (c != null && c.gameObject.name == go && c.gameObject.scene == networkScene.gameObject.scene) return c;
                }
                rep.Unresolved.Add(where + " -> scene " + s.ToString(Newtonsoft.Json.Formatting.None));
                return null;
            }

            static Object Asset(JObject a)
            {
                Type t = Types.OfAsset(a);
                if (t == null) return null;
                if (a["sound"] != null) return Sounds.Get((int)a["sound"]);
                if (a["piece"] != null)
                {
                    StaticEnvironmentPiece piece = StaticEnvironmentPiece.Load((uint)(long)a["piece"]);
                    if (piece == null || piece.Prefab == null) return null;
                    GameObject pg = piece.Prefab.gameObject;
                    if (t == typeof(GameObject)) return pg;
                    return pg.GetComponent(t);
                }
                string go = (string)a["go"];
                if (a["path"] != null)
                {
                    string path = (string)a["path"];
                    if (go != null)
                    {
                        GameObject prefab = Resources.Load<GameObject>(path);
                        if (prefab == null) return null;
                        if (a["root"] == null) return prefab.GetComponent(t);
                        foreach (Component c in prefab.GetComponentsInChildren(t, true))
                        {
                            if (c.gameObject.name == go) return c;
                        }
                        return null;
                    }
                    return Resources.Load(path, t);
                }
                if (a["via"] != null) Resources.Load((string)a["via"]);
                return ByName(a, t);
            }

            static Object ByName(JObject a, Type t)
            {
                string go = (string)a["go"];
                string name = (string)a["name"];
                bool child = a["child"] != null && (bool)a["child"];
                List<Object> hits = new List<Object>();
                foreach (Object o in Resources.FindObjectsOfTypeAll(t))
                {
                    if (go != null)
                    {
                        Component c = o as Component;
                        if (c != null && !c.gameObject.scene.IsValid() && c.gameObject.name == go) hits.Add(o);
                        continue;
                    }
                    if (o.name != name) continue;
                    GameObject g = o as GameObject;
                    if (g != null && (g.scene.IsValid() || (g.transform.parent != null) != child)) continue;
                    hits.Add(o);
                }
                if (hits.Count > 1) Mod.Log.Warning("forest: " + hits.Count + " loaded objects match " + a.ToString(Newtonsoft.Json.Formatting.None) + ", using the first");
                return hits.Count > 0 ? hits[0] : null;
            }
        }
    }

    public static class Bake
    {
        const int WholeAtOnce = 24;
        const double FrameBudgetMs = 3.0;
        static readonly Type[] Instructions = { typeof(SpawnInstruction), typeof(ForestGCoreInstruction), typeof(RiverGCoreInstruction), typeof(SplineInstruction) };
        static readonly HashSet<int> seen = new HashSet<int>();
        static readonly List<GameObject> steps = new List<GameObject>();
        static readonly Stopwatch spent = new Stopwatch();
        static int baked;
        static int parts;
        static int spread;
        static long slowest;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            HarmonyMethod postfix = new HarmonyMethod(typeof(Bake).GetMethod("SpawnPostfix"));
            foreach (Type t in Instructions)
            {
                MethodInfo execute = t.GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (execute == null)
                {
                    Mod.Log.Warning("forest: game member not found: " + t.Name + ".Execute(object); what it spawns stays unbaked");
                    continue;
                }
                harmony.Patch(execute, null, postfix);
            }
        }

        public static void SpawnPostfix(IInstructionResult __result)
        {
            if (!Builder.Active || NetworkSceneManager.IsServer) return;
            try
            {
                Prepare(__result as Component);
            }
            catch (Exception e)
            {
                Mod.Warn("piece bake failed: " + e.GetType().Name + ": " + e.Message, e);
            }
        }

        static void Prepare(Component made)
        {
            if (made == null) return;
            GameObject go = made.gameObject;
            if (!go.scene.IsValid() || !seen.Add(go.GetInstanceID())) return;
            BuildRendererBakeable[] found = go.GetComponentsInChildren<BuildRendererBakeable>(true);
            if (found.Length == 0) return;
            baked++;
            parts += found.Length;
            if (found.Length <= WholeAtOnce)
            {
                Stopwatch w = Stopwatch.StartNew();
                spent.Start();
                BuildRendererBakeable.BakeBuildBakeables(go, false);
                spent.Stop();
                slowest = Math.Max(slowest, w.ElapsedMilliseconds);
                return;
            }
            spread++;
            for (int i = found.Length - 1; i >= 0; i--)
            {
                if (found[i] != null && !steps.Contains(found[i].gameObject)) steps.Add(found[i].gameObject);
            }
        }

        public static void Tick()
        {
            if (steps.Count == 0) return;
            Stopwatch w = Stopwatch.StartNew();
            spent.Start();
            while (steps.Count > 0 && w.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                GameObject go = steps[0];
                steps.RemoveAt(0);
                if (go != null && go.GetComponent<BuildRendererBakeable>() != null) BuildRendererBakeable.BakeBuildBakeables(go, false);
            }
            spent.Stop();
            slowest = Math.Max(slowest, w.ElapsedMilliseconds);
        }

        public static string Summary()
        {
            return "baked " + baked + " pieces (" + parts + " platform parts, " + spread + " spread over frames) in " + spent.ElapsedMilliseconds + " ms, slowest frame " + slowest + " ms";
        }
    }
}
