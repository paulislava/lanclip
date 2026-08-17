using System;
using System.Collections.Generic;

namespace LanClip.Tests
{
    static class T
    {
        static int passed = 0;
        static readonly List<string> failures = new List<string>();

        public static void Run(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failures.Add(name + " threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Eq<TV>(TV expected, TV actual, string name)
        {
            if (!object.Equals(expected, actual))
            {
                failures.Add(name + ": expected <" + Show(expected) + ">, got <" + Show(actual) + ">");
            }
            else
            {
                passed++;
            }
        }

        public static void True(bool cond, string name)
        {
            if (!cond) { failures.Add(name + ": expected true"); } else { passed++; }
        }

        public static void Throws<TE>(Action body, string name) where TE : Exception
        {
            try
            {
                body();
                failures.Add(name + ": expected " + typeof(TE).Name + ", nothing thrown");
            }
            catch (TE)
            {
                passed++;
            }
            catch (Exception e)
            {
                failures.Add(name + ": expected " + typeof(TE).Name + ", got " + e.GetType().Name);
            }
        }

        static string Show(object v)
        {
            if (v == null) { return "null"; }
            return v.ToString();
        }

        public static int Summary()
        {
            foreach (string f in failures) { Console.WriteLine("FAIL " + f); }
            Console.WriteLine("passed=" + passed + " failed=" + failures.Count);
            return failures.Count == 0 ? 0 : 1;
        }
    }
}
