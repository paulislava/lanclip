using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanClip
{
    class ConfigException : Exception
    {
        public ConfigException(string message)
            : base(message)
        {
        }

        public ConfigException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    class Config
    {
        public const int DefaultPort = 8901;
        public const long DefaultMaxBytes = 536870912L;

        public int Port;
        public string Token;
        public List<string> Peers;
        public long MaxBytes;
        public bool AutoPaste;

        public static string DefaultPath()
        {
            string profile = Environment.GetEnvironmentVariable("USERPROFILE");
            return Path.Combine(profile, ".config", "lanclip", "config.json");
        }

        public static string GenerateToken()
        {
            byte[] bytes = new byte[16];
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            try
            {
                rng.GetBytes(bytes);
            }
            finally
            {
                rng.Dispose();
            }

            StringBuilder builder = new StringBuilder(32);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        public static Config Load(string path)
        {
            if (!File.Exists(path))
            {
                Config fresh = new Config();
                fresh.Port = DefaultPort;
                fresh.Token = GenerateToken();
                fresh.Peers = new List<string>();
                fresh.MaxBytes = DefaultMaxBytes;
                fresh.AutoPaste = true;
                fresh.Save(path);
                return fresh;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                throw new ConfigException("не удалось прочитать конфиг " + path + ": " + e.Message, e);
            }

            Dictionary<string, object> dict;
            try
            {
                dict = Json.Parse(text);
            }
            catch (Exception e)
            {
                throw new ConfigException("некорректный конфиг " + path + ": " + e.Message, e);
            }

            string token = Json.Str(dict, "token", null);
            if (token == null)
            {
                throw new ConfigException("в конфиге отсутствует обязательное поле token");
            }

            Config config = new Config();
            config.Port = Json.Int(dict, "port", DefaultPort);
            config.Token = token;
            config.Peers = ReadPeers(dict);
            config.MaxBytes = ToLong(dict, "maxBytes", DefaultMaxBytes);
            config.AutoPaste = Json.Bool(dict, "autoPaste", true);
            return config;
        }

        static List<string> ReadPeers(Dictionary<string, object> dict)
        {
            List<string> peers = new List<string>();
            foreach (object item in Json.Arr(dict, "peers"))
            {
                if (item is string)
                {
                    peers.Add((string)item);
                }
            }
            return peers;
        }

        static long ToLong(Dictionary<string, object> o, string key, long fallback)
        {
            object value;
            if (o.TryGetValue(key, out value) && value != null)
            {
                try
                {
                    return Convert.ToInt64(value);
                }
                catch (Exception)
                {
                    return fallback;
                }
            }
            return fallback;
        }

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["port"] = Port;
            obj["token"] = Token;
            obj["peers"] = Peers != null ? Peers : new List<string>();
            obj["maxBytes"] = MaxBytes;
            obj["autoPaste"] = AutoPaste;

            string json = Json.Write(obj);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        // Общие для всех команд проверки: порт, токен, лимит размера. Не проверяет `Peers` —
        // сервер без единого соседа (`serve`/`status`) законное состояние сразу после
        // установки, до того как обе машины познакомятся друг с другом. Соседа требуют
        // только команды, которым он реально нужен — см. ValidatePeers().
        public void Validate()
        {
            if (Port <= 0 || Port >= 65536)
            {
                throw new ConfigException("неверный порт: " + Port);
            }
            if (string.IsNullOrEmpty(Token))
            {
                throw new ConfigException("пустой токен");
            }
            if (MaxBytes <= 0)
            {
                throw new ConfigException("неверный maxBytes: " + MaxBytes);
            }
        }

        // Отдельная проверка для команд, которым без соседа делать нечего (get/pull).
        public void ValidatePeers()
        {
            if (Peers == null || Peers.Count == 0)
            {
                throw new ConfigException("не сконфигурирован ни один сосед");
            }
        }
    }
}
