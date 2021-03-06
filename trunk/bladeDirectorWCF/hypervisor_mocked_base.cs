using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using hypervisors;

namespace bladeDirectorWCF
{
    public class hypervisor_mocked_base<T> : hypervisorWithSpec<T>
    {
        protected T _spec;
        private readonly hostStateManagerMocked.mockedExecutionDelegate _onMockedExecution;

        public List<mockedCall> events = new List<mockedCall>(); 

        /// <summary>
        /// What files are have been saved to disk
        /// </summary>
        public Dictionary<string, string> files = new Dictionary<string, string>();

        private bool powerState;

        public hypervisor_mocked_base(T spec, hostStateManagerMocked.mockedExecutionDelegate onMockedExecution)
        {
            _spec = spec;
            _onMockedExecution = onMockedExecution;
        }

        public override void restoreSnapshot()
        {
            events.Add(new mockedCall("restoreSnapshot", null));
        }

        public override void connect()
        {
            events.Add(new mockedCall("connect", null));
        }

        public override void powerOn(cancellableDateTime deadline)
        {
            events.Add(new mockedCall("powerOn", "deadline: " + deadline.ToString()));

            powerState = true;
        }

        public override void powerOff(cancellableDateTime deadline)
        {
            events.Add(new mockedCall("powerOff", "deadline: " + deadline.ToString()));

            powerState = false;
        }

        public override void WaitForStatus(bool isPowerOn, cancellableDateTime deadline)
        {
            events.Add(new mockedCall("WaitForStatus", "deadline: " + deadline.ToString()));
        }

        public override void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline)
        {
            events.Add(new mockedCall("copyToGuest", "source: '" +  srcpath + "' dest: '" + dstpath + "'"));
            lock (files)
            {
                files.Add(dstpath, File.ReadAllText(srcpath));
            }
        }

        public void copyDataToGuest(string dstpath, string fileContents)
        {
            lock (files)
            {
                files.Add(dstpath, fileContents);
            }
        }

        public void copyDataToGuest(string dstpath, Byte[] fileContents)
        {
            lock (files)
            {
                files.Add(dstpath, Encoding.ASCII.GetString(fileContents));
            }
        }

        public override string getFileFromGuest(string srcpath, cancellableDateTime deadline)
        {
            events.Add(new mockedCall("getFileFromGuest", "timeout: " + deadline.ToString()));
            lock (files)
            {
                return files[srcpath];
            }
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null, cancellableDateTime deadline = null)
        {
            events.Add(new mockedCall("startExecutable", "toExecute: '" +  toExecute + "' args: '" + args + "'" + " working dir: '" + (workingdir ?? "<null>") + "'"));

            return _onMockedExecution.Invoke(this, toExecute, args, workingdir, deadline);
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            events.Add(new mockedCall("startExecutableAsync", "toExecute: '" +  toExecute + "' args: '" + args + "'" + " working dir: '" + (workingDir ?? "<null>") + "'"));

            executionResult res = _onMockedExecution.Invoke(this, toExecute, args, workingDir);
            return new asycExcecutionResult_mocked(res);
        }

        public override IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null)
        {
            events.Add(new mockedCall("startExecutableAsyncWithRetry", "toExecute: '" +  toExecute + "' args: '" + args + "'" + " working dir: '" + (workingDir ?? "<null>") + "'"));

            executionResult res = _onMockedExecution.Invoke(this, toExecute, args, workingDir);
            return new asycExcecutionResult_mocked(res);
        }

        public override IAsyncExecutionResult startExecutableAsyncInteractively(string cmdExe, string args, string workingDir = null)
        {
            events.Add(new mockedCall("startExecutableAsyncInteractively", "toExecute: '" + cmdExe + "' args: '" + args + "'" + " working dir: '" + (workingDir ?? "<null>") + "'"));

            executionResult res = _onMockedExecution.Invoke(this, cmdExe, args, workingDir);
            return new asycExcecutionResult_mocked(res);
        }

        public override void mkdir(string newDir, cancellableDateTime deadline)
        {
            events.Add(new mockedCall("mkdir", "newDir: '" +  newDir + "'"));
        }

        public override T getConnectionSpec()
        {
            return _spec;
        }

        public override bool getPowerStatus()
        {
            return powerState;
        }
    }
}