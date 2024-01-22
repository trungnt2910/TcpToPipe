using System.CommandLine;
using System.IO.Pipes;
using System.Net.Sockets;

var rootCommand = new RootCommand("Forwards a named pipe server to a remote TCP server");

var remoteOption = new Option<string>(
    name: "--remote",
    description: "The remote TCP server (host:port)."
);
rootCommand.AddOption(remoteOption);

var pipeOption = new Option<string>(
    name: "--pipe",
    description: "The pipe server name."
);
rootCommand.AddOption(pipeOption);

rootCommand.SetHandler(async (remote, name) =>
{
    string host = "localhost";
    int port = 3333;
    string pipe = "windbg";

    if (!string.IsNullOrEmpty(remote))
    {
        var colonIndex = remote.LastIndexOf(":");
        if (colonIndex != -1
            && int.TryParse(remote.Substring(colonIndex + 1), out port))
        {
            host = remote.Substring(0, colonIndex);
            if (string.IsNullOrEmpty(host))
            {
                host = "localhost";
            }
        }
        else
        {
            host = remote;
        }
    }

    if (!string.IsNullOrEmpty(name))
    {
        pipe = name;
    }

    await Main(host, port, pipe);
}, remoteOption, pipeOption);

return await rootCommand.InvokeAsync(args);

async Task Main(string host, int port, string pipe)
{
    var tcpToPipe = new MemoryStream();
    var tcpToPipeSemaphore = new SemaphoreSlim(0);
    var pipeToTcp = new MemoryStream();
    var pipeToTcpSemaphore = new SemaphoreSlim(0);

    var consoleLock = new object();

    async Task TcpLoopAsync()
    {
        var tcpClient = new TcpClient();

        void Log(string message)
        {
            lock (consoleLock)
            {
                Console.Error.WriteLine($"[TCP]  {message}");
            }
        }

        while (true)
        {
            try
            {
                await tcpClient.ConnectAsync(host, port);
                using var networkStream = tcpClient.GetStream();

                Log($"Connected to {host}:{port}.");

                async Task TcpReadLoopAsync()
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        int readCount = await networkStream.ReadAsync(buffer, 0, buffer.Length);
                        lock (tcpToPipe)
                        {
                            tcpToPipe.Write(buffer, 0, readCount);
                            if (tcpToPipeSemaphore.CurrentCount == 0)
                            {
                                tcpToPipeSemaphore.Release();
                            }
                        }
                    }
                }

                async Task TcpWriteLoopAsync()
                {
                    while (true)
                    {
                        await pipeToTcpSemaphore.WaitAsync();
                        byte[] buffer;
                        lock (pipeToTcp)
                        {
                            buffer = pipeToTcp.ToArray();
                            pipeToTcp.Seek(0, SeekOrigin.Begin);
                            pipeToTcp.SetLength(0);
                        }
                        await networkStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                }

                await Task.WhenAll(TcpReadLoopAsync(), TcpWriteLoopAsync());
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                tcpClient.Close();
                tcpClient = new();
            }
        }
    }

    async Task PipeLoopAsync()
    {
        using var pipeServer = new NamedPipeServerStream(pipe, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        void Log(string message)
        {
            lock (consoleLock)
            {
                Console.Error.WriteLine($"[PIPE] {message}");
            }
        }

        while (true)
        {
            try
            {
                await pipeServer.WaitForConnectionAsync();

                Log("Got a connection.");

                async Task PipeReadLoopAsync()
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        int readCount = await pipeServer.ReadAsync(buffer, 0, buffer.Length);
                        lock (pipeToTcp)
                        {
                            pipeToTcp.Write(buffer, 0, readCount);
                            if (pipeToTcpSemaphore.CurrentCount == 0)
                            {
                                pipeToTcpSemaphore.Release();
                            }
                        }
                    }
                }

                async Task PipeWriteLoopAsync()
                {
                    while (true)
                    {
                        await tcpToPipeSemaphore.WaitAsync();
                        byte[] buffer;
                        lock (tcpToPipe)
                        {
                            buffer = tcpToPipe.ToArray();
                            tcpToPipe.Seek(0, SeekOrigin.Begin);
                            tcpToPipe.SetLength(0);
                        }
                        await pipeServer.WriteAsync(buffer, 0, buffer.Length);
                    }
                }

                await Task.WhenAll(PipeReadLoopAsync(), PipeWriteLoopAsync());
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                pipeServer.Disconnect();
            }
        }
    }

    await Task.WhenAny(TcpLoopAsync(), PipeLoopAsync());
}
