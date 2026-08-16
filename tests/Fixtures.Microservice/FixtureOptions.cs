using System.Globalization;
using System.Net;
using System.Text;

namespace Nekolla.Nekostick.Tests.Fixtures.Microservice;

internal sealed class FixtureOptions
{
    internal const int MaximumRequestBytes = 1_048_576;
    internal const int MaximumResponseBytes = 64 * 1_024 * 1_024;
    internal const int MaximumPatternBytes = 1_024;

    private FixtureOptions()
    {
    }

    internal IPAddress ListenIpAddress { get; private set; } = IPAddress.Loopback;

    internal string ListenAddress { get; private set; } = "127.0.0.1";

    internal int Port { get; private set; }

    internal FixtureMode Mode { get; private set; } = FixtureMode.Echo;

    internal int ResponseBytes { get; private set; } = 4_096;

    internal byte[] ResponsePattern { get; private set; } = "fixture\n"u8.ToArray();

    internal int ChunkSize { get; private set; } = 1_024;

    internal bool Chunked { get; private set; }

    internal int ChunkDelayMilliseconds { get; private set; }

    internal int DelayMilliseconds { get; private set; }

    internal int HoldMilliseconds { get; private set; }

    internal int FailureStatusCode { get; private set; } = 503;

    internal int StartupDelayMilliseconds { get; private set; }

    internal bool FailStartup { get; private set; }

    internal int ExitAfterMilliseconds { get; private set; }

    internal int WebSocketCloseAfterFrames { get; private set; }

    internal int WebSocketCloseAfterMilliseconds { get; private set; }

    internal ushort WebSocketCloseCode { get; private set; } = 1000;

    internal static string Usage =>
        "usage: Fixtures.Microservice [--listen-address 127.0.0.1|::1] [--port 0..65535] "
        + "[--mode echo|stream|websocket|fail|hold|delay|mixed] [options]\n"
        + "options: --response-bytes N --response-pattern TEXT --chunk-size N --chunked "
        + "--chunk-delay-ms N --delay-ms N --hold-ms N --status-code 400..599 "
        + "--startup-delay-ms N --fail-startup --exit-after-ms N "
        + "--ws-close-after-frames N --ws-close-after-ms N --ws-close-code N\n"
        + "endpoints: /fixture/health, /fixture/ws (HTTP/1.1 WebSocket upgrade)";

    internal static FixtureOptions Parse(string[] args, out bool showHelp)
    {
        showHelp = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var options = new FixtureOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                if (args.Length != 1)
                {
                    throw new FixtureArgumentException();
                }

                showHelp = true;
                continue;
            }

            var (name, inlineValue) = SplitArgument(argument);
            if (!seen.Add(CanonicalName(name)))
            {
                throw new FixtureArgumentException();
            }

            switch (name)
            {
                case "--listen-address":
                    options.SetListenAddress(ReadValue(args, ref index, inlineValue));
                    break;
                case "--port":
                case "--listen-port":
                    options.Port = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 65_535);
                    break;
                case "--mode":
                    options.Mode = ParseMode(ReadValue(args, ref index, inlineValue));
                    break;
                case "--response-bytes":
                case "--body-bytes":
                    options.ResponseBytes = ParseInteger(ReadValue(args, ref index, inlineValue), 0, MaximumResponseBytes);
                    break;
                case "--response-pattern":
                case "--pattern":
                    options.ResponsePattern = ParsePattern(ReadValue(args, ref index, inlineValue));
                    break;
                case "--chunk-size":
                    options.ChunkSize = ParseInteger(ReadValue(args, ref index, inlineValue), 1, 1_048_576);
                    break;
                case "--chunked":
                    RequireNoInlineValue(inlineValue);
                    options.Chunked = true;
                    break;
                case "--chunk-delay-ms":
                    options.ChunkDelayMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 60_000);
                    break;
                case "--delay-ms":
                    options.DelayMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 120_000);
                    break;
                case "--hold-ms":
                    options.HoldMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 86_400_000);
                    break;
                case "--status-code":
                case "--fail-status":
                    options.FailureStatusCode = ParseInteger(ReadValue(args, ref index, inlineValue), 400, 599);
                    break;
                case "--startup-delay-ms":
                    options.StartupDelayMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 120_000);
                    break;
                case "--fail-startup":
                    RequireNoInlineValue(inlineValue);
                    options.FailStartup = true;
                    break;
                case "--exit-after-ms":
                    options.ExitAfterMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 1, 86_400_000);
                    break;
                case "--ws-close-after-frames":
                    options.WebSocketCloseAfterFrames = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 1_000_000);
                    break;
                case "--ws-close-after-ms":
                    options.WebSocketCloseAfterMilliseconds = ParseInteger(ReadValue(args, ref index, inlineValue), 0, 86_400_000);
                    break;
                case "--ws-close-code":
                    options.WebSocketCloseCode = ParseCloseCode(ReadValue(args, ref index, inlineValue));
                    break;
                default:
                    throw new FixtureArgumentException();
            }
        }

        return options;
    }

    private static (string Name, string? InlineValue) SplitArgument(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new FixtureArgumentException();
        }

        var separator = argument.IndexOf('=', 2);
        return separator < 0
            ? (argument, null)
            : (argument[..separator], argument[(separator + 1)..]);
    }

    private static string ReadValue(string[] args, ref int index, string? inlineValue)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length == 0)
            {
                throw new FixtureArgumentException();
            }

            return inlineValue;
        }

        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new FixtureArgumentException();
        }

        return args[index];
    }

    private static string CanonicalName(string name)
    {
        return name switch
        {
            "--listen-port" => "--port",
            "--body-bytes" => "--response-bytes",
            "--pattern" => "--response-pattern",
            "--fail-status" => "--status-code",
            _ => name,
        };
    }

    private static void RequireNoInlineValue(string? inlineValue)
    {
        if (inlineValue is not null)
        {
            throw new FixtureArgumentException();
        }
    }

    private static int ParseInteger(string value, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < minimum
            || result > maximum)
        {
            throw new FixtureArgumentException();
        }

        return result;
    }

    private static byte[] ParsePattern(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length == 0 || bytes.Length > MaximumPatternBytes || bytes.Contains((byte)0))
        {
            throw new FixtureArgumentException();
        }

        return bytes;
    }

    private static FixtureMode ParseMode(string value)
    {
        return value switch
        {
            "echo" => FixtureMode.Echo,
            "stream" => FixtureMode.Stream,
            "websocket" or "ws" => FixtureMode.WebSocket,
            "fail" => FixtureMode.Fail,
            "hold" => FixtureMode.Hold,
            "delay" => FixtureMode.Delay,
            "mixed" => FixtureMode.Mixed,
            _ => throw new FixtureArgumentException(),
        };
    }

    private static ushort ParseCloseCode(string value)
    {
        var code = ParseInteger(value, 1_000, 4_999);
        if (code is 1_004 or 1_005 or 1_006 or 1_015)
        {
            throw new FixtureArgumentException();
        }

        return (ushort)code;
    }

    private void SetListenAddress(string value)
    {
        if (value == "127.0.0.1")
        {
            ListenAddress = value;
            ListenIpAddress = IPAddress.Loopback;
            return;
        }

        if (value == "::1")
        {
            ListenAddress = value;
            ListenIpAddress = IPAddress.IPv6Loopback;
            return;
        }

        throw new FixtureArgumentException();
    }

    internal enum FixtureMode
    {
        Echo,
        Stream,
        WebSocket,
        Fail,
        Hold,
        Delay,
        Mixed,
    }
}

internal sealed class FixtureArgumentException : Exception
{
}
