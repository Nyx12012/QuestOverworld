using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;

namespace QuestOverworld
{
    public static class Listing
    {
        public const string PayloadType = "TavernLib.Backend.Server.ServerListingPayload";
        static MelonPreferences_Category prefs;
        static MelonPreferences_Entry<bool> listForHeadsets;
        public static MethodInfo FromConfig;
        public static FieldInfo QuestField;
        public static FieldInfo TutorialField;
        static int flipped;

        public static bool On
        {
            get { return listForHeadsets == null || listForHeadsets.Value; }
        }

        public static string Bind(Type payload)
        {
            if (payload == null) return "TavernLib's " + PayloadType + " not found";
            BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FromConfig = payload.GetMethod("FromConfig", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            QuestField = payload.GetField("<Quest>k__BackingField", inst);
            TutorialField = payload.GetField("<Tutorial>k__BackingField", inst);
            if (FromConfig == null || FromConfig.ReturnType != payload) return "TavernLib's ServerListingPayload.FromConfig not found";
            if (QuestField == null || QuestField.FieldType != typeof(bool)) return "TavernLib's ServerListingPayload.Quest not found";
            if (TutorialField == null || TutorialField.FieldType != typeof(bool)) return "TavernLib's ServerListingPayload.Tutorial not found";
            return null;
        }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            prefs = MelonPreferences.CreateCategory("QuestOverworld");
            listForHeadsets = prefs.CreateEntry<bool>("ListForHeadsets", true, "List for headsets", "While this server runs the PC overworld, tell the server list it is a Quest server so headsets with QuestOverworld find it on the board. PC launchers still load the PC overworld.");
            Type payload = AccessTools.TypeByName(PayloadType);
            string missing = Bind(payload);
            if (missing != null)
            {
                Mod.Log.Warning("listing: " + missing + "; headsets won't find this server on the board (needs TavernLib 1.5.3 or newer)");
                return;
            }
            harmony.Patch(FromConfig, null, new HarmonyMethod(typeof(Listing).GetMethod("Postfix")));
            Mod.Log.Msg("listing: hooked " + Version(payload.Assembly) + " ServerListingPayload.FromConfig; " + (On ? "on Overworld Chunked the listing says quest: true" : "ListForHeadsets is off, the listing stays as TavernLib makes it"));
        }

        static string Version(Assembly a)
        {
            foreach (object o in a.GetCustomAttributes(typeof(MelonInfoAttribute), false)) return "TavernLib " + ((MelonInfoAttribute)o).Version;
            return a.GetName().Name;
        }

        public static void Postfix(ref object __result)
        {
            try
            {
                if (!On || !Server.OnOverworld || __result == null) return;
                if ((bool)TutorialField.GetValue(__result) || (bool)QuestField.GetValue(__result)) return;
                QuestField.SetValue(__result, true);
                if (Interlocked.Increment(ref flipped) == 1 && Mod.Log != null) Mod.Log.Msg("listing: the server list now hears quest: true, so headsets with QuestOverworld find this server on the board");
            }
            catch (Exception e)
            {
                Mod.Warn("listing: " + e.GetType().Name + ": " + e.Message, e);
            }
        }
    }
}
