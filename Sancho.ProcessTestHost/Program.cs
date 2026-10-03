using System.Text.Json;

// Deliberately isolated from production and any agent SDK: this executable only exercises pipes.
using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
using var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
using var input = new StreamReader(Console.OpenStandardInput());
switch (args.FirstOrDefault())
{
    case "script":
        // A fixture owns the protocol: each line is {kind,value}. Read steps wait for stdin.
        foreach (var instruction in File.ReadLines(args[1]))
        {
            using var step = JsonDocument.Parse(instruction);
            var value = step.RootElement.GetProperty("value").GetString()!;
            switch (step.RootElement.GetProperty("kind").GetString())
            {
                case "stdout": await output.WriteLineAsync(value); break;
                case "stderr": await error.WriteLineAsync(value); break;
                case "read":
                    var received = await input.ReadLineAsync();
                    if (!string.IsNullOrEmpty(value))
                        await File.AppendAllTextAsync(value, received + Environment.NewLine);
                    break;
                case "capture":
                    await File.WriteAllTextAsync(value, JsonSerializer.Serialize(new { arguments = args.Skip(2), directory = Directory.GetCurrentDirectory() }));
                    break;
                case "pid": await File.WriteAllTextAsync(value, Environment.ProcessId.ToString()); break;
                case "child":
                    var launch = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    launch.ArgumentList.Add(typeof(Program).Assembly.Location);
                    launch.ArgumentList.Add("child-wait");
                    using (var child = System.Diagnostics.Process.Start(launch)!)
                        await File.WriteAllTextAsync(value, child.Id.ToString());
                    break;
                case "delay": await Task.Delay(int.Parse(value)); break;
                case "exit": return int.Parse(value);
                default: throw new InvalidOperationException("Unknown script step");
            }
        }
        break;
    case "echo":
        await output.WriteLineAsync(JsonSerializer.Serialize(new { arguments = args.Skip(1), directory = Directory.GetCurrentDirectory() }));
        while (await input.ReadLineAsync() is { } line)
            await output.WriteLineAsync(line);
        break;
    case "wait":
        await output.WriteLineAsync("ready");
        await Task.Delay(Timeout.Infinite);
        break;
    case "child-wait":
        await Task.Delay(Timeout.Infinite);
        break;
    case "exit":
        await error.WriteLineAsync("controlled failure");
        return int.Parse(args[1]);
    case "flood":
        for (var i = 0; i < 4096; i++)
        {
            await output.WriteLineAsync(new string('o', 1024));
            await error.WriteLineAsync(new string('e', 1024));
        }
        break;
    default:
        await error.WriteLineAsync("Unknown test-host mode");
        return 2;
}
return 0;
