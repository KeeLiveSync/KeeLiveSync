// Prüft mit KeePass' eigener Synchronisation (PwDatabase.MergeIn, PwMergeMethod.Synchronize), ob ein
// „Überschreiben“ vom Handy beim nächsten Zusammenführen am PC wieder geheilt wird.
// Arbeitet nur mit Testdatenbanken im angegebenen Ordner (Testpasswort, keine echten Daten).

using System;
using System.IO;
using System.Linq;
using System.Threading;
using KeePassLib;
using KeePassLib.Interfaces;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;

namespace MergeTest
{
    internal static class Program
    {
        private static CompositeKey Key()
        {
            var k = new CompositeKey();
            k.AddUserKey(new KcpPassword("test"));
            return k;
        }

        private static PwDatabase Open(string path)
        {
            var db = new PwDatabase();
            db.Open(IOConnectionInfo.FromPath(path), Key(), new NullStatusLogger());
            return db;
        }

        private static PwEntry Find(PwDatabase db, string title)
        {
            return db.RootGroup.GetEntries(true).FirstOrDefault(e => e.Strings.ReadSafe(PwDefs.TitleField) == title);
        }

        private static void Edit(PwDatabase db, string title, string password)
        {
            var e = Find(db, title);
            e.CreateBackup(db);
            e.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, password));
            e.Touch(true);
        }

        private static void Add(PwDatabase db, string title, string password)
        {
            var e = new PwEntry(true, true);
            e.Strings.Set(PwDefs.TitleField, new ProtectedString(false, title));
            e.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, password));
            db.RootGroup.AddEntry(e, true);
        }

        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "make" || args[0] == "touch" || args[0] == "delete")) return MakeTestDb.Run(args);
            string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "kdbx-mergetest");
            Directory.CreateDirectory(dir);
            string basePath = Path.Combine(dir, "base.kdbx"), pc = Path.Combine(dir, "pc.kdbx"), phone = Path.Combine(dir, "handy.kdbx");

            // Ausgangsstand, den PC und Handy beide haben
            var b = new PwDatabase();
            b.New(IOConnectionInfo.FromPath(basePath), Key());
            foreach (var t in new[] { "E1", "E2", "E5", "E6" }) Add(b, t, "alt");
            b.Save(new NullStatusLogger());
            b.Close();
            File.Copy(basePath, pc, true);
            File.Copy(basePath, phone, true);
            Thread.Sleep(1100);

            // PC: E3 neu, E1 und E6 geändert – sein Stand ist noch nicht in der Cloud
            var p = Open(pc);
            Add(p, "E3", "pc");
            Edit(p, "E1", "pc");
            Edit(p, "E6", "pc");
            p.Save(new NullStatusLogger());
            p.Close();
            Thread.Sleep(1100);

            // Handy (auf altem Stand): E4 neu, E2 und E6 geändert (E6 später als der PC), E5 gelöscht – überschreibt dann die Cloud-Datei
            var h = Open(phone);
            Add(h, "E4", "handy");
            Edit(h, "E2", "handy");
            Edit(h, "E6", "handy");
            var e5 = Find(h, "E5");
            h.RootGroup.Entries.Remove(e5);
            h.DeletedObjects.Add(new PwDeletedObject(e5.Uuid, DateTime.UtcNow));
            h.Save(new NullStatusLogger());
            h.Close();

            // PC holt die (vom Handy überschriebene) Cloud-Datei und führt zusammen
            var target = Open(pc);
            var source = Open(phone);
            target.MergeIn(source, PwMergeMethod.Synchronize, null);
            source.Close();

            Console.WriteLine("Ergebnis am PC nach dem Zusammenführen:");
            int fails = 0;
            Action<string, string, string> expect = (title, pw, why) =>
            {
                var e = Find(target, title);
                string got = e == null ? "(fehlt)" : e.Strings.ReadSafe(PwDefs.PasswordField);
                string hist = e == null ? "" : "  Historie: " + string.Join(", ", e.History.Select(x => x.Strings.ReadSafe(PwDefs.PasswordField)));
                bool ok = got == pw;
                if (!ok) fails++;
                Console.WriteLine((ok ? "  OK   " : "  FEHL ") + title + " = " + got + " (erwartet " + pw + ")" + hist + "  – " + why);
            };
            expect("E1", "pc", "nur am PC geändert");
            expect("E2", "handy", "nur am Handy geändert");
            expect("E3", "pc", "nur am PC neu – fehlte in der überschriebenen Datei");
            expect("E4", "handy", "nur am Handy neu");
            expect("E5", "(fehlt)", "am Handy gelöscht");
            expect("E6", "handy", "beide geändert, Handy später");
            target.Close();
            Console.WriteLine(fails == 0 ? "Alle Erwartungen erfüllt." : fails + " Abweichung(en).");
            return fails == 0 ? 0 : 3;
        }
    }
}
