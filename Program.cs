using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace windows_hosts_writer
{
    class Program
    {
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_HOSTPATH = "hosts_path";
        private const string ERROR_HOSTPATH = "could not change hosts file at {0} inside of the container. You can change that path through environment variable " + ENV_HOSTPATH;
        private const string EVENT_MSG = "got a {0} event from {1}";
        private static DockerClient _client;
        private static bool _debug = false;
        private const string WhwIdentifier = "Added by whw";

        static void Main(string[] args)
        {
            if (Environment.GetEnvironmentVariable("debug") != null) {
                _debug = true;
                Console.WriteLine("Starting Windows hosts writer");
            }

            var progress = new Progress<Message>(message =>
            {
                if (message.Action == "connect")
                {
                    if (_debug)
                        Console.WriteLine(EVENT_MSG, "connect", message.Actor.Attributes["container"]);
                    HandleHosts(true, message);
                }
                else if (message.Action == "disconnect")
                {
                    if (_debug)
                        Console.WriteLine(EVENT_MSG, "disconnect", message.Actor.Attributes["container"]);
                    HandleHosts(false, message);
                }
            });

            var containerEventsParams = new ContainerEventsParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                    {
                        {
                            "event", new Dictionary<string, bool>()
                            {
                                {
                                    "connect", true
                                },
                                {
                                    "disconnect", true
                                }
                            }
                        },
                        {
                            "type", new Dictionary<string, bool>()
                            {
                                {
                                    "network", true
                                }
                            }
                        }
                    }
            };

            try
            {
                CleanHostFile();
                DockerClient client = GetClient();
                IList<ContainerListResponse> containers = client.Containers.ListContainersAsync(new ContainersListParameters()).Result;
                foreach (ContainerListResponse containerListResponse in containers)
                {
                    WriteHostNames(true, containerListResponse.ID);
                }

                client.System.MonitorEventsAsync(containerEventsParams, progress, default(CancellationToken)).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Something went wrong. Likely the Docker engine is not listening at " + GetClient().Configuration.EndpointBaseUri.ToString() + " inside of the container.");
                Console.WriteLine("You can change that path through environment variable " + ENV_ENDPOINT);
                if (_debug)
                {
                    Console.WriteLine("Exception is " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("InnerException is " + ex.InnerException.Message);
                        Console.WriteLine(ex.InnerException.StackTrace);
                    }
                }

            }
        }

        private static void HandleHosts(bool add, Message message)
        {
            if (message.Actor.Attributes["type"] == "nat")
            {
                string containerId = message.Actor.Attributes["container"];
                WriteHostNames(add, containerId);
            }
        }

        private static void WriteHostNames(bool add, string containerId)
        {
            FileStream hostsFileStream = FindHostFile();
            if (hostsFileStream == null)
                return;
            try
            {
                WriteHostNames(add, hostsFileStream, containerId);
            }
            finally
            {
                hostsFileStream.Dispose();
            }
        }

        private static FileStream FindHostFile()
        {
            FileStream hostsFileStream = null;
            while (hostsFileStream == null)
            {
                int tryCount = 0;
                string hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH) ?? "c:\\driversetc\\hosts";
                hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
                try
                {
                    hostsFileStream = File.Open(hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (FileNotFoundException)
                {
                    // no access to hosts
                    Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    // no access to hosts
                    Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                    return null;
                }
                catch (IOException)
                {
                    if (tryCount == 5)
                    {
                        Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                        return null;
                    }

                    Thread.Sleep(1000);
                    tryCount++;
                }
            }

            return hostsFileStream;
        }

        private static void CleanHostFile()
        {
            FileStream hostsFileStream = FindHostFile();
            if (hostsFileStream == null)
                return;

            var hostsLines = new List<string>();
            using (StreamReader reader = new StreamReader(hostsFileStream))
            using (StreamWriter writer = new StreamWriter(hostsFileStream))
            {
                while (!reader.EndOfStream)
                    hostsLines.Add(reader.ReadLine());

                hostsFileStream.Position = 0;
                int removed = hostsLines.RemoveAll(l => l.EndsWith(WhwIdentifier));

                foreach (string line in hostsLines)
                    writer.WriteLine(line);
                hostsFileStream.SetLength(hostsFileStream.Position);
            }
        }

        private static void WriteHostNames(bool add, FileStream hostsFileStream, string containerId)
        {
            try
            {
                ContainerInspectResponse response = GetClient().Containers.InspectContainerAsync(containerId).Result;
                IDictionary<string, EndpointSettings> networks = response.NetworkSettings.Networks;
                EndpointSettings network = null;
                if (networks.TryGetValue("nat", out network))
                {
                    var hostsLines = new List<string>();
                    using (StreamReader reader = new StreamReader(hostsFileStream))
                    using (StreamWriter writer = new StreamWriter(hostsFileStream))
                    {
                        while (!reader.EndOfStream)
                            hostsLines.Add(reader.ReadLine());

                        hostsFileStream.Position = 0;
                        int removed = hostsLines.RemoveAll(l => l.EndsWith($"#{containerId} {WhwIdentifier}"));

                        foreach (string alias in network.Aliases)
                        {
                            if (add)
                                hostsLines.Add($"{network.IPAddress}\t{alias}\t\t#{containerId} {WhwIdentifier}");
                        }

                        foreach (string line in hostsLines)
                            writer.WriteLine(line);
                        hostsFileStream.SetLength(hostsFileStream.Position);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_debug)
                {
                    Console.WriteLine("Something went wrong. Maybe looking for a container that is already gone? Exception is " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("InnerException is " + ex.InnerException.Message);
                        Console.WriteLine(ex.InnerException.StackTrace);
                    }
                }
            }
        }

        private static DockerClient GetClient()
        {
            if (_client == null)
            {
                string endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";
                _client = new DockerClientConfiguration(new System.Uri(endpoint)).CreateClient();
            }
            return _client;
        }
    }
}
