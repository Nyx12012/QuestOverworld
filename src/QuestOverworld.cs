using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Alta.Chunks;
using Alta.Forests;
using Alta.Networking;
using Alta.Networking.Servers;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(QuestOverworld.Mod), "QuestOverworld", QuestOverworld.Mod.Version, "Shadow")]
[assembly: MelonGame("Alta", "A Township Tale")]
[assembly: HarmonyDontPatchAll]

namespace QuestOverworld
{
    public class Mod : MelonMod
    {
        public const string Version = "0.1.0";
        public const string ServerScene = "Overworld Chunked";
        public const string QuestScene = "Overworld Chunked[Q]";
        internal static MelonLogger.Instance Log;
        internal static bool Armed;
        public static Table Map;
        static float nextWatch;
        static string lastError;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            try
            {
                Map = Table.FromAssembly(typeof(Mod).Assembly);
                Data.Load();
            }
            catch (Exception e)
            {
                Log.Error("the embedded data did not load, nothing is hooked: " + e);
                return;
            }
            List<string> bad = Map.Problems();
            if (bad.Count > 0)
            {
                foreach (string b in bad) Log.Error("overworld: table problem: " + b);
                Log.Error("overworld: the embedded table is broken, nothing is hooked");
                return;
            }
            Armed = Hooks.Install(HarmonyInstance);
            if (!Armed) return;
            Log.Msg("QuestOverworld " + Version + " is running: a server on " + ServerScene + " lists itself for headsets and checks their joins; a client on " + QuestScene + " rebuilds the PC overworld when the server runs QuestOverworld. Table " + Map.Hash + " (from " + Map.Source + "), Forest data " + Data.Hash + ", " + Data.Objects.Count + " objects");
        }

        public override void OnUpdate()
        {
            if (!Armed) return;
            try
            {
                Server.Tick();
                Bake.Tick();
                if (Time.unscaledTime >= nextWatch)
                {
                    nextWatch = Time.unscaledTime + 1f;
                    Builder.Watch();
                }
            }
            catch (Exception e)
            {
                Warn("update failed: " + e.Message, e);
            }
        }

