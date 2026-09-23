using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimServerGuard.Tests
{
    // A deliberately small harness. The plugin has two NuGet dependencies; a test
    // framework would be a bigger change than the tests themselves.
    internal static class T
    {
        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> TempDirs = new List<string>();
        private static string _current = "";
        private static int _run, _failed;

        public static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name);
        }

        public static void Run(string name, Action body)
        {
            _run++;
            _current = name;
            int before = Failures.Count;
            try { body(); }
            catch (Exception ex) { Failures.Add("threw " + ex.GetType().Name + ": " + ex.Message); }
            bool ok = Failures.Count == before;
            if (!ok) _failed++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            for (int i = before; i < Failures.Count; i++) Console.WriteLine("          " + Failures[i]);
        }

        public static void True(bool condition, string what)
        {
            if (!condition) Failures.Add("expected: " + what);
        }

        public static void False(bool condition, string what)
        {
            if (condition) Failures.Add("expected not: " + what);
        }

        public static void Equal<TValue>(TValue expected, TValue actual, string what)
        {
            if (!EqualityComparer<TValue>.Default.Equals(expected, actual))
                Failures.Add(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        // A fresh directory under the system temp folder, deleted when the run ends.
        public static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "serverguard-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            TempDirs.Add(dir);
            return dir;
        }

        public static int Finish()
        {
            foreach (var dir in TempDirs)
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine("ServerGuard tests: " + (_run - _failed) + "/" + _run + " passed.");
            return _failed == 0 ? 0 : 1;
        }
    }

    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("ServerGuard Unity-free tests (Shared/CustomsProtocol.cs, Shared/CustomsLedger.cs)");
            CustomsItemsTests.Run();
            CustomsWireTests.Run();
            CustomsPolicyTests.Run();
            CustomsSessionTests.Run();
            CustomsStoreTests.Run();
            return T.Finish();
        }
    }
}
