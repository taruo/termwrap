using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace TermWrap
{
    internal sealed class CliException : InvalidOperationException
    {
        public readonly int ExitCode;
        public readonly string Code;

        public CliException(int exitCode, string code, string message) : base(message)
        {
            ExitCode = exitCode;
            Code = code;
        }

        public static CliException Usage(string message) { return new CliException(2, "usage", message); }
        public static CliException NotFound(string message) { return new CliException(3, "not_found", message); }
        public static CliException Ambiguous(string message) { return new CliException(4, "ambiguous", message); }
        public static CliException Timeout(string message) { return new CliException(5, "timeout", message); }
        public static CliException Conflict(string message) { return new CliException(6, "conflict", message); }
    }

    internal static class CliOutput
    {
        public static bool Json;

        public static Dictionary<string, object> Fields(params object[] pairs)
        {
            Dictionary<string, object> fields = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                fields.Add((string)pairs[i], pairs[i + 1]);
            }
            return fields;
        }

        public static Dictionary<string, object> Result(string command, params object[] pairs)
        {
            Dictionary<string, object> result = Fields("ok", true, "command", command);
            foreach (KeyValuePair<string, object> field in Fields(pairs))
            {
                result.Add(field.Key, field.Value);
            }
            return result;
        }

        public static string Serialize(object value)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Serialize(value);
        }

        public static void Write(object result, string humanText)
        {
            if (Json) { Console.WriteLine(Serialize(result)); }
            else if (humanText != null) { Console.WriteLine(humanText); }
        }

        public static void Diagnostic(string message)
        {
            // Diagnostics never share stdout with machine-readable results.
            Console.Error.WriteLine(message);
        }

        public static int Error(Exception exception)
        {
            CliException error = exception as CliException;
            int exitCode = error == null ? 1 : error.ExitCode;
            string code = error == null ? "failure" : error.Code;
            if (Json)
            {
                Console.Error.WriteLine(Serialize(Fields("ok", false,
                    "error", Fields("code", code, "message", exception.Message))));
            }
            else { Console.Error.WriteLine("error: " + exception.Message); }
            return exitCode;
        }
    }
}
