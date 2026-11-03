using System.Diagnostics;
using Darp.CodeEditor.Lsp.Hosting;

// A process for the tests of ChildProcess.
//   echo    copies its input to its output line by line, writes "stderr" to standard error, exits with 3 at the end
//   sleep   prints "ready" and sleeps; it never reads its input, so only being killed ends it
//   host    starts this program in sleep mode as a ChildProcess, prints "child <id>" and sleeps
//   args    prints each further argument in brackets, one per line
switch (args)
{
    case ["args", .. string[] rest]:
        foreach (string argument in rest)
            Console.WriteLine($"[{argument}]");
        return 0;
    case ["echo"]:
        Console.Error.WriteLine("stderr");
        while (Console.ReadLine() is { } line)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        }
        return 3;
    case ["sleep"]:
        Console.WriteLine("ready");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    case ["host"]:
        var start = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet");
        // Started as "dotnet <dll>": the child runs the same way.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("sleep");
        ChildProcess child = await ChildProcess.StartAsync(start);
        await new StreamReader(child.StandardOutput).ReadLineAsync();
        Console.WriteLine($"child {child.Id}");
        Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
        return 0;
    default:
        Console.Error.WriteLine("Usage: echo | sleep | host | args ...");
        return 2;
}
