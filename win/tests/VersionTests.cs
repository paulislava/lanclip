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
            CliArgsTests.Register();
            RelPathTests.Register();
            ManifestTests.Register();
            ConfigTests.Register();
            SnapshotTests.Register();
            StaExecutorTests.Register();
            HttpServerTests.Register();
            HttpClientTests.Register();
            StagingTests.Register();
            PeerResolverTests.Register();
            PullClientTests.Register();
            WinClipboardTests.Register();
            return T.Summary();
        }
    }
}
