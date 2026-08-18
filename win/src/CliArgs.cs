using System;

namespace LanClip
{
    // Ошибки разбора аргументов lanclipd. CodeHelpRequested и CodeUnknownCommand
    // ведут к разным кодам выхода в Program.cs (0 против 2), поэтому это разные
    // случаи, а не один общий "usage error". Зеркало Mac-стороннего CliArgsError
    // (mac/Sources/LanClipCore/CliArgs.swift): helpRequested | missingConfigValue |
    // unknownCommand(String).
    class CliArgsException : Exception
    {
        public const string CodeHelpRequested = "helpRequested";
        public const string CodeMissingConfigValue = "missingConfigValue";
        public const string CodeUnknownCommand = "unknownCommand";

        public readonly string Code;
        public readonly string CommandName; // осмысленно только при Code == CodeUnknownCommand

        CliArgsException(string code, string commandName, string message)
            : base(message)
        {
            Code = code;
            CommandName = commandName;
        }

        public static CliArgsException HelpRequested()
        {
            return new CliArgsException(CodeHelpRequested, null, "запрошена справка");
        }

        public static CliArgsException MissingConfigValue()
        {
            return new CliArgsException(CodeMissingConfigValue, null,
                "флагу --config нужно значение: путь к файлу конфига");
        }

        public static CliArgsException UnknownCommand(string name)
        {
            return new CliArgsException(CodeUnknownCommand, name, "неизвестная команда: " + name);
        }
    }

    // Разбор аргументов командной строки lanclipd без сторонних зависимостей (никакого
    // System.CommandLine). Живёт в отдельном файле, а не в Program.cs — build.ps1
    // исключает Program.cs из сборки тестов, а этот тип обязан быть тестируемым без
    // запуска процесса. Зеркало mac/Sources/LanClipCore/CliArgs.swift.
    class CliArgs
    {
        public static readonly string[] KnownCommands = { "serve", "status", "get", "pull" };
        public const string DefaultCommand = "serve";

        public string Command;
        public string ConfigPath;

        // argv — уже без имени процесса (эквивалент CommandLine.arguments.dropFirst()
        // на Mac; в C# args, переданные в Main, уже не включают имя процесса).
        //
        // --config принимается и до, и после позиционной команды — обе формы
        // ("lanclipd status --config x" и "lanclipd --config x status") равноправны.
        // Вторая позиционная лексема (когда команда уже распознана) трактуется как
        // неизвестная команда, а не игнорируется — иначе "lanclipd status get" молча
        // выполнил бы только status.
        public static CliArgs Parse(string[] argv)
        {
            string configPath = null;
            string command = null;

            int index = 0;
            while (index < argv.Length)
            {
                string arg = argv[index];

                if (arg == "-h" || arg == "--help")
                {
                    throw CliArgsException.HelpRequested();
                }
                else if (arg == "--config")
                {
                    int valueIndex = index + 1;
                    if (valueIndex >= argv.Length)
                    {
                        throw CliArgsException.MissingConfigValue();
                    }
                    configPath = argv[valueIndex];
                    index = valueIndex;
                }
                else
                {
                    if (command != null)
                    {
                        throw CliArgsException.UnknownCommand(arg);
                    }
                    command = arg;
                }

                index++;
            }

            string resolvedCommand = command != null ? command : DefaultCommand;
            if (Array.IndexOf(KnownCommands, resolvedCommand) < 0)
            {
                throw CliArgsException.UnknownCommand(resolvedCommand);
            }

            CliArgs result = new CliArgs();
            result.Command = resolvedCommand;
            result.ConfigPath = configPath != null ? configPath : Config.DefaultPath();
            return result;
        }
    }
}
