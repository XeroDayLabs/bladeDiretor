using System;
using System.Diagnostics;
using System.IO;
using System.ServiceModel;
using System.Threading;
using bladeDirectorClient.bladeDirectorService;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace bladeDirectorClient
{
    public class BladeDirectorServices : IDisposable
    {
        public readonly ServicesClient svc;

        private Process _bladeDirectorProcess;
        public string servicesURL { get; private set; }

        protected string baseURL { get; private set; }

        /// <summary>
        /// Launch the given exe with the the specified port, on a random URL.
        /// </summary>
        /// <param name="bladeDirectorWCFExe"></param>
        /// <param name="port"></param>
        public BladeDirectorServices(string bladeDirectorWCFExe, int port)
        {
            baseURL = string.Format("http://127.0.0.1:{0}/{1}", port, Guid.NewGuid().ToString());
            servicesURL = baseURL + "/bladeDirector";

            connectWithArgs(bladeDirectorWCFExe, "--baseURL " + baseURL);

            WSHttpBinding baseBinding = new WSHttpBinding
            {
                MaxReceivedMessageSize = Int32.MaxValue,
                ReaderQuotas = { MaxStringContentLength = Int32.MaxValue }
            };
            svc = new ServicesClient(baseBinding, new EndpointAddress(servicesURL));
        }

        /// <summary>
        /// Connect to a remote blade director as specified.
        /// </summary>
        public BladeDirectorServices(string url)
        {
            servicesURL = url;
            WSHttpBinding baseBinding = new WSHttpBinding
            {
                MaxReceivedMessageSize = Int32.MaxValue,
                ReaderQuotas = { MaxStringContentLength = Int32.MaxValue }
            };
            svc = new ServicesClient(baseBinding, new EndpointAddress(servicesURL));
        }

        protected void connectWithArgs(string bladeDirectorWCFExe, string args)
        {
            ProcessStartInfo bladeDirectorExeInfo = new ProcessStartInfo(bladeDirectorWCFExe);
            bladeDirectorExeInfo.WorkingDirectory = Path.GetDirectoryName(bladeDirectorWCFExe);
            bladeDirectorExeInfo.Arguments = args;
            bladeDirectorExeInfo.UseShellExecute = false;
            bladeDirectorExeInfo.RedirectStandardOutput = true;
            _bladeDirectorProcess = Process.Start(bladeDirectorExeInfo);

            while (true)
            {
                string line = _bladeDirectorProcess.StandardOutput.ReadLine();
                if (line == null)
                    Assert.Fail("bladedirectorWCF did not start, perhaps there is another running?");
                if (line.Contains("to exit"))
                    break;
            }

            Thread.Sleep(TimeSpan.FromSeconds(3));

        }

        public resultAndBladeName waitForSuccess(resultAndBladeName res, TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (res.result.code != resultCode.success)
            {
                switch (res.result.code)
                {
                    case resultCode.success:
                    case resultCode.noNeedLah:
                        break;
                    case resultCode.pending:
                        if (DateTime.Now > deadline)
                            throw new TimeoutException();
                        res = (resultAndBladeName) this.svc.getProgress(res.waitToken);
                        continue;
                    default:
                        throw new Exception("Unexpected status during .getProgress: " + res.result.code + " / " + res.result.errMsg);
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            return res;
        }

        public resultAndWaitToken waitForSuccess(resultAndWaitToken res, TimeSpan timeout)
        {
            DateTime deadline = DateTime.Now + timeout;
            while (res.result.code != resultCode.success)
            {
                switch (res.result.code)
                {
                    case resultCode.success:
                    case resultCode.noNeedLah:
                        break;
                    case resultCode.pending:
                        if (DateTime.Now > deadline)
                            throw new TimeoutException();
                        res = this.svc.getProgress(res.waitToken);
                        continue;
                    default:
                        throw new Exception("Unexpected status during .getProgress: " + res.result.code + " / " + res.result.errMsg);
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            return res;
        }

        public virtual void Dispose()
        {
            try
            {
                Debug.WriteLine("Log entries from bladeDirector:");
                foreach (string msg in svc.getLogEvents())
                    Debug.WriteLine(msg);
            }
            catch (Exception) { }

            // FIXME: why these casts?
            try { ((IDisposable)svc).Dispose(); }
            catch (CommunicationException) { }

            if (_bladeDirectorProcess != null)
            {
                try
                {
                    _bladeDirectorProcess.Kill();
                }
                catch (Exception)
                {
                    // ...
                }

                _bladeDirectorProcess.Dispose();
            }
        }
    }
}