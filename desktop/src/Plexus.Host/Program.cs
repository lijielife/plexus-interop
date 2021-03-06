/**
 * Copyright 2017-2018 Plexus Interop Deutsche Bank AG
 * SPDX-License-Identifier: Apache-2.0
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
namespace Plexus.Host
{
    using CommandLine;
    using Plexus.Host.Internal;
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class Program
    {
        private static readonly TimeSpan ShutdownTimeout = TimeoutConstants.Timeout5Sec;

        public static int Main(string[] args)
        {
            InitializeProcess();
            return new Program().RunAsync(args).GetResult();
        }

        public async Task<int> RunAsync(string[] args)
        {
            return await Parser.Default
                .ParseArguments<BrokerOptions, StartOptions, LaunchOptions, StopOptions, StudioOptions>(args)
                .MapResult(
                    (BrokerOptions opts) => StartBrokerAsync(opts),
                    (StartOptions opts) => StartBrokerAsync(opts),
                    (LaunchOptions opts) => LaunchAppAsync(opts),
                    (StopOptions opts) => StopBrokerAsync(),
                    (StudioOptions opts) => StartStudioAsync(),
                    errs => Task.FromResult(1));
        }        

        private static async Task<int> LoadAndRunProgramAsync(IProgram program)
        {
            using (var loader = new ProgramLoader(program))
            {
                return await loader.LoadAndRunAsync().ConfigureAwait(false);
            }
        }

        private static async Task<int> LaunchAppAsync(LaunchOptions opts)
        {
            var program = new InteropCliProgram(opts.ApplicationIds);
            return await LoadAndRunProgramAsync(program).ConfigureAwait(false);
        }

        private static async Task<int> StartBrokerAsync(StartOptions opts)
        {
            var program = new BrokerProgram(opts.Metadata);
            return await LoadAndRunProgramAsync(program).ConfigureAwait(false);
        }

        private static async Task<int> StopBrokerAsync()
        {
            const string lockFileName = "plexus-interop-broker-lock";
            var pid = -1;
            if (File.Exists(lockFileName))
            {
                using (var stream = new FileStream(lockFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var streamReader = new StreamReader(stream))
                {
                    if (!int.TryParse(streamReader.ReadToEnd(), out pid))
                    {
                        pid = -1;
                    }
                }
            }

            if (pid == -1)
            {
                Console.WriteLine("Plexus Broker is not running in current directory {0}", Directory.GetCurrentDirectory());
                return 0;
            }

            var processes = Process.GetProcesses().Where(x => x.Id == pid);

            async Task ShutdownProcessAsync(Process process)
            {
                Console.WriteLine($"Shutting down plexus process {process.Id}");
                process.EnableRaisingEvents = true;
                var exitPromise = new Promise<int>();
                process.Exited += (sender, eventArgs) => exitPromise.TryComplete(((Process) sender).ExitCode);
                if (process.HasExited)
                {
                    exitPromise.TryComplete(process.ExitCode);
                }
                else
                {
                    var evtName = "plexus-host-shutdown-" + process.Id;
                    var evt = new EventWaitHandle(false, EventResetMode.AutoReset, evtName);
                    evt.Set();
                    var completed = await Task.WhenAny(exitPromise.Task,
                        Task.Delay(ShutdownTimeout));
                    if (completed != exitPromise.Task)
                    {
                        Console.WriteLine($"Killing plexus process {process.Id} which failed to shutdown gracefully in the given timeout {ShutdownTimeout.TotalSeconds} sec");
                        process.Kill();
                    }
                }
                var exitCode = await exitPromise.Task.ConfigureAwait(false);
                Console.WriteLine($"Plexus process {process.Id} exited with code {exitCode}");
            }

            var tasks = processes.Select(ShutdownProcessAsync);

            await Task.WhenAll(tasks).IgnoreExceptions().ConfigureAwait(false);

            return 0;
        }

        private static Task<int> StartStudioAsync()
        {
            var dir = Directory.GetCurrentDirectory();
            var addressFile = Path.Combine(dir, "servers", "ws-v1", "address");
            if (!File.Exists(addressFile))
            {
                throw new InvalidOperationException($"Broker is not running in the current folder {dir}");
            }
            var wsUri = new Uri(File.ReadAllText(addressFile));
            var uriBuilder = new UriBuilder(wsUri)
            {
                Scheme = "http",
                Path = "studio/index.html",               
                Query = $"transport=native-ws&wsUrl={wsUri}"
            };
            var uri = uriBuilder.Uri;
            Console.WriteLine("Starting " + uri);
            Process.Start(new ProcessStartInfo(uri.ToString()) {UseShellExecute = true});
            return Task.FromResult(0);
        }

        private static void InitializeProcess()
        {
            // by default, .NET Core doesn't have all code pages needed for Console apps.
            // see the .NET Core Notes in https://msdn.microsoft.com/en-us/library/system.diagnostics.process(v=vs.110).aspx
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
