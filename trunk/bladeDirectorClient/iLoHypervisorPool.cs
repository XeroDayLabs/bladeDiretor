using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading;
using bladeDirectorWCF;
using hypervisors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace bladeDirectorClient
{
    public class iLoHypervisorPool
    {
        private readonly object keepaliveThreadLock = new object();
        private Thread keepaliveThread = null;

        private bool isConnected = false;

        private readonly object _connectionLock = new object();

        public hypervisorCollection<hypSpec_vmware> requestVMs(VMSpec[] specs, bool allowPartialAllocation = false)
        {
            initialiseIfNeeded();

            Stopwatch allocWatch = new Stopwatch();

            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(30);
                resultAndBladeName[] results = new resultAndBladeName[specs.Length];
                for (int n = 0; n < specs.Length; n++)
                {
                    results[n] = director.svc.RequestAnySingleVM(specs[n].hw, specs[n].sw);

                    if (results[n].result.code == resultCode.bladeQueueFull)
                    {
                        if (allowPartialAllocation)
                        {
                            results[n] = null;
                            break;
                        }
                    }

                    if (results[n].result.code != resultCode.success &&
                        results[n].result.code != resultCode.pending)
                    {
                        throw new bladeAllocationException(results[n].result.code);
                    }
                }

                hypervisorCollection<hypSpec_vmware> toRet = new hypervisorCollection<hypSpec_vmware>();
                try
                {
                    foreach (resultAndBladeName res in results)
                    {
                        if (res == null)
                            continue;

                        resultAndBladeName progress = director.waitForSuccessWithoutThrowing(res, deadline - DateTime.Now);
                        if (progress == null)
                            throw new TimeoutException();
                        if (progress.result.code == resultCode.bladeQueueFull)
                        {
                            if (allowPartialAllocation)
                                continue;
                            throw new Exception("Not enough blades available to accomodate new VMs");
                        }
                        if (progress.result.code != resultCode.success)
                        {
                            if (allowPartialAllocation)
                                continue;
                            throw new Exception("Unexpected status while allocing: " + progress.result.code);
                        }

                        vmSpec vmSpec = director.svc.getVMByIP_withoutLocking(progress.bladeName);
                        bladeSpec vmServerSpec = director.svc.getBladeByIP_withoutLocking(vmSpec.parentBladeIP);
                        snapshotDetails snapshotInfo = director.svc.getCurrentSnapshotDetails(vmSpec.VMIP);
                        NASParams nasParams = director.svc.getNASParams();

                        hypervisor_vmware_FreeNAS newVM = utils.createHypForVM(vmSpec, vmServerSpec, snapshotInfo, nasParams);

                        ipUtils.ensurePortIsFree(vmSpec.kernelDebugPort);

                        newVM.setDisposalCallback(onDestruction);

                        if (!toRet.TryAdd(vmSpec.VMIP, newVM))
                            throw new Exception();
                    }
                }
                catch (Exception)
                {
                    foreach (KeyValuePair<string, hypervisorWithSpec<hypSpec_vmware>> allocedBlades in toRet)
                    {
                        if (allocedBlades.Key != null)
                        {
                            try
                            {
                                director.svc.ReleaseBladeOrVM(allocedBlades.Key);
                            }
                            catch (Exception)
                            {
                                // ...
                            }
                        }
                    }
                    throw;
                }
                allocWatch.Stop();
                Debug.WriteLine("Allocated all VMs, took " + allocWatch.Elapsed.ToString());
                return toRet;
            }
        }

        public hypervisorCollection<hypSpec_iLo> requestAsManyHypervisorsAsPossible(string snapshotName)
        {
            initialiseIfNeeded();
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                int nodeCount = director.svc.getAllBladeIP().Length;

                hypervisorCollection<hypSpec_iLo> toRet = new hypervisorCollection<hypSpec_iLo>();
                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(60);
                try
                {
                    for(int i = 0; i < nodeCount; i++)
                    {
                        resultAndBladeName res = director.svc.RequestAnySingleNode();
                        resultAndBladeName progress = director.waitForSuccess(res, deadline - DateTime.Now);

                        bladeSpec bladeConfig = director.svc.getBladeByIP_withoutLocking(progress.bladeName);
                        resultAndWaitToken snapRes =  director.svc.selectSnapshotForBladeOrVM(progress.bladeName, snapshotName);
                        director.waitForSuccess(snapRes, TimeSpan.FromMinutes(3));

                        snapshotDetails snapshot = director.svc.getCurrentSnapshotDetails(progress.bladeName);
                        userDesc cred = bladeConfig.credentials.First();
                        NASParams nas = director.svc.getNASParams();
                        hypSpec_iLo spec = new hypSpec_iLo(
                            bladeConfig.bladeIP, cred.username, cred.password,
                            bladeConfig.iLOIP, bladeConfig.iLoUsername, bladeConfig.iLoPassword,
                            nas.IP, nas.username, nas.password,
                            snapshot.friendlyName, snapshot.path, 
                            bladeConfig.kernelDebugPort, bladeConfig.kernelDebugKey
                            );

                        ipUtils.ensurePortIsFree(bladeConfig.kernelDebugPort);

                        bladeDirectedHypervisor_iLo newHyp = new bladeDirectedHypervisor_iLo(spec);
                        newHyp.setDisposalCallback(onDestruction);

                        if (!toRet.TryAdd(bladeConfig.bladeIP, newHyp))
                            throw new Exception();
                    }
                    return toRet;
                }
                catch (Exception)
                {
                    toRet.Dispose();
                    throw;
                }
            }
        }


        public bladeDirectedHypervisor_iLo createSingleHypervisor(string snapshotName)
        {
            initialiseIfNeeded();

            // We request a blade from the blade director, and use them for all our tests, blocking if none are available.
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                // Request a node. If all queues are full, then wait and retry until we get one.
                resultAndBladeName allocatedBladeResult;
                while (true)
                {
                    allocatedBladeResult = director.svc.RequestAnySingleNode();
                    if (allocatedBladeResult.result.code == resultCode.success ||
                        allocatedBladeResult.result.code == resultCode.pending)
                        break;
                    if (allocatedBladeResult.result.code == resultCode.bladeQueueFull)
                    {
                        Debug.WriteLine("All blades are fully queued, waiting until one is spare");
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                        continue;
                    }
                    throw new Exception("Blade director returned unexpected return status '" + allocatedBladeResult.result + "'");
                }

                // Now, wait until our blade is available.
                try
                {
                    bool isMine = director.svc.isBladeMine(allocatedBladeResult.bladeName);
                    while (!isMine)
                    {
                        Debug.WriteLine("Blade " + allocatedBladeResult.bladeName + " not released yet, awaiting release..");
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                        isMine = director.svc.isBladeMine(allocatedBladeResult.bladeName);
                    }

                    // Great, now we have ownership of the blade, so we can use it safely.
                    //bladeSpec bladeConfig = director.svc.getConfigurationOfBlade(allocatedBladeResult.bladeName);
                    resultAndWaitToken res = director.svc.selectSnapshotForBladeOrVM(allocatedBladeResult.bladeName, snapshotName);
                    director.waitForSuccess(res, TimeSpan.FromMinutes(5));

                    bladeSpec bladeConfig = director.svc.getBladeByIP_withoutLocking(allocatedBladeResult.bladeName);
                    snapshotDetails currentShot = director.svc.getCurrentSnapshotDetails(allocatedBladeResult.bladeName);
                    userDesc cred = bladeConfig.credentials.First();
                    NASParams nas = director.svc.getNASParams();

                    hypSpec_iLo spec = new hypSpec_iLo(
                        bladeConfig.bladeIP, cred.username, cred.password,
                        bladeConfig.iLOIP, bladeConfig.iLoUsername, bladeConfig.iLoPassword,
                        nas.IP, nas.username, nas.password,
                        currentShot.friendlyName, currentShot.path, 
                        bladeConfig.kernelDebugPort, bladeConfig.kernelDebugKey
                        );

                    ipUtils.ensurePortIsFree(bladeConfig.kernelDebugPort);

                    bladeDirectedHypervisor_iLo toRet = new bladeDirectedHypervisor_iLo(spec);
                    toRet.setDisposalCallback(onDestruction);
                    return toRet;
                }
                catch (Exception)
                {
                    director.svc.ReleaseBladeOrVM(allocatedBladeResult.bladeName);
                    throw;
                }
            }
        }

        public static bool doesCallStackHasTrait(string traitName)
        {
            StackTrace stack = new StackTrace();

            // Find the stack trace that has the TestMethodAttribute set
            StackFrame[] frames = stack.GetFrames();
            StackFrame testFrame = frames.SingleOrDefault(x =>
                x.GetMethod().GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0);

            // If no [TestMethod] is found, we're probably being called from a different thread than the main one, or from 
            // non-test code. 
            if (testFrame == null)
            {
                Debug.WriteLine("Cannot ensure this has required trait " + traitName + "; please check it manually if required");
                return true;
            }

            // Check it has the correct trait.
            List<Attribute> attribs = testFrame.GetMethod().GetCustomAttributes(typeof(TestCategoryAttribute)).ToList();
            if (attribs.SingleOrDefault(x => ((TestCategoryAttribute)x).TestCategories.Contains(traitName)) == null)
                return false;

            return true;
        }


        private void initialiseIfNeeded()
        {
            if (!doesCallStackHasTrait("requiresBladeDirector"))
                Assert.Fail("This test uses the blade director; please decorate it with [TestCategory(\"requiresBladeDirector\")] for easier maintenence");

            if (keepaliveThread == null)
            {
                lock (keepaliveThreadLock)
                {
                    if (keepaliveThread == null)
                    {
                        using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
                        {
                            resultAndWaitToken waitToken = director.svc.logIn();
                            // If there's a login already going for this machine, we will just use that one.
                            if (waitToken.result.code == resultCode.alreadyInProgress)
                                waitToken.result.code = resultCode.pending;
                            director.waitForSuccess(waitToken, TimeSpan.FromMinutes(10));
                        }
                        keepaliveThread = new Thread(keepaliveThreadMain);
                        keepaliveThread.Name = "Blade director keepalive thread";
                        keepaliveThread.Start();
                    }
                }
            }

            if (!isConnected)
            {
                lock (_connectionLock)
                {
                    isConnected = true;
                }
            }
        }

        private void keepaliveThreadMain()
        {
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                while (true)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                    try
                    {
                        director.svc.keepAlive();
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
        }

        private void onDestruction(hypSpec_iLo obj)
        {
            // Notify the director that this blade is no longer in use.
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                director.svc.ReleaseBladeOrVM(obj.kernelDebugIPOrHostname);
            }
        }

        private void onDestruction(hypSpec_vmware obj)
        {
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                director.svc.ReleaseBladeOrVM(obj.kernelDebugIPOrHostname);
            }
        }
    }

    public class bladeAllocationException : Exception
    {
        private readonly resultCode _code;

        public bladeAllocationException(resultCode code)
        {
            _code = code;
        }

        public override string ToString()
        {
            return "Blade allocation service returned code " + _code;
        }
    }

    public class bladeDirectedHypervisor_iLo : hypervisor_iLo
    {
        public bladeDirectedHypervisor_iLo(hypSpec_iLo spec)
            : base(spec)
        {
        }

        public void setBIOSConfig(string newBiosXML)
        {
            hypSpec_iLo spec = getConnectionSpec();
            using (BladeDirectorServices director = new BladeDirectorServices(machinePools.bladeDirectorURL))
            {
                resultAndWaitToken res = director.svc.rebootAndStartDeployingBIOSToBlade(spec.kernelDebugIPOrHostname, newBiosXML);
                director.waitForSuccess(res, TimeSpan.FromMinutes(3));
            }
        }
    }

}