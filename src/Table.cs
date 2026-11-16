using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuestOverworld
{
    public class Row
    {
        public int Id;
        public uint Hash;
        public int QuestId;
        public uint QuestHash;
        public string By;

        public bool Build
        {
            get { return QuestId < 0; }
        }
    }

    public class HeadsetOnly
    {
        public int QuestId;
        public uint Hash;
        public string Name;
    }

    public class Unskip
    {
        public int Id;
        public int X;
        public int Z;
    }

    public class Table
    {
        public const string Resource = "overworld-table.txt";
        public const int Count = 4162;
        public static readonly uint[] Built = { 64120, 2300 };
        public readonly List<Row> Rows = new List<Row>();
        public readonly List<HeadsetOnly> Headset = new List<HeadsetOnly>();
        public readonly List<Unskip> Chunks = new List<Unskip>();
        public string Source;
        public string Hash;

        public static Table FromAssembly(System.Reflection.Assembly assembly)
        {
            using (Stream s = assembly.GetManifestResourceStream(Resource))
            {
                if (s == null) throw new InvalidOperationException(Resource + " is not inside the DLL");
                using (MemoryStream m = new MemoryStream())
                {
                    byte[] buffer = new byte[65536];
                    int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0) m.Write(buffer, 0, n);
                    return Parse(m.ToArray());
                }
            }
        }

        public static Table Parse(byte[] bytes)
        {
            Table t = new Table();
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    t.Hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
                }
            }
            catch (Exception e)
            {
                t.Hash = "unhashed (" + e.GetType().Name + ")";
            }
            foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] p = line.Split(' ');
                switch (p[0])
                {
                    case "source":
                        t.Source = p[1];
                        break;
                    case "row":
                        Row r = new Row();
                        r.Id = Int(p[1]);
                        r.Hash = (uint)Int(p[2]);
                        r.QuestId = p[3] == "-" ? -1 : Int(p[3]);
                        r.QuestHash = p[4] == "-" ? 0 : (uint)Int(p[4]);
                        r.By = p[5];
                        t.Rows.Add(r);
                        break;
                    case "headset":
                        HeadsetOnly h = new HeadsetOnly();
                        h.QuestId = Int(p[1]);
                        h.Hash = (uint)Int(p[2]);
                        h.Name = string.Join(" ", p, 3, p.Length - 3);
                        t.Headset.Add(h);
                        break;
                    case "unskip":
                        Unskip u = new Unskip();
                        u.Id = Int(p[1]);
                        u.X = Int(p[2]);
                        u.Z = Int(p[3]);
                        t.Chunks.Add(u);
                        break;
                    default:
                        throw new InvalidDataException(Resource + ": unknown line '" + line + "'");
                }
            }
            return t;
        }

        static int Int(string s)
        {
            return int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        public List<string> Problems()
        {
            List<string> bad = new List<string>();
            if (Rows.Count != Count) bad.Add(Rows.Count + " rows, level4 has " + Count + " embedded ids");
            bool[] used = new bool[Count];
            int built = 0;
            for (int i = 0; i < Rows.Count; i++)
            {
                Row r = Rows[i];
                if (r.Id != i) bad.Add("row " + i + " has id " + r.Id + ": ids are not dense");
                if (r.Build)
                {
                    built++;
                    if (r.By != "build") bad.Add("row " + r.Id + " has no headset entity but says " + r.By);
                    if (Array.IndexOf(Built, r.Hash) < 0) bad.Add("row " + r.Id + " is built but hash " + r.Hash + " is not the Forest's or its atmosphere's");
                    continue;
                }
                if (r.QuestId >= Count)
                {
                    bad.Add("row " + r.Id + " takes headset id " + r.QuestId + ", past the end");
                    continue;
                }
                if (used[r.QuestId]) bad.Add("headset id " + r.QuestId + " is used twice");
                used[r.QuestId] = true;
                if (r.By == "hash" && r.QuestHash != r.Hash) bad.Add("row " + r.Id + " matched by hash but the hashes differ");
                if (r.By == "path" && r.QuestHash == r.Hash) bad.Add("row " + r.Id + " matched by path but the hashes agree");
                if (r.By != "hash" && r.By != "path") bad.Add("row " + r.Id + " says " + r.By);
            }
            if (built != Built.Length) bad.Add(built + " rows to build, the Forest data has " + Built.Length);
            foreach (uint h in Built)
            {
                if (Rows.FindAll(delegate(Row r) { return r.Build && r.Hash == h; }).Count != 1) bad.Add("hash " + h + " is not built exactly once");
            }
            if (Headset.Count != 2) bad.Add(Headset.Count + " headset-only entities, expected 2");
            foreach (HeadsetOnly h in Headset)
            {
                if (h.QuestId < 0 || h.QuestId >= Count) bad.Add("headset-only " + h.Name + " has id " + h.QuestId);
                else if (used[h.QuestId]) bad.Add("headset-only " + h.Name + " (" + h.QuestId + ") is also used by a row");
                else used[h.QuestId] = true;
            }
            for (int q = 0; q < Count; q++)
            {
                if (!used[q])
                {
                    bad.Add("headset id " + q + " is neither used nor left out");
                    break;
                }
            }
            if (Chunks.Count != 2) bad.Add(Chunks.Count + " chunks to unskip, expected 2");
            foreach (Unskip u in Chunks)
            {
                if (u.Id < 0 || u.Id >= Rows.Count || Rows[u.Id].Build) bad.Add("chunk " + u.X + "-" + u.Z + " points at id " + u.Id);
            }
            return bad;
        }

        public List<string> Match(int count, Func<int, long> hashAt)
        {
            List<string> bad = new List<string>();
            if (count != Count)
            {
                bad.Add("the scene has " + count + " embedded entities, the table expects " + Count);
                return bad;
            }
            foreach (Row r in Rows)
            {
                if (r.Build) continue;
                long h = hashAt(r.QuestId);
                if (h < 0) bad.Add("scene entity " + r.QuestId + " is missing");
                else if (h != r.QuestHash) bad.Add("scene entity " + r.QuestId + " has hash " + h + ", the table expects " + r.QuestHash + " for level4 id " + r.Id);
            }
            foreach (HeadsetOnly o in Headset)
            {
                long h = hashAt(o.QuestId);
                if (h != o.Hash) bad.Add("headset-only " + o.Name + " at " + o.QuestId + " has hash " + h + ", the table expects " + o.Hash);
            }
            return bad;
        }

        public Row BuiltRow(uint hash)
        {
            return Rows.Find(delegate(Row r) { return r.Build && r.Hash == hash; });
        }
    }
}
