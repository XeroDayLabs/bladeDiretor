using System;
using System.Collections.Generic;
using System.Threading;
using bladeDirectorWCF;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace tests
{
    [TestClass]
    public class mockedVMDestruction
    {
        [TestMethod]
        public void willDeallocateOldVMsOnLogon()
        {
            using (bladeDirectorDebugServices svc = new bladeDirectorDebugServices(basicBladeTests.WCFPath, "172.17.129.131", true))
            {
                string hostIP = "1.1.1.1";
                testUtils.doLogin(svc, hostIP);

                // Allocate all the blades, then login again. The allocated blades should no longer be allocated.
                string ourVM = testUtils.doVMAllocationForTest(svc, hostIP);

                // Find the parent blade of the VM we got, and make sure it is now in use (by the blade director)
                vmSpec VMSpec = svc.svc.getVMByIP_withoutLocking(ourVM);
                bladeSpec bladeSpec = svc.svc.getBladeByIP_withoutLocking(VMSpec.parentBladeIP);

                GetBladeStatusResult bladeState = svc.svcDebug._GetBladeStatus(hostIP, bladeSpec.bladeIP);
                string bladeIP = bladeSpec.bladeIP;
                Assert.AreEqual(bladeState, GetBladeStatusResult.notYours);

                // Do a new login, which should cause our blades to be deallocated.
                testUtils.doLogin(svc, hostIP);

                // The VM should now not exist.
                Assert.AreEqual(null, svc.svc.getVMByIP_withoutLocking(ourVM));

                // Find the parent blade of the VM we got, and make sure it is now unused.
                GetBladeStatusResult bladeState2 = svc.svcDebug._GetBladeStatus(hostIP, bladeIP);
                Assert.AreEqual(bladeState2, GetBladeStatusResult.unused);
            }
        }

        [TestMethod]
        public void willReUseOldVMsAfterLogon()
        {
            using (bladeDirectorDebugServices svc = new bladeDirectorDebugServices(basicBladeTests.WCFPath, "172.17.129.131", true))
            {
                string hostIP = "1.1.1.1";

                testUtils.doLogin(svc, hostIP);
                string firstVM = testUtils.doVMAllocationForTest(svc, hostIP);

                testUtils.doLogin(svc, hostIP);
                string secondVM = testUtils.doVMAllocationForTest(svc, hostIP);

                Assert.AreEqual(firstVM, secondVM);
            }
        }

        [TestMethod]
        public void willReUseOldVMsAfterLogonDuringBIOSOperation()
        {
            using (bladeDirectorDebugServices svc = new bladeDirectorDebugServices(basicBladeTests.WCFPath))
            {
                string hostIP = "1.1.1.1";
                svc.svcDebug.initWithBladesFromIPList(new[] { "172.17.129.131" }, true, NASFaultInjectionPolicy.retunSuccessful);

                // Start a 5-minute long BIOS operation, then cancel it by logging in again.
                svc.svcDebug._setBIOSOperationTimeIfMocked((int)TimeSpan.FromMinutes(5).TotalSeconds);

                testUtils.doLogin(svc, hostIP);
                
                // Start to allocate a VM
                VMHardwareSpec hwspec = new VMHardwareSpec { cpuCount = 1, memoryMB = 1024 * 3 };
                VMSoftwareSpec swspec = new VMSoftwareSpec();

                resultAndWaitToken res = svc.svcDebug._requestAnySingleVM(hostIP, hwspec, swspec);
                Assert.AreEqual(resultCode.pending, res.result.code);

                // The blade director should have assumed ownership of the blade itself, in order to deploy the VM
                Assert.AreEqual(true, svc.svcDebug._isBladeMine("vmserver", "172.17.129.131", true));
                
                // Now login again, cancelling the BIOS operation.
                testUtils.doLogin(svc, hostIP);

                // The blade should no longer be used by the blade director, nor by us
                Assert.AreEqual(false, svc.svcDebug._isBladeMine("vmserver", "172.17.129.131", false));
                Assert.AreEqual(false, svc.svcDebug._isBladeMine(hostIP, "172.17.129.131", false));

                // And after an allocation, our blade should be re-used successfully.
                svc.svcDebug._setBIOSOperationTimeIfMocked((int)2);
                testUtils.doVMAllocationForTest(svc, hostIP);
            }
        }
    }
}