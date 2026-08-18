using System;

namespace LanClip.Tests
{
    static class CliArgsTests
    {
        public static void Register()
        {
            T.Run("default command is serve", delegate
            {
                CliArgs args = CliArgs.Parse(new string[0]);
                T.Eq("serve", args.Command, "command");
                T.Eq(Config.DefaultPath(), args.ConfigPath, "configPath");
            });

            T.Run("config flag after command is respected", delegate
            {
                CliArgs args = CliArgs.Parse(new string[] { "status", "--config", "/tmp/lanclip-test-config.json" });
                T.Eq("status", args.Command, "command");
                T.Eq("/tmp/lanclip-test-config.json", args.ConfigPath, "configPath");
            });

            T.Run("config flag before command is respected", delegate
            {
                CliArgs args = CliArgs.Parse(new string[] { "--config", "/tmp/lanclip-test-config.json", "pull" });
                T.Eq("pull", args.Command, "command");
                T.Eq("/tmp/lanclip-test-config.json", args.ConfigPath, "configPath");
            });

            T.Run("unknown command throws", delegate
            {
                try
                {
                    CliArgs.Parse(new string[] { "bogus" });
                    T.True(false, "expected CliArgsException");
                }
                catch (CliArgsException e)
                {
                    T.Eq(CliArgsException.CodeUnknownCommand, e.Code, "code");
                    T.Eq("bogus", e.CommandName, "commandName");
                }
            });

            T.Run("help short flag is recognized", delegate
            {
                try
                {
                    CliArgs.Parse(new string[] { "-h" });
                    T.True(false, "expected CliArgsException");
                }
                catch (CliArgsException e)
                {
                    T.Eq(CliArgsException.CodeHelpRequested, e.Code, "code");
                }
            });

            T.Run("help long flag is recognized", delegate
            {
                try
                {
                    CliArgs.Parse(new string[] { "--help" });
                    T.True(false, "expected CliArgsException");
                }
                catch (CliArgsException e)
                {
                    T.Eq(CliArgsException.CodeHelpRequested, e.Code, "code");
                }
            });

            T.Run("missing config value throws", delegate
            {
                try
                {
                    CliArgs.Parse(new string[] { "--config" });
                    T.True(false, "expected CliArgsException");
                }
                catch (CliArgsException e)
                {
                    T.Eq(CliArgsException.CodeMissingConfigValue, e.Code, "code");
                }
            });

            T.Run("two positional arguments throw unknown command", delegate
            {
                // Вторая позиционная лексема после уже распознанной команды — не
                // другая команда, а мусор: тоже должна вести к сообщению об использовании.
                try
                {
                    CliArgs.Parse(new string[] { "status", "get" });
                    T.True(false, "expected CliArgsException");
                }
                catch (CliArgsException e)
                {
                    T.Eq(CliArgsException.CodeUnknownCommand, e.Code, "code");
                    T.Eq("get", e.CommandName, "commandName");
                }
            });

            T.Run("all four commands are accepted", delegate
            {
                string[] names = { "serve", "status", "get", "pull" };
                foreach (string name in names)
                {
                    CliArgs args = CliArgs.Parse(new string[] { name });
                    T.Eq(name, args.Command, "command " + name);
                }
            });
        }
    }
}
