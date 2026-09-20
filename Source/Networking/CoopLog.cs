using System;

namespace RimCoopMod.Networking
{
    /// <summary>
    /// Logging desacoplado: el mod lo conecta a Verse.Log, el servidor standalone
    /// lo conecta a Console.WriteLine. Así Networking no depende de RimWorld/Unity.
    /// </summary>
    public static class CoopLog
    {
        public static Action<string> OnMessage = msg => Console.WriteLine("[MSG] " + msg);
        public static Action<string> OnWarning = msg => Console.WriteLine("[WARN] " + msg);
        public static Action<string> OnError = msg => Console.WriteLine("[ERROR] " + msg);

        public static void Message(string msg) => OnMessage?.Invoke(msg);
        public static void Warning(string msg) => OnWarning?.Invoke(msg);
        public static void Error(string msg) => OnError?.Invoke(msg);
    }
}