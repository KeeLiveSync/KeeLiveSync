// Legt eine Test-Datenbank an (Passwort „test“, keine echten Daten): MergeTest.exe make <datei> [anzahl]
// oder ändert/ergänzt Einträge in einer vorhandenen: MergeTest.exe touch <datei> <titel> <passwort> [gruppe]
// oder löscht einen Eintrag endgültig (mit Löschvermerk): MergeTest.exe delete <datei> <titel>

using System.IO;
using KeePassLib;
using KeePassLib.Interfaces;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;

namespace MergeTest
{
    internal static class MakeTestDb
    {
        internal static int Run(string[] args)
        {
            var key = new CompositeKey();
            key.AddUserKey(new KcpPassword("test"));
            // Testdatenbank mit Schlüsseldatei: Umgebungsvariable MERGETEST_KEYFILE auf den Pfad setzen
            string keyFile = System.Environment.GetEnvironmentVariable("MERGETEST_KEYFILE");
            if (!string.IsNullOrEmpty(keyFile)) key.AddUserKey(new KcpKeyFile(keyFile));
            if (args[0] == "make")
            {
                string path = args[1];
                int count = args.Length > 2 ? int.Parse(args[2]) : 80;
                if (File.Exists(path)) File.Delete(path);
                var db = new PwDatabase();
                db.New(IOConnectionInfo.FromPath(path), key);
                db.Name = "KeeLiveSync Test";
                string[] groups = { "Banken", "Shopping", "Haushalt", "Arbeit" };
                foreach (var gname in groups)
                {
                    var g = new PwGroup(true, true, gname, PwIcon.Folder);
                    db.RootGroup.AddGroup(g, true);
                }
                for (int i = 1; i <= count; i++)
                {
                    var e = new PwEntry(true, true);
                    e.Strings.Set(PwDefs.TitleField, new ProtectedString(false, "Testeintrag " + i.ToString("000")));
                    e.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, "nutzer" + i + "@example.com"));
                    e.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, "pw-" + i));
                    e.Strings.Set(PwDefs.UrlField, new ProtectedString(false, "https://example.com/" + i));
                    db.RootGroup.AddEntry(e, true);
                }
                db.Save(new NullStatusLogger());
                db.Close();
                System.Console.WriteLine("angelegt: " + path + " (" + count + " Einträge)");
                return 0;
            }
            if (args[0] == "touch")
            {
                var db = new PwDatabase();
                db.Open(IOConnectionInfo.FromPath(args[1]), key, new NullStatusLogger());
                PwEntry found = null;
                foreach (var e in db.RootGroup.GetEntries(true))
                    if (e.Strings.ReadSafe(PwDefs.TitleField) == args[2]) found = e;
                if (found == null)
                {
                    found = new PwEntry(true, true);
                    found.Strings.Set(PwDefs.TitleField, new ProtectedString(false, args[2]));
                    // optional 5. Argument: Name einer Gruppe direkt unter der Wurzel
                    PwGroup target = db.RootGroup;
                    if (args.Length > 4)
                        foreach (var g in db.RootGroup.Groups)
                            if (g.Name == args[4]) target = g;
                    target.AddEntry(found, true);
                }
                else
                {
                    found.CreateBackup(db);
                }
                found.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, args[3]));
                found.Touch(true);
                db.Save(new NullStatusLogger());
                db.Close();
                System.Console.WriteLine("geändert: " + args[2]);
                return 0;
            }
            if (args[0] == "delete")
            {
                var db = new PwDatabase();
                db.Open(IOConnectionInfo.FromPath(args[1]), key, new NullStatusLogger());
                PwEntry found = null;
                foreach (var e in db.RootGroup.GetEntries(true))
                    if (e.Strings.ReadSafe(PwDefs.TitleField) == args[2]) found = e;
                if (found == null)
                {
                    System.Console.WriteLine("nicht gefunden: " + args[2]);
                    db.Close();
                    return 1;
                }
                found.ParentGroup.Entries.Remove(found);
                db.DeletedObjects.Add(new PwDeletedObject(found.Uuid, System.DateTime.UtcNow));
                db.Save(new NullStatusLogger());
                db.Close();
                System.Console.WriteLine("gelöscht: " + args[2]);
                return 0;
            }
            return 1;
        }
    }
}
