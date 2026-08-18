using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanClip.Tests
{
    static class ConfigTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lanclip-config-" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Cleanup(string dir)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        static string Write(string dir, string json)
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
            return path;
        }

        public static void Register()
        {
            T.Run("generates thirty two hex token", delegate
            {
                string token = Config.GenerateToken();
                T.Eq(32, token.Length, "token length");
                bool allLowerHex = true;
                foreach (char c in token)
                {
                    bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                    if (!isHex) { allLowerHex = false; }
                }
                T.True(allLowerHex, "token is lowercase hex");
                T.True(Config.GenerateToken() != Config.GenerateToken(), "tokens differ");
            });

            T.Run("creates file when missing", delegate
            {
                string dir = NewTempDir();
                try
                {
                    string path = Path.Combine(dir, "config.json");
                    Config config = Config.Load(path);

                    T.True(File.Exists(path), "file created");
                    T.Eq(8901, config.Port, "default port");
                    T.Eq(536870912L, config.MaxBytes, "default maxBytes");
                    T.True(config.AutoPaste, "default autoPaste");
                    T.Eq(32, config.Token.Length, "generated token length");
                    T.Eq(0, config.Peers.Count, "default empty peers");

                    byte[] bytes = File.ReadAllBytes(path);
                    bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    T.True(!hasBom, "no BOM in written config");
                }
                finally
                {
                    Cleanup(dir);
                }
            });

            T.Run("applies defaults for missing keys", delegate
            {
                string dir = NewTempDir();
                try
                {
                    string path = Write(dir, "{\"token\":\"abc\",\"peers\":[\"pc\"]}");
                    Config config = Config.Load(path);
                    T.Eq(8901, config.Port, "default port");
                    T.Eq(536870912L, config.MaxBytes, "default maxBytes");
                    T.True(config.AutoPaste, "default autoPaste");
                    T.Eq(1, config.Peers.Count, "peers count");
                    T.Eq("pc", config.Peers[0], "peers[0]");
                }
                finally
                {
                    Cleanup(dir);
                }
            });

            T.Run("reads explicit values", delegate
            {
                string dir = NewTempDir();
                try
                {
                    string path = Write(dir, "{\"port\":9001,\"token\":\"t\",\"peers\":[\"a\",\"b\"],\"maxBytes\":1024,\"autoPaste\":false}");
                    Config config = Config.Load(path);
                    T.Eq(9001, config.Port, "port");
                    T.Eq("t", config.Token, "token");
                    T.Eq(2, config.Peers.Count, "peers count");
                    T.Eq("a", config.Peers[0], "peers[0]");
                    T.Eq("b", config.Peers[1], "peers[1]");
                    T.Eq(1024L, config.MaxBytes, "maxBytes");
                    T.True(!config.AutoPaste, "autoPaste false");
                }
                finally
                {
                    Cleanup(dir);
                }
            });

            T.Run("rejects malformed json", delegate
            {
                string dir = NewTempDir();
                try
                {
                    string path = Write(dir, "{ не json");
                    T.Throws<ConfigException>(delegate
                    {
                        Config.Load(path);
                    }, "malformed json throws ConfigException");
                }
                finally
                {
                    Cleanup(dir);
                }
            });

            T.Run("validate rejects bad port", delegate
            {
                Config config = new Config();
                config.Port = 0;
                config.Token = "t";
                config.Peers = new List<string>(new string[] { "pc" });
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                T.Throws<ConfigException>(delegate
                {
                    config.Validate();
                }, "invalid port");
            });

            T.Run("validate rejects empty token", delegate
            {
                Config config = new Config();
                config.Port = 8901;
                config.Token = "";
                config.Peers = new List<string>(new string[] { "pc" });
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                T.Throws<ConfigException>(delegate
                {
                    config.Validate();
                }, "empty token");
            });

            T.Run("validate rejects bad maxBytes", delegate
            {
                Config config = new Config();
                config.Port = 8901;
                config.Token = "t";
                config.Peers = new List<string>(new string[] { "pc" });
                config.MaxBytes = 0;
                config.AutoPaste = true;
                T.Throws<ConfigException>(delegate
                {
                    config.Validate();
                }, "invalid maxBytes");
            });

            T.Run("validate accepts empty peers", delegate
            {
                // validate() не проверяет peers — сервер без соседа законное состояние
                // сразу после установки (serve/status), см. Swift-сторону.
                Config config = new Config();
                config.Port = 8901;
                config.Token = "t";
                config.Peers = new List<string>();
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                config.Validate();
                T.True(true, "no throw on empty peers");
            });

            T.Run("validate accepts good config", delegate
            {
                Config config = new Config();
                config.Port = 8901;
                config.Token = "t";
                config.Peers = new List<string>(new string[] { "pc" });
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                config.Validate();
                T.True(true, "no throw on good config");
            });

            T.Run("validatePeers rejects empty peers", delegate
            {
                Config config = new Config();
                config.Port = 8901;
                config.Token = "t";
                config.Peers = new List<string>();
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                T.Throws<ConfigException>(delegate
                {
                    config.ValidatePeers();
                }, "no peers");
            });

            T.Run("validatePeers accepts non empty peers", delegate
            {
                Config config = new Config();
                config.Port = 8901;
                config.Token = "t";
                config.Peers = new List<string>(new string[] { "pc" });
                config.MaxBytes = 536870912;
                config.AutoPaste = true;
                config.ValidatePeers();
                T.True(true, "no throw on non-empty peers");
            });

            T.Run("default path is under userprofile config lanclip", delegate
            {
                string expected = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), ".config");
                expected = Path.Combine(expected, "lanclip");
                expected = Path.Combine(expected, "config.json");
                T.Eq(expected, Config.DefaultPath(), "default path");
            });
        }
    }
}
