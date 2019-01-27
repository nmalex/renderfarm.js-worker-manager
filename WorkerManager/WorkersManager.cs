using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace WorkerManager
{
    public class WorkersManager : IWorkersManager
    {
        private readonly Configuration config;
        private readonly Dictionary<int, IWorker> workers = new Dictionary<int, IWorker>();

        private readonly string workDir;
        private readonly string exeFile;
        private readonly TimeSpan unresponsiveTimeout;

        static readonly Random Rnd = new Random();

        private readonly IPAddress controllerHost;
        private readonly object sync = new object();
        private readonly int workerPortFrom;
        private readonly int wokerPortTo;

        public event EventHandler<IWorker> Added;
        public event EventHandler<IWorker> Deleted;
        public event EventHandler<IWorker> Updated;

        public int Count
        {
            get
            {
                lock (this.sync)
                {
                    return this.workers.Count;
                }
            }
        }

        public IEnumerable<IWorker> Workers
        {
            get
            {
                lock (this.sync)
                {
                    return this.workers.Values.ToList();
                }
            }
        }

        public WorkersManager(Configuration config)
        {
            this.config = config;

            this.workDir = this.config.AppSettings.Settings["work_dir"].Value;
            this.exeFile = this.config.AppSettings.Settings["exe_file"].Value;

            var controllerHostValue = this.config.AppSettings.Settings["controller_host"].Value;
            if (!IPAddress.TryParse(controllerHostValue, out this.controllerHost))
            {
                var hostEntry = Dns.GetHostEntry(controllerHostValue);
                this.controllerHost = hostEntry.AddressList.FirstOrDefault();
                if (this.controllerHost == null)
                {
                    MessageBox.Show("Error", "Can't resolve DNS name: " + controllerHostValue, MessageBoxButtons.OK);
                    Application.Exit();
                }
            }
            
            var workerPortRange = this.config.AppSettings.Settings["worker_port_range"].Value;
            var parts = workerPortRange.Split(new[] {'-'}, StringSplitOptions.RemoveEmptyEntries);
            this.workerPortFrom = int.Parse(parts[0]);
            this.wokerPortTo = int.Parse(parts[1]);

            this.unresponsiveTimeout = TimeSpan.FromSeconds(int.Parse(this.config.AppSettings.Settings["unresponsive_timeout"].Value));
        }

        public IWorker AddWorker()
        {
            IWorker worker;
            lock (this.sync)
            {
                worker = this.CreateWorker();
                this.workers[worker.Port] = worker;
                this.SetWorkerCountSettings(this.workers.Count);
            }

            worker.Restarted += this.OnWorkerRestarted;
            worker.ProgressChanged += this.OnWorkerProgressChanged;
            worker.Start();

            this.Added?.Invoke(this, worker);
            return worker;
        }

        private void OnWorkerRestarted(object sender, EventArgs e)
        {
            this.Updated?.Invoke(this, (IWorker)sender);
        }

        private void OnWorkerProgressChanged(object sender, string e)
        {
            this.Updated?.Invoke(this, (IWorker)sender);
        }

        public void DeleteWorker(IWorker worker)
        {
            lock (this.sync)
            {
                if (!this.workers.ContainsKey(worker.Port))
                {
                    return;
                }

                this.workers.Remove(worker.Port);
                this.SetWorkerCountSettings(this.workers.Count);
            }

            worker.Restarted -= this.OnWorkerRestarted;
            worker.Kill();
            worker.Dispose();

            this.Deleted?.Invoke(this, worker);
        }

        public void Load()
        {
            var workerCount = this.GetWorkerCountSettings();

            for (var i = 0; i < workerCount; i++)
            {
                this.AddWorker();
            }
        }

        private IWorker CreateWorker()
        {
            Worker worker;

            lock (this.sync)
            {
                do
                {
                    var portRange = this.wokerPortTo - this.workerPortFrom;
                    var randomPort = (int)Math.Floor(this.workerPortFrom + portRange * Rnd.NextDouble());
                    var ip = GetLocalIp();
                    worker = new Worker(ip, randomPort, this.controllerHost.ToString(), this.exeFile, this.workDir, this.unresponsiveTimeout);
                } while (this.workers.ContainsKey(worker.Port));
            }

            return worker;
        }

        private string GetLocalIp()
        {
            var ifs = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(i => i.OperationalStatus == OperationalStatus.Up
                                     && i.GetIPProperties()?.UnicastAddresses.Count > 0
                                     &&
                                     i.GetIPProperties()
                                         .UnicastAddresses.Any(
                                             a => a.Address.AddressFamily == AddressFamily.InterNetwork));

            return ifs?
                .GetIPProperties()
                .UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?
                .Address
                .ToString();
        }

        private int GetWorkerCountSettings()
        {
            int workerCount;
            if (int.TryParse(this.config.AppSettings.Settings["worker_count"].Value, out workerCount))
            {
                return workerCount;
            }
            else
            {
                this.config.AppSettings.Settings["worker_count"].Value = "0";
                this.config.Save(ConfigurationSaveMode.Modified);
                return 0;
            }
        }

        private void SetWorkerCountSettings(int workerCount)
        {
            this.config.AppSettings.Settings["worker_count"].Value = workerCount.ToString();
            this.config.Save(ConfigurationSaveMode.Modified);
        }

        // ReSharper disable once UnusedMember.Local
        private float GetPhysicalRam()
        {
            var memStatus = new NativeMethods.MEMORYSTATUSEX();
            if (!NativeMethods.GlobalMemoryStatusEx(memStatus))
            {
                return 0.0f;
            }

            return memStatus.ullTotalPhys / 1024.0f / 1024.0f / 1024.0f;
        }

        public void Close()
        {
            List<IWorker> workersCopy;
            lock (this.sync)
            {
                workersCopy = this.workers.Values.ToList();
                this.workers.Clear();
            }

            foreach (var worker in workersCopy)
            {
                worker.Kill();
                worker.Dispose();
            }
        }

        public bool DeleteWorker(int port)
        {
            lock (this.sync)
            {
                var worker2Delete = this.workers.Values.FirstOrDefault(w => w.Port == port);
                if (worker2Delete == null)
                {
                    return false;
                }

                this.DeleteWorker(worker2Delete);

                return true;
            }
        }

        public bool KillWorker(int port)
        {
            lock (this.sync)
            {
                var worker2Kill = this.workers.Values.FirstOrDefault(w => w.Port == port);
                if (worker2Kill == null)
                {
                    return false;
                }

                if (worker2Kill.Pid != null)
                {
                    var workerProcess = Process.GetProcessById((int) worker2Kill.Pid, Environment.MachineName);
                    workerProcess.Kill();
                }

                return true;
            }
        }
    }
}