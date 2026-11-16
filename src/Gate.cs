using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.Serialization;
using HarmonyLib;

namespace QuestOverworld
{
    public static class Gate
    {
        public const uint Magic = 0x514F5731;
        public const int MaxWords = 8;
        public const int MetaQuest = 40;
        public const string MarkerStart = "QuestOverworld:";
        public const string Marker = "QuestOverworld: this server runs the PC overworld";
        public const string Refusal = "This server needs the QuestOverworld mod on your Quest: get it from the Download mods board.";
        const int RefusedCode = 1000;
        static readonly Dictionary<Connection, Seen> seen = new Dictionary<Connection, Seen>();
        public static Type RequestType;
        public static MethodInfo SerializeMethod;
        public static PropertyInfo ModeProperty;
        public static MethodInfo ApprovedMethod;
        public static MethodInfo SequenceMethod;
        public static readonly List<MethodInfo> AllowedMethods = new List<MethodInfo>();
        static int welcomed;
        static int refused;
        static int pc;
        static int platformPassed;

        public static bool ServerRunsOverworld;

        class Seen
        {
            public int Mode;
            public bool Marked;
        }

        public static List<string> Resolve()
        {
            List<string> missing = new List<string>();
            BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            RequestType = typeof(Connection).Assembly.GetType("Alta.Networking.RequestJoinMessage", false);
            SerializeMethod = RequestType == null ? null : RequestType.GetMethod("Serialize", inst, null, new Type[] { typeof(Connection), typeof(Stream) }, null);
            ModeProperty = RequestType == null ? null : RequestType.GetProperty("PlayerMode", inst);
            ApprovedMethod = typeof(JoinServerPipeline).GetMethod("ConnectionApprovedByServer", inst, null, new Type[] { typeof(Connection) }, null);
            SequenceMethod = typeof(JoinServerPipeline).GetMethod("ConnectionSequence", inst, null, new Type[] { typeof(Connection), typeof(Stream) }, null);
            FieldInfo message = typeof(JoinServerPipeline).GetField("lastPrerequisiteMessage", inst);
            if (SerializeMethod == null) missing.Add("RequestJoinMessage.Serialize(Connection, Stream)");
            if (ModeProperty == null || !ModeProperty.PropertyType.IsEnum || ModeProperty.PropertyType.Name != "PlayerMode") missing.Add("RequestJoinMessage.PlayerMode");
            if (ApprovedMethod == null) missing.Add("JoinServerPipeline.ConnectionApprovedByServer(Connection)");
            if (SequenceMethod == null) missing.Add("JoinServerPipeline.ConnectionSequence(Connection, Stream)");
            if (message == null || message.FieldType != typeof(string)) missing.Add("JoinServerPipeline.lastPrerequisiteMessage");
            if (typeof(ServerHandler).GetMethod("AddConnectionStep") == null) missing.Add("ServerHandler.AddConnectionStep");
            AllowedMethods.Clear();
            foreach (string name in new string[] { "CheckIfPlayerIsAllowed", "CheckIfPlayerIsAllowedCustom" })
            {
                MethodInfo m = typeof(ServerPlayerConnectionHandlerOld).GetMethod(name, inst);
                if (m == null) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 6 && p[5].Name == "platformTarget" && p[5].ParameterType == typeof(PlatformTarget)) AllowedMethods.Add(m);
                else missing.Add("ServerPlayerConnectionHandlerOld." + name + "(..., PlatformTarget platformTarget)");
            }
            if (AllowedMethods.Count == 0) missing.Add("ServerPlayerConnectionHandlerOld.CheckIfPlayerIsAllowed");
            if (!Enum.IsDefined(typeof(MessageType), "RequestJoinResponse")) missing.Add("MessageType.RequestJoinResponse");
            return missing;
        }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            HarmonyMethod post = new HarmonyMethod(typeof(Gate).GetMethod("SerializePostfix"));
            post.priority = Priority.Last;
            harmony.Patch(SerializeMethod, null, post);
            harmony.Patch(ApprovedMethod, new HarmonyMethod(typeof(Gate).GetMethod("ApprovedPrefix")));
            harmony.Patch(SequenceMethod, null, new HarmonyMethod(typeof(Gate).GetMethod("SequencePostfix")));
            foreach (MethodInfo m in AllowedMethods) harmony.Patch(m, new HarmonyMethod(typeof(Gate).GetMethod("AllowedPrefix")));
        }

        public static void AllowedPrefix(ref PlatformTarget platformTarget)
        {
            if (!Server.OnOverworld || (platformTarget & PlatformTarget.Quest) == 0 || (platformTarget & PlatformTarget.PC) != 0) return;
            platformTarget |= PlatformTarget.PC;
            Interlocked.Increment(ref platformPassed);
        }

        public static void SerializePostfix(object __instance, Connection connection, Stream stream)
        {
            try
            {
                if (stream.IsWriting)
                {
                    uint mark = Magic;
                    stream.SerializeUnsignedInt(ref mark);
                    return;
                }
                if (!Server.OnOverworld) return;
                int mode = Convert.ToInt32(ModeProperty.GetValue(__instance, null));
                bool marked = Scan(stream);
                lock (seen)
                {
                    Seen s;
                    if (!seen.TryGetValue(connection, out s))
                    {
                        s = new Seen();
                        seen[connection] = s;
                    }
                    s.Mode = mode;
                    s.Marked |= marked;
                }
            }
            catch (Exception e)
            {
                Mod.Warn("join check failed: " + e.GetType().Name + ": " + e.Message, e);
            }
        }

        public static bool Scan(Stream stream)
        {
            int read = 0;
            bool found = false;
            try
            {
                while (read < MaxWords && (long)stream.TotalByteCount * 8 - stream.BitIndex >= 32)
                {
                    uint word = 0;
                    stream.SerializeUnsignedInt(ref word);
                    read++;
                    if (word == Magic)
                    {
                        found = true;
                        break;
                    }
                }
            }
            finally
            {
                if (read > 0) stream.BackTrack((uint)(32 * read));
            }
            return found;
        }

        public static bool IsHeadset(int mode)
        {
            return (mode & MetaQuest) == MetaQuest;
        }

        public static void ApprovedPrefix()
        {
            ServerRunsOverworld = false;
        }

        public static void SequencePostfix(string ___lastPrerequisiteMessage)
        {
            if (ServerRunsOverworld || ___lastPrerequisiteMessage == null || !___lastPrerequisiteMessage.StartsWith(MarkerStart, StringComparison.Ordinal)) return;
            ServerRunsOverworld = true;
            Mod.Log.Msg("join: the server runs QuestOverworld; the PC overworld is rebuilt when " + Mod.QuestScene + " starts");
        }

        public static void AddStep(ServerHandler handler)
        {
            handler.AddConnectionStep(new ServerHandler.ConnectionStep(Step));
        }

        public static async Task Step(Connection connection, Action<string> progress)
        {
            Seen s;
            lock (seen)
            {
                seen.TryGetValue(connection, out s);
                seen.Remove(connection);
            }
            if (s != null && s.Marked)
            {
                Interlocked.Increment(ref welcomed);
                Mod.Log.Msg("join: let in " + connection.UserInfo + " with QuestOverworld (" + (IsHeadset(s.Mode) ? "headset" : "PC") + ", mode " + s.Mode + "); " + Summary());
                progress(Marker);
                await Task.Delay(300);
                return;
            }
            if (s == null || !IsHeadset(s.Mode))
            {
                Interlocked.Increment(ref pc);
                Mod.Log.Msg("join: let in " + connection.UserInfo + " without QuestOverworld (" + (s == null ? "join request not read" : "PC, mode " + s.Mode) + "); " + Summary());
                return;
            }
            Interlocked.Increment(ref refused);
            Mod.Log.Msg("join: turned away a headset without QuestOverworld (" + connection.UserInfo + "); " + Summary());
            progress(Refusal);
            await Task.Delay(2500);
            string text = Refusal;
            connection.Send(null, MessageType.RequestJoinResponse, delegate(Connection c, Stream st)
            {
                bool allowed = false;
                bool prerequisites = false;
                int code = RefusedCode;
                st.SerializeBool(ref allowed);
                st.SerializeBool(ref prerequisites);
                st.SerializeInteger(ref code);
                st.SerializeString(ref text);
            });
            connection.FlushPacketManagers();
            await Task.Delay(500);
            throw new InvalidOperationException("QuestOverworld: the headset has no QuestOverworld");
        }

        public static string Summary()
        {
            return "join check: " + welcomed + " let in with the mod, " + refused + " headsets turned away without it, " + pc + " PC players let in without it; " + platformPassed + " Quest-platform joins let past the server's PC-only target check";
        }
    }
}
