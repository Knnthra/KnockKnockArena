using KnockKnockArena.Shared.Simulation;

namespace KnockKnockArena.Server
{
    public static class Log
    {
        public static bool VerboseEnabled;

        private static readonly object Gate = new();

        public static void Info(string message) => Write("INFO", ConsoleColor.Gray, message);
        public static void Warn(string message) => Write("WARN", ConsoleColor.Yellow, message);
        public static void Error(string message) => Write("ERR", ConsoleColor.Red, message);

        public static void Packet(string message)
        {
            if (VerboseEnabled)
                Write("PKT ", ConsoleColor.DarkCyan, message);
        }

        public static void Write(string level, ConsoleColor color, string message)
        {
            lock (Gate)
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
                Console.ResetColor();
            }
        }
    }
}