        internal static void Warn(string message, Exception e)
        {
            if (message == lastError || Log == null) return;
            lastError = message;
            Log.Warning(message);
            if (e != null) Log.Msg(e.ToString());
        }
    }

    public static class Hooks
    {
        public static MethodInfo SceneInitialize;
        public static FieldInfo HashField;
        public static FieldInfo SkipField;
        public static FieldInfo SeedField;
        public static FieldInfo ModifiersField;
        public static FieldInfo GenerationField;
        public static FieldInfo SoundMapField;

        public static List<string> Resolve()
        {
            List<string> missing = new List<string>();
            BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in typeof(NetworkScene).GetMethods(inst))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "Initialize" && p.Length == 2 && p[0].ParameterType == typeof(ISocket) && p[0].Name == "socket" && p[1].ParameterType == typeof(Action<string>)) SceneInitialize = m;
            }
            HashField = typeof(NetworkEntity).GetField("hash", inst);
            SkipField = typeof(LocationChunk).GetField("isSkippingStaticContent", inst);
            SeedField = typeof(ForestGenerator).GetField("seed", inst);
            ModifiersField = typeof(ForestGenerator).GetField("nodeMapModifiers", inst);
            GenerationField = typeof(ForestGenerator).GetField("forestGeneration", inst);
            SoundMapField = typeof(AudioManager).GetField("currentSoundEffectsMapping", inst);
            PropertyInfo embedded = typeof(NetworkScene).GetProperty("Embedded");
            PropertyInfo embeddedId = typeof(NetworkEntity).GetProperty("EmbeddedId");
            PropertyInfo hash = typeof(NetworkEntity).GetProperty("Hash");
            if (SceneInitialize == null) missing.Add("NetworkScene.Initialize(ISocket socket, Action<string>)");
            if (embedded == null || embedded.PropertyType != typeof(List<NetworkEntity>)) missing.Add("NetworkScene.Embedded as List<NetworkEntity>");
            if (embeddedId == null || !embeddedId.CanWrite || embeddedId.PropertyType != typeof(int)) missing.Add("NetworkEntity.EmbeddedId { get; set; }");
            if (hash == null || hash.PropertyType != typeof(uint)) missing.Add("NetworkEntity.Hash");
            if (HashField == null || HashField.FieldType != typeof(int)) missing.Add("NetworkEntity.hash");
            if (SkipField == null || SkipField.FieldType != typeof(bool)) missing.Add("LocationChunk.isSkippingStaticContent");
            if (typeof(OverworldChunk).GetProperty("GridXIndex") == null || typeof(OverworldChunk).GetProperty("GridZIndex") == null) missing.Add("OverworldChunk.GridXIndex, GridZIndex");
            if (SeedField == null || SeedField.FieldType != typeof(int)) missing.Add("ForestGenerator.seed");
            if (ModifiersField == null || !ModifiersField.FieldType.IsArray) missing.Add("ForestGenerator.nodeMapModifiers");
            if (GenerationField == null || !GenerationField.FieldType.IsArray) missing.Add("ForestGenerator.forestGeneration");
            if (SoundMapField == null || SoundMapField.FieldType != typeof(SoundEffectsMap)) missing.Add("AudioManager.currentSoundEffectsMapping");
            return missing;
        }

        public static bool Install(HarmonyLib.Harmony harmony)
        {
            List<string> missing = Resolve();
            missing.AddRange(Gate.Resolve());
            if (missing.Count > 0)
            {
                foreach (string m in missing) Mod.Log.Error("game member not found: " + m);
                Mod.Log.Error("this game build does not match QuestOverworld " + Mod.Version + ", nothing is hooked");
                return false;
            }
            harmony.Patch(SceneInitialize, new HarmonyMethod(typeof(Hooks).GetMethod("ScenePrefix")));
            Gate.Install(harmony);
            Bake.Install(harmony);
            Listing.Install(harmony);
            return true;
        }

        public static void ScenePrefix(NetworkScene __instance, ISocket socket)
        {
            try
            {
                bool server = socket != null ? socket.IsServer : NetworkSceneManager.IsServer;
                string name = __instance.gameObject.scene.name;
                if (server) Server.OnScene(name);
                else Overworld.OnScene(__instance, name);
            }
            catch (Exception e)
            {
                Mod.Log.Error("overworld: scene setup failed: " + e);
            }
        }
    }

    public static class Overworld
    {
        static NetworkScene doneFor;

        public static void OnScene(NetworkScene networkScene, string name)
        {
            if (name != Mod.QuestScene) return;
            if (doneFor == networkScene) return;
            doneFor = networkScene;
            if (!Gate.ServerRunsOverworld)
            {
                Mod.Log.Msg("overworld: this server doesn't run QuestOverworld; the scene stays as it is");
                return;
            }
            Rebuild(networkScene);
        }

        static void Fail(string what, List<string> problems)
        {
            Mod.Log.Error("overworld: " + what + " (" + problems.Count + " problems); NOTHING was changed, this join will desync");
            for (int i = 0; i < problems.Count && i < 20; i++) Mod.Log.Error("overworld:   " + problems[i]);
        }

        static void Rebuild(NetworkScene networkScene)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Table map = Mod.Map;
            List<NetworkEntity> list = networkScene.Embedded;
            List<NetworkEntity> old = new List<NetworkEntity>(list);
            List<string> bad = map.Match(old.Count, delegate(int i) { return i < 0 || i >= old.Count || old[i] == null ? -1L : (long)old[i].Hash; });
            foreach (Unskip u in map.Chunks)
            {
                if (bad.Count > 0) break;
                OverworldChunk c = old[map.Rows[u.Id].QuestId].GetComponent<OverworldChunk>();
                if (c == null || c.GridXIndex != u.X || c.GridZIndex != u.Z) bad.Add("level4 id " + u.Id + " is not chunk " + u.X + "-" + u.Z);
            }
            if (bad.Count > 0)
            {
                Fail("the scene does not match the table " + map.Hash, bad);
                return;
            }
            Dictionary<uint, NetworkEntity> built;
            try
            {
                built = Builder.Build(networkScene);
            }
            catch (Exception e)
            {
                Builder.Discard();
                Mod.Log.Error("forest: build failed: " + e);
                Fail("the Forest did not build", new List<string> { e.GetType().Name + ": " + e.Message });
                return;
            }
            foreach (uint h in Table.Built)
            {
                NetworkEntity e;
                if (!built.TryGetValue(h, out e) || e == null) bad.Add("the Forest data made no entity with hash " + h);
            }
            if (bad.Count > 0)
            {
                Builder.Discard();
                Fail("the built Forest is not what the table expects", bad);
                return;
            }
            long forestMs = timer.ElapsedMilliseconds;
            List<string> rehashed = new List<string>();
            List<string> forest = new List<string>();
            int kept = 0;
            for (int i = 0; i < map.Rows.Count; i++)
            {
                Row r = map.Rows[i];
                NetworkEntity e = r.Build ? built[r.Hash] : old[r.QuestId];
                list[i] = e;
                e.EmbeddedId = i;
                if (r.Build) forest.Add(e.name + " at " + i);
                else if (r.QuestId == i) kept++;
                if (e.Hash != r.Hash)
                {
                    rehashed.Add(e.name + " " + e.Hash + " -> " + r.Hash);
                    HashField.SetValue(e, (int)r.Hash);
                }
            }
            List<string> left = new List<string>();
            foreach (HeadsetOnly h in map.Headset) left.Add(old[h.QuestId].name + " (was " + h.QuestId + ")");
            List<string> chunks = new List<string>();
            foreach (Unskip u in map.Chunks)
            {
                LocationChunk c = list[u.Id].GetComponent<OverworldChunk>();
                bool was = (bool)SkipField.GetValue(c);
                SkipField.SetValue(c, false);
                chunks.Add(u.X + "-" + u.Z + " at " + u.Id + (was ? " (skipped its static content, now loads it)" : " (already loaded its static content)"));
            }
            Mod.Log.Msg("overworld: scene rebuilt in level4's order in " + timer.ElapsedMilliseconds + " ms (Forest " + forestMs + " ms): " + list.Count + " ids, " + kept + " kept their id, " + (map.Rows.Count - Table.Built.Length - kept) + " moved; Forest " + string.Join(", ", forest.ToArray()) + "; level4's hash for " + string.Join(", ", rehashed.ToArray()) + "; left out " + string.Join(", ", left.ToArray()));
            Mod.Log.Msg("overworld: chunks " + string.Join("; ", chunks.ToArray()));
        }

        static FieldInfo HashField
        {
            get { return Hooks.HashField; }
        }

        static FieldInfo SkipField
        {
            get { return Hooks.SkipField; }
        }
    }

    public static class Server
    {
        public static volatile bool OnOverworld;
        static bool stepAdded;

        public static void OnScene(string name)
        {
            OnOverworld = name == Mod.ServerScene;
            if (!OnOverworld)
            {
                Mod.Log.Msg("server: scene " + name + " is not " + Mod.ServerScene + "; QuestOverworld stays off here");
                return;
            }
            Mod.Log.Msg("server: " + name + ": headsets need QuestOverworld to join, PC players join as before; listing for headsets " + (Listing.On ? "on" : "off (ListForHeadsets)"));
            Tick();
        }

        public static void Tick()
        {
            if (!OnOverworld || stepAdded) return;
            ServerHandler handler = ServerHandler.Current;
            if (handler == null) return;
            stepAdded = true;
            Gate.AddStep(handler);
            Mod.Log.Msg("server: join check queued");
        }
    }
}
