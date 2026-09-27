using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GGDealsWishlist.Infrastructure
{
    /// <summary>Destination for diagnostic messages. Every message is redacted before it reaches a sink.</summary>
    public interface ILogSink
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    internal sealed class DebugLogSink : ILogSink
    {
        public void Debug(string message) => Trace.WriteLine("[DEBUG] " + message);
        public void Info(string message) => Trace.WriteLine("[INFO] " + message);
        public void Warn(string message) => Trace.WriteLine("[WARN] " + message);
        public void Error(string message) => Trace.WriteLine("[ERROR] " + message);
    }

    /// <summary>
    /// Central logging facade. Exceptions are flattened to redacted text rather than handed to the
    /// underlying logger, so an API key embedded in a request URI can never reach the log file.
    /// </summary>
    public static class Log
    {
        private static ILogSink sink = new DebugLogSink();

        public static void SetSink(ILogSink newSink) => sink = newSink ?? new DebugLogSink();

        public static void Debug(string message) => Write(s => s.Debug(SecretRedactor.Redact(message)));
        public static void Info(string message) => Write(s => s.Info(SecretRedactor.Redact(message)));
        public static void Warn(string message) => Write(s => s.Warn(SecretRedactor.Redact(message)));
        public static void Error(string message) => Write(s => s.Error(SecretRedactor.Redact(message)));

        public static void Error(Exception exception, string message)
        {
            Write(s => s.Error(SecretRedactor.Redact(message + Environment.NewLine + exception)));
        }

        public static void Warn(Exception exception, string message)
        {
            Write(s => s.Warn(SecretRedactor.Redact(message + " (" + exception?.GetType().Name + ": " + exception?.Message + ")")));
        }

        private static void Write(Action<ILogSink> action)
        {
            try { action(sink); }
            catch { /* Logging must never take down the extension. */ }
        }
    }

    /// <summary>Removes registered secrets (API keys) and any key=... query values from text.</summary>
    public static class SecretRedactor
    {
        public const string Placeholder = "[REDACTED]";
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Regex KeyQuery = new Regex(@"(?<=[?&]key=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void Register(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 4)
            {
                return;
            }

            lock (Sync)
            {
                Secrets.Add(secret);
                Secrets.Add(Uri.EscapeDataString(secret));
            }
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            lock (Sync)
            {
                foreach (var secret in Secrets)
                {
                    if (text.IndexOf(secret, StringComparison.Ordinal) >= 0)
                    {
                        text = text.Replace(secret, Placeholder);
                    }
                }
            }

            return KeyQuery.Replace(text, Placeholder);
        }
    }
}
