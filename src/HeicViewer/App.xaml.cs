using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace HeicViewer;

public partial class App : Application
{
    private const string MutexName = "Local\\HeicViewer.SingleInstance";
    private const string PipeName = "HeicViewer.Ipc";

    private Mutex? _singleInstanceMutex;
    private MainWindow? _mainWindow;

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "HEIC Viewer — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThumbnailService.Start();

        var args = e.Args.ToList();
        bool register = args.Any(a => a is "--register" or "/register");
        string? target = args.FirstOrDefault(File.Exists) ?? args.FirstOrDefault(Directory.Exists);

        // Headless registration: HeicViewer.exe --register (silent, exits immediately)
        if (register)
        {
            FileAssociation.RegisterAll();
            if (target is null)
            {
                Shutdown();
                return;
            }
        }

        bool createdNew;
        _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            if (target is not null)
                SendToRunningInstance(target);
            Shutdown();
            return;
        }

        StartPipeServer();

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        if (File.Exists(target))
            _mainWindow.OpenFileInViewer(target);
        else if (Directory.Exists(target))
            _mainWindow.OpenFolderLibrary(target);
        else
            _mainWindow.StartLibrary();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThumbnailService.Shutdown();
        base.OnExit(e);
    }

    private static void SendToRunningInstance(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("open|" + path);
        }
        catch
        {
            // The existing instance may be shutting down — nothing to do.
        }
    }

    private void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync();
                    if (line is not null && line.StartsWith("open|", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = line["open|".Length..];
                        if (File.Exists(path) || Directory.Exists(path))
                            await Dispatcher.BeginInvoke(() => _mainWindow?.HandleExternal(path));
                    }
                }
                catch
                {
                    // Keep listening even if a connection breaks mid-read.
                }
            }
        });
    }
}
