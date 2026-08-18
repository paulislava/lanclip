using System;

namespace LanClip.Tests
{
    static class VersionTests
    {
        public static void Register()
        {
            T.Run("protocol version is 1", delegate
            {
                T.Eq(1, LanClip.Version.Protocol, "protocol");
            });
        }
    }

    static class Program
    {
        static int Main()
        {
            VersionTests.Register();
            RelPathTests.Register();
            ManifestTests.Register();
            ConfigTests.Register();
            return T.Summary();
        }
    }
}
